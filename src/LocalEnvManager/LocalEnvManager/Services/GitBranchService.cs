using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// Read-only access to a git repository's current branch. Used by the Execute tab to
    /// show the user the branch each project is on before they run anything, so they don't
    /// accidentally deploy from the wrong branch.
    /// </summary>
    public interface IGitBranchService
    {
        /// <summary>
        /// Returns the current branch name for the repository at <paramref name="repositoryPath"/>,
        /// or an empty string when the path isn't a git repo / git isn't installed / a detached
        /// HEAD is reported. Never throws — failures collapse to "" so the UI can show "—".
        /// </summary>
        Task<string> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default);
    }

    public sealed class GitBranchService : IGitBranchService
    {
        public async Task<string> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
                return string.Empty;

            var startInfo = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = repositoryPath
            };
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("--abbrev-ref");
            startInfo.ArgumentList.Add("HEAD");

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null) return string.Empty;

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken);
                var stdout = await stdoutTask;
                _ = await stderrTask; // drain stderr but ignore it

                if (process.ExitCode != 0) return string.Empty;

                var branch = stdout.Trim();
                // "HEAD" means detached HEAD — show nothing rather than a confusing literal "HEAD".
                return string.IsNullOrEmpty(branch) || branch == "HEAD" ? string.Empty : branch;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}