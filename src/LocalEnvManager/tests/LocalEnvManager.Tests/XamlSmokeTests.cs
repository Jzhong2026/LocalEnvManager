using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using LocalEnvManager.Behaviors;
using Xunit;

namespace LocalEnvManager.Tests;

/// <summary>
/// STA-threaded tests for the column resize grip — the Execute tab's primary new
/// interaction. The grip's output width is bound back to a real grid column (as the
/// XAML binds it to the view-model), so these verify the full loop: drag → clamped
/// width → column resize → star "项目" column protected at its MinWidth.
///
/// XAML structural validity of the new styles is covered by the BAML compile step;
/// loading the merged dictionary at runtime would require a full Application +
/// MaterialDesign theme, which is out of scope for a unit test.
/// </summary>
public sealed class XamlSmokeTests
{
    [Fact]
    public void ResizeGripClampsGrowthToStarSlack()
    {
        RunOnSta(() =>
        {
            var (grid, thumb, star) = BuildHeader();
            ColumnResizeBehavior.SetColumnWidth(thumb, new GridLength(80));
            ColumnResizeBehavior.SetMinWidth(thumb, 60);
            // 600 wide: auto(0) + star + fixed(80) => star = 520, slack = 520 - 160 = 360.
            Layout(grid, 600);

            RaiseDragStarted(thumb);
            RaiseDragDelta(thumb, 1000);
            Layout(grid, 600);

            // Growth capped at the 360px slack: 80 + 360 = 440, project column keeps 160.
            Assert.Equal(440, ColumnResizeBehavior.GetColumnWidth(thumb).Value);
            Assert.Equal(160, star.ActualWidth, 3);

            // Budget exhausted: further rightward drag does not squeeze the project column.
            RaiseDragDelta(thumb, 100);
            Layout(grid, 600);
            Assert.Equal(440, ColumnResizeBehavior.GetColumnWidth(thumb).Value);
            Assert.Equal(160, star.ActualWidth, 3);
        });
    }

    [Fact]
    public void ResizeGripRespectsMinWidth()
    {
        RunOnSta(() =>
        {
            var (grid, thumb, _) = BuildHeader();
            ColumnResizeBehavior.SetColumnWidth(thumb, new GridLength(80));
            ColumnResizeBehavior.SetMinWidth(thumb, 60);
            Layout(grid, 600);

            RaiseDragStarted(thumb);
            RaiseDragDelta(thumb, -1000);

            Assert.Equal(60, ColumnResizeBehavior.GetColumnWidth(thumb).Value);
        });
    }

    private static (Grid grid, Thumb thumb, ColumnDefinition star) BuildHeader()
    {
        var star = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 };
        var fixedColumn = new ColumnDefinition { Width = new GridLength(80) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(star);
        grid.ColumnDefinitions.Add(fixedColumn);

        // Single-cell wrapper (0 explicit columns) so FindStarColumn skips it and
        // resolves the header Grid's star column, exactly like the real header cells.
        var cell = new Grid();
        var thumb = new Thumb();
        cell.Children.Add(thumb);
        grid.Children.Add(cell);
        Grid.SetColumn(cell, 2);

        // Mirror the XAML: the column width tracks the grip's ColumnWidth output, so
        // a drag actually resizes the column and reflows the star column.
        BindingOperations.SetBinding(
            fixedColumn,
            ColumnDefinition.WidthProperty,
            new Binding
            {
                Source = thumb,
                Path = new PropertyPath("(0)", ColumnResizeBehavior.ColumnWidthProperty)
            });

        return (grid, thumb, star);
    }

    private static void Layout(Grid grid, double width)
    {
        grid.Measure(new Size(width, 100));
        grid.Arrange(new Rect(0, 0, width, 100));
        grid.UpdateLayout();
    }

    private static void RaiseDragStarted(Thumb thumb)
        => thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });

    private static void RaiseDragDelta(Thumb thumb, double horizontalChange)
        => thumb.RaiseEvent(new DragDeltaEventArgs(horizontalChange, 0) { RoutedEvent = Thumb.DragDeltaEvent });

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }
}