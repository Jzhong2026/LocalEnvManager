using System.ComponentModel;
using System.Text.Json;
using LocalEnvManager.Mcp.Contracts;
using ModelContextProtocol.Server;

namespace LocalEnvManager.Mcp;

/// <summary>
/// MCP tools exposed to Grok Build. Every tool only addresses projects that
/// already exist in LocalEnvManager; there is no arbitrary-command tool.
/// </summary>
[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(Name = "list_projects")]
    [Description("List every project configured in LocalEnvManager, including category, deployment mode, state and running-process flag.")]
    public static Task<string> ListProjects(CancellationToken cancellationToken)
        => CallAsync(McpCommandProtocol.ListProjects, null, cancellationToken);

    [McpServerTool(Name = "get_project_status")]
    [Description("Get one project's current run state, last message, latest log snippet and whether its process is still running.")]
    public static Task<string> GetProjectStatus(
        [Description("Project display name (unique) or numeric project id.")] string project,
        CancellationToken cancellationToken)
        => CallAsync(
            McpCommandProtocol.GetProjectStatus,
            new ProjectRef(project, null),
            cancellationToken);

    [McpServerTool(Name = "get_project_logs")]
    [Description("Read the most recent in-memory output lines for a project (stdout, stderr and lifecycle messages).")]
    public static Task<string> GetProjectLogs(
        [Description("Project display name (unique) or numeric project id.")] string project,
        [Description("Maximum number of most-recent lines to return (default 200).")] int maxLines = 200,
        CancellationToken cancellationToken = default)
        => CallAsync(
            McpCommandProtocol.GetProjectLogs,
            new ProjectLogsParams(new ProjectRef(project, null), maxLines),
            cancellationToken);

    [McpServerTool(Name = "run_project")]
    [Description("Run one configured project's update/deploy pipeline. By default its declared dependencies are included and run first. Waits for completion up to timeoutSeconds; if it is still running, call get_project_status/get_project_logs.")]
    public static Task<string> RunProject(
        [Description("Project display name (unique) or numeric project id.")] string project,
        [Description("Also run declared dependencies first (default true).")] bool includeDependencies = true,
        [Description("How long to wait for completion, in seconds (default 600).")] int timeoutSeconds = 600,
        CancellationToken cancellationToken = default)
        => CallAsync(
            McpCommandProtocol.RunProject,
            new RunProjectParams(new ProjectRef(project, null), includeDependencies, timeoutSeconds),
            cancellationToken);

    [McpServerTool(Name = "stop_project")]
    [Description("Stop a running uvicorn/PowerShell project process (dotnet publish/build exit on their own).")]
    public static Task<string> StopProject(
        [Description("Project display name (unique) or numeric project id.")] string project,
        CancellationToken cancellationToken)
        => CallAsync(
            McpCommandProtocol.StopProject,
            new StopProjectParams(new ProjectRef(project, null)),
            cancellationToken);

    // Return a structured error instead of throwing: the MCP SDK otherwise
    // replaces the useful message with "An error occurred invoking ...".
    private static async Task<string> CallAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CommandClient.CallAsync(method, parameters, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.Message
            });
        }
    }
}
