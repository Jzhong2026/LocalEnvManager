namespace LocalEnvManager.Messages
{
    /// <summary>
    /// Published when the user clicks the footer Run button.
    /// </summary>
    public sealed class RunSelectedMessage { }

    /// <summary>
    /// Published when the user clicks the footer Cancel button (only meaningful
    /// when a pipeline is running).
    /// </summary>
    public sealed class CancelRunMessage { }

    /// <summary>
    /// Published when the user clicks the "刷新分支" icon next to the Branch
    /// column header.
    /// </summary>
    public sealed class RefreshBranchesMessage { }

    /// <summary>
    /// Published when the user toggles the "select all" checkbox in the
    /// Execute-tab table header.
    /// </summary>
    public sealed class ToggleSelectAllMessage { }
}
