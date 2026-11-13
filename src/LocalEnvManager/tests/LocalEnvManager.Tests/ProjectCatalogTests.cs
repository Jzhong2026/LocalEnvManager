using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Caliburn.Micro;
using LocalEnvManager.Models;
using LocalEnvManager.Services;
using Xunit;

namespace LocalEnvManager.Tests;

public sealed class ProjectCatalogTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ProjectCatalogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lem-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "projects.json");
    }

    [Fact]
    public void MissingFileSeedsProjectsAndCreatesConfig()
    {
        var logger = new FakeLogger();
        var catalog = new ProjectCatalog(new JsonProjectStore(_path), logger, new EventAggregator());

        Assert.NotEmpty(catalog.Categories);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void ValidEmptyConfigIsNotReplacedWithSeedData()
    {
        File.WriteAllText(_path, """{"Categories":[]}""");
        var logger = new FakeLogger();

        var catalog = new ProjectCatalog(new JsonProjectStore(_path), logger, new EventAggregator());

        Assert.Empty(catalog.Categories);
        Assert.DoesNotContain("LLMAgentApi", File.ReadAllText(_path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptConfigIsBackedUpBeforeSeeding()
    {
        const string corrupt = "{ not json";
        File.WriteAllText(_path, corrupt);
        var logger = new FakeLogger();

        var catalog = new ProjectCatalog(new JsonProjectStore(_path), logger, new EventAggregator());

        Assert.NotEmpty(catalog.Categories);
        var backups = Directory.GetFiles(_directory, "projects.json.corrupt-*");
        var backup = Assert.Single(backups);
        Assert.Equal(corrupt, File.ReadAllText(backup));
        Assert.Contains(logger.Entries, entry => entry.Contains("备份", StringComparison.Ordinal));
    }

    [Fact]
    public void RestoredTerminalResultIsMarkedStale()
    {
        // ExecutionState serialises as an integer (Success = 2), matching DeploymentMode.
        File.WriteAllText(_path, """
        {"Categories":[{"Name":"C","Projects":[{"Name":"P","LastRunState":2,"LastRunFinishedAt":"2026-11-13T10:00:00","LastExecutionMessage":"ok"}]}]}
        """);
        var catalog = new ProjectCatalog(new JsonProjectStore(_path), new FakeLogger(), new EventAggregator());

        var project = Assert.Single(catalog.Projects);
        Assert.Equal(ExecutionState.Success, project.LastRunState);
        Assert.True(project.IsResultStale);
        Assert.NotNull(project.LastRunFinishedAt);
        Assert.Equal("ok", project.LastExecutionMessage);
    }

    [Fact]
    public void PersistedRunningStateLoadsAsIdle()
    {
        File.WriteAllText(_path, """
        {"Categories":[{"Name":"C","Projects":[{"Name":"P","LastRunState":1}]}]}
        """);
        var catalog = new ProjectCatalog(new JsonProjectStore(_path), new FakeLogger(), new EventAggregator());

        var project = Assert.Single(catalog.Projects);
        Assert.Equal(ExecutionState.Idle, project.LastRunState);
        Assert.False(project.IsResultStale);
    }

    [Fact]
    public void RunStateChangeIsPersisted()
    {
        var catalog = new ProjectCatalog(new JsonProjectStore(_path), new FakeLogger(), new EventAggregator());
        var project = catalog.Projects[0];
        project.LastRunState = ExecutionState.Failed;
        project.LastRunFinishedAt = new DateTime(2026, 11, 13, 12, 0, 0);
        catalog.FlushPendingSave();

        var reloaded = new JsonProjectStore(_path).Load();
        var record = reloaded.Snapshot.Categories
            .SelectMany(c => c.Projects)
            .First(p => p.Name == project.Name);
        Assert.Equal(ExecutionState.Failed, record.LastRunState);
        Assert.NotNull(record.LastRunFinishedAt);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class FakeLogger : IRuntimeLogger
    {
        public List<string> Entries { get; } = [];
        public string LogFilePath => string.Empty;

        public void Append(LogEntry.LogLevel level, string message) => Entries.Add(message);

        public IProgress<string> Tee(IProgress<string>? inner) => inner ?? new Progress<string>();
    }
}
