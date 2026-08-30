using Avalonia.Controls;
using Avalonia.Media;
using XTerm.Search;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The renderer half of scrollback search: highlights as an overlay, navigation that moves the
/// viewport, and all of it reachable from the host without touching the inner view.
/// </summary>
[TestClass]
public class SearchSurfaceTests
{
    private static (TerminalView view, Window window) Realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        window.UpdateLayout();
        return (control.View(), window);
    }

    [AvaloniaTest]
    public void Finding_reports_the_count_a_find_box_shows()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("error one\r\nfine\r\nerror two\r\n");

            view.FindInBuffer("error").Should().Be(2);
            view.SearchHitCount.Should().Be(2);
            view.SearchCurrentIndex.Should().Be(-1, "no match chosen until the box steps");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Stepping_moves_the_viewport_to_an_offscreen_match()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("needle\r\n");
            for (var i = 0; i < 200; i++)
                view.Terminal.Write($"filler {i}\r\n");

            view.FindInBuffer("needle");
            var bottom = view.Terminal.Buffer.ViewportY;

            view.FindNext().Should().BeTrue();
            view.Terminal.Buffer.ViewportY.Should().BeLessThan(bottom, "the viewport should have moved up to show the match");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Stepping_with_no_search_is_left_unhandled()
    {
        var (view, window) = Realised();
        try
        {
            view.FindNext().Should().BeFalse();
            view.FindPrevious().Should().BeFalse();
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Clearing_removes_the_result()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("error\r\n");
            view.FindInBuffer("error");
            view.ClearSearch();

            view.SearchHitCount.Should().Be(0);
            view.SearchCurrentIndex.Should().Be(-1);
        }
        finally { window.Close(); }
    }

    /// <summary>A frame with hits paints without throwing; the overlay path is exercised.</summary>
    [AvaloniaTest]
    public void A_frame_with_matches_renders()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("error error error\r\n");
            view.FindInBuffer("error");
            view.FindNext();

            var group = new DrawingGroup();
            using (var context = group.Open())
                view.Render(context);

            view.SearchHitCount.Should().Be(3);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Setting a brush on the CONTROL must change what the view paints with. SurfaceParityTests
    /// proves the member exists; this proves the wiring — which is exactly the half the review
    /// found missing.
    /// </summary>
    [AvaloniaTest]
    public void Brushes_set_on_the_control_reach_the_view()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);
        try
        {
            control.SearchHighlightBrush = Brushes.HotPink;
            control.SearchCurrentBrush = Brushes.Lime;
            window.UpdateLayout();

            var view = control.View();
            view.SearchHighlightBrush.Should().Be(Brushes.HotPink);
            view.SearchCurrentBrush.Should().Be(Brushes.Lime);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Options_pass_through()
    {
        var (view, window) = Realised();
        try
        {
            view.Terminal.Write("Error error\r\n");

            view.FindInBuffer("error", new SearchOptions { CaseSensitive = true }).Should().Be(1);
        }
        finally { window.Close(); }
    }
}
