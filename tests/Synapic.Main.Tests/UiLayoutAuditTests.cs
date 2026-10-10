using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
using Synapic.Main.Views.Settings;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The layout audit: Avalonia lays a horizontal <see cref="StackPanel"/> out at
/// its children's natural widths and clips whatever does not fit, so a row that
/// is 30px too wide at a 1024px window looks exactly like a working one until
/// somebody opens the window that size. This walks the real compiled visual tree
/// and reports the two shapes the bug takes: sibling controls whose bounds
/// intersect, and children that extend past the panel laying them out.
///
/// Layout only runs in the headless platform for a window that has been shown,
/// so each case boots the real <see cref="App"/> (see TestAppBuilder) and runs
/// measure/arrange at a real window size.
/// </summary>
public class UiLayoutAuditTests
{
    private const double Tolerance = 1.0;

    private static MainWindowViewModel Shell(string sourceType = "local", string path = "")
    {
        var session = new Session();
        session.Datasource.Type = sourceType;
        session.Datasource.LocalPath = path;
        if (sourceType == "daminion")
            session.Datasource.DaminionUrl = "http://damserver.local/daminion";
        return new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), session,
            () => null, null, null, _ => null);
    }

    private static int _panelsAudited;

    private static string Audit(Window window)
    {
        var report = new StringBuilder();
        _panelsAudited = 0;

        // Deliberately no manual Measure/Arrange: the platform's own layout pass
        // (which runs on Show and again on a resize) is the thing under audit.
        foreach (var panel in window.GetVisualDescendants().OfType<Panel>())
        {
            // A Grid places children in cells on purpose and a Canvas places
            // them where the author asked; the flow layouts are where a row
            // that is too wide turns into an overlap or a clip.
            if (panel is not (StackPanel or WrapPanel or UniformGrid or DockPanel))
                continue;

            _panelsAudited++;

            var children = panel.Children
                .Where(c => c.IsVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0)
                .ToList();

            foreach (var child in children)
            {
                if (child.Bounds.Right <= panel.Bounds.Width + Tolerance &&
                    child.Bounds.Bottom <= panel.Bounds.Height + Tolerance)
                    continue;
                report.AppendLine(
                    $"CLIP: {Describe(child)} at {child.Bounds} inside {Describe(panel)} " +
                    $"{panel.Bounds.Size} (path: {ControlPath(window, child)})");
            }

            for (var i = 0; i < children.Count; i++)
            for (var j = i + 1; j < children.Count; j++)
            {
                if (!Intersects(children[i].Bounds, children[j].Bounds))
                    continue;
                report.AppendLine(
                    $"OVERLAP: {Describe(children[i])} {children[i].Bounds} vs " +
                    $"{Describe(children[j])} {children[j].Bounds} inside {Describe(panel)} " +
                    $"(path: {ControlPath(window, children[j])})");
            }
        }

        return report.ToString();
    }

    private static bool Intersects(Rect a, Rect b) =>
        a.X + Tolerance < b.Right && b.X + Tolerance < a.Right &&
        a.Y + Tolerance < b.Bottom && b.Y + Tolerance < a.Bottom;

    private static string Describe(Control control) =>
        control.GetType().Name +
        (string.IsNullOrEmpty(control.Name) ? "" : $"#{control.Name}") +
        (control is ContentControl { Content: string text } ? $"(\"{text}\")" : "");

    private static string ControlPath(Window window, Control control)
    {
        var names = new List<string>();
        for (Control? node = control; node is not null && node != window; node = node.Parent as Control)
            names.Add(Describe(node));
        names.Reverse();
        return string.Join(" > ", names);
    }

    private static void AuditWindow(Window window, string screen, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        window.Show();
        // Settle: a resize queues another layout pass, and auditing the tree
        // between the two passes reports the intermediate one (controls measured
        // against the old width) as a defect that no user could ever see.
        for (var i = 0; i < 3; i++)
            Dispatcher.UIThread.RunJobs();
        try
        {
            // A vacuous pass would be worse than a failure: the window really has
            // to be at the audited size, and the walk really has to find panels.
            Assert.Equal(width, window.ClientSize.Width);
            Assert.Equal(height, window.ClientSize.Height);

            var report = Audit(window);
            Assert.True(_panelsAudited > 10,
                $"{screen}: only {_panelsAudited} flow panels found - the audit is not walking the real tree");
            Assert.True(report.Length == 0,
                $"{screen} at {width}x{height} ({_panelsAudited} panels):\n{report}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The Settings view is where the app opens (the first sidebar row), and it is
    /// the densest screen in the app — the source form, the inference server and
    /// engine and the operation rules all live there. It has to lay out cleanly at
    /// every audited size.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    [InlineData(640, 480)]
    public void Settings_view_has_no_overlapping_or_clipped_controls(double width, double height)
    {
        AuditWindow(new MainWindow { DataContext = Shell("daminion") },
            "Settings (Daminion form)", width, height);
    }

    /// <summary>
    /// The three processing views — the run surface and its output on one page now
    /// that the step chain is gone: every region the view renders has to lay out
    /// cleanly at every audited size.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    [InlineData(640, 480)]
    public void Processing_views_have_no_overlapping_or_clipped_controls(double width, double height)
    {
        var tagging = Shell("local", Path.GetTempPath());
        tagging.StartTaggingRouteCommand.Execute(null);
        AuditWindow(new MainWindow { DataContext = tagging }, "Tagging view", width, height);

        var dedup = Shell("local", Path.GetTempPath());
        dedup.StartDedupRouteCommand.Execute(null);
        AuditWindow(new MainWindow { DataContext = dedup }, "Deduplication view", width, height);

        var upscale = Shell("local", Path.GetTempPath());
        upscale.StartUpscaleRouteCommand.Execute(null);
        AuditWindow(new MainWindow { DataContext = upscale }, "Upscaling view", width, height);
    }

    /// <summary>
    /// The processing view through each route at every audited size: the read-only
    /// source summary above the output, nothing clipped or overlapping.
    /// </summary>
    private static void AuditRegions(Window window, string screen, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        window.Show();
        for (var i = 0; i < 3; i++)
            Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(width, window.ClientSize.Width);
            Assert.Equal(height, window.ClientSize.Height);

            var tree = window.GetVisualDescendants().ToList();
            var summary = Assert.Single(tree.OfType<Border>(),
                b => b.Classes.Contains("sourceSummaryRegion"));
            var output = Assert.Single(tree.OfType<Border>(),
                b => b.Classes.Contains("outputRegion"));

            var summaryTop = summary.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var outputTop = output.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            Assert.True(summaryTop < outputTop,
                $"{screen}: the source summary is not above the output region");

            var report = Audit(window);
            Assert.True(_panelsAudited > 10,
                $"{screen}: only {_panelsAudited} flow panels found - the audit is not walking the real tree");
            Assert.True(report.Length == 0,
                $"{screen} at {width}x{height} ({_panelsAudited} panels):\n{report}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    [InlineData(640, 480)]
    public void Processing_views_audit_clean_with_the_source_summary_above_the_output(
        double width, double height)
    {
        var tagging = Shell("local", Path.GetTempPath());
        tagging.StartTaggingRouteCommand.Execute(null);
        AuditRegions(new MainWindow { DataContext = tagging }, "Tagging view", width, height);

        var dedup = Shell("local", Path.GetTempPath());
        dedup.StartDedupRouteCommand.Execute(null);
        AuditRegions(new MainWindow { DataContext = dedup }, "Dedup view", width, height);

        var upscale = Shell("local", Path.GetTempPath());
        upscale.StartUpscaleRouteCommand.Execute(null);
        AuditRegions(new MainWindow { DataContext = upscale }, "Upscale view", width, height);
    }

    /// <summary>
    /// The Settings view is one reading-order column of configuration sections:
    /// the connection first, then the inference server, the engine and the
    /// operation rules, with the app preferences last. The sections stack in that
    /// order at every audited size.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    [InlineData(640, 480)]
    public void Settings_view_stacks_its_sections_in_reading_order(double width, double height)
    {
        var window = new MainWindow { DataContext = Shell("daminion") };
        window.Width = width;
        window.Height = height;
        window.Show();
        for (var i = 0; i < 3; i++)
            Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(width, window.ClientSize.Width);
            Assert.Equal(height, window.ClientSize.Height);

            var settings = Assert.Single(window.GetVisualDescendants().OfType<SettingsTab>());
            double Top(string name) => settings.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Name == name)
                .TranslatePoint(new Point(0, 0), window)!.Value.Y;

            var ordered = new[]
            {
                "SectionDataSource", "SectionInferenceServer", "SectionEngine",
                "SectionDedup", "SectionUpscale",
            };

            for (var i = 1; i < ordered.Length; i++)
                Assert.True(Top(ordered[i]) > Top(ordered[i - 1]),
                    $"{ordered[i]} is not below {ordered[i - 1]} at {width}x{height}");

            // The app preferences follow the operation rules.
            var preferences = settings.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Name == "SectionAppearance");
            Assert.True(preferences.TranslatePoint(new Point(0, 0), window)!.Value.Y > Top("SectionUpscale"),
                "the app preferences are not below the operation settings");

            var report = Audit(window);
            Assert.True(report.Length == 0,
                $"Settings at {width}x{height} ({_panelsAudited} panels):\n{report}");
        }
        finally
        {
            window.Close();
        }
    }
}
