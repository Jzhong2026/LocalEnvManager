using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LocalEnvManager.Behaviors
{
    /// <summary>
    /// Keeps a log ScrollViewer pinned to its newest content. The scroll is
    /// deferred until WPF has measured the newly added log lines.
    /// </summary>
    public static class AutoScrollToEndBehavior
    {
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

        private static void OnLoaded(object sender, RoutedEventArgs args) =>
            ScrollToEndAfterLayout((ScrollViewer)sender);

        private static void OnScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            if (args.ExtentHeightChange > 0)
            {
                ScrollToEndAfterLayout((ScrollViewer)sender);
            }
        }

        private static void ScrollToEndAfterLayout(ScrollViewer scrollViewer)
        {
            _ = scrollViewer.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(scrollViewer.ScrollToEnd));
        }
    }
}
