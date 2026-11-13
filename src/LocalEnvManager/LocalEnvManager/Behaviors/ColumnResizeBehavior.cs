using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LocalEnvManager.Behaviors
{
    /// <summary>
    /// Turns a header <see cref="Thumb"/> into a table column resize grip. The
    /// resized width is pushed through a two-way <see cref="ColumnWidth"/> binding
    /// to a view-model GridLength property that both the header and every row bind
    /// to, so the whole column tracks the drag live.
    ///
    /// Growth is budgeted against the star "项目" column's remaining slack so a drag
    /// can never squeeze the project column below its MinWidth (which would overflow
    /// the fixed-total table). The budget is captured at DragStarted because rapid
    /// DragDelta events within a single layout pass would otherwise each read a stale
    /// ActualWidth and overshoot.
    /// </summary>
    public static class ColumnResizeBehavior
    {
        public static readonly DependencyProperty ColumnWidthProperty =
            DependencyProperty.RegisterAttached(
                "ColumnWidth",
                typeof(GridLength),
                typeof(ColumnResizeBehavior),
                new FrameworkPropertyMetadata(
                    // Default must differ from any real bound width so the first
                    // binding apply fires the change callback that hooks the drag
                    // events. A pixel default (e.g. 80) would silently skip the
                    // callback for a column whose initial width happens to equal it.
                    GridLength.Auto,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnColumnWidthChanged));

        public static readonly DependencyProperty MinWidthProperty =
            DependencyProperty.RegisterAttached(
                "MinWidth", typeof(double), typeof(ColumnResizeBehavior), new PropertyMetadata(48.0));

        public static readonly DependencyProperty MaxWidthProperty =
            DependencyProperty.RegisterAttached(
                "MaxWidth", typeof(double), typeof(ColumnResizeBehavior), new PropertyMetadata(800.0));

        // Per-thumb drag budget (pixels the star column can still shrink).
        private static readonly DependencyProperty BudgetProperty =
            DependencyProperty.RegisterAttached(
                "Budget", typeof(double), typeof(ColumnResizeBehavior), new PropertyMetadata(0.0));

        public static GridLength GetColumnWidth(DependencyObject obj) => (GridLength)obj.GetValue(ColumnWidthProperty);
        public static void SetColumnWidth(DependencyObject obj, GridLength value) => obj.SetValue(ColumnWidthProperty, value);
        public static double GetMinWidth(DependencyObject obj) => (double)obj.GetValue(MinWidthProperty);
        public static void SetMinWidth(DependencyObject obj, double value) => obj.SetValue(MinWidthProperty, value);
        public static double GetMaxWidth(DependencyObject obj) => (double)obj.GetValue(MaxWidthProperty);
        public static void SetMaxWidth(DependencyObject obj, double value) => obj.SetValue(MaxWidthProperty, value);

        private static void OnColumnWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Thumb thumb) return;
            thumb.DragStarted -= OnDragStarted;
            thumb.DragDelta -= OnDragDelta;
            thumb.DragStarted += OnDragStarted;
            thumb.DragDelta += OnDragDelta;
        }

        private static void OnDragStarted(object sender, DragStartedEventArgs e)
        {
            if (sender is not Thumb thumb) return;
            var star = FindStarColumn(thumb);
            var budget = star is null ? 0.0 : Math.Max(0.0, star.ActualWidth - star.MinWidth);
            thumb.SetValue(BudgetProperty, budget);
        }

        private static void OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb) return;

            var current = GetColumnWidth(thumb).Value;
            var min = GetMinWidth(thumb);
            var max = GetMaxWidth(thumb);
            var budget = (double)thumb.GetValue(BudgetProperty);
            var delta = e.HorizontalChange;

            double next;
            if (delta > 0)
            {
                var grow = Math.Max(0, Math.Min(delta, Math.Min(budget, max - current)));
                next = current + grow;
                thumb.SetValue(BudgetProperty, budget - grow);
            }
            else
            {
                var shrink = Math.Max(0, Math.Min(-delta, current - min));
                next = current - shrink;
                thumb.SetValue(BudgetProperty, budget + shrink);
            }

            SetColumnWidth(thumb, new GridLength(Math.Max(min, next)));
        }

        // Walk up to the header Grid (the ancestor with several columns including a
        // star column). Single-cell wrapper Grids have 0 explicit columns and the
        // branch header is a DockPanel, so neither is mistaken for the header.
        private static ColumnDefinition? FindStarColumn(DependencyObject start)
        {
            var current = start;
            while (current is not null)
            {
                if (current is Grid grid && grid.ColumnDefinitions.Count > 1)
                {
                    var star = grid.ColumnDefinitions.FirstOrDefault(c => c.Width.IsStar);
                    if (star is not null) return star;
                }
                current = LogicalTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}