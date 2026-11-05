using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LocalEnvManager.Behaviors
{
    /// <summary>
    /// Keeps a log ScrollViewer pinned to its newest content, but only while the
    /// user is already at (or very near) the bottom. Scrolling up to read history
    /// detaches the auto-scroll; scrolling back to the bottom re-attaches it.
    /// The scroll is deferred until WPF has measured the newly added lines.
    /// </summary>
    public static class AutoScrollToEndBehavior
    {
        private const double BottomThreshold = 8;
        private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();

        private sealed class ScrollState
        {
            public bool IsPinned = true;
        }

        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(AutoScrollToEndBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject element) =>
            (bool)element.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject element, bool value) =>
            element.SetValue(IsEnabledProperty, value);

        private static void OnIsEnabledChanged(DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            if (dependencyObject is not ScrollViewer scrollViewer) return;

            if ((bool)args.OldValue)
            {
                scrollViewer.ScrollChanged -= OnScrollChanged;
                scrollViewer.Loaded -= OnLoaded;
            }
            if ((bool)args.NewValue)
            {
                scrollViewer.Loaded += OnLoaded;
                scrollViewer.ScrollChanged += OnScrollChanged;
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs args)
        {
            var scrollViewer = (ScrollViewer)sender;
            States.GetOrCreateValue(scrollViewer).IsPinned = true;
            ScrollToEndAfterLayout(scrollViewer);
        }

        private static void OnScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            if (sender is not ScrollViewer scrollViewer) return;
            var state = States.GetOrCreateValue(scrollViewer);

            if (args.ExtentHeightChange > 0)
            {
                if (state.IsPinned)
                {
                    ScrollToEndAfterLayout(scrollViewer);
                }
                return;
            }

            // A user scroll (wheel / drag / keyboard) changes the offset without
            // changing the extent. Re-evaluate whether we should keep following.
            if (args.VerticalChange != 0)
            {
                state.IsPinned = IsAtBottom(scrollViewer);
            }
        }

        private static bool IsAtBottom(ScrollViewer scrollViewer) =>
            scrollViewer.VerticalOffset + scrollViewer.ViewportHeight
                >= scrollViewer.ExtentHeight - BottomThreshold;

        private static void ScrollToEndAfterLayout(ScrollViewer scrollViewer)
        {
            _ = scrollViewer.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(scrollViewer.ScrollToEnd));
        }
    }
}
