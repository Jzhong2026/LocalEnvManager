using System;
using System.IO;
using LocalEnvManager.Models;
using LocalEnvManager.Services;
using Xunit;

namespace LocalEnvManager.Tests;

public sealed class ProjectStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ProjectStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lem-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "projects.json");
    }

    [Fact]
    public void MissingFileIsReportedAsFirstRun()
    {
        var store = new JsonProjectStore(_path);

        var result = store.Load();

        Assert.Equal(ProjectStoreLoadStatus.Missing, result.Status);
        Assert.Empty(result.Snapshot.Categories);
    }

    [Fact]
    public void EmptyCategoryListIsLoadedNotTreatedAsFirstRun()
    {
        File.WriteAllText(_path, """{"Categories":[]}""");
        var store = new JsonProjectStore(_path);

        var result = store.Load();

        Assert.Equal(ProjectStoreLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Snapshot.Categories);
    }

    [Fact]
    public void CorruptFileIsBackedUpAndReportedAsFailure()
    {
        const string corrupt = "{ this is not json";
        File.WriteAllText(_path, corrupt);
        var store = new JsonProjectStore(_path);

        var result = store.Load();

        Assert.Equal(ProjectStoreLoadStatus.Failed, result.Status);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(corrupt, File.ReadAllText(result.BackupPath!));
        Assert.Equal(corrupt, File.ReadAllText(_path));
    }

    [Fact]
    public void SaveThenLoadRoundTripsProjects()
    {
        var store = new JsonProjectStore(_path);
        var snapshot = new ProjectStoreSnapshot
        {
            Categories =
            [
                new CategoryRecord
                {
                    Name = "测试分类",
                    Projects =
                    [
                        new ProjectRecord
                        {
                            Name = "测试项目",
                            SourcePath = @"D:\Source\Demo",
                            Command = "publish -c Release",
                            DeploymentMode = DeploymentMode.DotNetPublish,
                            TargetPath = @"D:\Deploy\Demo",
                            BackupFolder = @"D:\Overlay\Demo"
                        }
                    ]
                }
            ]
        };

        store.Save(snapshot);
        var loaded = store.Load();

        Assert.Equal(ProjectStoreLoadStatus.Loaded, loaded.Status);
        var project = Assert.Single(Assert.Single(loaded.Snapshot.Categories).Projects);
        Assert.Equal("测试项目", project.Name);
        Assert.Equal(@"D:\Overlay\Demo", project.BackupFolder);
        Assert.Equal(DeploymentMode.DotNetPublish, project.DeploymentMode);
    }

    [Fact]
    public void SaveThenLoadRoundTripsRunState()
    {
        var store = new JsonProjectStore(_path);
        var finished = new DateTime(2026, 11, 13, 10, 0, 0);
        var snapshot = new ProjectStoreSnapshot
        {
            Categories =
            [
                new CategoryRecord
                {
                    Name = "C",
                    Projects =
                    [
                        new ProjectRecord
                        {
                            Name = "P",
                            LastRunState = ExecutionState.Success,
                            LastRunFinishedAt = finished,
                            LastExecutionMessage = "ok"
                        }
                    ]
                }
            ]
        };

        store.Save(snapshot);
        var loaded = store.Load();

        var project = Assert.Single(Assert.Single(loaded.Snapshot.Categories).Projects);
        Assert.Equal(ExecutionState.Success, project.LastRunState);
        Assert.Equal(finished, project.LastRunFinishedAt);
        Assert.Equal("ok", project.LastExecutionMessage);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
