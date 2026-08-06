using System.ComponentModel;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// Contract shared by every top-level page (tab) view-model. The host
    /// (ContentHostViewModel) keeps a collection of these and renders each one
    /// inside a <c>ContentControl</c> — the matching <c>View</c> is resolved
    /// by Caliburn.Micro's <c>ViewLocator</c> (e.g. ManageViewModel → ManageView).
    ///
    /// Each page is responsible for its own data, its own subscriptions, and its
    /// own communication with other pages — pages never reference each other
    /// directly. Cross-page data goes through <c>IEventAggregator</c> or the
    /// shared <c>IProjectCatalog</c> singleton.
    /// </summary>
    public interface IPageViewModel : INotifyPropertyChanged
    {
        /// <summary>Short label rendered on the tab header button.</summary>
        string DisplayName { get; }

        /// <summary>
        /// Stable ordering hint for the host (lower = leftmost). Use distinct
        /// non-negative integers so the host's tab strip stays predictable.
        /// </summary>
        int Order { get; }

        /// <summary>True when this page is the currently-active tab.</summary>
        bool IsActive { get; set; }

        /// <summary>
        /// Called by the host immediately after the page becomes the visible tab.
        /// Use it to kick off lazy initialization (branch refresh, etc.) that
        /// should happen the first time the user lands on the page.
        /// </summary>
        void OnActivated();
    }
}