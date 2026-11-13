using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Caliburn.Micro;
using LocalEnvManager.Mcp.Contracts;
using LocalEnvManager.Messages;
using LocalEnvManager.Models;

namespace LocalEnvManager.Services.Mcp;

/// <summary>
/// Local-only MCP command bridge hosted by the WPF process.
///
/// The GUI runs elevated, while Grok Build / the stdio bridge normally does not.
/// A named pipe created by the elevated process rejects the lower-integrity
/// client, so the internal hop is a loopback-only TCP listener with a random
/// bearer token. No non-loopback interface is bound, and the bridge learns the
/// port/token from a per-user endpoint file.
/// </summary>
public sealed class McpCommandServer : IHandle<ConsoleLineMessage>,
    IHandle<ProjectRunStartedMessage>,
    IHandle<ProjectRunFinishedMessage>,
    IHandle<RunProjectResultMessage>,
    IHandle<StopProjectResultMessage>
{
    private const int MaxLogLinesPerProject = 2000;

    private readonly IProjectCatalog _catalog;
    private readonly IEventAggregator _events;
    private readonly IRuntimeLogger _logger;
    private readonly string _endpointPath;

    private readonly object _logGate = new();
    private readonly Dictionary<int, Queue<ProjectLogLine>> _logs = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RunProjectResult>> _runWaiters = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<StopProjectResult>> _stopWaiters = new();

    private CancellationTokenSource? _lifetimeCts;
    private TcpListener? _listener;
    private string _token = string.Empty;
    private int _started;

    public McpCommandServer(
        IProjectCatalog catalog,
        IEventAggregator events,
        IRuntimeLogger logger,
        string? endpointPath = null)
    {
        _catalog = catalog;
        _events = events;
        _logger = logger;
        _endpointPath = string.IsNullOrWhiteSpace(endpointPath)
            ? McpCommandProtocol.ResolveEndpointPath()
            : endpointPath;
        _events.SubscribeOnUI(this);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        try
        {
            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            WriteEndpointFile(port);

            _lifetimeCts = new CancellationTokenSource();
            var token = _lifetimeCts.Token;
            _ = Task.Run(() => AcceptLoopAsync(token), CancellationToken.None);

            _logger.Append(LogEntry.LogLevel.Info,
                $"[MCP] 命令端点已启动：127.0.0.1:{port}（仅回环 + Bearer token）");
        }
        catch (Exception ex)
        {
            _logger.Append(LogEntry.LogLevel.Error, $"[MCP] 启动命令端点失败：{ex.Message}");
            try { _listener?.Stop(); } catch { }
            _listener = null;
            Interlocked.Exchange(ref _started, 0);
        }
    }

    public void Stop()
    {
        try { _lifetimeCts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _events.Unsubscribe(this); } catch { }

        foreach (var waiter in _runWaiters.Values)
        {
            waiter.TrySetException(new OperationCanceledException("LocalEnvManager 正在退出。"));
        }
        foreach (var waiter in _stopWaiters.Values)
        {
            waiter.TrySetException(new OperationCanceledException("LocalEnvManager 正在退出。"));
        }
        _runWaiters.Clear();
        _stopWaiters.Clear();

        TryDeleteEndpointFile();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                if (cancellationToken.IsCancellationRequested) return;
                _logger.Append(LogEntry.LogLevel.Warn, $"[MCP] 接受连接失败：{ex.Message}");
                continue;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
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

            McpCommandResponse response;
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                var request = JsonSerializer.Deserialize<McpCommandRequest>(line, McpCommandProtocol.Json)
                    ?? throw new InvalidOperationException("Invalid MCP command request.");

                if (!IsAuthorized(request.Token))
                {
                    _logger.Append(LogEntry.LogLevel.Warn, "[MCP] 拒绝未授权连接。");
                    response = new McpCommandResponse(request.Id, false, Error: "Unauthorized.");
                }
                else
                {
                    response = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Append(LogEntry.LogLevel.Error, $"[MCP] 请求失败：{ex.Message}");
                response = new McpCommandResponse("0", false, Error: ex.Message);
            }

            try
            {
                var payload = JsonSerializer.Serialize(response, McpCommandProtocol.Json);
                await writer.WriteLineAsync(payload).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Append(LogEntry.LogLevel.Warn, $"[MCP] 回写响应失败：{ex.Message}");
            }
        }
    }

    private bool IsAuthorized(string? candidate)
    {
        if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(candidate)) return false;

        var expected = Encoding.UTF8.GetBytes(_token);
        var actual = Encoding.UTF8.GetBytes(candidate);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private async Task<McpCommandResponse> DispatchAsync(
        McpCommandRequest request,
        CancellationToken cancellationToken)
    {
        _logger.Append(LogEntry.LogLevel.Info, $"[MCP] {request.Method}");

        try
        {
            object result = request.Method switch
            {
                McpCommandProtocol.ListProjects => await ListProjectsAsync().ConfigureAwait(false),
                McpCommandProtocol.GetProjectStatus => await GetProjectStatusAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                McpCommandProtocol.GetProjectLogs => await GetProjectLogsAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                McpCommandProtocol.RunProject => await RunProjectAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                McpCommandProtocol.StopProject => await StopProjectAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown MCP method: {request.Method}")
            };

            var element = JsonSerializer.SerializeToElement(result, McpCommandProtocol.Json);
            return new McpCommandResponse(request.Id, true, element);
        }
        catch (Exception ex)
        {
            _logger.Append(LogEntry.LogLevel.Error, $"[MCP] {request.Method} 失败：{ex.Message}");
            return new McpCommandResponse(request.Id, false, Error: ex.Message);
        }
    }

    private Task<List<ProjectSummary>> ListProjectsAsync()
        => OnUiAsync(() => _catalog.Projects.Select(ToSummary).ToList());

    private async Task<ProjectSummary> GetProjectStatusAsync(
        McpCommandRequest request,
        CancellationToken cancellationToken)
    {
        var reference = Deserialize<ProjectRef>(request)
            ?? throw new InvalidOperationException("Missing project reference.");
        var project = await ResolveProjectAsync(reference, cancellationToken).ConfigureAwait(false);
        return await OnUiAsync(() => ToSummary(project)).ConfigureAwait(false);
    }

    private async Task<ProjectLogsResult> GetProjectLogsAsync(
        McpCommandRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = Deserialize<ProjectLogsParams>(request)
            ?? throw new InvalidOperationException("Missing project logs parameters.");
        var project = await ResolveProjectAsync(parameters.Project, cancellationToken).ConfigureAwait(false);
        var projectId = await OnUiAsync(() => _catalog.GetProjectId(project)).ConfigureAwait(false);
        var maxLines = Math.Clamp(parameters.MaxLines, 1, MaxLogLinesPerProject);

        lock (_logGate)
        {
            if (!_logs.TryGetValue(projectId, out var queue))
            {
                return new ProjectLogsResult(projectId, project.Name, []);
            }

            var lines = queue.Count <= maxLines
                ? queue.ToArray()
                : queue.Skip(queue.Count - maxLines).ToArray();
            return new ProjectLogsResult(projectId, project.Name, lines);
        }
    }

    private async Task<RunProjectResult> RunProjectAsync(
        McpCommandRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = Deserialize<RunProjectParams>(request)
            ?? throw new InvalidOperationException("Missing run parameters.");
        var project = await ResolveProjectAsync(parameters.Project, cancellationToken).ConfigureAwait(false);
        var projectId = await OnUiAsync(() => _catalog.GetProjectId(project)).ConfigureAwait(false);

        var correlationId = Guid.NewGuid();
        var waiter = new TaskCompletionSource<RunProjectResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _runWaiters[correlationId] = waiter;

        var started = DateTimeOffset.UtcNow;
        try
        {
            await PublishOnUiAsync(new RunProjectRequestMessage(
                correlationId, projectId, parameters.IncludeDependencies)).ConfigureAwait(false);

            var timeout = TimeSpan.FromSeconds(Math.Clamp(parameters.TimeoutSeconds, 1, 3600));
            var completed = await Task.WhenAny(
                waiter.Task,
                Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

            if (completed == waiter.Task)
            {
                return await waiter.Task.ConfigureAwait(false);
            }

            _runWaiters.TryRemove(correlationId, out _);
            var summary = await OnUiAsync(() => ToSummary(project)).ConfigureAwait(false);
            return new RunProjectResult(
                projectId,
                project.Name,
                summary.State,
                Success: false,
                Cancelled: false,
                Message: $"运行仍在进行中（已等待 {timeout.TotalSeconds:0}s）。可调用 get_project_status / get_project_logs 继续查询。",
                (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
        }
        finally
        {
            _runWaiters.TryRemove(correlationId, out _);
        }
    }

    private async Task<StopProjectResult> StopProjectAsync(
        McpCommandRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = Deserialize<StopProjectParams>(request)
            ?? throw new InvalidOperationException("Missing stop parameters.");
        var project = await ResolveProjectAsync(parameters.Project, cancellationToken).ConfigureAwait(false);
        var projectId = await OnUiAsync(() => _catalog.GetProjectId(project)).ConfigureAwait(false);

        var correlationId = Guid.NewGuid();
        var waiter = new TaskCompletionSource<StopProjectResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _stopWaiters[correlationId] = waiter;

        try
        {
            await PublishOnUiAsync(new StopProjectRequestMessage(correlationId, projectId))
                .ConfigureAwait(false);

            var timeout = TimeSpan.FromSeconds(30);
            var completed = await Task.WhenAny(
                waiter.Task,
                Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

            if (completed == waiter.Task)
            {
                return await waiter.Task.ConfigureAwait(false);
            }

            return new StopProjectResult(projectId, project.Name, false,
                "停止请求已发出，但 30 秒内未收到执行器结果。");
        }
        finally
        {
            _stopWaiters.TryRemove(correlationId, out _);
        }
    }

    private async Task<ManagedProject> ResolveProjectAsync(
        ProjectRef reference,
        CancellationToken cancellationToken)
    {
        var project = await OnUiAsync(() => ResolveProject(_catalog, reference)).ConfigureAwait(false);

        return project ?? throw new InvalidOperationException(
            $"Project not found: {reference.Name ?? reference.Id?.ToString() ?? "(empty)"}");
    }

    /// <summary>
    /// Resolves a project reference with precedence: explicit Id, then exact
    /// (unique) display name, then a numeric name treated as an id fallback.
    /// Name must be tried before the numeric-id fallback so a project literally
    /// named "2" is not shadowed by another project whose runtime id is 2.
    /// </summary>
    public static ManagedProject? ResolveProject(IProjectCatalog catalog, ProjectRef reference)
    {
        var name = reference.Name?.Trim();

        if (reference.Id is > 0)
        {
            var byId = catalog.FindById(reference.Id.Value);
            if (byId is not null) return byId;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = catalog.Projects.FirstOrDefault(candidate =>
                string.Equals(candidate.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }

        if (int.TryParse(name, out var numericId) && numericId > 0)
        {
            return catalog.FindById(numericId);
        }

        return null;
    }

    private ProjectSummary ToSummary(ManagedProject project)
    {
        var id = _catalog.GetProjectId(project);
        var category = _catalog.Categories
            .FirstOrDefault(candidate => candidate.Projects.Contains(project))?.Name ?? string.Empty;

        return new ProjectSummary(
            id,
            project.Name,
            category,
            project.DeploymentModeDisplay,
            project.SourcePath,
            project.TargetPath,
            project.BackupFolder,
            project.LogsDirectory,
            project.LastRunState.ToString(),
            project.HasRunningProcess,
            project.LastExecutionMessage,
            project.LatestLogSnippet,
            project.CurrentBranch);
    }

    private async Task PublishOnUiAsync(object message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _events.PublishOnCurrentThread(message);
            return;
        }

        await dispatcher.InvokeAsync(() => _events.PublishOnCurrentThread(message));
    }

    private static Task<T> OnUiAsync<T>(Func<T> factory)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(factory());
        }
        return dispatcher.InvokeAsync(factory).Task;
    }

    private static T? Deserialize<T>(McpCommandRequest request)
    {
        if (request.Params is not { } element) return default;
        return element.Deserialize<T>(McpCommandProtocol.Json);
    }

    private void AddLog(int projectId, string channel, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_logGate)
        {
            if (!_logs.TryGetValue(projectId, out var queue))
            {
                queue = new Queue<ProjectLogLine>();
                _logs[projectId] = queue;
            }

            queue.Enqueue(new ProjectLogLine(DateTimeOffset.Now, channel, text));
            while (queue.Count > MaxLogLinesPerProject)
            {
                queue.Dequeue();
            }
        }
    }

    public Task HandleAsync(ConsoleLineMessage message, CancellationToken cancellationToken)
    {
        AddLog(message.ProjectId, message.IsStderr ? "stderr" : "stdout", message.Line);
        return Task.CompletedTask;
    }

    public Task HandleAsync(ProjectRunStartedMessage message, CancellationToken cancellationToken)
    {
        AddLog(message.ProjectId, "system", $"update started ({message.Mode})");
        return Task.CompletedTask;
    }

    public Task HandleAsync(ProjectRunFinishedMessage message, CancellationToken cancellationToken)
    {
        AddLog(message.ProjectId, "system",
            message.Cancelled ? "update stopped by user" : $"update finished: {message.Message}");
        return Task.CompletedTask;
    }

    public Task HandleAsync(RunProjectResultMessage message, CancellationToken cancellationToken)
    {
        if (_runWaiters.TryRemove(message.CorrelationId, out var waiter))
        {
            waiter.TrySetResult(message.Result);
        }
        return Task.CompletedTask;
    }

    public Task HandleAsync(StopProjectResultMessage message, CancellationToken cancellationToken)
    {
        if (_stopWaiters.TryRemove(message.CorrelationId, out var waiter))
        {
            waiter.TrySetResult(message.Result);
        }
        return Task.CompletedTask;
    }

    private void WriteEndpointFile(int port)
    {
        var directory = Path.GetDirectoryName(_endpointPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var endpoint = new McpEndpointInfo(
            port, _token, Environment.ProcessId, DateTimeOffset.Now);
        var json = JsonSerializer.Serialize(endpoint, McpCommandProtocol.Json);
        var temp = _endpointPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _endpointPath, overwrite: true);
    }

    private void TryDeleteEndpointFile()
    {
        try
        {
            if (!File.Exists(_endpointPath)) return;

            var endpoint = JsonSerializer.Deserialize<McpEndpointInfo>(
                File.ReadAllText(_endpointPath), McpCommandProtocol.Json);
            if (endpoint is not null
                && endpoint.ProcessId == Environment.ProcessId
                && string.Equals(endpoint.Token, _token, StringComparison.Ordinal))
            {
                File.Delete(_endpointPath);
            }
        }
        catch
        {
            // Best effort: a stale endpoint file only causes a clear connect error.
        }
    }
}
