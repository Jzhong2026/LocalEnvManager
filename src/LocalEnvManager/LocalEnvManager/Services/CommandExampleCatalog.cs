using System.Collections.Generic;
using LocalEnvManager.Models;

namespace LocalEnvManager.Services
{
	// Per-deployment-mode Command defaults.
	//
	// Each mode has ONE default command (mirroring the user's real-world projects)
	// and a list of clickable examples shown in the parameter-help popup. The
	// default is what gets written into Command when the user switches
	// DeploymentMode; the examples are extra rows the user can pick from
	// (and one-click-copy to clipboard) inside the ? help popup.
	//
	// dotnet publish / build defaults splice in the project's TargetPath when set
	// so the placeholder -o "<TargetPath>" is meaningful. Otherwise the slot is
	// left blank for the user to fill.
	internal static class CommandExampleCatalog
	{
		// Builds the default command string for a mode, optionally splicing in the
		// project's TargetPath so the user gets a ready-to-run placeholder.
		public static string GetDefaultCommand(DeploymentMode mode, string targetPath)
		{
			var t = string.IsNullOrWhiteSpace(targetPath) ? string.Empty : targetPath.Trim();

			return mode switch
			{
				DeploymentMode.DotNetPublish =>
					string.IsNullOrEmpty(t)
						? "publish -c Release -p:Platform=\"any cpu\""
						: $"publish -c Release -p:Platform=\"any cpu\" -o \"{t}\"",

				DeploymentMode.DotNetBuild =>
					// Build mode intentionally doesn't splice -o TargetPath — the
					// runner already handles build output without it.
					"build -c Debug",

				DeploymentMode.PowerShellCommand =>
					".\\build_release.ps1",

				DeploymentMode.Uvicorn =>
					".venv\\Scripts\\python -m uvicorn hosting.main:app --host 0.0.0.0 --port 8000",

				_ => string.Empty
			};
		}

		// Examples surfaced in the parameter-help popup. These are user-friendly
		// preset commands — the first one per mode is also the auto-fill default.
		public static IReadOnlyList<CommandExample> GetFor(DeploymentMode mode)
		{
			return mode switch
			{
				DeploymentMode.DotNetPublish => PublishExamples,
				DeploymentMode.DotNetBuild => BuildExamples,
				DeploymentMode.PowerShellCommand => PowerShellExamples,
				DeploymentMode.Uvicorn => UvicornExamples,
				_ => []
			};
		}

		private static readonly IReadOnlyList<CommandExample> PublishExamples = new[]
		{
			// Default for the mode (no TargetPath slot).
			new CommandExample(
				"默认 — publish -c Release -p:Platform=\"any cpu\"（plus-api 格式）",
				"publish -c Release -p:Platform=\"any cpu\""),
			// Default with a TargetPath slot — UI auto-fills this when TargetPath is set.
			new CommandExample(
				"默认 + -o（拼接用户已填的 TargetPath）",
				"publish -c Release -p:Platform=\"any cpu\" -o \"<TargetPath>\""),
			// From current LLMAgentApi project.
			new CommandExample(
				"publish -c Release -o（LLMAgentApi 当前用法）",
				"publish -c Release -o \"D:\\Plus\\LLMAgentApi\""),
		};

		private static readonly IReadOnlyList<CommandExample> BuildExamples = new[]
		{
			new CommandExample(
				"默认 — build -c Debug",
				"build -c Debug"),
			new CommandExample(
				"build -c Release（plus-core 当前用法）",
				"build -c Release"),
		};

		private static readonly IReadOnlyList<CommandExample> PowerShellExamples = new[]
		{
			new CommandExample(
				"默认 — .\\build_release.ps1（ChatWidget 当前用法）",
				".\\build_release.ps1"),
			new CommandExample(
				"打包 publish 目录为 zip",
				"Compress-Archive -Path .\\publish -DestinationPath .\\dist\\api.zip -Force"),
		};

		private static readonly IReadOnlyList<CommandExample> UvicornExamples = new[]
		{
			new CommandExample(
				"默认 — PyAgents 当前用法（hosting.main:app）",
				".venv\\Scripts\\python -m uvicorn hosting.main:app --host 0.0.0.0 --port 8000"),
			new CommandExample(
				"最简 main:app",
				"main:app --host 0.0.0.0 --port 8000"),
			new CommandExample(
				"开发模式（带 --reload）",
				"main:app --host 0.0.0.0 --port 8000 --reload"),
		};
	}
}
