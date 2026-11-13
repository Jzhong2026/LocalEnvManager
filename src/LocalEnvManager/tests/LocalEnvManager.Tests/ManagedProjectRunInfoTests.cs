using System;
using LocalEnvManager.Models;
using Xunit;

namespace LocalEnvManager.Tests;

public sealed class ManagedProjectRunInfoTests
{
    [Fact]
    public void NewProjectHasNoRunTime()
    {
        var p = new ManagedProject();
        Assert.Null(p.LastRunFinishedAt);
        Assert.False(p.IsResultStale);
        Assert.Equal("—", p.TimeColumnDisplay);
        Assert.Equal("从未运行", p.TimeColumnTooltip);
    }

    [Fact]
    public void FinishedRunShowsRelativeTime()
    {
        var p = new ManagedProject { LastRunState = ExecutionState.Success };
        p.LastRunFinishedAt = DateTime.Now.AddMinutes(-2);
        Assert.Equal("2 分钟前", p.TimeColumnDisplay);
        Assert.NotEqual("从未运行", p.TimeColumnTooltip);
    }

    [Fact]
    public void RunningShowsElapsed()
    {
        var p = new ManagedProject
        {
            LastRunState = ExecutionState.Running,
            LastRunStartedAt = DateTime.Now.AddSeconds(-90)
        };
        Assert.Equal("1m30s", p.TimeColumnDisplay);
        Assert.StartsWith("开始于", p.TimeColumnTooltip);
    }

    [Fact]
    public void StaleFlagRaisesPropertyChanged()
    {
        var p = new ManagedProject();
        var raised = false;
        p.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ManagedProject.IsResultStale)) raised = true;
        };
        p.IsResultStale = true;
        Assert.True(raised);
        Assert.True(p.IsResultStale);
    }
}