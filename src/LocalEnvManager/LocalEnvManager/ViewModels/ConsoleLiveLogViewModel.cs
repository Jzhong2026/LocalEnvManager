using Caliburn.Micro;
using LocalEnvManager.Messages;
using LocalEnvManager.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// Right-pane child view-model for uvicorn (and other stdout-driven)
    /// projects. Subscribes to <see cref="ConsoleLineMessage"/> and
    /// <see cref="ProjectRunStartedMessage"/> via the EventAggregator and
    /// keeps a rolling buffer of the most recent lines.
    ///
    /// Important: stdout can fire hundreds of lines per second during a
    /// service startup. Adding each line directly to the BindableCollection
    /// would re-bind the entire ItemsControl and overwhelm the WPF
    /// dispatcher (we froze the UI doing exactly that). The handler instead
    /// enqueues lines into a private buffer and OnActivatedAsync starts a
    /// low-frequency flush timer that copies the buffered lines in one batch
    /// every <see cref="FlushIntervalMs"/> ms.
    /// </summary>
    public sealed class ConsoleLiveLogViewModel : Screen, ILiveLogViewModel,
        IHandle<ConsoleLineMessage>,
        IHandle<ProjectRunStartedMessage>
    {
        /// <summary>Cap on the in-memory buffer (matches the previous live-log cap).</summary>
        public const int MaxLines = 2000;

        /// <summary>How often the buffer is drained into the bound collection.</summary>
        public const int FlushIntervalMs = 50;

        private readonly IEventAggregator _events;
        private readonly BindableCollection<string> _lines = new();
        private readonly object _bufferGate = new();
        private readonly List<string> _pending = new();
        private System.Threading.Timer? _flushTimer;
        private bool _available;

        public ConsoleLiveLogViewModel(int projectId, IEventAggregator events)
        {
            ProjectId = projectId;
            _events = events;
            // Available flips to true the first time a ConsoleLineMessage arrives;
            // the parent's placeholder ("等待 stdout…") is shown in the meantime.
            _events.SubscribeOnUI(this);
            // Start the flush timer at construction time. We do NOT gate this
            // on OnActivatedAsync because WPF's ContentControl does not call
            // IActivate.ActivateAsync on its DataContext — only Caliburn's
            // WindowManager does. Without the timer, HandleAsync would append
            // to _pending but never drain into the bound collection, and the
            // user would see an empty console.
            //
            // The cost when the view-model is not in the visual tree is one
            // timer fire every 50ms that does nothing (the buffer is empty),
            // which is negligible. We rely on the parent deactivating us
            // when the user navigates away for good (the OnDeactivateAsync
            // hook stops the timer to free the resources).
            if (_flushTimer is null)
            {
                _flushTimer = new System.Threading.Timer(
                    _ => FlushPending(),
                    state: null,
                    dueTime: FlushIntervalMs,
                    period: FlushIntervalMs);
            }
        }

        public int ProjectId { get; }
        public LiveLogKind Kind => LiveLogKind.Console;
        public IEnumerable<string> Lines => _lines;
        public string FileName => string.Empty;

        public bool Available
        {
            get => _available;
            private set
            {
                if (_available == value) return;
                _available = value;
                NotifyOfPropertyChange();
            }
        }

        private string _hint = "运行后将在此显示 stdout…";
        public string Hint
        {
            get => _hint;
            private set
            {
                if (_hint == value) return;
                _hint = value;
                NotifyOfPropertyChange();
            }
        }

        public Task HandleAsync(ConsoleLineMessage message, CancellationToken cancellationToken)
        {
            if (message.ProjectId != ProjectId) return Task.CompletedTask;

            // Stderr lines (e.g. Python tracebacks) get a visual marker so the
            // user can spot them in the dark console.
            var line = message.IsError ? "⚠ " + message.Line : message.Line;
            lock (_bufferGate)
            {
                _pending.Add(line);
            }
            Available = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Seeds this view with output produced before the user selected its
        /// project. ProjectLiveLogViewModel retains that bounded backlog
        /// because ConsoleLineMessage is otherwise a transient event.
        /// </summary>
        public void Replay(IEnumerable<ConsoleLineMessage> messages)
        {
            foreach (var message in messages)
            {
                _ = HandleAsync(message, CancellationToken.None);
            }
        }

        public Task HandleAsync(ProjectRunStartedMessage message, CancellationToken cancellationToken)
        {
            if (message.ProjectId != ProjectId) return Task.CompletedTask;
            // Per OQ-3: clear the buffer when a new run starts. Both the
            // bound collection AND the pending buffer are dropped so the
            // user sees a clean console.
            lock (_bufferGate) { _pending.Clear(); }
            _lines.Clear();
            Available = false;
            Hint = "运行后将在此显示 stdout…";
            return Task.CompletedTask;
        }


        private void FlushPending()
        {
            // Timer callbacks run on a ThreadPool thread, but the bound
            // collection is UI-affine. Marshal to the dispatcher before
            // mutating. We do the dequeue on this thread to keep the
            // critical section short, then hop to UI for the Add.
            string[] toAdd;
            lock (_bufferGate)
            {
                if (_pending.Count == 0) return;
                toAdd = _pending.ToArray();
                _pending.Clear();
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                AppendToBound(toAdd);
            }
            else
            {
                dispatcher.BeginInvoke(new System.Action(() => AppendToBound(toAdd)));
            }
        }

        private void AppendToBound(string[] lines)
        {
            foreach (var line in lines)
            {
                _lines.Add(line);
            }
            // Trim in one shot so we don't trigger 2000 CollectionChanged
            // events when a burst pushes us over the cap. BindableCollection
            // exposes RemoveRange(IEnumerable<T>); we materialize the oldest
            // entries into a small list, then remove them in one Reset.
            var overflow = _lines.Count - MaxLines;
            if (overflow > 0)
            {
                var drop = new List<string>(overflow);
                for (var i = 0; i < overflow; i++)
                {
                    drop.Add(_lines[i]);
                }
                _lines.RemoveRange(drop);
            }
        }

        protected override System.Threading.Tasks.Task OnDeactivateAsync(bool close, System.Threading.CancellationToken cancellationToken)
        {
            if (close)
            {
                try { _events.Unsubscribe(this); } catch { }
                _flushTimer?.Dispose();
                _flushTimer = null;
            }
            return base.OnDeactivateAsync(close, cancellationToken);
        }
    }
}
