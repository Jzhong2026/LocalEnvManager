using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using LocalEnvManager.Messages;
using LocalEnvManager.Services;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// Owns the ordered list of <see cref="IPageViewModel"/> pages and tracks the
    /// currently visible one. Resolved by the container so the host itself
    /// stays free of page-specific knowledge — new pages just implement
    /// <see cref="IPageViewModel"/> and register themselves in
    /// <see cref="AppBootstrapper"/>.
    /// </summary>
    public sealed class ContentHostViewModel : Screen, IHandle<ActivatePageMessage>
    {
        private readonly IEventAggregator _events;

        public ContentHostViewModel(
            ManageViewModel manage,
            ExecuteViewModel execute,
            RunLogsViewModel runLogs,
            IEventAggregator events)
        {
            _events = events;
            _events.SubscribeOnUI(this);
            // Display order matches Order property on each page (lower = leftmost).
            Pages = new BindableCollection<IPageViewModel>
            {
                manage,
                execute,
                runLogs
            };
            ActivePage = Pages.OrderBy(p => p.Order).First();
        }

        public BindableCollection<IPageViewModel> Pages { get; }

        private IPageViewModel? _activePage;
        public IPageViewModel? ActivePage
        {
            get => _activePage;
            set
            {
                if (Set(ref _activePage, value))
                {
                    // Flip IsActive on every page so the tab strip can highlight
                    // the selected one via a DataTrigger.
                    foreach (var page in Pages)
                    {
                        var active = page == value;
                        if (page.IsActive != active)
                        {
                            page.IsActive = active;
                        }
                    }
                    value?.OnActivated();
                }
            }
        }

        /// <summary>
        /// View-facing command bound to the tab-strip buttons. Idempotent —
        /// setting <see cref="ActivePage"/> to the same value is a no-op.
        /// </summary>
        public void ActivatePage(IPageViewModel page)
        {
            if (page is null || !Pages.Contains(page)) return;
            ActivePage = page;
        }

        public Task HandleAsync(ActivatePageMessage message, CancellationToken cancellationToken)
        {
            var page = Pages.FirstOrDefault(candidate => candidate.Order == message.PageOrder);
            if (page is not null)
            {
                ActivatePage(page);
            }
            return Task.CompletedTask;
        }
    }
}
