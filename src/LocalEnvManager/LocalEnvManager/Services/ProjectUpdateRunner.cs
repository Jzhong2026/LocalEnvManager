using LocalEnvManager.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// Options that control a single project run. Passed in by the view-model rather than
    /// baked into the project itself so that "pull latest code" doesn't need to be a
    /// persistent setting.
    /// </summary>
    public sealed record ProjectExecutionOptions(bool PullLatest);

    public interface IProjectUpdateRunner
    {
        Task<ProjectExecutionResult> ExecuteAsync(
            ManagedProject project,
            ProjectExecutionOptions options,
            IProgress<string>? logProgress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops a currently-running process for <paramref name="project"/>. Returns
        /// true when a process was located and signalled to terminate; false when
        /// nothing was running for this project. Has no effect on dotnet publish /
        /// build, which exit on their own once the command completes.
        /// </summary>
        Task<bool> StopAsync(
            ManagedProject project,
            IProgress<string>? logProgress = null);
    }

    public readonly record struct ProjectExecutionResult(bool Success, string Message);

    public sealed class ProjectUpdateRunner : IProjectUpdateRunner
    {
        // Keep long-running uvicorn processes rooted so their async output readers
        // remain active and continue feeding log lines into the UI.
        private static readonly ConcurrentDictionary<string, Process> s_uvicornProcesses =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Registers a cancellation callback that kills the process when the token fires.
        /// Must be called immediately after Process.Start(). The registration is disposed
        /// when the process exits.
        /// </summary>
        private static CancellationTokenRegistration RegisterKillOnCancel(
            Process process,
            CancellationToken cancellationToken)
        {
            return cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
            });
        }

        public async Task<ProjectExecutionResult> ExecuteAsync(
            ManagedProject project,
            ProjectExecutionOptions options,
            IProgress<string>? logProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (project is null) return new ProjectExecutionResult(false, "No project supplied.");

            var workingDirectory = ResolveWorkingDirectory(project.SourcePath, project.Name);

            // Step 1: pull latest code (optional). If the user opted in and the working
            // directory exists, run `git pull`. A non-zero exit code aborts the rest of
            // the pipeline for THIS project — other projects running in parallel are
            // unaffected.
            if (options.PullLatest)
            {
                if (workingDirectory is null)
                {
                    return new ProjectExecutionResult(false,
                        $"{project.Name}: cannot pull — source path is not a valid directory.");
                }

                Report(logProgress, $"[{project.Name}] → git pull in {workingDirectory}");
                var pullResult = await RunGitPullAsync(project.Name, workingDirectory, logProgress, cancellationToken);
                if (!pullResult.Success)
                {
                    Report(logProgress, $"[{project.Name}] ✗ git pull failed; aborting update.");
                    return pullResult;
                }
                Report(logProgress, $"[{project.Name}] ✓ git pull completed.");
            }

            // Step 2: stop a configured AppPool once, and leave it stopped through
            // both the deploy command and the backup-folder copy. Starting it in
            // between lets w3wp lock the DLLs the copy is about to overwrite.
            // Restore happens exactly once in the finally below — including when
            // the command fails or the user cancels — and does not use the
            // caller's cancellation token.
            IReadOnlyList<string> heldAppPools = Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(project.IisAppPoolName))
            {
                heldAppPools = await StopAppPoolsHoldingOutputAsync(
                    project.TargetPath,
                    project,
                    project.Name,
                    logProgress,
                    cancellationToken);
            }

            var result = new ProjectExecutionResult(false, $"{project.Name} failed.");
            try
            {
                try
                {
                    result = project.DeploymentMode switch
                    {
                        DeploymentMode.DotNetPublish => await ExecuteDotNetAsync(project, "publish", workingDirectory, logProgress, cancellationToken),
                        DeploymentMode.DotNetBuild => await ExecuteDotNetAsync(project, "build", workingDirectory, logProgress, cancellationToken),
                        DeploymentMode.PowerShellCommand => await ExecutePowerShellCommandAsync(project, workingDirectory, logProgress, cancellationToken),
                        DeploymentMode.Uvicorn => await ExecuteUvicornAsync(project, workingDirectory, logProgress, cancellationToken),
                        _ => new ProjectExecutionResult(false, $"Unsupported deployment mode: {project.DeploymentMode}.")
                    };
                }
                catch (Exception ex)
                {
                    result = new ProjectExecutionResult(false, $"{project.Name} failed: {ex.Message}");
                }

                // Step 3: copy backup folder -> target while the pool is still
                // stopped. Skip for uvicorn — it already synced before launching
                // the long-running service. If the first stop did not take a pool
                // down, try once more here; do not stop a pool we are already holding.
                if (result.Success && project.DeploymentMode != DeploymentMode.Uvicorn)
                {
                    if (heldAppPools.Count == 0 && !string.IsNullOrWhiteSpace(project.IisAppPoolName))
                    {
                        heldAppPools = await StopAppPoolsHoldingOutputAsync(
                            project.TargetPath,
                            project,
                            project.Name,
                            logProgress,
                            cancellationToken);
                    }

                    Report(logProgress, $"[{project.Name}] → syncing backup folder to target");
                    var sync = await SyncBackupToTargetAsync(project);
                    Report(logProgress,
                        sync.Success
                            ? sync.Message.StartsWith("Skipped", StringComparison.Ordinal)
                                ? $"[{project.Name}] ⊘ backup sync skipped (paths not configured)"
                                : $"[{project.Name}] ✓ {sync.Message}"
                            : $"[{project.Name}] ✗ {sync.Message}");
                    result = Combine(result.Message, sync);
                }
            }
            finally
            {
                ProjectExecutionResult restore;
                try
                {
                    restore = await StartAppPoolsAsync(heldAppPools, project.Name, logProgress);
                }
                catch (Exception ex)
                {
                    var message = $"{project.Name}: 重新启动 IIS 应用程序池时出错：{ex.Message}";
                    Report(logProgress, $"[{project.Name}] ✗ {message}");
                    restore = new ProjectExecutionResult(false, message);
                }

                if (!restore.Success)
                {
                    result = new ProjectExecutionResult(false, JoinMessages(result.Message, restore.Message));
                }
            }

            return result;
        }

        // -------- stop --------

        // Stops a running process for a PowerShell or uvicorn project. Returns true if
        // something was signalled to terminate; false when no matching running process
        // could be found (already finished, or a dotnet publish/build that exits on
        // its own).
        //
        // For uvicorn: looks up the launcher in `s_uvicornProcesses` by project name,
        // calls `Kill(entireProcessTree: true)` on it, then walks the launcher tree via
        // WMI and kills every descendant (the FastAPI worker python process that holds
        // the LISTENING socket, not the launcher). Finally removes the dict entry.
        //
        // For PowerShell: triggers the per-project CTS cancellation registered by
        // `RunCapturedProcessAsync`. The `RegisterKillOnCancel` callback handles the
        // actual `process.Kill(entireProcessTree: true)`. The CTS is dropped after the
        // token fires so a subsequent "Stop" on the same project is a no-op until a
        // new run starts.
        //
        // Idempotent: calling Stop twice in a row on the same project is safe.
        public Task<bool> StopAsync(
            ManagedProject project,
            IProgress<string>? logProgress = null)
        {
            if (project is null) return Task.FromResult(false);

            switch (project.DeploymentMode)
            {
                case DeploymentMode.Uvicorn:
                    return Task.FromResult(StopUvicornProcess(project, logProgress));

                case DeploymentMode.PowerShellCommand:
                    if (s_powerShellCtses.TryGetValue(project.Name, out var cts))
                    {
                        try
                        {
                            cts.Cancel();
                            Report(logProgress, $"[{project.Name}] 正在终止 PowerShell 进程…");
                            return Task.FromResult(true);
                        }
                        catch { }
                    }
                    return Task.FromResult(false);

                default:
                    // dotnet publish / build exit on their own — nothing to stop.
                    return Task.FromResult(false);
            }
        }

        // Per-project CancellationTokenSource registry so Stop can cancel PowerShell
        // independently of the batch CTS. Populated by the PowerShell executor and
        // disposed when the process exits.
        private static readonly ConcurrentDictionary<string, CancellationTokenSource>
            s_powerShellCtses = new(StringComparer.OrdinalIgnoreCase);

        // Hard ceiling on a single Stop attempt. The previous implementation could
        // hang the UI because `process.Dispose()` waits for any pending
        // stdout/stderr reads to complete — for a uvicorn process that's actively
        // spewing log lines, those reads never finish and Dispose blocks forever.
        // We bound the whole stop sequence to 5 s and just abandon the dict
        // entry; the dead process gets reaped by the OS when the user closes
        // the app or restarts the project.
        private const int StopTimeoutMs = 5000;

        private static bool StopUvicornProcess(
            ManagedProject project,
            IProgress<string>? logProgress)
        {
            var killed = false;

            // Snapshot the keys first so we can mutate the dict while iterating.
            foreach (var key in s_uvicornProcesses.Keys.ToArray())
            {
                if (!key.StartsWith(project.Name + "|", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!s_uvicornProcesses.TryRemove(key, out var process) || process is null)
                    continue;

                // Run the kill sequence on a worker thread so a hung WMI query
                // or pending stdout read can't pin the dispatcher.
                var processId = -1;
                try { processId = process.Id; } catch { }

                var task = Task.Run(() => KillUvicornProcessTree(process, project.Name, logProgress));
                if (!task.Wait(StopTimeoutMs))
                {
                    Report(logProgress,
                        $"[{project.Name}] Stop 等待超过 {StopTimeoutMs / 1000}s,放弃等待(进程可能仍在被系统回收)。");
                }

                // CRITICAL: do NOT call process.Dispose(). With RedirectStandardOutput=true
                // (which uvicorn uses), Dispose synchronously waits for the stdout
                // read pump to drain, and a uvicorn worker keeps writing — the wait
                // never completes and the UI hangs. The Process object will be
                // GC'd once the dict stops referencing it; leaked OS handles are
                // reclaimed when the app exits.
                killed = true;

                _ = processId; // suppress unused warning
            }

            return killed;
        }

        // Run on a worker thread. Walks the process tree via WMI and kills every
        // descendant. Wrapped in try/catch at every level so a single failure
        // (e.g. permission on one descendant) doesn't take down the whole loop.
        private static void KillUvicornProcessTree(
            Process process,
            string projectName,
            IProgress<string>? logProgress)
        {
            int rootPid = -1;
            try { rootPid = process.Id; } catch { return; }

            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        Report(logProgress,
                            $"[{projectName}] 已请求终止 uvicorn 进程树(PID {rootPid})。");
                    }
                    catch (Exception ex)
                    {
                        Report(logProgress,
                            $"[{projectName}] 终止 launcher 失败:{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Report(logProgress,
                    $"[{projectName}] 检查 launcher 状态失败:{ex.Message}");
            }

            // Belt-and-braces: walk the WMI tree in case any descendants survived
            // the entireProcessTree kill (some Python workers ignore CTRL_BREAK
            // and need their own Kill). WMI is synchronous; that's why this
            // method runs on a worker thread.
            try
            {
                var tree = CollectProcessTree(rootPid);
                foreach (var pid in tree)
                {
                    if (pid == rootPid) continue;
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (!p.HasExited)
                        {
                            p.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // Process may have exited between the WMI snapshot and the
                        // kill attempt, or the OS may have revoked access. Either
                        // way, nothing to do for this PID.
                    }
                }
            }
            catch (Exception ex)
            {
                Report(logProgress,
                    $"[{projectName}] 遍历子进程失败:{ex.Message}");
            }
        }

        // -------- git pull --------

        // One git pull per repo at a time. Multiple projects can live in the same
        // repository; concurrent "git pull" on the same repo wastes time and can fail
        // on the index lock. Keyed by canonical repo root, case-insensitive.
        private static readonly ConcurrentDictionary<string, Lazy<Task<ProjectExecutionResult>>> s_repoPullGates =
            new(StringComparer.OrdinalIgnoreCase);

        private async Task<ProjectExecutionResult> RunGitPullAsync(
            string projectName,
            string workingDirectory,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            var repoRoot = await ResolveRepoRootAsync(workingDirectory, cancellationToken)
                ?? workingDirectory;

            Lazy<Task<ProjectExecutionResult>>? gate;
            bool isLeader;
            while (true)
            {
                var myGate = new Lazy<Task<ProjectExecutionResult>>(
                    () => RunGitPullCoreAsync(projectName, repoRoot, logProgress, cancellationToken));
                gate = s_repoPullGates.GetOrAdd(repoRoot, myGate);
                if (ReferenceEquals(gate, myGate))
                {
                    isLeader = true;
                    break;
                }

                if (gate.IsValueCreated && gate.Value.IsCompleted)
                {
                    s_repoPullGates.TryRemove(
                        new KeyValuePair<string, Lazy<Task<ProjectExecutionResult>>>(repoRoot, gate));
                    continue;
                }

                isLeader = false;
                break;
            }

            if (!isLeader)
            {
                Report(logProgress, $"[{projectName}] 同仓库已有项目在拉取，共享本次 git pull ({repoRoot})");
            }

            var result = await gate.Value;

            if (!isLeader)
            {
                Report(logProgress, result.Success
                    ? $"[{projectName}] ✓ 同仓库 git pull 已完成（共享结果）"
                    : $"[{projectName}] ✗ 同仓库 git pull 失败（共享结果）");
            }

            return result;
        }

        private static async Task<string?> ResolveRepoRootAsync(
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            try
            {
                var startInfo = new ProcessStartInfo("git")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                };
                startInfo.ArgumentList.Add("rev-parse");
                startInfo.ArgumentList.Add("--show-toplevel");

                using var process = new Process { StartInfo = startInfo };
                if (!process.Start()) return null;
                var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0) return null;
                var root = stdout.Trim();
                return string.IsNullOrWhiteSpace(root) ? null : root;
            }
            catch
            {
                return null;
            }
        }

        private async Task<ProjectExecutionResult> RunGitPullCoreAsync(
            string projectName,
            string repoRoot,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            Report(logProgress, $"[{projectName}] → git pull in {repoRoot}");

            var startInfo = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = repoRoot
            };
            startInfo.ArgumentList.Add("pull");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProjectExecutionResult(false, $"{projectName}: failed to start git pull.");
            }

            using var ctr = RegisterKillOnCancel(process, cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var combined = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var trimmed = TrimOutput(combined);

            if (process.ExitCode != 0)
            {
                var message = $"{projectName}: git pull failed (exit code {process.ExitCode}).";
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    message = $"{message}\n{trimmed}";
                }
                return new ProjectExecutionResult(false, message);
            }

            return new ProjectExecutionResult(true, $"{projectName}: git pull succeeded.");
        }

        // -------- dotnet publish / build --------

        private async Task<ProjectExecutionResult> ExecuteDotNetAsync(
            ManagedProject project,
            string defaultVerb,
            string? workingDirectory,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            if (workingDirectory is null)
            {
                return new ProjectExecutionResult(false,
                    $"{project.Name} requires a valid source path before running dotnet {defaultVerb}.");
            }

            var arguments = BuildDotNetArguments(project, defaultVerb);
            var preview = BuildPreview("dotnet", arguments);
            Report(logProgress, $"[{project.Name}] → dotnet {string.Join(' ', arguments)}");

            // Pre-create the -o output directory. dotnet/MSBuild will otherwise
            // try to create it under the working directory (MSB3191) or fail
            // outright when the parent path doesn't exist. Push the actual
            // Directory.CreateDirectory call onto the thread pool so a slow
            // SMB share / spinning-up disk doesn't stall the dispatcher.
            var outputDir = await EnsureDotNetOutputDirectoryAsync(arguments, logProgress, project.Name);

            // Long-term fix for "w3wp.exe is locking the output DLLs": when a build
            // target is the physical path of an IIS site, MSBuild retries 10 times
            // to overwrite the binaries and gives up. We try in order:
            //   1. Recycle the project's configured IIS AppPool (precise, fast).
            //   2. If that fails (no admin), auto-elevate via UAC to do the recycle.
            //   3. As a last resort, kill all w3wp.exe processes (also needs admin).
            IReadOnlyList<string> heldAppPools = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(project.IisAppPoolName))
            {
                heldAppPools = await StopAppPoolsHoldingOutputAsync(
                    outputDir, project, project.Name, logProgress, cancellationToken);
            }

            var startInfo = CreateProcessStartInfo("dotnet", arguments, workingDirectory);
            var result = await RunCapturedProcessAsync(project.Name, "dotnet", arguments, startInfo, logProgress, cancellationToken);
            Report(logProgress,
                result.Success
                    ? $"[{project.Name}] ✓ dotnet {defaultVerb} succeeded ({preview})"
                    : $"[{project.Name}] ✗ dotnet {defaultVerb} failed");

            // Pools stopped here are path-matched ones, used only when this project
            // has no IisAppPoolName. A configured pool is held by ExecuteAsync
            // until after the backup sync, so it must not be started here.
            // A failed start fails the run; swallowing it used to log success
            // while leaving the site offline.
            var restore = await StartAppPoolsAsync(heldAppPools, project.Name, logProgress);
            if (!restore.Success)
            {
                return new ProjectExecutionResult(false, JoinMessages(result.Message, restore.Message));
            }

            return result;
        }

        // -------- PowerShell command --------

        private async Task<ProjectExecutionResult> ExecutePowerShellCommandAsync(
            ManagedProject project,
            string? workingDirectory,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            var command = project.Command?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                return new ProjectExecutionResult(false,
                    $"{project.Name} requires a non-empty PowerShell command before execution.");
            }

            // Fail fast if the caller configured a SourcePath but the directory is
            // missing — otherwise the failure surfaces from deep inside PowerShell
            // as an opaque "term 'X' is not recognized" error, which is exactly
            // what caused ChatWidget to look like a script bug instead of a
            // missing-source-directory bug.
            if (workingDirectory is null && !string.IsNullOrWhiteSpace(project.SourcePath))
            {
                return new ProjectExecutionResult(false,
                    $"{project.Name}: 源路径不存在或不是有效目录：{project.SourcePath}");
            }

            // Normalise common shorthand so users don't have to type the .ps1 suffix.
            // .\build_release, ./build_release and build_release (when such a .ps1
            // sits in cwd) all get rewritten to & '.\build_release.ps1' so PowerShell
            // can actually find the script under the default execution policy.
            var resolved = ResolvePowerShellScriptPath(command, workingDirectory ?? Environment.CurrentDirectory, logProgress, project.Name);
            if (resolved is null)
            {
                return new ProjectExecutionResult(false,
                    $"{project.Name}: 找不到 PowerShell 脚本：{command}");
            }

            var cwd = workingDirectory ?? Environment.CurrentDirectory;
            var arguments = new[] { "-ExecutionPolicy", "Bypass", "-NoProfile", "-Command", resolved };
            Report(logProgress, $"[{project.Name}] → powershell {resolved}");

            var startInfo = CreateProcessStartInfo("powershell", arguments, cwd);

            // Build a linked CTS so the per-project "Stop" button can cancel
            // THIS project's PowerShell without affecting sibling projects that
            // share the batch token. Linked → batch Cancel still propagates.
            var projectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Register so StopAsync(project) can find it.
            s_powerShellCtses[project.Name] = projectCts;

            ProjectExecutionResult result;
            try
            {
                result = await RunCapturedProcessAsync(
                    project.Name, "powershell", arguments, startInfo, logProgress, projectCts.Token);
            }
            finally
            {
                // Drop the per-project registration so a later Stop on the same
                // project (after it finished) is a no-op.
                if (s_powerShellCtses.TryRemove(project.Name, out _))
                {
                    projectCts.Dispose();
                }
            }

            Report(logProgress,
                result.Success
                    ? $"[{project.Name}] ✓ PowerShell command succeeded"
                    : $"[{project.Name}] ✗ PowerShell command failed");
            return result;
        }

        // Inspect the configured PowerShell command. If it looks like a relative
        // script reference (".\build_release", "./build_release") and a matching
        // .ps1 file exists in the working directory, rewrite it so PowerShell
        // actually locates the script. Returns the command string to pass to
        // `powershell -Command`, or null when no candidate file was found.
        private static string? ResolvePowerShellScriptPath(
            string command,
            string workingDirectory,
            IProgress<string>? logProgress,
            string projectName)
        {
            // Bare relative script names: "build_release" or "build_release.ps1".
            // Skip anything that looks like a pipeline / multiple statements
            // (contains a space then something that's not a relative path).
            var trimmed = command.Trim();

            // Quoted executable path, e.g. "C:\Program Files\dotnet\dotnet.exe" publish ...
            // PowerShell parses a leading quoted string as a STRING LITERAL, so the next
            // token (publish) becomes "Unexpected token". Invoke it with the call operator
            // '&' — exactly how a user would type it interactively.
            if ((trimmed.StartsWith('"') || trimmed.StartsWith('\''))
                && !trimmed.StartsWith("&", StringComparison.Ordinal))
            {
                Report(logProgress, $"[{projectName}] 检测到带引号的可执行路径，自动补 & 调用运算符");
                return "& " + trimmed;
            }

            var candidates = new List<string>();

            if (trimmed.StartsWith(".\\", StringComparison.Ordinal) || trimmed.StartsWith("./", StringComparison.Ordinal))
            {
                candidates.Add(trimmed[2..]);
            }
            else if (!trimmed.Contains(' ') && !trimmed.Contains('|') && !trimmed.Contains(';'))
            {
                candidates.Add(trimmed);
            }

            if (candidates.Count == 0)
            {
                // Looks like a real expression (Get-Process, & { ... }, etc.),
                // nothing to rewrite — let it run as-is.
                return command;
            }

            foreach (var name in candidates)
            {
                var withExtension = name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? name : name + ".ps1";
                var fullPath = Path.IsPathRooted(withExtension)
                    ? withExtension
                    : Path.Combine(workingDirectory, withExtension);
                if (File.Exists(fullPath))
                {
                    if (string.Equals(withExtension, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return command; // user already typed the .ps1 — leave alone.
                    }
                    Report(logProgress,
                        $"[{projectName}] 自动补全脚本后缀：{trimmed} → .\\{withExtension}");
                    return $"& '.\\{withExtension}'";
                }
            }

            return command;
        }

        // -------- uvicorn --------

        // Fully-async uvicorn launcher. Every blocking call inside is now either
        // awaited (Task.Delay, Process.WaitForExitAsync, netstat.ReadToEndAsync)
        // or pushed onto the thread pool via Task.Run. The previous synchronous
        // implementation blocked the WPF dispatcher for up to 90 seconds during
        // the port-binding wait, freezing the entire UI for pyagents / LLMAgentApi
        // users. Async here keeps the run button / cancel button / branch list
        // responsive while uvicorn is still booting.
        private async Task<ProjectExecutionResult> ExecuteUvicornAsync(
            ManagedProject project,
            string? workingDirectory,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken = default)
        {
            if (workingDirectory is null)
            {
                return new ProjectExecutionResult(false,
                    $"{project.Name} requires a valid source path before starting uvicorn.");
            }

            // Sync backup files into the target before launching the service.
            // File.Copy / Directory.EnumerateFiles are synchronous and can take
            // seconds on large folders; push the whole call onto the thread pool
            // so we don't stall the dispatcher.
            Report(logProgress, $"[{project.Name}] → syncing backup folder to target");
            var sync = await SyncBackupToTargetAsync(project);
            Report(logProgress,
                sync.Success
                    ? sync.Message.StartsWith("Skipped", StringComparison.Ordinal)
                        ? $"[{project.Name}] ⊘ backup sync skipped (paths not configured)"
                        : $"[{project.Name}] ✓ {sync.Message}"
                    : $"[{project.Name}] ✗ {sync.Message}");
            if (!sync.Success)
            {
                return sync;
            }

            var arguments = BuildUvicornArguments(project);
            if (arguments is null)
            {
                return new ProjectExecutionResult(false,
                    $"{project.Name} requires uvicorn command arguments (e.g. main:app --port 8000).");
            }

            // Free the target port first: if a previous uvicorn (or anything else) is
            // still listening on it, the new instance can't bind. Kill the occupant so
            // the relaunch succeeds instead of silently failing to start.
            int port = 0;
            if (TryExtractPort(arguments, out port))
            {
                await FreePortIfOccupiedAsync(port, project.Name, logProgress);
            }

            var userCommand = project.Command.Trim();
            if (!TryParseCommandLine(userCommand, out var exe, out var args, out var parseError))
            {
                return new ProjectExecutionResult(false,
                    $"{project.Name}: 无法解析 uvicorn 启动命令：{parseError}");
            }

            // On Windows, Process.Start with UseShellExecute=false requires the
            // exact file name including the .exe extension. The user-supplied
            // command usually just types ".venv\Scripts\python" (no .exe) and
            // CreateProcess then returns ERROR_FILE_NOT_FOUND, surfacing as the
            // opaque "An error occurred trying to start process ... The system
            // cannot find the file specified" Win32Exception. Resolve the .exe
            // ourselves when the bare name exists as <exe>.exe next to it so
            // venv launches work without forcing the user to type ".exe".
            exe = ResolveWindowsExecutable(exe, workingDirectory);

            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONUTF8"] = "1";
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                return new ProjectExecutionResult(false, $"Failed to start uvicorn for {project.Name}: {ex.Message}");
            }
            if (process is null)
            {
                return new ProjectExecutionResult(false, $"Failed to start uvicorn for {project.Name}.");
            }

            // Stream child output directly into the right-side log pane. This removes
            // the external console pop-up and keeps runtime diagnostics inside the app.
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    Report(logProgress, $"[{project.Name}|uvicorn] {e.Data}");
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    Report(logProgress, $"[{project.Name}|uvicorn|stderr] {e.Data}");
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var preview = BuildPreview(exe, args);
            Report(logProgress, $"[{project.Name}] ✓ uvicorn launched ({preview})");

            var processKey = $"{project.Name}|{workingDirectory}";
            if (s_uvicornProcesses.TryGetValue(processKey, out var previous))
            {
                try
                {
                    if (!previous.HasExited)
                        previous.Kill(entireProcessTree: true);
                }
                catch { }
            }
            s_uvicornProcesses[processKey] = process;

            // A successful startup returns before the server exits. Keep the Stop
            // affordance in sync with the actual launcher lifetime instead of
            // relying on the one-shot update result.
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                if (s_uvicornProcesses.TryGetValue(processKey, out var tracked)
                    && ReferenceEquals(tracked, process))
                {
                    s_uvicornProcesses.TryRemove(processKey, out _);
                    project.HasRunningProcess = false;
                }
            };

            // Wire the cancellation token so a per-project Stop (or a batch
            // Cancel) tears down the launcher immediately. The token registration
            // is disposed by the `using` below — bound to the local lifetime.
            using var killRegistration = RegisterKillOnCancel(process, cancellationToken);

            // Process.Start returns immediately even when the child exits in milliseconds
            // (wrong venv / import error). Verify the port really binds before reporting
            // success to the execute grid.
            return await WaitForPortBoundAsync(port, process.Id, project.Name, logProgress, excludeLauncherPid: false, cancellationToken);
        }

        // Async port-binding wait. Two failure signals are watched: (1) the
        // launcher process dies, meaning the startup command collapsed;
        // (2) the port stays free past the timeout. 90s gives FastAPI Lifespans
        // that load Redis / metadata / templates enough headroom to bind before
        // we declare failure. The previous synchronous version blocked the UI
        // thread for the full 90s; now every iteration yields via Task.Delay so
        // the dispatcher stays free.
        private async Task<ProjectExecutionResult> WaitForPortBoundAsync(
            int port,
            int launcherPid,
            string projectName,
            IProgress<string>? logProgress,
            bool excludeLauncherPid,
            CancellationToken cancellationToken = default)
        {
            const int maxAttempts = 90;       // ~90 s total at 1 s cadence
            const int delayMs = 1000;

            // The launcher process is rarely the one that actually binds the port.
            // On Windows, uvicorn/FastAPI forks (or otherwise spawns) a worker
            // python process and the *worker* is what holds the LISTENING socket.
            // If we only strip the launcher PID from netstat output, the worker
            // PID is treated as "another process" and we report a false timeout
            // even though the service is healthy. Collect the launcher plus its
            // full descendant tree so we only flag *external* listeners as a
            // success signal. WMI queries are blocking so we run them on the
            // thread pool instead of the dispatcher.
            HashSet<int> launcherTree = excludeLauncherPid
                ? await Task.Run(() => CollectProcessTree(launcherPid))
                : new HashSet<int>();

            for (var i = 0; i < maxAttempts; i++)
            {
                // Honour StopAsync() calls — break out of the 90 s wait immediately
                // and let the caller report "cancelled by user".
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ProjectExecutionResult(false,
                        $"{projectName}: 已停止 — 用户在端口绑定前取消。");
                }

                await Task.Delay(delayMs).ConfigureAwait(false);

                var listeners = await FindPidsListeningOnPortAsync(port).ConfigureAwait(false);
                if (excludeLauncherPid)
                {
                    listeners.RemoveWhere(pid => launcherTree.Contains(pid));
                }
                if (listeners.Count > 0)
                {
                    var realPid = listeners.First();
                    Report(logProgress, $"[{projectName}] ✓ 端口 {port} 已绑定 (PID {realPid})");
                    return new ProjectExecutionResult(true, $"Started {projectName} on port {port}");
                }

                // Process.GetProcessById is cheap, but we still avoid blocking the
                // dispatcher for the rare case the OS is slow to enumerate PIDs.
                var launcherState = await Task.Run(() =>
                {
                    try
                    {
                        using var launcher = Process.GetProcessById(launcherPid);
                        if (launcher.HasExited)
                        {
                            return (hasExited: true, exitCode: launcher.ExitCode, gone: false);
                        }
                        return (hasExited: false, exitCode: 0, gone: false);
                    }
                    catch (ArgumentException)
                    {
                        // Process already gone (race with the OS).
                        return (hasExited: true, exitCode: -1, gone: true);
                    }
                }).ConfigureAwait(false);

                if (launcherState.hasExited)
                {
                    if (launcherState.gone)
                    {
                        return new ProjectExecutionResult(false,
                            $"{projectName}: 启动命令进程已消失。");
                    }
                    return new ProjectExecutionResult(false,
                        $"{projectName}: 启动命令已退出 (exit code {launcherState.exitCode})，通常是命令/路径/虚拟环境配置错误。");
                }
            }

            return new ProjectExecutionResult(false,
                $"{projectName}: uvicorn 启动超时 — {maxAttempts * delayMs / 1000}s 内端口 {port} 未被监听，FastAPI Lifespan 可能仍在初始化（Redis/元数据/模板加载较慢），或工作目录/虚拟环境配置有误。");
        }

        // Walks the parent -> child chain rooted at `rootPid` and returns the full
        // set of PIDs that belong to that process tree. Uses Win32_Process via WMI
        // because .NET's Process class has no cross-platform parent query and
        // Windows doesn't expose ParentProcessId through the Process API. If WMI
        // is unavailable (locked-down environment) or rootPid has already exited
        // we still return a set containing rootPid so the caller can still strip
        // the original launcher.
        private static HashSet<int> CollectProcessTree(int rootPid)
        {
            var tree = new HashSet<int> { rootPid };
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT ProcessId, ParentProcessId FROM Win32_Process WHERE ParentProcessId={rootPid}");
                foreach (var obj in searcher.Get())
                {
                    if (obj["ProcessId"] is null) continue;
                    var child = Convert.ToInt32(obj["ProcessId"]);
                    // Recurse to catch grandchildren, great-grandchildren, etc.
                    foreach (var descendant in CollectProcessTree(child))
                    {
                        tree.Add(descendant);
                    }
                }
            }
            catch
            {
                // WMI failure is non-fatal: caller still has rootPid in the set.
            }
            return tree;
        }

        // Parse a command line into executable + argv while preserving quoted
        // substrings. Supports both single and double quotes for paths with spaces.
        private static bool TryParseCommandLine(
            string command,
            out string executable,
            out IReadOnlyList<string> args,
            out string error)
        {
            executable = string.Empty;
            args = Array.Empty<string>();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(command))
            {
                error = "命令为空";
                return false;
            }

            var tokens = SplitCommandLine(command);
            if (tokens.Count == 0)
            {
                error = "未解析出可执行文件";
                return false;
            }

            executable = tokens[0];
            args = tokens.Skip(1).ToList();
            return true;
        }

        private static List<string> SplitCommandLine(string command)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            char quote = '\0';

            for (var i = 0; i < command.Length; i++)
            {
                var ch = command[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        quote = '\0';
                    }
                    else
                    {
                        current.Append(ch);
                    }
                    continue;
                }

                if (ch == '\'' || ch == '"')
                {
                    quote = ch;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(ch);
            }

            if (current.Length > 0)
                tokens.Add(current.ToString());

            return tokens;
        }

        // On Windows, when ProcessStartInfo.UseShellExecute=false the runtime
        // calls CreateProcess directly, bypassing cmd.exe's PATHEXT lookup. That
        // means ".venv\Scripts\python" fails even though ".venv\Scripts\python.exe"
        // exists next to it. This helper adds the .exe suffix when the bare name
        // doesn't resolve but the .exe variant does. Relative paths are resolved
        // against the supplied working directory; absolute paths are checked as-is.
        // If neither resolves, the original string is returned unchanged so the
        // eventual Process.Start error message still mentions what the user typed.
        private static string ResolveWindowsExecutable(string executable, string? workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(executable)) return executable;
            if (!OperatingSystem.IsWindows()) return executable;

            // Already has an extension — assume the user knows what they're doing.
            // (`.cmd`, `.bat`, `.ps1`, `.exe`, etc.)
            var lastDot = executable.LastIndexOf('.');
            var lastSlash = Math.Max(executable.LastIndexOf('\\'), executable.LastIndexOf('/'));
            if (lastDot > lastSlash && lastDot >= 0) return executable;

            // Reject names that obviously can't be .exe'd (drive letters, env vars, etc.)
            if (executable.Contains('%') || executable.StartsWith("\"", StringComparison.Ordinal)) return executable;

            string? ResolveCandidate(string candidate)
            {
                if (Path.IsPathRooted(candidate))
                {
                    return File.Exists(candidate) ? candidate : null;
                }
                if (!string.IsNullOrWhiteSpace(workingDirectory))
                {
                    var combined = Path.Combine(workingDirectory, candidate);
                    return File.Exists(combined) ? combined : null;
                }
                return null;
            }

            if (ResolveCandidate(executable) is { } exact) return exact;
            var withExe = executable + ".exe";
            if (ResolveCandidate(withExe) is { } resolved) return resolved;
            return executable;
        }

        // -------- argument / process plumbing --------

                private static IReadOnlyList<string> BuildDotNetArguments(ManagedProject project, string defaultVerb)
        {
            var trimmed = project.Command.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return [defaultVerb];

            // Preserve quoted argument values. In particular, MSBuild platform
            // names such as `-p:Platform="Any CPU"` are one argument, not two.
            return SplitCommandLine(trimmed);
        }

        // For `dotnet publish -o PATH` / `dotnet build -o PATH` make sure PATH exists.
        // Without this MSBuild emits MSB3191 (it tries to create the directory under
        // the working directory when the path is quoted, or just fails outright
        // when the parent chain doesn't exist). Returns the resolved output path so
        // the caller can use it for downstream work (e.g. unlocking file handles
        // held by w3wp.exe before publish). Async because Directory.CreateDirectory
        // on a slow / network share can block the dispatcher for several seconds.
        private static Task<string?> EnsureDotNetOutputDirectoryAsync(
            IReadOnlyList<string> arguments,
            IProgress<string>? logProgress,
            string projectName)
        {
            return Task.Run(() =>
            {
                string? resolved = null;
                for (var i = 0; i < arguments.Count; i++)
                {
                    var arg = arguments[i];
                    string? path = null;
                    if (string.Equals(arg, "-o", StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Count)
                    {
                        path = arguments[i + 1];
                    }
                    else if (arg.StartsWith("-o=", StringComparison.OrdinalIgnoreCase))
                    {
                        path = arg["-o=".Length..];
                    }

                    if (string.IsNullOrWhiteSpace(path)) continue;
                    resolved = path;

                    try
                    {
                        Directory.CreateDirectory(path);
                        Report(logProgress, $"[{projectName}] → 已确保输出目录存在: {path}");
                    }
                    catch (Exception ex)
                    {
                        Report(logProgress, $"[{projectName}] ✗ 无法创建输出目录 {path}: {ex.Message}");
                    }
                }
                return resolved;
            });
        }

        // IIS AppPool management. The build target is often the physical path of an
        // IIS site, so MSBuild's overwrite step collides with w3wp.exe holding the
        // DLLs open. Strategy (in order of preference):
        //   1. If the project has an IisAppPoolName configured, stop that pool
        //      directly via appcmd (precise, only affects the named pool).
        //   2. If that fails (insufficient permissions) OR no name is configured,
        //      enumerate IIS app pools and pick the ones whose physical paths
        //      match the target directory.
        //   3. If we still have w3wp workers holding the output files (verified by
        //      an exclusive FileShare.None probe), ask the user to elevate via UAC
        //      and kill them. IIS respawns workers on the next request, so this
        //      is a transient blip, not an outage.
        // Returns the list of AppPool names we stopped so the caller can start
        // them again after both publish and the backup sync (or sentinels like
        // "__w3wp_killed__").
        private async Task<IReadOnlyList<string>> StopAppPoolsHoldingOutputAsync(
            string? outputDir,
            Models.ManagedProject project,
            string projectName,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            var stopped = new List<string>();
            try
            {
                // 1. Honour the explicit IisAppPoolName the user configured.
                if (!string.IsNullOrWhiteSpace(project.IisAppPoolName))
                {
                    var pool = project.IisAppPoolName.Trim();
                    Report(logProgress, $"[{projectName}] → 使用配置的 IIS 应用池 {pool}");
                    // None: a cancel must not abandon appcmd after it has stopped
                    // the pool, or the pool would be missing from the restore list.
                    if (await TryAppPoolActionAsync(pool, "stop", projectName, logProgress, CancellationToken.None))
                    {
                        stopped.Add(pool);
                    }
                    else
                    {
                        // appcmd failed (insufficient permissions). Try to elevate
                        // so the user just has to click "Yes" on the UAC prompt
                        // instead of manually running appcmd.
                        Report(logProgress, $"[{projectName}] → 当前权限不足，无法停止应用程序池，尝试通过 UAC 提权");
                        if (await TryElevatedAppPoolActionAsync(pool, "stop", projectName, logProgress, CancellationToken.None))
                            stopped.Add(pool);
                    }
                }

                if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
                    return stopped;

                var fullPath = Path.GetFullPath(outputDir).TrimEnd(Path.DirectorySeparatorChar);
                Report(logProgress, $"[{projectName}] → 检查目标目录 {fullPath} 是否被 IIS 占用");

                // 2. Heuristic: find any AppPool bound to the publish target path.
                if (stopped.Count == 0)
                {
                    var matches = await FindAppPoolsForPathAsync(fullPath, cancellationToken);
                    if (matches.Count > 0)
                    {
                        foreach (var appPool in matches)
                        {
                            if (await TryAppPoolActionAsync(appPool, "stop", projectName, logProgress, CancellationToken.None))
                                stopped.Add(appPool);
                        }
                    }
                    else
                    {
                        Report(logProgress, $"[{projectName}] → 未找到与 {fullPath} 关联的 IIS 应用池");
                    }
                }

                // 3. Last resort: confirm a w3wp worker is still holding files in
                // the target directory. Even if recycle looked like it succeeded,
                // IIS may have already spawned a fresh w3wp before our publish
                // reached the copy step (we saw this on Windows Server: the
                // worker respawn is triggered by health probes or the very next
                // request). The most reliable cure is `iisreset /stop` — that
                // takes the whole W3SVC service down, so no new w3wp can be
                // spawned until we explicitly start it again. We trade "brief
                // outage of all IIS sites" for "no race window at all".
                // IsAnyW3wpHoldingPath does up to 8 FileShare.None probes; on a
                // large output folder that can briefly stall the dispatcher, so
                // we run it on the thread pool.
                if (CanUseGlobalIisReset() && stopped.Count == 0 && await Task.Run(() => IsAnyW3wpHoldingPath(fullPath)))
                {
                    Report(logProgress, $"[{projectName}] → 检测到 w3wp 仍在占用 {fullPath}，将通过 UAC 提权执行 iisreset /stop（暂时停掉整个 IIS）");
                    if (await TryElevatedIisResetStopAsync(projectName, logProgress, cancellationToken))
                    {
                        stopped.Add("__iisreset_stopped__");
                    }
                }

                // 4. Truly final fallback: kill the w3wp workers. Unlike
                // `iisreset /stop`, this is racy: IIS will respawn a new worker
                // within seconds (driven by site AlwaysRunning + health probes),
                // which is exactly what bit us before. Only reached if `iisreset`
                // itself is unavailable.
                if (CanUseGlobalIisReset() && stopped.Count == 0 && await Task.Run(() => IsAnyW3wpHoldingPath(fullPath)))
                {
                    Report(logProgress, $"[{projectName}] → iisreset 不可用，回退到 UAC 提权杀 w3wp（可能很快被 IIS 拉起）");
                    if (await TryElevatedKillW3wpAsync(projectName, logProgress, cancellationToken))
                    {
                        stopped.Add("__w3wp_killed__");
                    }
                }

                if (stopped.Count == 0 && await Task.Run(() => IsAnyW3wpHoldingPath(fullPath)))
                {
                    // Surface the underlying access error so the user knows what
                    // to do. Without this, publish silently fails with MSB3027
                    // and the user has no idea why.
                    Report(logProgress,
                        $"[{projectName}] ⚠️ 目标目录 {fullPath} 仍被占用，请以管理员身份运行 LocalEnvManager，或在 IIS 中手动停止对应应用池后再试");
                }
            }
            catch (Exception ex)
            {
                Report(logProgress, $"[{projectName}] ⚠️ 检查 IIS 应用池时出错：{ex.Message}");
            }
            return stopped;
        }

        // A process-level IIS stop is unsafe: if LocalEnvManager exits during
        // deployment, all sites can remain offline. Keep the legacy routines
        // below unavailable as an automatic fallback. Deployments may recycle
        // only an explicitly configured or path-matched application pool.
        private static bool CanUseGlobalIisReset() => false;

        // Start every pool we stopped. The caller's cancellation token is
        // intentionally not used: a cancel during publish must still bring the
        // site back, and a throw here used to escape the caller's finally and
        // leave the pool Stopped. Returns failure when any start did not succeed
        // so the caller can fail the run instead of logging a false success.
        private async Task<ProjectExecutionResult> StartAppPoolsAsync(
            IReadOnlyList<string> appPoolNames,
            string projectName,
            IProgress<string>? logProgress)
        {
            if (appPoolNames.Count == 0)
                return new ProjectExecutionResult(true, string.Empty);

            var failures = new List<string>();
            foreach (var name in appPoolNames)
            {
                try
                {
                    if (name == "__iisreset_stopped__")
                    {
                        Report(logProgress, $"[{projectName}] → 正在通过 UAC 提权恢复 IIS 服务 (iisreset /start)");
                        await TryElevatedIisResetStartAsync(projectName, logProgress, CancellationToken.None);
                    }
                    else if (name == "__w3wp_killed__")
                    {
                        Report(logProgress, $"[{projectName}] → IIS worker 已被结束，下次请求时 IIS 会自动重启");
                    }
                    else if (!string.IsNullOrEmpty(name))
                    {
                        if (!await TryAppPoolActionAsync(name, "start", projectName, logProgress, CancellationToken.None))
                            failures.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(name);
                    Report(logProgress, $"[{projectName}] → 启动 IIS 应用程序池 {name} 失败：{ex.Message}");
                }
            }

            if (failures.Count == 0)
                return new ProjectExecutionResult(true, string.Empty);

            var message = $"{projectName}: 未能重新启动 IIS 应用程序池 {string.Join(", ", failures)}。站点可能仍处于停止状态。";
            Report(logProgress, $"[{projectName}] ✗ {message}");
            return new ProjectExecutionResult(false, message);
        }

        // Enumerate IIS AppPools and pick the ones whose physical paths equal (or
        // sit under) the target publish directory. Uses the standard WMI provider
        // shipped with the IIS Management & Monitoring role; on systems without
        // it we silently return an empty set so the caller skips the IIS step.
        private static async Task<IReadOnlyList<string>> FindAppPoolsForPathAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            var found = new List<string>();
            try
            {
                // Use the PowerShell-based approach via WebAdministration so we
                // can ask IIS for the actual physical paths without needing admin.
                // This runs in a transient powershell.exe to avoid polluting the
                // host process with module imports.
                using var ps = Process.Start(new ProcessStartInfo("powershell.exe", [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$ErrorActionPreference='SilentlyContinue'; " +
                    "Import-Module WebAdministration; " +
                    "if (-not (Test-Path 'IIS:\\AppPools')) { exit 0 }; " +
                    "Get-WebApplication | ForEach-Object { " +
                    "  $app = $_; " +
                    "  Get-WebVirtualDirectory -Site $app.Site -Application $app.path | ForEach-Object { " +
                    "    Write-Output \"$($app.applicationPool)|$($_.physicalPath)\" " +
                    "  } " +
                    "}"
                ])
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });

                if (ps is null) return found;
                var output = await ps.StandardOutput.ReadToEndAsync(cancellationToken);
                await ps.WaitForExitAsync(cancellationToken);

                foreach (var raw in output.Split('\n'))
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    var parts = line.Split('|', 2);
                    if (parts.Length != 2) continue;
                    var appPool = parts[0].Trim();
                    var physPath = parts[1].Trim().TrimEnd(Path.DirectorySeparatorChar);
                    if (string.IsNullOrEmpty(appPool)) continue;
                    if (string.Equals(physPath, fullPath, StringComparison.OrdinalIgnoreCase) ||
                        fullPath.StartsWith(physPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!found.Contains(appPool)) found.Add(appPool);
                    }
                }
            }
            catch
            {
                // WebAdministration unavailable / WMI errored out → no app pools.
            }
            return found;
        }

        // Stop or start a single IIS AppPool by name via appcmd. The action string
        // is "stop" or "start", and the log line says which one ran. Returns true only when
        // appcmd exits 0. A cancelled settle delay after that exit does not turn
        // a successful action into a failure — otherwise a stopped pool would be
        // dropped from the caller's restart list.
        private static async Task<bool> TryAppPoolActionAsync(
            string appPoolName,
            string action,
            string projectName,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            var appcmd = Path.Combine(Environment.SystemDirectory, "inetsrv", "appcmd.exe");
            if (!File.Exists(appcmd)) return false;

            try
            {
                using var p = Process.Start(new ProcessStartInfo(appcmd, $"{action} apppool /apppool.name:\"{appPoolName}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (p is null) return false;
                await p.WaitForExitAsync(cancellationToken);
                if (p.ExitCode == 0)
                {
                    Report(logProgress, $"[{projectName}] → {AppPoolActionVerb(action)} IIS 应用程序池 {appPoolName}");
                    // After stop, give the old worker a moment to release file
                    // handles. After start this is just a short settle. Cancelling
                    // the wait must not undo the successful appcmd exit.
                    await DelayBestEffortAsync(cancellationToken);
                    return true;
                }
                Report(logProgress, $"[{projectName}] → appcmd {action} 退出码 {p.ExitCode}（可能需要管理员权限）");
            }
            catch (Exception ex)
            {
                Report(logProgress, $"[{projectName}] → appcmd 异常：{ex.Message}");
            }
            return false;
        }

        // Run an arbitrary appcmd action in an elevated PowerShell session. We use
        // a tiny .ps1 written to %TEMP% and invoke it via `powershell -Verb RunAs`
        // so the user gets a normal UAC consent prompt instead of a silent
        // failure. The elevated script returns 0 on success, non-zero otherwise.
        private static async Task<bool> TryElevatedAppPoolActionAsync(
            string appPoolName,
            string action,
            string projectName,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"lem-iis-{Guid.NewGuid():N}.ps1");
            var okFlag = scriptPath + ".ok";
            var script = $@"$ErrorActionPreference = 'Stop'
appcmd {action} apppool /apppool.name:""{appPoolName.Replace("\"", "")}"" 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {{ New-Item -ItemType File -Path '{okFlag}' -Force | Out-Null }}
exit $LASTEXITCODE
";
            try
            {
                await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
                // The host process already runs elevated (see app.manifest),
                // so powershell.exe inherits the admin token. No UAC prompt,
                // no "runas" verb needed.
                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p is null)
                {
                    Report(logProgress, $"[{projectName}] → 无法启动 PowerShell（app.manifest 未生效？）");
                    return false;
                }
                await p.WaitForExitAsync(cancellationToken);
                if (p.ExitCode == 0 && File.Exists(okFlag))
                {
                    Report(logProgress, $"[{projectName}] → {AppPoolActionVerb(action)} IIS 应用程序池 {appPoolName}");
                    await DelayBestEffortAsync(cancellationToken);
                    return true;
                }
                var stderr = await p.StandardError.ReadToEndAsync(cancellationToken);
                Report(logProgress, $"[{projectName}] → appcmd 退出码 {p.ExitCode}：{stderr.Trim()}");
            }
            catch (Exception ex)
            {
                Report(logProgress, $"[{projectName}] → appcmd 调用失败：{ex.Message}");
            }
            finally
            {
                TryDelete(scriptPath);
                TryDelete(okFlag);
            }
            return false;
        }

        // Elevate and kill every w3wp.exe so file handles on the publish output
        // directory get released. IIS respawns workers on the next request, so
        // the site just experiences a brief blip.
        private static async Task<bool> TryElevatedKillW3wpAsync(
            string projectName,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"lem-w3wp-{Guid.NewGuid():N}.ps1");
            var okFlag = scriptPath + ".ok";
            var script = $@"$ErrorActionPreference = 'SilentlyContinue'
Get-Process -Name w3wp -ErrorAction SilentlyContinue | ForEach-Object {{
  try {{ $_ | Stop-Process -Force }} catch {{}}
}}
# Verify all w3wp are gone
$remaining = (Get-Process -Name w3wp -ErrorAction SilentlyContinue | Measure-Object).Count
if ($remaining -eq 0) {{ New-Item -ItemType File -Path '{okFlag}' -Force | Out-Null }}
exit 0
";
            try
            {
                await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p is null)
                {
                    Report(logProgress, $"[{projectName}] → 无法启动 PowerShell（app.manifest 未生效？）");
                    return false;
                }
                await p.WaitForExitAsync(cancellationToken);
                if (File.Exists(okFlag))
                {
                    Report(logProgress, $"[{projectName}] → 已结束所有 w3wp 进程，IIS 会在下次请求时拉起新 worker");
                    await Task.Delay(800, cancellationToken);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Report(logProgress, $"[{projectName}] → w3wp 清理失败：{ex.Message}");
            }
            finally
            {
                TryDelete(scriptPath);
                TryDelete(okFlag);
            }
            return false;
        }

        // Stop the entire IIS service (W3SVC) via PowerShell. The host process
        // already runs elevated (see app.manifest), so no UAC prompt. This
        // is the most reliable way to guarantee no w3wp worker respawns while
        // publish is running: when W3SVC is stopped there is no Application
        // Host to monitor site health or accept requests, so workers stay dead
        // until we call iisreset /start. Trade-off: ALL IIS sites on the box
        // are briefly down. For single-purpose dev/test servers this is the
        // most robust fix.
        private static async Task<bool> TryElevatedIisResetStopAsync(
            string projectName,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"lem-iisreset-{Guid.NewGuid():N}.ps1");
            var okFlag = scriptPath + ".ok";
            // iisreset /stop runs synchronously and waits for W3SVC to fully
            // stop. We also force-kill any straggler w3wp just in case the
            // service didn't take them all down.
            var script = $@"$ErrorActionPreference = 'SilentlyContinue'
& iisreset /stop 2>&1 | Out-Null
Start-Sleep -Seconds 2
Get-Process -Name w3wp -ErrorAction SilentlyContinue | ForEach-Object {{
  try {{ $_ | Stop-Process -Force }} catch {{}}
}}
Start-Sleep -Seconds 1
$remaining = (Get-Process -Name w3wp -ErrorAction SilentlyContinue | Measure-Object).Count
$w3svc = (Get-Service W3SVC -ErrorAction SilentlyContinue).Status
if ($remaining -eq 0 -and $w3svc -ne 'Running') {{
  New-Item -ItemType File -Path '{okFlag}' -Force | Out-Null
}}
exit 0
";
            try
            {
                await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p is null)
                {
                    Report(logProgress, $"[{projectName}] → 无法启动 PowerShell（app.manifest 未生效？）");
                    return false;
                }
                await p.WaitForExitAsync(cancellationToken);
                if (File.Exists(okFlag))
                {
                    Report(logProgress, $"[{projectName}] → iisreset /stop 成功，IIS 已停,w3wp 已全部结束");
                    return true;
                }
                var stderr = await p.StandardError.ReadToEndAsync(cancellationToken);
                Report(logProgress, $"[{projectName}] → iisreset /stop 未能完全停止 IIS（残留 w3wp 或 W3SVC 未停）：{stderr.Trim()}");
            }
            catch (Exception ex)
            {
                Report(logProgress, $"[{projectName}] → iisreset /stop 调用失败：{ex.Message}");
            }
            finally
            {
                TryDelete(scriptPath);
                TryDelete(okFlag);
            }
            return false;
        }

        // Start the IIS service back up via UAC-elevated PowerShell. Mirrors
        // TryElevatedIisResetStopAsync. No new w3wp worker is spawned until
        // the first HTTP request — that gives publish its full run time
        // without any lock contention.
        private static async Task<bool> TryElevatedIisResetStartAsync(
            string projectName,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"lem-iisstart-{Guid.NewGuid():N}.ps1");
            var okFlag = scriptPath + ".ok";
            var script = $@"$ErrorActionPreference = 'SilentlyContinue'
& iisreset /start 2>&1 | Out-Null
Start-Sleep -Seconds 2
$w3svc = (Get-Service W3SVC -ErrorAction SilentlyContinue).Status
if ($w3svc -eq 'Running') {{
  New-Item -ItemType File -Path '{okFlag}' -Force | Out-Null
}}
exit 0
";
            try
            {
                await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p is null)
                {
                    Report(logProgress, $"[{projectName}] → 无法启动 PowerShell（app.manifest 未生效？）");
                    return false;
                }
                await p.WaitForExitAsync(cancellationToken);
                if (File.Exists(okFlag))
                {
                    Report(logProgress, $"[{projectName}] → iisreset /start 成功，IIS 已恢复服务");
                    return true;
                }
                var stderr = await p.StandardError.ReadToEndAsync(cancellationToken);
                Report(logProgress, $"[{projectName}] → iisreset /start 失败：{stderr.Trim()}");
            }
            catch (Exception ex)
            {
                Report(logProgress, $"[{projectName}] → iisreset /start 调用失败：{ex.Message}");
            }
            finally
            {
                TryDelete(scriptPath);
                TryDelete(okFlag);
            }
            return false;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string AppPoolActionVerb(string action) => action switch
        {
            "stop" => "已停止",
            "start" => "已启动",
            _ => action
        };

        // 800ms is long enough for a stopped w3wp to drop DLL handles on a local
        // disk, and short enough that publish is not noticeably slower.
        private static async Task DelayBestEffortAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(800, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static string JoinMessages(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second;
            if (string.IsNullOrWhiteSpace(second)) return first;
            return $"{first}\n{second}";
        }

        // Returns true if any w3wp.exe process is currently holding at least one
        // DLL or EXE under the given physical path. The check is a heuristic —
        // we walk the contents of the directory and try to open each file with
        // exclusive access. If FileShare.None fails, something has it open.
        // (A w3wp that doesn't serve a DLL in that folder is harmless to us;
        // it isn't holding the file we need to overwrite.)
        private static bool IsAnyW3wpHoldingPath(string fullPath)
        {
            if (!Directory.Exists(fullPath)) return false;

            // Probe up to N candidate files. .dll/.exe are the common lockers
            // but other binaries (e.g. .pdb, .deps.json) can also collide.
            string[]? candidates = null;
            try
            {
                candidates = Directory
                    .EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    .Take(8)
                    .ToArray();
            }
            catch
            {
                return false;
            }

            foreach (var file in candidates)
            {
                try
                {
                    // Open with FileShare.None: this fails the moment any other
                    // process has *any* handle (read, write, or delete) on the
                    // file. That's exactly the "another process is using it"
                    // condition MSBuild's overwrite would also trip.
                    using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                    // Opened with no sharing — nothing is holding it.
                }
                catch (IOException)
                {
                    // Someone has a handle (read or write) → treat as locked.
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    // ACL blocks us from opening the file at all. From a publish
                    // perspective the file is just as locked as if w3wp owned it.
                    return true;
                }
            }
            return false;
        }

        // Kill every running w3wp.exe (with the process tree) to release any
        // exclusive file handles on the publish output directory. IIS will
        // respawn a fresh worker the moment the next request arrives, so the
        // site experiences a brief blip — no manual restart needed.
        // WaitForExitAsync replaces the previous synchronous WaitForExit(3000)
        // so the dispatcher isn't held while waiting for each worker to die.
        private static async Task<bool> KillAllW3wpAsync(
            string projectName,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            var killed = 0;
            foreach (var proc in Process.GetProcessesByName("w3wp"))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    try
                    {
                        // Bounded wait via Task.WhenAny — if the worker takes
                        // more than 3s to die (typically because of hung DLL
                        // unload), move on; the publish will surface the real
                        // error later.
                        await Task.WhenAny(
                            proc.WaitForExitAsync(),
                            Task.Delay(3000, cancellationToken)).ConfigureAwait(false);
                        killed++;
                    }
                    catch
                    {
                        // Process may have exited on its own or be access-denied.
                    }
                }
                catch
                {
                    // Process may have exited on its own or be access-denied.
                }
                finally
                {
                    proc.Dispose();
                }
            }
            if (killed > 0)
            {
                Report(logProgress, $"[{projectName}] → 已结束 {killed} 个 w3wp 进程（IIS 会在下次请求时自动拉起新 worker）");
                // Brief settle: let kernel release the file handles before publish.
                await Task.Delay(500, cancellationToken);
                return true;
            }
            return false;
        }

        private static IReadOnlyList<string>? BuildUvicornArguments(ManagedProject project)
        {
            var trimmed = project.Command.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return null;

            return trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        }

        // Pull the TCP port out of uvicorn arguments. Handles "--port 8000" and
        // "--port=8000"; falls back to uvicorn's default of 8000 when none is given.
        private static bool TryExtractPort(IReadOnlyList<string> arguments, out int port)
        {
            for (var i = 0; i < arguments.Count; i++)
            {
                var arg = arguments[i];
                if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(arg["--port=".Length..], out port))
                        return true;
                }
                else if (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < arguments.Count
                    && int.TryParse(arguments[i + 1], out port))
                {
                    return true;
                }
            }

            port = 8000; // uvicorn's default when --port is omitted.
            return true;
        }

        // If something is already listening on the given port, terminate the owning
        // process(es) so the fresh uvicorn instance can bind. Uses `netstat -ano` to
        // map the port to PIDs — no admin rights needed for same-user processes.
        // After every kill we re-poll the port: if a leftover process (typically the
        // cmd.exe wrapper from the previous UseShellExecute=true launch, which holds
        // the listening socket via TCP inheritance even after the python child dies)
        // is still bound we escalate to `taskkill /F /T` via PowerShell. That's the
        // only reliable way to release a socket that the .NET Process.Kill missed,
        // because taskkill walks the full job-object tree.
        private async Task FreePortIfOccupiedAsync(int port, string projectName, IProgress<string>? logProgress)
        {
            int currentPid;
            try
            {
                currentPid = Environment.ProcessId;
            }
            catch
            {
                currentPid = -1;
            }

            // First pass: kill via the managed Process API. Fast path that handles
            // the common case (a same-user python.exe child) without spawning a
            // PowerShell host. WaitForExitAsync replaces the blocking
            // Process.WaitForExit(5000) so the dispatcher stays free during the
            // (rare) case the kernel is slow to release the handle.
            var pids = await FindPidsListeningOnPortAsync(port).ConfigureAwait(false);
            pids.Remove(currentPid);
            if (pids.Count == 0)
            {
                Report(logProgress, $"[{projectName}] 端口 {port} 空闲，直接启动");
                return;
            }

            foreach (var pid in pids)
            {
                try
                {
                    using var occupant = Process.GetProcessById(pid);
                    var name = occupant.ProcessName;
                    occupant.Kill(entireProcessTree: true);
                    try
                    {
                        await occupant.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Process may have raced — fine, we just wanted to give
                        // the kernel a beat to release the handle.
                    }
                    Report(logProgress, $"[{projectName}] 端口 {port} 被占用，已结束进程 {name} (PID {pid})");
                }
                catch (ArgumentException)
                {
                    // Process already gone between discovery and kill — nothing to do.
                }
                catch (Exception ex)
                {
                    Report(logProgress, $"[{projectName}] 无法结束占用端口 {port} 的进程 PID {pid}：{ex.Message}");
                }
            }

            // Second pass: wait briefly for the OS to actually release the socket.
            // On Windows, killing the python child doesn't always free the
            // LISTENING socket that the cmd.exe wrapper inherited via
            // CreateProcess — the kernel keeps the port in CLOSE_WAIT for a few
            // hundred ms. Without this pause the new uvicorn would bind() and get
            // WSAEADDRINUSE ("Only one usage of each socket address is normally
            // permitted") even though netstat no longer shows anyone on the port.
            var stillBound = await WaitForPortFreeAsync(port, seconds: 3).ConfigureAwait(false);
            if (!stillBound)
            {
                Report(logProgress, $"[{projectName}] ✓ 端口 {port} 已释放");
                return;
            }

            // Third pass: the .NET kill missed something. Escalate to
            // `taskkill /F /T /PID <pid>` via PowerShell. /T kills the process tree
            // (same job-object walk we wanted), /F is the FORCE flag that
            // bypasses WM_CLOSE/WM_QUERYENDSESSION and terminates immediately.
            // We re-resolve the PIDs because the first pass may have partially
            // succeeded: leftover entries are the ones taskkill needs to deal with.
            var leftover = await FindPidsListeningOnPortAsync(port).ConfigureAwait(false);
            leftover.Remove(currentPid);
            if (leftover.Count == 0)
            {
                Report(logProgress, $"[{projectName}] ✓ 端口 {port} 已释放");
                return;
            }

            Report(logProgress, $"[{projectName}] → 端口 {port} 仍被占用，使用 taskkill /F /T 强制结束残留进程");
            foreach (var pid in leftover)
            {
                await TryTaskKillAsync(pid, projectName, logProgress).ConfigureAwait(false);
            }

            // Final verification — give the OS up to 5s to settle after the
            // escalated kill (taskkill can return instantly while the kernel is
            // still tearing down the socket). If still bound, the new uvicorn is
            // going to fail no matter what we do; just log and let
            // WaitForPortBound surface the user-visible error so they know to
            // reboot / use a different port.
            if (await WaitForPortFreeAsync(port, seconds: 5).ConfigureAwait(false))
            {
                Report(logProgress, $"[{projectName}] ⚠️ 端口 {port} 释放超时，uvicorn 启动可能失败");
            }
            else
            {
                Report(logProgress, $"[{projectName}] ✓ 端口 {port} 已释放");
            }
        }

        // Poll netstat until the port is no longer in LISTENING state, or until
        // the timeout elapses. Returns true when the port is STILL bound at the
        // timeout (i.e. the kill didn't free it). Task.Delay replaces
        // Thread.Sleep so the dispatcher isn't held during the poll loop.
        private static async Task<bool> WaitForPortFreeAsync(int port, int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                var pids = await FindPidsListeningOnPortAsync(port).ConfigureAwait(false);
                if (pids.Count == 0) return false;
                await Task.Delay(200).ConfigureAwait(false);
            }
            return true;
        }

        // Last-resort killer: spawn `taskkill /F /T /PID <pid>` via cmd.exe so we
        // can terminate processes that the .NET Process.Kill refused (typically
        // because they're owned by another user / SYSTEM, or because the process
        // is stuck in a non-killable state). Runs synchronously because the
        // caller (FreePortIfOccupied) needs the port free before continuing.
        private static async Task TryTaskKillAsync(int pid, string projectName, IProgress<string>? logProgress)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", $"/F /T /PID {pid}")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p is null)
                {
                    Report(logProgress, $"[{projectName}] → taskkill 启动失败");
                    return;
                }
                await p.WaitForExitAsync();
                var stdout = await p.StandardOutput.ReadToEndAsync();
                var stderr = await p.StandardError.ReadToEndAsync();
                if (p.ExitCode == 0)
                {
                    Report(logProgress, $"[{projectName}] → taskkill 已结束 PID {pid}：{stdout.Trim()}");
                }
                else
                {
                    // Exit code 128 / "process not found" means the kill already
                    // landed between our netstat snapshot and the taskkill call.
                    // Anything else is a real failure (access denied, etc.).
                    var trimmed = (stdout + " " + stderr).Trim();
                    if (trimmed.Contains("not found", StringComparison.OrdinalIgnoreCase)
                        || trimmed.Contains("could not find", StringComparison.OrdinalIgnoreCase))
                    {
                        Report(logProgress, $"[{projectName}] → PID {pid} 已退出，无需 taskkill");
                    }
                    else
                    {
                        Report(logProgress, $"[{projectName}] → taskkill 失败 (exit {p.ExitCode})：{trimmed}");
                    }
                }
            }
            catch (Exception ex)
            {
                Report(logProgress, $"[{projectName}] → taskkill 调用异常：{ex.Message}");
            }
        }

        // Parse `netstat -ano` output and collect PIDs that are LISTENING on the port.
        // A port is considered "occupied" if anything binds to it on ANY interface:
        //  - IPv4 wildcard  0.0.0.0:port  (and its IPv4-mapped equivalent)
        //  - IPv6 wildcard  [::]:port
        //  - any specific   127.0.0.1:port, 192.168.x.x:port, [fe80::...]:port
        // Otherwise a process bound to 127.0.0.1:port would still block another from
        // binding 0.0.0.0:port (same TCP/UDP port), so we must catch all of them.
        // Async because netstat's stdout drain can take several hundred ms on a
        // busy host; the previous synchronous version blocked the dispatcher.
        private static async Task<HashSet<int>> FindPidsListeningOnPortAsync(int port)
        {
            var pids = new HashSet<int>();
            try
            {
                var startInfo = new ProcessStartInfo("netstat", "-ano")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var netstat = Process.Start(startInfo);
                if (netstat is null)
                    return pids;

                var output = await netstat.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await netstat.WaitForExitAsync().ConfigureAwait(false);

                var suffix = ":" + port.ToString(CultureInfo.InvariantCulture);
                var ipv6Wildcard = "[::]" + suffix;
                foreach (var raw in output.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || !line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var columns = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (columns.Length < 5)
                        continue;

                    // columns: TCP  local  foreign  STATE  PID
                    var local = columns[1];
                    var endsWithPort = local.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
                    var isWildcard = local == ipv6Wildcard;
                    if (!endsWithPort && !isWildcard)
                        continue;

                    if (int.TryParse(columns[^1], out var pid) && pid > 0)
                        pids.Add(pid);
                }
            }
            catch
            {
                // Best-effort: if netstat is unavailable we just skip freeing the port.
            }

            return pids;
        }

        private static ProcessStartInfo CreateProcessStartInfo(string fileName, IEnumerable<string> arguments, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            return startInfo;
        }

        private static string? ResolveWorkingDirectory(string path, string projectName)
        {
            var resolved = NormalizeAbsolutePath(path);
            if (string.IsNullOrWhiteSpace(resolved))
                return null;

            if (Directory.Exists(resolved))
                return Path.GetFullPath(resolved);

            if (File.Exists(resolved))
                return Path.GetDirectoryName(Path.GetFullPath(resolved))
                    ?? throw new DirectoryNotFoundException($"Could not resolve a working directory from '{resolved}' for {projectName}.");

            return null;
        }

        // All paths on a project are absolute. If the user accidentally supplies a relative path
        // we don't try to splice it onto anything — we return it as-is and let the operating
        // system complain. This keeps the "no-base-path" model honest.
        private static string? NormalizeAbsolutePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            return path.Trim().Trim('"');
        }

        // -------- backup -> target sync --------

        // File copy can take seconds on large folders. Push the whole thing onto
        // the thread pool so the WPF dispatcher stays responsive during the
        // uvicorn pipeline (and the dotnet publish pipeline — both call this).
        private static Task<ProjectExecutionResult> SyncBackupToTargetAsync(ManagedProject project)
        {
            return Task.Run(() =>
            {
                var backup = NormalizeAbsolutePath(project.BackupFolder);
                var target = NormalizeAbsolutePath(project.TargetPath);

                if (string.IsNullOrWhiteSpace(backup) || string.IsNullOrWhiteSpace(target))
                    return new ProjectExecutionResult(true, "Skipped backup sync (target path or backup folder not set).");

                if (!Directory.Exists(backup))
                    return new ProjectExecutionResult(false, $"Backup folder does not exist: {backup}");

                try
                {
                    CopyDirectory(backup, target);
                    return new ProjectExecutionResult(true, $"Synced backup -> target: {target}");
                }
                catch (Exception ex)
                {
                    return new ProjectExecutionResult(false, $"Backup sync failed: {ex.Message}");
                }
            });
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source))
            {
                var destFile = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        private static ProjectExecutionResult Combine(string buildMessage, ProjectExecutionResult sync)
        {
            if (!sync.Success)
                return new ProjectExecutionResult(false, $"{buildMessage}\n{sync.Message}");

            var skipped = sync.Message.StartsWith("Skipped", StringComparison.Ordinal);
            return new ProjectExecutionResult(true, skipped ? buildMessage : $"{buildMessage}\n{sync.Message}");
        }

        private static async Task<ProjectExecutionResult> RunCapturedProcessAsync(
            string projectName,
            string fileName,
            IEnumerable<string> arguments,
            ProcessStartInfo startInfo,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken)
        {
            var preview = BuildPreview(fileName, arguments);
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new ProjectExecutionResult(false, $"Failed to start {projectName}: {preview}");

            using var ctr = RegisterKillOnCancel(process, cancellationToken);
            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Report(logProgress, $"[{projectName}] 已取消");
                return new ProjectExecutionResult(false, $"{projectName}: cancelled by user.");
            }

            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;

            // Forward tool output lines into the per-project log so users can see
            // what `dotnet publish` actually printed (warnings, restore output, etc.).
            if (!string.IsNullOrWhiteSpace(standardOutput))
                ReportJoined(logProgress, projectName, standardOutput);
            if (!string.IsNullOrWhiteSpace(standardError))
                ReportJoined(logProgress, projectName, standardError, prefix: "stderr");

            var combinedOutput = string.Join(Environment.NewLine,
                new[] { standardOutput, standardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var message = $"{projectName} {(process.ExitCode == 0 ? "completed" : "failed")} with exit code {process.ExitCode}.\nCommand: {preview}";
            var trimmedOutput = TrimOutput(combinedOutput);
            if (!string.IsNullOrWhiteSpace(trimmedOutput))
                message = $"{message}\n{trimmedOutput}";

            return new ProjectExecutionResult(process.ExitCode == 0, message);
        }

        private static string BuildPreview(string fileName, IEnumerable<string> arguments)
        {
            return string.Join(' ', new[] { fileName }.Concat(arguments.Select(QuoteArgument)));
        }

        private static string QuoteArgument(string argument)
        {
            return argument.Contains(' ') ? $"\"{argument}\"" : argument;
        }

        private static string TrimOutput(string output)
        {
            const int maxLength = 4000;
            return output.Length <= maxLength ? output : output[^maxLength..];
        }

        // Push a single log line to the per-project progress reporter. Empty messages
        // are skipped so we don't pollute the UI with empty rows.
        private static void Report(IProgress<string>? progress, string line)
        {
            if (progress is null || string.IsNullOrWhiteSpace(line))
                return;
            progress.Report(line.Trim());
        }

        // Split multi-line tool output into individual rows so each appears as its own
        // log line on the Log tab.
        private static void ReportJoined(IProgress<string>? progress, string projectName, string text, string? prefix = null)
        {
            if (progress is null || string.IsNullOrWhiteSpace(text))
                return;

            var header = string.IsNullOrEmpty(prefix) ? $"[{projectName}]" : $"[{projectName}|{prefix}]";
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                progress.Report($"{header} {line.TrimEnd()}");
            }
        }
    }
}
