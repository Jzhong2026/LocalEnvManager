using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalEnvManager.Mcp.Contracts;

namespace LocalEnvManager.Mcp;

/// <summary>
/// Thin client for the LocalEnvManager loopback command endpoint. The MCP
/// bridge is launched by Grok Build; the GUI must already be running.
/// </summary>
internal static class CommandClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static async Task<string> CallAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var endpoint = await ReadEndpointAsync(cancellationToken).ConfigureAwait(false);

        using var client = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, connectCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or TimeoutException)
        {
            throw new InvalidOperationException(
                "LocalEnvManager is not running, or its MCP command endpoint is unavailable. " +
                "Start LocalEnvManager (administrator) and try again.",
                ex);
        }

        client.NoDelay = true;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var request = new McpCommandRequest(
            Guid.NewGuid().ToString("N"),
            method,
            parameters is null
                ? null
                : JsonSerializer.SerializeToElement(parameters, McpCommandProtocol.Json),
            endpoint.Token);

        var payload = JsonSerializer.Serialize(request, McpCommandProtocol.Json);
        await writer.WriteLineAsync(payload).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidOperationException("LocalEnvManager returned an empty MCP response.");
        }

        var response = JsonSerializer.Deserialize<McpCommandResponse>(line, McpCommandProtocol.Json)
            ?? throw new InvalidOperationException("LocalEnvManager returned an invalid MCP response.");

        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error ?? "LocalEnvManager MCP request failed.");
        }

        return response.Result?.GetRawText() ?? "null";
    }

    private static async Task<McpEndpointInfo> ReadEndpointAsync(CancellationToken cancellationToken)
    {
        var path = McpCommandProtocol.ResolveEndpointPath();
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<McpEndpointInfo>(json, McpCommandProtocol.Json)
                ?? throw new InvalidOperationException("LocalEnvManager MCP endpoint file is invalid.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(
                "LocalEnvManager is not running (MCP endpoint file was not found). " +
                "Start LocalEnvManager (administrator) and try again.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new InvalidOperationException(
                "LocalEnvManager MCP endpoint file could not be read. " +
                "Restart LocalEnvManager and try again.",
                ex);
        }
    }
}
