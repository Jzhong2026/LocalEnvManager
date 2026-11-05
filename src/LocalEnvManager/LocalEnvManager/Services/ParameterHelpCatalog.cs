using System.Collections.Generic;
using LocalEnvManager.Models;

namespace LocalEnvManager.Services
{
	// Central registry of help text for every parameter exposed in EditProjectDialog.
	// Keys are stable string tokens referenced by the dialog code-behind. Each entry
	// explains one field and gives a concrete usage example the user can copy.
	internal static class ParameterHelpCatalog
	{
		public const string ProjectName = "ProjectName";
		public const string DeploymentMode = "DeploymentMode";
		public const string SourcePath = "SourcePath";
		public const string Command = "Command";
		public const string TargetPath = "TargetPath";
		public const string BackupFolder = "BackupFolder";
		public const string IisAppPool = "IisAppPool";
		public const string PullLatest = "PullLatest";
		public const string DependsOn = "DependsOn";
		public const string LogsDirectory = "LogsDirectory";

		private static readonly IReadOnlyDictionary<string, ParameterHelp> Entries =
			new Dictionary<string, ParameterHelp>
			{
				[ProjectName] = new ParameterHelp(
					"项目名称",
					"项目的显示名称，会出现在左侧分类下、运行列表以及右侧详情面板中。名称必须唯一，便于在依赖项目下拉框里识别。",
					"示例:\n  LLMAgentApi\n  plus-core\n  llm-admin-web"),

				[DeploymentMode] = new ParameterHelp(
					"部署方式",
					"选择如何部署或运行该项目，决定「命令参数」如何被解释，以及何时把「覆盖目录」同步到「目标路径」。\n\n" +
					"• dotnet publish：执行 `dotnet publish <Command>`，命令成功后同步。适合 ASP.NET / WPF 等需要发布产物的项目。\n" +
					"• dotnet build：执行 `dotnet build <Command>`，只编译不发布，命令成功后同步。\n" +
					"• PowerShell：把 Command 当作 PowerShell 表达式执行（不通过 -File），命令成功后同步。\n" +
					"• uvicorn：把 Command 作为 uvicorn 参数，启动 Python Web 服务（不等待退出），启动前同步。\n\n" +
					"切换模式会改变 Command / Logs 区域显示的提示文本。",
					"dotnet publish 示例:\n  publish -c Release -o \"D:\\Plus\\LLMAgentApi\"\n\n" +
					"dotnet publish（指定 Platform）:\n  publish -c Release -p:Platform=\"Any CPU\" -o \"D:\\Plus\\Api-Publish\"\n\n" +
					"dotnet build 示例:\n  build -c Release\n\n" +
					"uvicorn 示例:\n  .venv\\Scripts\\python -m uvicorn hosting.main:app --host 0.0.0.0 --port 8000\n\n" +
					"PowerShell 示例:\n  .\\build_release.ps1"),

				[SourcePath] = new ParameterHelp(
					"源路径",
					"命令执行的工作目录。dotnet / uvicorn / PowerShell 都会以此目录作为 cwd 运行；git pull 也会在此目录下执行。\n\n" +
					"必须使用绝对路径（例如 D:\\Projects\\LLMAgentApi）。当前版本不再支持相对于分类的相对路径。\n\n" +
					"对于 dotnet 项目，通常指向包含 .csproj / .sln 的目录。",
					"绝对路径示例:\n  D:\\Projects\\LLMAgentApi\n  D:\\Source\\AI-Search\\src\\backend\\LLMAgentApi"),

				[Command] = new ParameterHelp(
					"Command",
					"传给所选部署方式的参数（去掉命令名本身）。切换部署方式时会自动填入对应默认值：\n" +
					"• dotnet publish → publish -c Release -p:Platform=\"any cpu\"（若目标路径已填则拼接 -o）\n" +
					"• dotnet build   → build -c Debug（不拼接 -o）\n" +
					"• PowerShell → .\\build_release.ps1（来自 ChatWidget）\n" +
					"• uvicorn → .venv\\Scripts\\python -m uvicorn hosting.main:app --host 0.0.0.0 --port 8000（来自 PyAgents）\n\n" +
					"下方每条示例都来自你当前 LocalEnvManager 配置里已经能跑的项目；点击「复制」即可写入剪贴板。",
					string.Empty,
					CommandExampleRows.All),

				[TargetPath] = new ParameterHelp(
					"目标路径",
					"部署目标目录。命令成功（退出码 0）后，「覆盖目录」会被递归复制到这里（覆盖同名文件；目标目录不存在会自动创建）。\n\n" +
					"对部署型项目（如 IIS 站点）通常指向 IIS 应用目录，例如 D:\\Sites\\LLMAgentApi。\n\n" +
					"对 uvicorn 模式，目标目录会在服务启动前同步一次。",
					"示例:\n  D:\\Sites\\LLMAgentApi\n  D:\\inetpub\\wwwroot\\plus-api"),

				[BackupFolder] = new ParameterHelp(
					"覆盖目录",
					"命令成功（退出码 0）后会被递归复制到「目标路径」。\n\n" +
					"对于 dotnet publish / build，通常指向 `-o` 输出目录，例如 D:\\Projects\\LLMAgentApi\\out。\n" +
					"对于 uvicorn / PowerShell，指向已经构建好的产物目录。\n\n" +
					"同步时会覆盖同名文件；目标目录中不存在于覆盖目录的文件不会被删除。",
					"dotnet publish 输出示例:\n  D:\\Projects\\LLMAgentApi\\out\n\n" +
					"uvicorn 项目示例（虚拟环境已激活的部署目录）:\n  D:\\Projects\\llm-agent-api\\dist"),

				[IisAppPool] = new ParameterHelp(
					"IIS 应用池",
					"可选的 IIS 应用程序池名称。设置后，dotnet publish / dotnet build 之前会用 %SystemRoot%\\System32\\inetsrv\\appcmd.exe 回收该 AppPool，让 w3wp 释放对目标路径的文件占用，避免覆盖时出现文件被占用的错误。\n\n" +
					"留空表示该项目不跑在 IIS 下，部署流程不会尝试回收任何 AppPool。",
					"示例:\n  LLMAgentApi\n  PlusApiAppPool"),

				[PullLatest] = new ParameterHelp(
					"更新前执行 git pull",
					"勾选后，每次更新会在源路径下执行 `git pull`（非交互模式）。若 git pull 返回非零退出码，会停止该项目的后续步骤并标记失败。\n\n" +
					"适合本地仓库直接作为部署源的情况。如果源路径不是 git 仓库，请勿勾选，否则会报错。",

					"适用场景:\n" +
					"  • 源路径是 git 仓库的工作目录\n" +
					"  • 部署前希望先拉取远端最新代码\n\n" +
					"不适用场景:\n" +
					"  • 源路径是已发布产物目录\n" +
					"  • 需要手动控制何时拉取"),

				[DependsOn] = new ParameterHelp(
					"依赖项目",
					"同一批次（Run selected）中需要先完成的项目。选择后，本次更新会先执行依赖项目（成功后才执行本项目）。\n\n" +
					"依赖项目执行失败、或依赖项目没有一起勾选时，本项目会被跳过并标记为失败。\n\n" +
					"下拉框列出当前所有其他项目；点击「清除」按钮可清除当前依赖。",
					"示例:\n  选择 plus-core 后，勾选 plus-core 和本项目再运行，会先更新 plus-core，成功后再更新本项目。"),

				[LogsDirectory] = new ParameterHelp(
					"日志目录",
					"可选。显式指定该项目运行时产生的日志所在目录；右侧日志面板会 tail 此目录中最新的 .log / .txt 文件。\n\n" +
					"留空时按以下顺序自动发现：\n" +
					"  1) 目标路径的父目录下 Logs / logs / Logs\\<项目名> / logs\\<项目名>\n" +
					"  2) 源路径的父目录下 Logs / logs / Logs\\<项目名> / logs\\<项目名>\n" +
					"  3) 项目名同时尝试原样与 kebab-case（LLMAgentApi → llm-agent-api）\n\n" +
					"如果自动发现不到，可以在右侧详情面板查看实际解析出的目录。",
					"显式指定示例:\n  D:\\Plus\\Logs\\llm-agent-api\n\n" +
					"留空 + 自动发现示例（取决于源路径 / 目标路径 / 项目名）:\n  D:\\Projects\\LLMAgentApi\\logs"),
			};

		public static ParameterHelp Get(string key)
		{
			return Entries.TryGetValue(key, out var entry)
				? entry
				: new ParameterHelp("Unknown", "没有找到对应的说明。", string.Empty);
		}

		// Clickable Command examples shown inside the Command help popup. Grouped by
		// deployment mode and prefixed so users can pick the right one at a glance.
		// Mirrors CommandExampleCatalog so the inline auto-fill and the help popup
		// share the same source of truth.
		internal static class CommandExampleRows
		{
			public static readonly IReadOnlyList<HelpExample> All = new[]
			{
				new HelpExample(
					"[publish] publish -c Release（LLMAgentApi 当前用法）",
					"publish -c Release -o \"D:\\Plus\\LLMAgentApi\""),
				new HelpExample(
					"[publish] publish -c Release -p:Platform=\"Any CPU\"（plus-api 当前用法）",
					"publish -c Release -p:Platform=\"Any CPU\" -o \"D:\\Plus\\Api-Publish\""),
				new HelpExample(
					"[publish] 最小可用",
					"publish -c Release -o out"),
				new HelpExample(
					"[build] build -c Release（plus-core 当前用法）",
					"build -c Release"),
				new HelpExample(
					"[uvicorn] PyAgents 当前用法",
					".venv\\Scripts\\python -m uvicorn hosting.main:app --host 0.0.0.0 --port 8000"),
				new HelpExample(
					"[uvicorn] 最简 main:app",
					"main:app --host 0.0.0.0 --port 8000"),
				new HelpExample(
					"[uvicorn] 开发模式（带 --reload）",
					"main:app --host 0.0.0.0 --port 8000 --reload"),
				new HelpExample(
					"[PowerShell] .\\build_release.ps1（ChatWidget 当前用法）",
					".\\build_release.ps1"),
				new HelpExample(
					"[PowerShell] 打包 publish 目录为 zip",
					"Compress-Archive -Path .\\publish -DestinationPath .\\dist\\api.zip -Force"),
			};
		}
	}
}
