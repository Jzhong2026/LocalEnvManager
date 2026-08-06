using System.Collections.Generic;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// Right-pane child view-model contract. Each implementation represents a
    /// different way of surfacing a project's runtime log (console stream vs.
    /// tailing a log file). The parent (ProjectLiveLogViewModel) only sees
    /// this interface, so the XAML can swap the active implementation by
    /// simply changing the <c>Content</c> binding on a ContentControl.
    /// </summary>
    public interface ILiveLogViewModel
    {
        /// <summary>
        /// Stable id of the project this child view-model belongs to. Used by
        /// the parent to route ConsoleLineMessage / LogFileChangedMessage
        /// to the right instance.
        /// </summary>
        int ProjectId { get; }

        /// <summary>Display kind — used by the parent for diagnostics.</summary>
        LiveLogKind Kind { get; }

        /// <summary>The lines currently being shown to the user.</summary>
        IEnumerable<string> Lines { get; }

        /// <summary>
        /// True when the dark console should be visible. When false, the view
        /// shows the placeholder (e.g. "等待 stdout…" or "未配置实时日志").
        /// </summary>
        bool Available { get; }

        /// <summary>Hint text shown when <see cref="Available"/> is false.</summary>
        string Hint { get; }

        /// <summary>Name of the file currently being tailed (empty for console mode).</summary>
        string FileName { get; }
    }

    public enum LiveLogKind
    {
        Console,
        LogFile,
    }
}
