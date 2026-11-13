using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using LocalEnvManager.Mcp.Contracts;
using LocalEnvManager.Messages;
using LocalEnvManager.Models;
using LocalEnvManager.Services;
using LocalEnvManager.ViewModels;
using Xunit;

namespace LocalEnvManager.Tests;

public sealed class McpEventFlowTests
{
    [Fact]
    public async Task McpRunUpdatesProjectStateAndRunLogs()
    {
        var project = new ManagedProject
        {
            Name = "Demo",
            DeploymentMode = DeploymentMode.DotNetBuild,
            SourcePath = @"D:\Source\Demo"
        };
        var category = new ProjectCategory { Name = "Tests" };
        category.Projects.Add(project);
        var catalog = new FakeCatalog(category);
        var events = new EventAggregator();
        var logger = new FakeLogger();

        var runLogs = new RunLogsViewModel(catalog, events);
        var execute = new ExecuteViewModel(
            catalog,
            new FakeRunner(),
            new FakeGit(),
            logger,
            events,
            new FakeLiveLogFactory(),
            new FakeUiSettingsStore());

        var resultCollector = new ResultCollector();
        events.SubscribeOnUI(resultCollector);

        await execute.HandleAsync(
            new RunProjectRequestMessage(Guid.NewGuid(), 1, IncludeDependencies: false),
            CancellationToken.None);

        var result = await resultCollector.Result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success, result.Message);
        Assert.Equal(ExecutionState.Success, project.LastRunState);

        var tab = Assert.Single(runLogs.ProjectLogs);
        var messages = tab.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains("Update started.", messages);
        Assert.Contains("[Demo] hello from runner", messages);
        Assert.Contains("ok", messages);
    }

    [Fact]
    public async Task BusyRunRequestReturnsFailureEvenIfTargetPreviouslySucceeded()
    {
        // Project 1 occupies the runner; project 2 is idle but its history is Success.
        // A busy rejection targeting project 2 must not inherit that historical success.
        var running = new ManagedProject
        {
            Name = "Running",
            DeploymentMode = DeploymentMode.DotNetBuild,
            SourcePath = @"D:\Source\Running"
        };
        var idle = new ManagedProject
        {
            Name = "Idle",
            DeploymentMode = DeploymentMode.DotNetBuild,
            SourcePath = @"D:\Source\Idle",
            LastRunState = ExecutionState.Success
        };
        var category = new ProjectCategory { Name = "Tests" };
        category.Projects.Add(running);
        category.Projects.Add(idle);
        var catalog = new FakeCatalog(category);
        var events = new EventAggregator();
        var runner = new BlockingRunner();

        var execute = new ExecuteViewModel(
            catalog, runner, new FakeGit(), new FakeLogger(), events,
            new FakeLiveLogFactory(), new FakeUiSettingsStore());

        var first = execute.HandleAsync(
            new RunProjectRequestMessage(Guid.NewGuid(), 1, IncludeDependencies: false),
            CancellationToken.None);
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var collector = new ResultCollector();
        events.SubscribeOnUI(collector);
        await execute.HandleAsync(
            new RunProjectRequestMessage(Guid.NewGuid(), 2, IncludeDependencies: false),
            CancellationToken.None);

        var result = await collector.Result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.Contains("already running", result.Message, StringComparison.OrdinalIgnoreCase);

        runner.Release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ResultCollector : IHandle<RunProjectResultMessage>
    {
        public TaskCompletionSource<RunProjectResult> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandleAsync(RunProjectResultMessage message, CancellationToken cancellationToken)
        {
            Result.TrySetResult(message.Result);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunner : IProjectUpdateRunner
    {
        public Task<ProjectExecutionResult> ExecuteAsync(
            ManagedProject project,
            ProjectExecutionOptions options,
            IProgress<string>? logProgress = null,
            CancellationToken cancellationToken = default)
        {
            logProgress?.Report($"[{project.Name}] hello from runner");
            return Task.FromResult(new ProjectExecutionResult(true, "ok"));
        }

        public Task<bool> StopAsync(
            ManagedProject project,
            IProgress<string>? logProgress = null)
            => Task.FromResult(false);
    }

    private sealed class BlockingRunner : IProjectUpdateRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProjectExecutionResult> ExecuteAsync(
            ManagedProject project,
            ProjectExecutionOptions options,
            IProgress<string>? logProgress = null,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            return new ProjectExecutionResult(true, "ok");
        }

        public Task<bool> StopAsync(
            ManagedProject project,
            IProgress<string>? logProgress = null)
            => Task.FromResult(false);
    }

    private sealed class FakeGit : IGitBranchService
    {
        public Task<string> GetCurrentBranchAsync(
            string repositoryPath,
            CancellationToken cancellationToken = default)
            => Task.FromResult("test");
    }

    private sealed class FakeLogger : IRuntimeLogger
    {
        public string LogFilePath => string.Empty;
        public void Append(LogEntry.LogLevel level, string message) { }
        public IProgress<string> Tee(IProgress<string>? inner) => inner ?? new Progress<string>();
    }

    private sealed class FakeLiveLogFactory : ILiveLogViewModelFactory
    {
        public ILiveLogViewModel Create(
            int projectId,
            DeploymentMode mode,
            Func<ManagedProject?> projectResolver)
            => new DummyLiveLog(projectId);
    }

    private sealed class FakeUiSettingsStore : IUiSettingsStore
    {
        public UiSettings Load() => new();
        public void Save(UiSettings settings) { }
    }

    private sealed class DummyLiveLog : ILiveLogViewModel
    {
        public DummyLiveLog(int projectId) => ProjectId = projectId;
        public int ProjectId { get; }
        public LiveLogKind Kind => LiveLogKind.Console;
        public IEnumerable<string> Lines => [];
        public bool HasLines => false;
        public bool Available => false;
        public string Hint => string.Empty;
        public string FileName => string.Empty;
        public void Dispose() { }
    }

    private sealed class FakeCatalog : IProjectCatalog
    {
        private readonly Dictionary<ManagedProject, int> _ids = new();

        public FakeCatalog(ProjectCategory category)
        {
            Categories = new BindableCollection<ProjectCategory> { category };
            Projects = new BindableCollection<ManagedProject>(category.Projects);
            var id = 1;
            foreach (var project in Projects)
            {
                _ids[project] = id++;
            }
        }

        public BindableCollection<ProjectCategory> Categories { get; }
        public BindableCollection<ManagedProject> Projects { get; }

        public int GetProjectId(ManagedProject? project)
            => project is not null && _ids.TryGetValue(project, out var id) ? id : 0;

        public ManagedProject? FindById(int projectId)
            => _ids.FirstOrDefault(pair => pair.Value == projectId).Key;

        public void FlushPendingSave() { }
    }
}
