namespace LocalEnvManager.Models
{
    public enum DeploymentMode
    {
        // Explicit values: projects.json serialises this enum as an integer, so
        // inserting a member above another would silently change what existing
        // configurations mean. Keep these numbers stable.
        DotNetPublish = 0,
        DotNetBuild = 1,
        PowerShellCommand = 2,
        Uvicorn = 3
    }
}