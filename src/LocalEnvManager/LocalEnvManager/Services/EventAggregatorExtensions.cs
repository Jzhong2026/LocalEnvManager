using Caliburn.Micro;
using System;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// Helper extensions over Caliburn.Micro's <see cref="IEventAggregator"/>
    /// so the rest of the codebase doesn't have to deal with the v5 async
    /// surface (PublishAsync / Subscribe) directly. Two things matter:
    /// (1) subscribe handlers to the UI thread (so they can safely mutate
    /// BindableCollection / raise PropertyChanged);
    /// (2) publish on the caller's thread (fire-and-forget).
    /// </summary>
    public static class EventAggregatorExtensions
    {
        /// <summary>
        /// Subscribe a handler so its <c>HandleAsync</c> runs on the WPF UI
        /// thread. Use this for handlers that mutate BindableCollection or
        /// raise PropertyChanged — the EventAggregator fires callbacks on the
        /// publishing thread by default, and FileSystemWatcher / child-process
        /// callbacks come in on background threads.
        /// </summary>
        public static void SubscribeOnUI(this IEventAggregator events, object subscriber)
        {
            if (events is null) throw new ArgumentNullException(nameof(events));
            if (subscriber is null) throw new ArgumentNullException(nameof(subscriber));
            // Caliburn's own SubscribeOnUIThread wraps IHandle<T> handlers the
            // way we want — see Caliburn.Micro.Core/EventAggregatorExtensions.cs
            // in the framework source.
            events.SubscribeOnUIThread(subscriber);
        }

        /// <summary>
        /// Publish a message on the current thread (fire-and-forget). The
        /// PublishAsync Task is intentionally not awaited — the publish/
        /// subscribe graph is best-effort and the UI never blocks on it.
        /// </summary>
        public static void PublishOnCurrentThread(this IEventAggregator events, object message)
        {
            if (events is null) throw new ArgumentNullException(nameof(events));
            if (message is null) throw new ArgumentNullException(nameof(message));
            // Kick the task; exceptions are surfaced via
            // TaskScheduler.UnobservedTaskException which Caliburn logs in
            // its default implementation.
            _ = events.PublishOnCurrentThreadAsync(message);
        }
    }
}
