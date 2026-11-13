using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Caliburn.Micro;
using LocalEnvManager.Mcp.Contracts;
using LocalEnvManager.Messages;
using LocalEnvManager.Models;
using LocalEnvManager.Services;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// Owns the "执行更新" (run updates) tab. Three responsibilities:
    /// <list type="bullet">
    /// <item>The left-side project list (table with checkboxes, branch column,
    ///       state column) and the bottom Run/Cancel bar.</item>
    /// <item>The right-side live log panes — one <see cref="IProjectLiveLogPane"/>
    ///       per project, only the pane matching the selected project is shown.</item>
    /// <item>The maximize / restore toggle that hides the left list so the right
    ///       log fills the full tab width. Driven by a published message so the
    ///       button can sit anywhere on the right pane.</item>
    /// </list>
    ///
    /// Communication: in for data (the catalog singleton), out for messages
    /// (toggle maximize, run/cancel/refresh, project-selection). The view-model
    /// never references <see cref="ManageViewModel"/> or <see cref="RunLogsViewModel"/>.
    /// </summary>
    public sealed class ExecuteViewModel : Screen, IPageViewModel,
        IHandle<RefreshBranchesMessage>,
        IHandle<RunSelectedMessage>,
        IHandle<CancelRunMessage>,
        IHandle<ToggleSelectAllMessage>,
        IHandle<ToggleExecuteDetailMaximizeMessage>,
        IHandle<ProjectsChangedMessage>,
        IHandle<ProjectSelectedDetailMessage>,
        IHandle<ConsoleLineMessage>,
        IHandle<ProjectRunStartedMessage>,
        IHandle<RunProjectRequestMessage>,
        IHandle<StopProjectRequestMessage>
    {
        private readonly IProjectCatalog _catalog;
        private readonly IProjectUpdateRunner _runner;
        private readonly IGitBranchService _git;
        private readonly IRuntimeLogger _logger;
        private readonly IEventAggregator _events;
        private readonly ILiveLogViewModelFactory _liveLogFactory;
        private readonly IUiSettingsStore _uiSettings;
        private readonly Dictionary<int, List<ConsoleLineMessage>> _consoleBacklog = new();
        private readonly System.Windows.Threading.DispatcherTimer _clockTimer;

        private GridLength _modeColumnWidth = new(80);
        private GridLength _branchColumnWidth = new(260);
        private GridLength _pullColumnWidth = new(56);
        private GridLength _stateColumnWidth = new(80);
        private GridLength _timeColumnWidth = new(110);

        private static readonly Dictionary<string, double> DefaultColumnWidths = new()
        {
            ["mode"] = 80,
            ["branch"] = 260,
            ["pull"] = 56,
            ["state"] = 80,
            ["time"] = 110
        };

        private ManagedProject? _selectedProject;
        private bool _isRefreshingBranches;
        private bool _isRunning;
        private CancellationTokenSource? _runCts;
        private bool _isDetailMaximized;
        private bool _isActive;

        public ExecuteViewModel(
            IProjectCatalog catalog,
            IProjectUpdateRunner runner,
            IGitBranchService git,
            IRuntimeLogger logger,
            IEventAggregator events,
            ILiveLogViewModelFactory liveLogFactory,
            IUiSettingsStore uiSettings)
        {
            _catalog = catalog;
            _runner = runner;
            _git = git;
            _logger = logger;
            _events = events;
            _liveLogFactory = liveLogFactory;
            _uiSettings = uiSettings;

            var savedWidths = _uiSettings.Load().ExecuteColumnWidths;
            if (savedWidths.TryGetValue("mode", out var mode)) _modeColumnWidth = new GridLength(ClampWidth(mode));
            if (savedWidths.TryGetValue("branch", out var branch)) _branchColumnWidth = new GridLength(ClampWidth(branch));
            if (savedWidths.TryGetValue("pull", out var pull)) _pullColumnWidth = new GridLength(ClampWidth(pull));
            if (savedWidths.TryGetValue("state", out var state)) _stateColumnWidth = new GridLength(ClampWidth(state));
            if (savedWidths.TryGetValue("time", out var time)) _timeColumnWidth = new GridLength(ClampWidth(time));

            // Drives the live elapsed readout on running rows and keeps relative
            // finish times ("3 分钟前") from going stale while the tab is open.
            _clockTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += (_, _) => RefreshTimeDisplays();
            _clockTimer.Start();

            LiveLogPanes = new Dictionary<int, IProjectLiveLogPane>();
            _events.SubscribeOnUI(this);

            _catalog.Projects.CollectionChanged += (_, _) =>
            {
                NotifyOfPropertyChange(nameof(HasProjects));
                NotifyOfPropertyChange(nameof(SelectedCount));
                NotifyOfPropertyChange(nameof(CanRunSelected));
                NotifyOfPropertyChange(nameof(IsAllSelected));
                NotifyOfPropertyChange(nameof(UpdateSummary));
            };
            // Per-project state changes also drive UpdateSummary (Running/Success/Failed counts).
            foreach (var project in _catalog.Projects)
            {
                project.PropertyChanged += OnExecutionProjectChanged;
            }
        }

        // ===== IPageViewModel =====
        public new string DisplayName { get; } = "执行更新";
        public int Order { get; } = 1;
        public new bool IsActive
        {
            get => _isActive;
            set => Set(ref _isActive, value);
        }

        public void OnActivated()
        {
            // Refresh branch info the moment the user lands on the tab so they
            // can sanity-check they aren't about to deploy from the wrong branch.
            _ = RefreshBranchesAsync();
        }

        // ===== View-facing commands (called via Caliburn Message.Attach) =====
        public void RefreshBranches() => _events.PublishOnCurrentThread(new RefreshBranchesMessage());
        public void RunSelected() => _events.PublishOnCurrentThread(new RunSelectedMessage());
        public void Cancel() => _events.PublishOnCurrentThread(new CancelRunMessage());
        public void ToggleSelectAll() => _events.PublishOnCurrentThread(new ToggleSelectAllMessage());
        public void PublishMaximizeToggle() => _events.PublishOnCurrentThread(new ToggleExecuteDetailMaximizeMessage());
        public void SelectProject(ManagedProject project) => SelectedProject = project;

        // ===== Bound properties (Execute tab) =====
        public BindableCollection<ManagedProject> Projects => _catalog.Projects;

        public bool HasProjects => _catalog.Projects.Count > 0;

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (Set(ref _isRunning, value))
                {
                    NotifyOfPropertyChange(nameof(CanRunSelected));
                }
            }
        }

        public bool IsRefreshingBranches
        {
            get => _isRefreshingBranches;
            private set => Set(ref _isRefreshingBranches, value);
        }

        public bool IsDetailMaximized
        {
            get => _isDetailMaximized;
            private set
            {
                if (Set(ref _isDetailMaximized, value))
                {
                    NotifyOfPropertyChange(nameof(LeftColumnWidth));
                    NotifyOfPropertyChange(nameof(RightColumnWidth));
                    NotifyOfPropertyChange(nameof(MaximizeIconKind));
                    NotifyOfPropertyChange(nameof(MaximizeButtonLabel));
                }
            }
        }

        /// <summary>MaterialDesign pack-icon Kind for the maximize / restore toggle button.</summary>
        public string MaximizeIconKind => IsDetailMaximized ? "WindowRestore" : "WindowMaximize";

        /// <summary>Button label that flips with the icon so the user always knows what clicking it does.</summary>
        public string MaximizeButtonLabel => IsDetailMaximized ? "还原" : "放大";

        /// <summary>GridLength string for the left project-list column.</summary>
        public string LeftColumnWidth => IsDetailMaximized ? "0" : "5*";

        /// <summary>GridLength string for the right detail column.</summary>
        public string RightColumnWidth => IsDetailMaximized ? "*" : "4*";

        // ===== Resizable table columns (Execute tab) =====
        // Header and every row bind to these same GridLength values so a drag in
        // the header resizes the whole column live. The drag math/clamp lives in
        // ColumnResizeBehavior; the VM only owns the values and their persistence.
        public GridLength ModeColumnWidth { get => _modeColumnWidth; set => Set(ref _modeColumnWidth, value); }
        public GridLength BranchColumnWidth { get => _branchColumnWidth; set => Set(ref _branchColumnWidth, value); }
        public GridLength PullColumnWidth { get => _pullColumnWidth; set => Set(ref _pullColumnWidth, value); }
        public GridLength StateColumnWidth { get => _stateColumnWidth; set => Set(ref _stateColumnWidth, value); }
        public GridLength TimeColumnWidth { get => _timeColumnWidth; set => Set(ref _timeColumnWidth, value); }

        private static double ClampWidth(double width) => Math.Clamp(width, 40, 800);

        /// <summary>Double-click on a column grip restores that column's default width.</summary>
        public void ResetColumn(string key)
        {
            if (!DefaultColumnWidths.TryGetValue(key, out var def)) return;
            switch (key)
            {
                case "mode": ModeColumnWidth = new GridLength(def); break;
                case "branch": BranchColumnWidth = new GridLength(def); break;
                case "pull": PullColumnWidth = new GridLength(def); break;
                case "state": StateColumnWidth = new GridLength(def); break;
                case "time": TimeColumnWidth = new GridLength(def); break;
            }
            SaveColumnWidths();
        }

        /// <summary>Invoked by the grip's DragCompleted (Caliburn Message.Attach).</summary>
        public void SaveColumnWidths()
        {
            try
            {
                _uiSettings.Save(new UiSettings
                {
                    ExecuteColumnWidths = new Dictionary<string, double>
                    {
                        ["mode"] = _modeColumnWidth.Value,
                        ["branch"] = _branchColumnWidth.Value,
                        ["pull"] = _pullColumnWidth.Value,
                        ["state"] = _stateColumnWidth.Value,
                        ["time"] = _timeColumnWidth.Value
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Append(LogEntry.LogLevel.Warn, $"保存列宽失败：{ex.Message}");
            }
        }

        private void RefreshTimeDisplays()
        {
            foreach (var project in _catalog.Projects)
            {
                project.NotifyOfPropertyChange(nameof(ManagedProject.TimeColumnDisplay));
                project.NotifyOfPropertyChange(nameof(ManagedProject.TimeColumnTooltip));
            }
        }

        public int SelectedCount => _catalog.Projects.Count(p => p.IsChecked);

        public bool CanRunSelected => !IsRunning && SelectedCount > 0;

        public bool IsAllSelected => _catalog.Projects.Count > 0
            && _catalog.Projects.All(p => p.IsChecked);

        public string UpdateSummary
        {
            get
            {
                var total = _catalog.Projects.Count;
                if (total == 0) return "暂无项目";
                var running = _catalog.Projects.Count(p => p.LastRunState == ExecutionState.Running);
                var success = _catalog.Projects.Count(p => p.LastRunState == ExecutionState.Success);
                var failed = _catalog.Projects.Count(p => p.LastRunState == ExecutionState.Failed);
                var idle = _catalog.Projects.Count(p => p.LastRunState == ExecutionState.Idle);
                return $"共 {total} 项 · 更新中 {running} · 成功 {success} · 失败 {failed} · 未运行 {idle}";
            }
        }

        public ManagedProject? SelectedProject
        {
            get => _selectedProject;
            set
            {
                if (Set(ref _selectedProject, value))
                {
                    // Mirror to ManagedProject.IsSelected so the row DataTrigger
                    // (ExecuteProjectRowStyle) actually highlights the row the user
                    // clicked. Without this, switching from Manage tab → Execute
                    // tab leaves the previously-selected project's highlight
                    // stuck on, and clicking a new row never lights up.
                    foreach (var project in _catalog.Projects)
                    {
                        var isSelected = project == value;
                        if (project.IsSelected != isSelected)
                        {
                            project.IsSelected = isSelected;
                        }
                    }

                    if (value is not null)
                    {
                        EnsurePaneFor(value);
                        _events.PublishOnCurrentThread(new ProjectSelectedDetailMessage(
                            _catalog.GetProjectId(value), value));
                    }
                    else
                    {
                        _events.PublishOnCurrentThread(new ProjectSelectedDetailMessage(0, null!));
                    }
                    NotifyOfPropertyChange(nameof(ActiveLiveLog));
                }
            }
        }

        public ILiveLogViewModel? ActiveLiveLog =>
            _selectedProject is null
                ? null
                : LiveLogPanes.TryGetValue(_catalog.GetProjectId(_selectedProject), out var pane)
                    ? pane.ViewModel
                    : null;

        public Dictionary<int, IProjectLiveLogPane> LiveLogPanes { get; }

        private void EnsurePaneFor(ManagedProject project)
        {
            var id = _catalog.GetProjectId(project);
            if (LiveLogPanes.ContainsKey(id)) return;

            var vm = _liveLogFactory.Create(id, project.DeploymentMode, () => project);
            LiveLogPanes[id] = new ProjectLiveLogPane(id, vm);

            // File-backed panes need to start their watcher as soon as the
            // project is selected. Console panes subscribe in their constructor.
            if (vm is LogFileLiveLogViewModel fileLog)
            {
                fileLog.Initialize();
            }

            // Seed the new pane with any backlog that already accumulated while
            // the user was on a different tab.
            if (vm is ConsoleLiveLogViewModel console && _consoleBacklog.Remove(id, out var backlog))
            {
                console.Replay(backlog);
            }
        }

        private sealed class ProjectLiveLogPane : IProjectLiveLogPane
        {
            public ProjectLiveLogPane(int projectId, ILiveLogViewModel viewModel)
            {
                ProjectId = projectId;
                ViewModel = viewModel;
            }
            public int ProjectId { get; }
            public ILiveLogViewModel? ViewModel { get; }
        }

        // ===== Run pipeline =====

        public async Task RefreshBranchesAsync()
        {
            if (IsRefreshingBranches) return;
            IsRefreshingBranches = true;
            try
            {
                var snapshot = _catalog.Projects.ToList();
                foreach (var project in snapshot)
                {
                    project.IsBranchLoading = true;
                }
                foreach (var project in snapshot)
                {
                    if (project.IsRunEnabled is false)
                    {
                        project.IsBranchLoading = false;
                        continue;
                    }
                    try
                    {
                        var branch = await _git.GetCurrentBranchAsync(project.SourcePath);
                        project.CurrentBranch = branch;
                    }
                    catch (Exception ex)
                    {
                        _logger.Append(LogEntry.LogLevel.Warn, $"无法读取 {project.Name} 的分支信息 {ex.Message}");
                        project.CurrentBranch = string.Empty;
                    }
                    finally
                    {
                        project.IsBranchLoading = false;
                    }
                }
            }
            finally
            {
                IsRefreshingBranches = false;
            }
        }

        public void CancelRun()
        {
            if (!IsRunning || _runCts is null) return;
            try { _runCts.Cancel(); } catch { }
            _logger.Append(LogEntry.LogLevel.Warn, "用户取消了当前运行。");
        }

        // Per-project Stop. Triggers IProjectUpdateRunner.StopAsync for the named
        // project — kills the launcher + worker for uvicorn, cancels the linked
        // CTS for PowerShell. The pipeline then unwinds with a ProjectRunFinished
        // message whose body says "已停止"; the pipeline handler treats that as a
        // soft cancellation rather than a failure (state stays Idle, no error tint).
        //
        // Synchronous void wrapper: Caliburn.Micro's Message.Attach invokes the
        // action via reflection and silently drops the Task returned from an
        // async method (any exception inside is swallowed). Wrapping the async
        // body in a void method that fire-and-forgets `_ = StopProjectCore(...)`
        // keeps the call site simple AND guarantees exceptions surface in the
        // runtime log instead of vanishing.
        public void StopProject(ManagedProject project)
        {
            if (project is null) return;
            _logger.Append(LogEntry.LogLevel.Info,
                $"[Stop] 点击按钮: project={project.Name} LastRunState={project.LastRunState} HasRunningProcess={project.HasRunningProcess} DeploymentMode={project.DeploymentMode}");
            _ = StopProjectCoreAsync(project);
        }

        private async Task<bool> StopProjectCoreAsync(ManagedProject project)
        {
            // For uvicorn the runner keeps a Process reference in s_uvicornProcesses
            // until the user (or relaunch) kills it, so we must allow Stop after
            // the pipeline has flipped state to Success. The runner is the source
            // of truth: if it can't find a process to kill, StopAsync returns
            // false and we do nothing.
            // For PowerShell we only allow Stop while Running (the script exits
            // on its own on completion, so a Success/Failed Stop would be a no-op).
            var allowWhenNotRunning = project.DeploymentMode == DeploymentMode.Uvicorn;
            if (!allowWhenNotRunning && project.LastRunState != ExecutionState.Running) return false;

            // Optimistic UI: hide the Stop button the instant the kill command is
            // dispatched so a frantic user can't double-click and re-signal the
            // same process tree. The pipeline's own state update happens later,
            // after Process.Kill completes and the cancellation propagates back.
            project.HasRunningProcess = false;

            try
            {
                var sink = new Progress<string>(line =>
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _events.PublishOnCurrentThread(new ConsoleLineMessage(
                            _catalog.GetProjectId(project), line));
                    }
                });
                var stopped = await _runner.StopAsync(project, sink);
                if (stopped)
                {
                    // A uvicorn service that had already bound its port is no longer
                    // "Successful" once the user stops it. Reflect that immediately;
                    // a stop during startup is handled by the pipeline's Cancelled path.
                    if (project.DeploymentMode == DeploymentMode.Uvicorn
                        && project.LastRunState == ExecutionState.Success)
                    {
                        project.LastRunState = ExecutionState.Idle;
                        project.LastExecutionMessage = "已停止（用户停止服务）。";
                        project.LatestLogSnippet = "已停止";
                        project.HasLatestError = false;
                    }
                    _logger.Append(LogEntry.LogLevel.Warn,
                        $"[{project.Name}] 用户请求停止进程。");
                }
                else if (project.DeploymentMode == DeploymentMode.Uvicorn)
                {
                    _logger.Append(LogEntry.LogLevel.Warn,
                        $"[{project.Name}] 未找到运行中的 uvicorn 进程,可能已退出。");
                }

                return stopped;
            }
            catch (Exception ex)
            {
                _logger.Append(LogEntry.LogLevel.Error,
                    $"[{project.Name}] 停止失败：{ex.Message}");
                return false;
            }
        }

        public void SelectAll()
        {
            if (IsRunning) return;
            foreach (var project in _catalog.Projects)
            {
                if (project.IsCheckEnabled && !project.IsChecked)
                {
                    project.IsChecked = true;
                }
            }
        }

        public void DeselectAll()
        {
            if (IsRunning) return;
            foreach (var project in _catalog.Projects)
            {
                if (project.IsCheckEnabled && project.IsChecked)
                {
                    project.IsChecked = false;
                }
            }
        }

        public async Task RunSelectedAsync()
        {
            if (IsRunning) return;
            var targets = _catalog.Projects.Where(p => p.IsChecked).ToList();
            if (targets.Count == 0) return;

            MarkStaleExcept(targets);
            IsRunning = true;
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            try
            {
                await RunProjectsInDependencyOrderAsync(targets, _runCts.Token);
                // Surface the first failure immediately so the user lands on the
                // error log instead of hunting for the red row.
                var firstFailed = targets.FirstOrDefault(p => p.LastRunState == ExecutionState.Failed);
                if (firstFailed is not null) SelectedProject = firstFailed;
            }
            catch (OperationCanceledException) { /* expected on cancel */ }
            finally
            {
                IsRunning = false;
                _runCts?.Dispose();
                _runCts = null;
            }
        }

        // Results left over from a previous batch (or restored from disk) must not
        // read as fresh once a new run starts. Dim every non-target terminal result.
        private void MarkStaleExcept(IEnumerable<ManagedProject> targets)
        {
            var set = new HashSet<ManagedProject>(targets);
            foreach (var project in _catalog.Projects)
            {
                if (set.Contains(project)) continue;
                if (project.LastRunState is ExecutionState.Success or ExecutionState.Failed)
                    project.IsResultStale = true;
            }
        }

        private async Task RunProjectsInDependencyOrderAsync(
            IReadOnlyList<ManagedProject> targets,
            CancellationToken cancellationToken)
        {
            var allByName = _catalog.Projects
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var selectedByName = targets
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var deps = new Dictionary<ManagedProject, List<ManagedProject>>();
            var blockedReasons = new Dictionary<ManagedProject, string>();
            foreach (var project in targets)
            {
                var list = new List<ManagedProject>();
                foreach (var name in GetDependencyNames(project))
                {
                    if (!allByName.TryGetValue(name, out var dep))
                    {
                        _logger.Append(LogEntry.LogLevel.Warn,
                            $"[{project.Name}] 依赖项目 \"{name}\" 不存在。");
                        continue;
                    }
                    if (!selectedByName.TryGetValue(name, out var sel))
                    {
                        // Do not run a project whose declared dependency was left
                        // unchecked — that silently breaks the ordering contract the
                        // help text promises. Block it and tell the user which box
                        // to tick.
                        blockedReasons[project] =
                            $"依赖项目 {dep.Name} 未勾选，已跳过执行。请勾选依赖项目后重试。";
                        _logger.Append(LogEntry.LogLevel.Warn,
                            $"[{project.Name}] {blockedReasons[project]}");
                        continue;
                    }
                    list.Add(sel);
                }
                deps[project] = list;
            }

            var remaining = new HashSet<ManagedProject>(targets);
            var completed = new HashSet<ManagedProject>();
            var running = new Dictionary<Task, ManagedProject>();

            foreach (var (project, reason) in blockedReasons)
            {
                remaining.Remove(project);
                project.LastRunState = ExecutionState.Failed;
                project.LastExecutionMessage = reason;
                project.LatestLogSnippet = reason;
                project.HasLatestError = true;
                project.LastRunFinishedAt = DateTime.Now;
                project.IsResultStale = false;
                completed.Add(project);
            }

            while (remaining.Count > 0 || running.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // Anything still queued never started. Mark it as cancelled
                    // (Idle) rather than leaving a stale Running/Failed state.
                    foreach (var project in remaining)
                    {
                        project.LastRunState = ExecutionState.Idle;
                        project.LastExecutionMessage = "已取消（未执行）。";
                        project.LatestLogSnippet = "已取消";
                        project.HasLatestError = false;
                        project.LastRunFinishedAt = DateTime.Now;
                        project.IsResultStale = false;
                    }
                    try { await Task.WhenAll(running.Keys); } catch { }
                    return;
                }

                var ready = remaining
                    .Where(p => deps[p].All(completed.Contains))
                    .ToList();

                foreach (var project in ready)
                {
                    remaining.Remove(project);

                    var failedDep = deps[project]
                        .FirstOrDefault(d => d.LastRunState != ExecutionState.Success);
                    if (failedDep is not null)
                    {
                        project.LastRunState = ExecutionState.Failed;
                        project.LastExecutionMessage = $"依赖 {failedDep.Name} 未成功，已跳过。";
                        project.LatestLogSnippet = project.LastExecutionMessage;
                        project.HasLatestError = true;
                        project.LastRunFinishedAt = DateTime.Now;
                        project.IsResultStale = false;
                        completed.Add(project);
                        continue;
                    }

                    var task = RunProjectPipelineAsync(
                        project,
                        new ProjectExecutionOptions(project.PullLatest),
                        cancellationToken);
                    running.Add(task, project);
                }

                if (running.Count > 0)
                {
                    var finished = await Task.WhenAny(running.Keys);
                    await finished;
                    completed.Add(running[finished]);
                    running.Remove(finished);
                    continue;
                }

                foreach (var project in remaining)
                {
                    project.LastRunState = ExecutionState.Failed;
                    project.LastExecutionMessage = "依赖关系存在循环，未执行更新。";
                    project.LatestLogSnippet = project.LastExecutionMessage;
                    project.HasLatestError = true;
                    project.LastRunFinishedAt = DateTime.Now;
                    project.IsResultStale = false;
                }
                return;
            }
        }

        private static IEnumerable<string> GetDependencyNames(ManagedProject project)
        {
            return (project.DependsOn ?? string.Empty)
                .Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private async Task RunProjectPipelineAsync(
            ManagedProject project,
            ProjectExecutionOptions options,
            CancellationToken cancellationToken)
        {
            var projectId = _catalog.GetProjectId(project);

            // State must change before the first lifecycle message/output so
            // both the Execute row and the Update Log tab immediately show
            // that this project is in progress.
            project.LastRunState = ExecutionState.Running;
            project.LastExecutionMessage = string.Empty;
            project.LatestLogSnippet = string.Empty;
            project.HasLatestError = false;
            project.LastRunStartedAt = DateTime.Now;
            project.IsResultStale = false;
            // HasRunningProcess drives the per-row Stop button visibility. For a
            // uvicorn service, it remains true after the update itself succeeds:
            // the launcher is still a live console process until Stop is clicked
            // (or it exits naturally). PowerShell is stoppable only while its
            // command is still running.
            project.HasRunningProcess =
                project.DeploymentMode is DeploymentMode.Uvicorn
                    or DeploymentMode.PowerShellCommand;

            // Clear the right-pane console buffer so the new run starts on a clean slate.
            _events.PublishOnCurrentThread(new ProjectRunStartedMessage(projectId, project.DeploymentMode));

            var teeSink = new Progress<string>(line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    // The runner tags stderr output with a "|stderr] " channel prefix;
                    // surface that as a channel marker without treating it as an error.
                    var isStderr = line.Contains("|stderr]", StringComparison.OrdinalIgnoreCase);
                    _events.PublishOnCurrentThread(new ConsoleLineMessage(projectId, line, isStderr));
                }
            });
            var progress = new ChainedProgress(_logger.Tee(new Progress<string>(_ => { })), teeSink);

            try
            {
                var result = await _runner.ExecuteAsync(project, options, progress, cancellationToken);
                // A completed PowerShell command no longer has a console process
                // to stop. Uvicorn is different: a successful readiness check
                // means the server process remains alive after this method returns.
                project.HasRunningProcess =
                    project.DeploymentMode == DeploymentMode.Uvicorn && result.Success;
                switch (result.EffectiveOutcome)
                {
                    case ProjectExecutionOutcome.Cancelled:
                        // User-initiated Stop/Cancel is a soft outcome: Idle, no red tint.
                        project.LastRunState = ExecutionState.Idle;
                        project.LastExecutionMessage = result.Message;
                        project.LatestLogSnippet = "已停止";
                        project.HasLatestError = false;
                        break;

                    case ProjectExecutionOutcome.Success:
                        project.LastRunState = ExecutionState.Success;
                        project.LastExecutionMessage = result.Message;
                        project.LatestLogSnippet = "执行完成";
                        project.HasLatestError = false;
                        break;

                    default:
                        project.LastRunState = ExecutionState.Failed;
                        project.LastExecutionMessage = result.Message;
                        project.LatestLogSnippet = $"执行失败：{Truncate(result.Message, 80)}";
                        project.HasLatestError = true;
                        break;
                }
                project.LastRunFinishedAt = DateTime.Now;
                _events.PublishOnCurrentThread(new ProjectRunFinishedMessage(
                    projectId, result.Success, result.Message,
                    result.EffectiveOutcome == ProjectExecutionOutcome.Cancelled));
            }
            catch (OperationCanceledException)
            {
                project.LastRunState = ExecutionState.Idle;
                project.LastExecutionMessage = "已停止（用户取消）。";
                project.LatestLogSnippet = "已停止";
                project.HasLatestError = false;
                project.LastRunFinishedAt = DateTime.Now;
                _events.PublishOnCurrentThread(new ProjectRunFinishedMessage(
                    projectId, false, project.LastExecutionMessage, cancelled: true));
            }
            catch (Exception ex)
            {
                project.LastRunState = ExecutionState.Failed;
                project.LastExecutionMessage = ex.Message;
                project.LatestLogSnippet = "执行异常";
                project.HasLatestError = true;
                project.LastRunFinishedAt = DateTime.Now;
                _events.PublishOnCurrentThread(new ProjectRunFinishedMessage(projectId, false, ex.Message));
            }
        }

        private static string Truncate(string text, int maxLength)
            => string.IsNullOrEmpty(text)
                ? string.Empty
                : text.Length <= maxLength ? text : text[..maxLength] + "…";

        private sealed class ChainedProgress : IProgress<string>
        {
            private readonly IProgress<string> _first;
            private readonly IProgress<string> _second;
            public ChainedProgress(IProgress<string> first, IProgress<string> second)
            {
                _first = first;
                _second = second;
            }
            public void Report(string value)
            {
                _first.Report(value);
                _second.Report(value);
            }
        }

        // ===== IHandle<T> =====
        public Task HandleAsync(RefreshBranchesMessage message, CancellationToken cancellationToken)
            => RefreshBranchesAsync();

        public Task HandleAsync(RunSelectedMessage message, CancellationToken cancellationToken)
            => RunSelectedAsync();

        public Task HandleAsync(CancelRunMessage message, CancellationToken cancellationToken)
        {
            CancelRun();
            return Task.CompletedTask;
        }

        public Task HandleAsync(ToggleSelectAllMessage message, CancellationToken cancellationToken)
        {
            if (!IsRunning)
            {
                if (IsAllSelected) DeselectAll();
                else SelectAll();
            }
            return Task.CompletedTask;
        }

        public Task HandleAsync(ToggleExecuteDetailMaximizeMessage message, CancellationToken cancellationToken)
        {
            IsDetailMaximized = !IsDetailMaximized;
            return Task.CompletedTask;
        }

        public Task HandleAsync(ProjectsChangedMessage message, CancellationToken cancellationToken)
        {
            // Drop pane entries whose backing project has been removed. Dispose the
            // child view-model so its flush timer, EventAggregator subscription and
            // FileSystemWatcher are actually released instead of leaked.
            var liveIds = new HashSet<int>(_catalog.Projects.Select(_catalog.GetProjectId));
            var dead = LiveLogPanes.Keys.Where(id => !liveIds.Contains(id)).ToList();
            foreach (var id in dead)
            {
                if (LiveLogPanes.Remove(id, out var pane))
                {
                    pane.ViewModel?.Dispose();
                }
                _consoleBacklog.Remove(id);
            }
            if (_selectedProject is not null && !_catalog.Projects.Contains(_selectedProject))
            {
                _selectedProject = _catalog.Projects.FirstOrDefault();
                NotifyOfPropertyChange(nameof(SelectedProject));
                NotifyOfPropertyChange(nameof(ActiveLiveLog));
            }
            return Task.CompletedTask;
        }

        public Task HandleAsync(ProjectSelectedDetailMessage message, CancellationToken cancellationToken)
        {
            // External setter (e.g. from ManageViewModel) — ensure a pane exists so
            // that switching back to the Execute tab shows the right project's log.
            var project = message.Project;
            if (project is not null)
            {
                EnsurePaneFor(project);
                NotifyOfPropertyChange(nameof(ActiveLiveLog));
            }
            return Task.CompletedTask;
        }

        public Task HandleAsync(ConsoleLineMessage message, CancellationToken cancellationToken)
        {
            // A pane that already exists receives the line itself. Until one is
            // created, retain output so selecting PyAgents after an update can
            // replay what its process already wrote.
            if (LiveLogPanes.TryGetValue(message.ProjectId, out var pane)
                && pane.ViewModel is ConsoleLiveLogViewModel)
            {
                return Task.CompletedTask;
            }
            if (!_consoleBacklog.TryGetValue(message.ProjectId, out var backlog))
            {
                backlog = new List<ConsoleLineMessage>();
                _consoleBacklog[message.ProjectId] = backlog;
            }
            backlog.Add(message);
            if (backlog.Count > ConsoleLiveLogViewModel.MaxLines)
            {
                backlog.RemoveRange(0, backlog.Count - ConsoleLiveLogViewModel.MaxLines);
            }
            return Task.CompletedTask;
        }

        public Task HandleAsync(ProjectRunStartedMessage message, CancellationToken cancellationToken)
        {
            // Drop any backlog for this project so the new run starts on a clean console.
            _consoleBacklog.Remove(message.ProjectId);
            return Task.CompletedTask;
        }

        // ===== MCP requests (named-pipe bridge) =====

        public async Task HandleAsync(RunProjectRequestMessage message, CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            var project = _catalog.FindById(message.ProjectId);
            if (project is null)
            {
                _events.PublishOnCurrentThread(new RunProjectResultMessage(
                    message.CorrelationId,
                    new RunProjectResult(message.ProjectId, string.Empty, "Unknown",
                        Success: false, Cancelled: false,
                        Message: "Project not found.", DurationMs: 0)));
                return;
            }

            if (IsRunning)
            {
                // A rejected request never ran: report failure explicitly instead of
                // deriving Success from the target's previous (possibly successful) run,
                // which would let a caller treat a skipped deploy as a success.
                _events.PublishOnCurrentThread(new RunProjectResultMessage(
                    message.CorrelationId,
                    new RunProjectResult(
                        message.ProjectId, project.Name, project.LastRunState.ToString(),
                        Success: false, Cancelled: false,
                        Message: "Another update is already running in LocalEnvManager.",
                        DurationMs: (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds)));
                return;
            }

            SelectedProject = project;
            // Bring the run to the foreground so an MCP/Grok-triggered deploy is
            // observable: status row, selected project and live log pane.
            _events.PublishOnCurrentThread(new ActivatePageMessage(1));
            MarkStaleExcept(message.IncludeDependencies
                ? CollectWithDependencies(project)
                : new[] { project });
            IsRunning = true;
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();

            try
            {
                if (message.IncludeDependencies)
                {
                    await RunProjectsInDependencyOrderAsync(
                        CollectWithDependencies(project), _runCts.Token);
                }
                else
                {
                    await RunProjectPipelineAsync(
                        project,
                        new ProjectExecutionOptions(project.PullLatest),
                        _runCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // The pipeline maps cancellation to Idle; nothing else to do here.
            }
            catch (Exception ex)
            {
                _logger.Append(LogEntry.LogLevel.Error,
                    $"[{project.Name}] MCP 运行失败：{ex.Message}");
                project.LastRunState = ExecutionState.Failed;
                project.LastExecutionMessage = ex.Message;
            }
            finally
            {
                IsRunning = false;
                _runCts?.Dispose();
                _runCts = null;
            }

            _events.PublishOnCurrentThread(new RunProjectResultMessage(
                message.CorrelationId,
                BuildRunProjectResult(project, started, null)));
        }

        public async Task HandleAsync(StopProjectRequestMessage message, CancellationToken cancellationToken)
        {
            var project = _catalog.FindById(message.ProjectId);
            if (project is null)
            {
                _events.PublishOnCurrentThread(new StopProjectResultMessage(
                    message.CorrelationId,
                    new StopProjectResult(message.ProjectId, string.Empty, false, "Project not found.")));
                return;
            }

            var stopped = await StopProjectCoreAsync(project);
            _events.PublishOnCurrentThread(new StopProjectResultMessage(
                message.CorrelationId,
                new StopProjectResult(
                    message.ProjectId,
                    project.Name,
                    stopped,
                    stopped
                        ? $"Stop requested for {project.Name}."
                        : $"No running process to stop for {project.Name}.")));
        }

        private List<ManagedProject> CollectWithDependencies(ManagedProject root)
        {
            var ordered = new List<ManagedProject>();
            var seen = new HashSet<ManagedProject>();

            void Visit(ManagedProject project)
            {
                if (!seen.Add(project)) return;

                foreach (var name in GetDependencyNames(project))
                {
                    var dependency = _catalog.Projects.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name?.Trim(), name,
                            StringComparison.OrdinalIgnoreCase));
                    if (dependency is null)
                    {
                        _logger.Append(LogEntry.LogLevel.Warn,
                            $"[{project.Name}] 依赖项目 \"{name}\" 不存在。");
                        continue;
                    }
                    Visit(dependency);
                }

                ordered.Add(project);
            }

            Visit(root);
            return ordered;
        }

        private RunProjectResult BuildRunProjectResult(
            ManagedProject project,
            DateTimeOffset started,
            string? overrideMessage)
        {
            var state = project.LastRunState;
            var message = overrideMessage ?? project.LastExecutionMessage ?? string.Empty;
            var cancelled = state == ExecutionState.Idle
                && (message.Contains("取消", StringComparison.Ordinal)
                    || message.Contains("停止", StringComparison.Ordinal));

            return new RunProjectResult(
                _catalog.GetProjectId(project),
                project.Name,
                state.ToString(),
                Success: state == ExecutionState.Success,
                Cancelled: cancelled,
                Message: message,
                DurationMs: (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
        }

        private void OnExecutionProjectChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ManagedProject.IsChecked))
            {
                NotifyOfPropertyChange(nameof(SelectedCount));
                NotifyOfPropertyChange(nameof(CanRunSelected));
                NotifyOfPropertyChange(nameof(IsAllSelected));
            }
            else if (e.PropertyName == nameof(ManagedProject.LastRunState))
            {
                NotifyOfPropertyChange(nameof(UpdateSummary));
            }
            else if (e.PropertyName is nameof(ManagedProject.DeploymentMode)
                or nameof(ManagedProject.LogsDirectory)
                or nameof(ManagedProject.SourcePath)
                or nameof(ManagedProject.TargetPath))
            {
                // The right-pane implementation depends on all of these (console vs
                // file tail, and where the tail points). A cached pane would otherwise
                // keep watching the old directory / keep showing the old kind.
                if (sender is ManagedProject project)
                {
                    InvalidatePane(project);
                }
            }
        }

        private void InvalidatePane(ManagedProject project)
        {
            var id = _catalog.GetProjectId(project);
            if (LiveLogPanes.Remove(id, out var pane))
            {
                pane.ViewModel?.Dispose();
            }
            _consoleBacklog.Remove(id);

            if (ReferenceEquals(_selectedProject, project))
            {
                EnsurePaneFor(project);
                NotifyOfPropertyChange(nameof(ActiveLiveLog));
            }
        }

        protected override Task OnDeactivateAsync(bool close, CancellationToken cancellationToken)
        {
            if (close)
            {
                _clockTimer.Stop();
                foreach (var pane in LiveLogPanes.Values)
                {
                    pane.ViewModel?.Dispose();
                }
                LiveLogPanes.Clear();
                _consoleBacklog.Clear();
            }
            return base.OnDeactivateAsync(close, cancellationToken);
        }
    }
}
