namespace LocalEnvManager.Messages
{
    /// <summary>
    /// Published by the Execute tab's right-pane toolbar when the user clicks
    /// the maximize / restore button. <see cref="ViewModels.Execute.ExecuteViewModel"/>
    /// listens for it and toggles whether the left project list is collapsed, so
    /// the right-pane live log can fill the full tab width.
    /// </summary>
    public sealed class ToggleExecuteDetailMaximizeMessage { }
}