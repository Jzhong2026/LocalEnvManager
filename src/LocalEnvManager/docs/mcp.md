# MCP bridge for Grok Build

LocalEnvManager exposes a **stdio MCP server** that forwards commands to the running
WPF process over a **loopback-only TCP endpoint** protected by a random bearer
token. The GUI runs elevated while Grok Build / the bridge normally does not; a
named pipe created by the elevated process rejects the lower-integrity client, so
loopback TCP is used for the internal hop.

```
Grok Build ──stdio──> LocalEnvManager.Mcp.exe ──127.0.0.1 + token──> LocalEnvManager.exe (admin)
                                                                      ├─ IProjectCatalog
                                                                      ├─ ExecuteViewModel
                                                                      └─ ProjectUpdateRunner
```

The GUI writes `%LOCALAPPDATA%\LocalEnvManager\mcp-endpoint.json` with the
ephemeral port and token. The bridge can use a different file via
`LOCALENVMANAGER_MCP_ENDPOINT` (useful for tests).

## Tools

| Tool | Purpose |
|---|---|
| `list_projects` | List configured projects, category, mode, state, running flag. |
| `get_project_status` | One project's state, last message, latest log snippet. |
| `get_project_logs` | Recent in-memory stdout/stderr/lifecycle lines (default 200). |
| `run_project` | Run one project (dependencies included by default), wait up to `timeoutSeconds` (default 600). |
| `stop_project` | Stop a running uvicorn / PowerShell process. |

`run_project` never accepts an arbitrary command: it can only run projects that
already exist in LocalEnvManager. The tool call returns structured JSON text.

When an MCP run starts, the GUI switches to the “执行更新” tab, selects the
project, shows the running state and streams dotnet / PowerShell / uvicorn output
through the same EventAggregator path used by UI-triggered runs.

## Build and deploy

```powershell
$app = "D:\AI\Projects\LocalEnvManager\src\LocalEnvManager\LocalEnvManager\bin\Release\net10.0-windows\win-x64"

dotnet publish src/LocalEnvManager/LocalEnvManager/LocalEnvManager.csproj `
  -c Release -r win-x64 --self-contained false -o $app

dotnet publish src/LocalEnvManager/LocalEnvManager.Mcp/LocalEnvManager.Mcp.csproj `
  -c Release -r win-x64 --self-contained false -o "$app\mcp"
```

Start `LocalEnvManager.exe` first. The GUI starts the loopback endpoint after its
main window is ready.

## Register with Grok Build

```powershell
grok mcp add local-env-manager -- "$app\mcp\LocalEnvManager.Mcp.exe"
```

Or add to `~/.grok/config.toml`:

```toml
[mcp_servers.local-env-manager]
command = "D:\\AI\\Projects\\LocalEnvManager\\src\\LocalEnvManager\\LocalEnvManager\\bin\\Release\\net10.0-windows\\win-x64\\mcp\\LocalEnvManager.Mcp.exe"
args = []
```

Verify:

```powershell
grok mcp doctor
grok inspect
```

Inside Grok Build, `/mcps` lists the registered servers. Example prompts:

- “List the LocalEnvManager projects.”
- “Run plus-api, include dependencies, and show me the last 100 log lines.”
- “Stop PyAgents.”

## Security / data

- The listener binds `127.0.0.1` only; it is unreachable from other machines.
- Every request must carry the random token from the per-user endpoint file; wrong tokens are rejected.
- The MCP bridge writes logs to stderr; stdout is reserved for MCP frames.
- The GUI remains the only administrator process and executes only configured projects.
- Tool calls are written to the runtime log under `%LOCALAPPDATA%\LocalEnvManager\logs\runtime-*.log`.
- The MCP bridge does **not** read or write `projects.json`; the GUI owns configuration.
  Running a project can modify its configured target/overlay directories as part of
  deployment, exactly like clicking “运行” in the UI.
- If the GUI is not running, tools return a clear “LocalEnvManager is not running” error.
- Long runs: `run_project` returns “still running” after the timeout; poll
  `get_project_status` / `get_project_logs` or call `stop_project`.
