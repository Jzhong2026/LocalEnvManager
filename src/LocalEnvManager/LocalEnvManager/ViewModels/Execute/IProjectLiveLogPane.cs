namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// A right-pane live-log "pane" scoped to a single project. Multiple pane
    /// instances live side-by-side in <see cref="ExecuteViewModel"/>'s
    /// <c>LiveLogPanes</c> dictionary; only the one whose <c>ProjectId</c>
    /// matches the currently-selected project is shown.
    /// </summary>
    public interface IProjectLiveLogPane
    {
        /// <summary>Correlation id (assigned by <c>IProjectCatalog</c>).</summary>
        int ProjectId { get; }

        /// <summary>The view-model to bind to the right-pane UI.</summary>
        ILiveLogViewModel? ViewModel { get; }
    }
}