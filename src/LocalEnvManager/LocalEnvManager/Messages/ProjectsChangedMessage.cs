namespace LocalEnvManager.Messages
{
    /// <summary>
    /// Published by <see cref="Services.IProjectCatalog"/> whenever its flat
    /// <c>Projects</c> list is rebuilt (after a category / project add or remove).
    /// Subscribers use it to refresh their dependent counts (HasProjects,
    /// SelectedCount, Run-button enabled state, etc.) without holding a direct
    /// reference to the catalog.
    /// </summary>
    public sealed class ProjectsChangedMessage { }
}