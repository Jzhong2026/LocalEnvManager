using LocalEnvManager.Mcp.Contracts;

namespace LocalEnvManager.Messages;

/// <summary>Raised by the MCP pipe server and handled on the UI thread by
/// <see cref="ViewModels.ExecuteViewModel"/>. CorrelationId lets the pipe server
/// match the async result even when several clients are connected.</summary>
public sealed record RunProjectRequestMessage(
    Guid CorrelationId,
    int ProjectId,
    bool IncludeDependencies);

public sealed record RunProjectResultMessage(
    Guid CorrelationId,
    RunProjectResult Result);

public sealed record StopProjectRequestMessage(
    Guid CorrelationId,
    int ProjectId);

public sealed record StopProjectResultMessage(
    Guid CorrelationId,
    StopProjectResult Result);
