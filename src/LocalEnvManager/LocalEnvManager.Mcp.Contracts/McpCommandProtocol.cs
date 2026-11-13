using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalEnvManager.Mcp.Contracts;

/// <summary>
/// Wire protocol shared by the WPF host and the stdio MCP bridge.
///
/// The GUI runs elevated while Grok Build / the bridge normally does not. A
/// named pipe created by the elevated process rejects the lower-integrity
/// client, so the internal hop is a loopback-only TCP listener with a random
/// bearer token. The MCP-facing transport remains stdio.
/// </summary>
public static class McpCommandProtocol
{
    public const string ListProjects = "list_projects";
    public const string GetProjectStatus = "get_project_status";
    public const string GetProjectLogs = "get_project_logs";
    public const string RunProject = "run_project";
    public const string StopProject = "stop_project";

    public const string EndpointPathEnvironmentVariable = "LOCALENVMANAGER_MCP_ENDPOINT";
    public const string EndpointFileName = "mcp-endpoint.json";

    public static string DefaultEndpointPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalEnvManager",
        EndpointFileName);

    public static string ResolveEndpointPath()
    {
        var overridden = Environment.GetEnvironmentVariable(EndpointPathEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overridden) ? DefaultEndpointPath : overridden;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}

public sealed record McpCommandRequest(
    string Id,
    string Method,
    JsonElement? Params = null,
    string? Token = null);

public sealed record McpCommandResponse(
    string Id,
    bool Ok,
    JsonElement? Result = null,
    string? Error = null);

/// <summary>Written by the GUI after its loopback listener starts. Read by the
/// stdio bridge for the current port/token/process id.</summary>
public sealed record McpEndpointInfo(
    int Port,
    string Token,
    int ProcessId,
    DateTimeOffset StartedAt);

/// <summary>Reference by stable runtime id and/or display name. Name is unique in the UI.</summary>
public sealed record ProjectRef(string? Name = null, int? Id = null);

public sealed record ProjectSummary(
    int Id,
    string Name,
    string Category,
    string DeploymentMode,
    string SourcePath,
    string TargetPath,
    string OverlayFolder,
    string LogsDirectory,
    string State,
    bool HasRunningProcess,
    string LastMessage,
    string LatestLogSnippet,
    string Branch);

public sealed record RunProjectParams(
    ProjectRef Project,
    bool IncludeDependencies = true,
    int TimeoutSeconds = 600);

public sealed record StopProjectParams(ProjectRef Project);

public sealed record ProjectLogsParams(ProjectRef Project, int MaxLines = 200);

public sealed record ProjectLogLine(DateTimeOffset Timestamp, string Channel, string Text);

public sealed record ProjectLogsResult(
    int ProjectId,
    string ProjectName,
    IReadOnlyList<ProjectLogLine> Lines);

public sealed record RunProjectResult(
    int ProjectId,
    string ProjectName,
    string State,
    bool Success,
    bool Cancelled,
    string Message,
    long DurationMs);

public sealed record StopProjectResult(
    int ProjectId,
    string ProjectName,
    bool Stopped,
    string Message);
