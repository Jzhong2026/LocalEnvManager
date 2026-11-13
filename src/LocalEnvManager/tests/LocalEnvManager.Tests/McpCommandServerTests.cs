using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Caliburn.Micro;
using LocalEnvManager.Mcp.Contracts;
using LocalEnvManager.Models;
using LocalEnvManager.Services;
using LocalEnvManager.Services.Mcp;
using Xunit;

namespace LocalEnvManager.Tests;

public sealed class McpCommandServerTests : IDisposable
{
    private readonly string _directory;
    private readonly string _endpointPath;

    public McpCommandServerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lem-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _endpointPath = Path.Combine(_directory, McpCommandProtocol.EndpointFileName);
    }

    [Fact]
    public async Task ListProjectsRoundTripsOverLoopbackEndpoint()
    {
        var category = new ProjectCategory { Name = "Demo" };
        category.Projects.Add(new ManagedProject
        {
            Name = "Alpha",
            DeploymentMode = DeploymentMode.DotNetPublish,
            SourcePath = @"D:\Source\Alpha"
        });
        category.Projects.Add(new ManagedProject
        {
            Name = "Beta",
            DeploymentMode = DeploymentMode.Uvicorn,
            SourcePath = @"D:\Source\Beta"
        });

        var server = new McpCommandServer(
            new FakeCatalog(category), new EventAggregator(), new FakeLogger(), _endpointPath);
        server.Start();
        try
        {
            var endpoint = JsonSerializer.Deserialize<McpEndpointInfo>(
                await File.ReadAllTextAsync(_endpointPath), McpCommandProtocol.Json);
            Assert.NotNull(endpoint);

            var response = await SendAsync(
                endpoint!.Port,
                new McpCommandRequest("1", McpCommandProtocol.ListProjects, Token: endpoint.Token));

            Assert.True(response.Ok, response.Error);
            var projects = response.Result!.Value.Deserialize<List<ProjectSummary>>(McpCommandProtocol.Json);
            Assert.NotNull(projects);
            Assert.Equal(2, projects!.Count);
            Assert.Contains(projects, p => p.Name == "Alpha" && p.Category == "Demo");
            Assert.Contains(projects, p => p.Name == "Beta" && p.DeploymentMode == "uvicorn");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task RequestsWithWrongTokenAreRejected()
    {
        var category = new ProjectCategory { Name = "Demo" };
        var server = new McpCommandServer(
            new FakeCatalog(category), new EventAggregator(), new FakeLogger(), _endpointPath);
        server.Start();
        try
        {
            var endpoint = JsonSerializer.Deserialize<McpEndpointInfo>(
                await File.ReadAllTextAsync(_endpointPath), McpCommandProtocol.Json);

            var response = await SendAsync(
                endpoint!.Port,
                new McpCommandRequest("1", McpCommandProtocol.ListProjects, Token: "wrong-token"));

            Assert.False(response.Ok);
            Assert.Equal("Unauthorized.", response.Error);
        }
        finally
        {
            server.Stop();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static async Task<McpCommandResponse> SendAsync(int port, McpCommandRequest request)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);

        await using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(
            stream, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, McpCommandProtocol.Json));
        var line = await reader.ReadLineAsync(timeout.Token);
        Assert.NotNull(line);

        return JsonSerializer.Deserialize<McpCommandResponse>(line, McpCommandProtocol.Json)
            ?? throw new InvalidOperationException("Invalid response.");
    }

    private sealed class FakeCatalog : IProjectCatalog
    {
        private readonly Dictionary<ManagedProject, int> _ids = new();

        public FakeCatalog(ProjectCategory category)
        {
            Categories = new BindableCollection<ProjectCategory> { category };
            Projects = new BindableCollection<ManagedProject>(category.Projects);
            var id = 1;
            foreach (var project in Projects)
            {
                _ids[project] = id++;
            }
        }

        public BindableCollection<ProjectCategory> Categories { get; }
        public BindableCollection<ManagedProject> Projects { get; }

        public int GetProjectId(ManagedProject? project)
            => project is not null && _ids.TryGetValue(project, out var id) ? id : 0;

        public ManagedProject? FindById(int projectId)
            => _ids.FirstOrDefault(pair => pair.Value == projectId).Key;

        public void FlushPendingSave() { }
    }

    private sealed class FakeLogger : IRuntimeLogger
    {
        public string LogFilePath => string.Empty;
        public void Append(LogEntry.LogLevel level, string message) { }
        public IProgress<string> Tee(IProgress<string>? inner) => inner ?? new Progress<string>();
    }
}
