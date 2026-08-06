namespace LocalEnvManager.Models
{
    public enum DeploymentMode
    {
        DotNetPublish,
        DotNetBuild,
        PowerShellCommand,
        Uvicorn
    }
}