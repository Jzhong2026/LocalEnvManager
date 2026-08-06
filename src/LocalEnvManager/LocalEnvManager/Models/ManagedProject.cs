using Caliburn.Micro;
using LocalEnvManager.Models;
using LocalEnvManager.Services;
using System.Windows.Media;

namespace LocalEnvManager.Models
{
	public enum ExecutionState
	{
		Idle,
		Running,
		Success,
		Failed
	}

	public sealed class ManagedProject : PropertyChangedBase
	{
		private string _name = string.Empty;
		private string _sourcePath = string.Empty;
		private string _command = string.Empty;
		private DeploymentMode _deploymentMode;
		private bool _isSelected;
		private string _lastExecutionMessage = "Not started";
		private string _targetPath = string.Empty;
		private string _backupFolder = string.Empty;
		private string _iisAppPoolName = string.Empty;
		private ExecutionState _lastRunState = ExecutionState.Idle;
		private bool _isChecked = true;
		private bool _pullLatest;
		private string _currentBranch = string.Empty;
		private bool _isBranchLoading;
		private string _latestLogSnippet = string.Empty;
	private bool _hasLatestError;
	private string _logsDirectory = string.Empty;
	private string _dependsOn = string.Empty;

	public string Name
		{
			get => _name;
			set => Set(ref _name, value);
		}

		// Working directory / project source folder. May be relative to the category base path.
		public string SourcePath
		{
			get => _sourcePath;
			set
			{
				if (Set(ref _sourcePath, value))
					NotifyDerivedChanged();
			}
		}

		// Command arguments for dotnet publish / dotnet build / uvicorn (e.g. "publish -c Release -o out").
		public string Command
		{
			get => _command;
			set
			{
				if (Set(ref _command, value))
					NotifyDerivedChanged();
			}
		}

		// PowerShell now uses the shared Command field instead of a separate script path.

		public DeploymentMode DeploymentMode
		{
			get => _deploymentMode;
			set
			{
				if (!Set(ref _deploymentMode, value))
				{
					return;
				}

				NotifyDerivedChanged();
			}
		}

		// Destination directory that gets overwritten by the backup folder after a successful build.
		public string TargetPath
		{
			get => _targetPath;
			set
			{
				if (Set(ref _targetPath, value))
					NotifyDerivedChanged();
			}
		}

		// Source folder whose files replace the target path after a successful build.
		public string BackupFolder
		{
			get => _backupFolder;
			set
			{
				if (Set(ref _backupFolder, value))
					NotifyDerivedChanged();
			}
		}

	// Explicit override for the runtime log directory. When set, the right-pane
	// live log viewer tails this directory directly. When empty, the watcher
	// auto-discovers the directory by trying a list of conventional locations
	// based on SourcePath / TargetPath / project name. Blank by default so
	// existing configurations keep working without re-editing every project.
	public string LogsDirectory
	{
		get => _logsDirectory;
		set
		{
			if (Set(ref _logsDirectory, value ?? string.Empty))
				NotifyDerivedChanged();
		}
	}

	// Comma-separated project names that must finish successfully before this
	// project runs when they are selected in the same update.
	public string DependsOn
	{
		get => _dependsOn;
		set
		{
			if (Set(ref _dependsOn, value ?? string.Empty))
				NotifyOfPropertyChange(nameof(DependsOnDisplay));
		}
	}

	public string DependsOnDisplay =>
		string.IsNullOrWhiteSpace(DependsOn) ? "\u2014" : DependsOn;

	// Optional IIS AppPool name to recycle before publishing into TargetPath.
	// When set, the publish pipeline will ask IIS to recycle this pool (via
	// appcmd) so w3wp workers release their file handles on the output
	// directory. Leave blank for projects that don't run under IIS.
	public string IisAppPoolName
	{
		get => _iisAppPoolName;
		set
		{
			if (Set(ref _iisAppPoolName, value ?? string.Empty))
			{
				NotifyDerivedChanged();
				NotifyOfPropertyChange(nameof(IisAppPoolNameDisplay));
			}
		}
	}

	public string IisAppPoolNameDisplay =>
		string.IsNullOrWhiteSpace(IisAppPoolName) ? "\u2014" : IisAppPoolName;

	// Raises PropertyChanged for every derived property the UI binds to,
	// so edits made in EditProjectDialog are reflected in the main view immediately.
	private void NotifyDerivedChanged()
	{
		NotifyOfPropertyChange(nameof(DeploymentModeDisplay));
		NotifyOfPropertyChange(nameof(DeploymentModeIconKind));
		NotifyOfPropertyChange(nameof(DeploymentModeShortLabel));
		NotifyOfPropertyChange(nameof(ProjectPathDisplay));
		NotifyOfPropertyChange(nameof(CommandDetailLabel));
		NotifyOfPropertyChange(nameof(CommandDetailValue));
		NotifyOfPropertyChange(nameof(BackupFolderDisplay));
		NotifyOfPropertyChange(nameof(TargetPathDisplay));
		NotifyOfPropertyChange(nameof(LogsDirectoryDisplay));
		NotifyOfPropertyChange(nameof(ResolvedLogsDirectoryDisplay));
		NotifyOfPropertyChange(nameof(SummaryLine));
		NotifyOfPropertyChange(nameof(DisplayInfo));
	}

		public bool IsSelected
		{
			get => _isSelected;
			set => Set(ref _isSelected, value);
		}

		public string LastExecutionMessage
		{
			get => _lastExecutionMessage;
			set => Set(ref _lastExecutionMessage, value);
		}

		public ExecutionState LastRunState
		{
			get => _lastRunState;
			set
			{
			if (Set(ref _lastRunState, value))
			{
				NotifyOfPropertyChange(nameof(LastRunStateDisplay));
				NotifyOfPropertyChange(nameof(UpdateStateDisplay));
				// These values are derived from LastRunState. Notify them explicitly
				// so bindings re-enable the controls when a run finishes.
				NotifyOfPropertyChange(nameof(IsRunEnabled));
				NotifyOfPropertyChange(nameof(IsCheckEnabled));
			}
			}
		}

		public string LastRunStateDisplay => LastRunState switch
		{
			ExecutionState.Running => "运行中",
			ExecutionState.Success => "成功",
			ExecutionState.Failed => "失败",
			_ => "未运行"
		};

		public string UpdateStateDisplay => LastRunState switch
		{
			ExecutionState.Idle => "\u672a\u8fd0\u884c",
			ExecutionState.Running => "\u66f4\u65b0\u4e2d",
			ExecutionState.Success => "\u6210\u529f",
			ExecutionState.Failed => "\u5931\u8d25",
			_ => "\u672a\u8fd0\u884c"
		};

		// Whether the per-row "Run" button is currently enabled. Disabled while a run is in progress.
		public bool IsRunEnabled => LastRunState != ExecutionState.Running;

		// ===== Execution-tab checkbox =====
		// True when the user wants this project to run as part of "Run selected".
		public bool IsChecked
		{
			get => _isChecked;
			set => Set(ref _isChecked, value);
		}

		// Whether the user can toggle the checkbox right now. Disabled while this project is running
		// so an in-flight run isn't silently re-queued or skipped mid-flight.
		public bool IsCheckEnabled => LastRunState != ExecutionState.Running;

		// Whether this project should do `git pull` before building. Persisted per-project.
	public bool PullLatest
	{
		get => _pullLatest;
		set
		{
			if (Set(ref _pullLatest, value))
				NotifyOfPropertyChange(nameof(PullLatestDisplay));
		}
	}

	public string PullLatestDisplay => PullLatest ? "\u5df2\u542f\u7528" : "\u672a\u542f\u7528";

		// ===== Current git branch (loaded lazily on the Execute tab) =====
		public string CurrentBranch
		{
			get => _currentBranch;
			set
			{
				if (Set(ref _currentBranch, value))
					NotifyOfPropertyChange(nameof(BranchDisplay));
			}
		}

		public bool IsBranchLoading
		{
			get => _isBranchLoading;
			set
			{
				if (Set(ref _isBranchLoading, value))
					NotifyOfPropertyChange(nameof(BranchDisplay));
			}
		}

		public string BranchDisplay
		{
			get
			{
				if (IsBranchLoading) return "加载中…";
				if (string.IsNullOrWhiteSpace(CurrentBranch)) return "—";
				return CurrentBranch;
			}
		}

		// ===== Latest execution log snippet shown on the Execute tab row =====
		// A short status line for the most recent step (pull / build / sync) so the user can
		// see what's happening without expanding the per-project log tab.
		public string LatestLogSnippet
		{
			get => _latestLogSnippet;
			set
			{
				if (Set(ref _latestLogSnippet, value))
					NotifyOfPropertyChange(nameof(HasLatestLog));
			}
		}

		public bool HasLatestError
		{
			get => _hasLatestError;
			set => Set(ref _hasLatestError, value);
		}

		public bool HasLatestLog => !string.IsNullOrWhiteSpace(LatestLogSnippet);

		// One-line summary shown in the collapsed expander header: command -> target path.
		public string SummaryLine
		{
			get
			{
				var command = CommandDetailValue;
				var target = TargetPathDisplay;
				var hasCommand = !string.IsNullOrWhiteSpace(command) && command != "\u2014";
				var hasTarget = !string.IsNullOrWhiteSpace(target) && target != "\u2014";

				if (hasCommand && hasTarget)
				{
					return $"{command}  \u279C  {target}";
				}

				if (hasCommand)
				{
					return command;
				}

				if (hasTarget)
				{
					return $"\u279C  {target}";
				}

				return string.IsNullOrWhiteSpace(DisplayInfo) ? "未配置部署信息" : DisplayInfo;
			}
		}

		public bool UsesCommand =>
			DeploymentMode is DeploymentMode.DotNetPublish
			or DeploymentMode.DotNetBuild
			or DeploymentMode.PowerShellCommand
			or DeploymentMode.Uvicorn;

		public string DeploymentModeDisplay => DeploymentMode switch
		{
			DeploymentMode.DotNetPublish => "dotnet publish",
			DeploymentMode.DotNetBuild => "dotnet build",
			DeploymentMode.PowerShellCommand => "PowerShell",
			DeploymentMode.Uvicorn => "uvicorn",
			_ => DeploymentMode.ToString()
		};

        // Material Design icon Kind for the deployment mode. Used in the UI to make the
        // mode tag instantly recognizable without mixing plain text with the command line.
        public string DeploymentModeIconKind => DeploymentMode switch
        {
            DeploymentMode.DotNetPublish => "PackageVariantClosed",
            DeploymentMode.DotNetBuild => "Hammer",
            DeploymentMode.PowerShellCommand => "Console",
            DeploymentMode.Uvicorn => "RocketLaunchOutline",
            _ => "CogOutline"
        };

        // A short single-word label for the mode, used when an icon is shown alongside it.
        public string DeploymentModeShortLabel => DeploymentMode switch
        {
            DeploymentMode.DotNetPublish => "dotnet",
            DeploymentMode.DotNetBuild => "build",
            DeploymentMode.PowerShellCommand => "PowerShell",
            DeploymentMode.Uvicorn => "uvicorn",
            _ => DeploymentMode.ToString()
        };

        // Solid colour used to tint the project avatar in the left list. One accent per mode
        // so the icon tile looks like a coloured badge (matches the example screenshot).
        public Brush DeploymentModeAccentBrush => DeploymentMode switch
        {
            DeploymentMode.DotNetPublish => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), // blue
            DeploymentMode.DotNetBuild => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), // amber
            DeploymentMode.PowerShellCommand => new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1)), // indigo
            DeploymentMode.Uvicorn => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)), // emerald
            _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)) // slate
        };

        public string ProjectPathDisplay =>
            string.IsNullOrWhiteSpace(SourcePath) ? "\u2014" : SourcePath;

        public string CommandDetailLabel => "Command";

        public string CommandDetailValue =>
            string.IsNullOrWhiteSpace(Command) ? "\u2014" : Command;

		public string BackupFolderDisplay =>
			string.IsNullOrWhiteSpace(BackupFolder) ? "\u2014" : BackupFolder;

		public string TargetPathDisplay =>
			string.IsNullOrWhiteSpace(TargetPath) ? "\u2014" : TargetPath;

	public string LogsDirectoryDisplay =>
		string.IsNullOrWhiteSpace(LogsDirectory) ? "\u2014（自动发现）" : LogsDirectory;

		public string ResolvedLogsDirectoryDisplay =>
			ProjectLogsWatcher.ResolveLogsDirectory(this) ?? "\u2014 (not found)";

		public string DisplayInfo
		{
			get
			{
				var path = string.IsNullOrEmpty(SourcePath) ? "" : $"\\{SourcePath}";
				var info = DeploymentMode switch
				{
					DeploymentMode.Uvicorn => path,
					DeploymentMode.PowerShellCommand => $"{path}  {Command}",
					_ => path
				};
				if (!string.IsNullOrEmpty(BackupFolder))
					info += $"  \u279C {BackupFolder}";
				return info;
			}
		}
	}
}
