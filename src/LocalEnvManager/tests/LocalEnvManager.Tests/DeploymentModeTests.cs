using LocalEnvManager.Models;
using LocalEnvManager.Services;
using Xunit;

namespace LocalEnvManager.Tests;

public sealed class DeploymentModeTests
{
    [Fact]
    public void PersistedValuesStayStable()
    {
        // projects.json stores the enum as an integer. Changing these numbers
        // would silently reinterpret every existing configuration.
        Assert.Equal(0, (int)DeploymentMode.DotNetPublish);
        Assert.Equal(1, (int)DeploymentMode.DotNetBuild);
        Assert.Equal(2, (int)DeploymentMode.PowerShellCommand);
        Assert.Equal(3, (int)DeploymentMode.Uvicorn);
    }

    [Fact]
    public void ResultDefaultsToSuccessOrFailure()
    {
        var success = new ProjectExecutionResult(true, "ok");
        var failure = new ProjectExecutionResult(false, "bad");
        var cancelled = ProjectExecutionResult.Cancelled("stopped");

        Assert.Equal(ProjectExecutionOutcome.Success, success.EffectiveOutcome);
        Assert.Equal(ProjectExecutionOutcome.Failed, failure.EffectiveOutcome);
        Assert.Equal(ProjectExecutionOutcome.Cancelled, cancelled.EffectiveOutcome);
        Assert.False(cancelled.Success);
    }
}
