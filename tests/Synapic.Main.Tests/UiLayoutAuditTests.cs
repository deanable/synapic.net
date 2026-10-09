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
using Synapic.Main.Views.Dashboard;
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

    /// <summary>The home route is the dashboard now (docs/ui-design.md D1/D2).</summary>
    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    public void Home_screen_has_no_overlapping_or_clipped_controls(double width, double height)
    {
        AuditWindow(new MainWindow { DataContext = Shell("daminion") },
            "Home (dashboard, Daminion form)", width, height);
    }

    /// <summary>
    /// The tagging mode — the whole form and its run on one page now that the
    /// step chain is gone (D5): every region the mode renders has to lay out
    /// cleanly at every audited size.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    public void Tagging_mode_has_no_overlapping_or_clipped_controls(double width, double height)
    {
        var shell = Shell("local", Path.GetTempPath());
        shell.StartTaggingRouteCommand.Execute(null);

        AuditWindow(new MainWindow { DataContext = shell }, "Tagging mode", width, height);
    }

    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    public void Dedup_and_upscale_modes_have_no_overlapping_or_clipped_controls(
        double width, double height)
    {
        var dedup = Shell("local", Path.GetTempPath());
        dedup.StartDedupRouteCommand.Execute(null);
        AuditWindow(new MainWindow { DataContext = dedup }, "Deduplication mode", width, height);

        var upscale = Shell("local", Path.GetTempPath());
        upscale.StartUpscaleRouteCommand.Execute(null);
        AuditWindow(new MainWindow { DataContext = upscale }, "Upscaling mode", width, height);
    }

    /// <summary>
    /// The operation template through each of the three routes at every audited
    /// size: the same three regions in reading order (top to bottom — the stacked
    /// form Avalonia renders without a breakpoint), nothing clipped or
    /// overlapping. ui-design §10 criteria 2 and 5, on the template itself.
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
            var dataSource = Assert.Single(tree.OfType<Border>(),
                b => b.Classes.Contains("dataSourceRegion"));
            var parameters = Assert.Single(tree.OfType<Border>(),
                b => b.Classes.Contains("parametersRegion"));
            var output = Assert.Single(tree.OfType<Border>(),
                b => b.Classes.Contains("outputRegion"));

            var sourceTop = dataSource.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var parametersTop = parameters.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var outputTop = output.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            Assert.True(sourceTop < parametersTop,
                $"{screen}: the data source region is not above the parameters region");
            Assert.True(parametersTop < outputTop,
                $"{screen}: the parameters region is not above the output region");

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
    public void Operation_modes_audit_clean_with_the_three_regions_in_order(
        double width, double height)
    {
        var tagging = Shell("local", Path.GetTempPath());
        tagging.StartTaggingRouteCommand.Execute(null);
        AuditRegions(new MainWindow { DataContext = tagging }, "Tagging mode", width, height);

        var dedup = Shell("local", Path.GetTempPath());
        dedup.StartDedupRouteCommand.Execute(null);
        AuditRegions(new MainWindow { DataContext = dedup }, "Dedup mode", width, height);

        var upscale = Shell("local", Path.GetTempPath());
        upscale.StartUpscaleRouteCommand.Execute(null);
        AuditRegions(new MainWindow { DataContext = upscale }, "Upscale mode", width, height);
    }

    /// <summary>The dashboard's four panels, by the class every one of them carries.</summary>
    private static List<Control> DashboardPanels(Window window) =>
        window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Classes.Contains("dashboardPanel")).ToList();

    /// <summary>
    /// The dashboard's wide form (ui-design §2.1: a 2x2 grid of four panels, read
    /// row-major — Settings, Tag, Dedup, Upscale). Settings and Tag share the
    /// first row, Dedup and Upscale the second; the second row sits under the
    /// first and in the same two columns. The case asserts the two-column rule
    /// itself (DashboardView.MinPanelWidth) before measuring, so a dashboard that
    /// stacked early fails here rather than passing this fact on the narrow
    /// form's geometry — a layout fact that does not check which layout it is
    /// looking at cannot tell the two apart.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1600, 900)]
    public void Dashboard_is_a_two_by_two_grid_of_its_four_panels(double width, double height)
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

            var panels = DashboardPanels(window);
            Assert.Equal(4, panels.Count);

            var dashboard = Assert.Single(window.GetVisualDescendants().OfType<DashboardView>());
            Assert.True(dashboard.Bounds.Width >= DashboardView.MinPanelWidth * 2,
                $"the dashboard is {dashboard.Bounds.Width} px wide at {width}x{height}: " +
                "too narrow for the two-column form this case measures");

            double Top(Control c) => c.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            double Left(Control c) => c.TranslatePoint(new Point(0, 0), window)!.Value.X;

            var settings = panels.Single(p => p.Name == "SettingsPanel");
            var tag = panels.Single(p => p.Name == "TagPanel");
            var dedup = panels.Single(p => p.Name == "DedupPanel");
            var upscale = panels.Single(p => p.Name == "UpscalePanel");

            // First row: Settings | Tag.
            Assert.True(System.Math.Abs(Top(settings) - Top(tag)) < Tolerance,
                $"Settings and Tag are not on the first row at {width}x{height}");
            Assert.True(Left(settings) < Left(tag),
                $"Settings is not left of Tag at {width}x{height}");

            // Second row: Dedup | Upscale, under the first row, same columns.
            Assert.True(System.Math.Abs(Top(dedup) - Top(upscale)) < Tolerance,
                $"Dedup and Upscale are not on the second row at {width}x{height}");
            Assert.True(Top(dedup) > Top(settings),
                $"the second row is not below the first at {width}x{height}");
            Assert.True(Left(dedup) < Left(upscale),
                $"Dedup is not left of Upscale at {width}x{height}");
            Assert.True(System.Math.Abs(Left(dedup) - Left(settings)) < Tolerance &&
                        System.Math.Abs(Left(upscale) - Left(tag)) < Tolerance,
                $"the second row is not aligned with the first at {width}x{height}");

            var report = Audit(window);
            Assert.True(report.Length == 0,
                $"Dashboard at {width}x{height} ({_panelsAudited} panels):\n{report}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The dashboard's narrow form (ui-design §7/D8: below the switch point the
    /// grid stacks): one full-width column in reading order — Settings, Tag,
    /// Dedup, Upscale — every panel below the previous one and sharing one left
    /// edge, with nothing clipped at the smallest audited size. The case asserts
    /// the one-column rule before measuring, for the same reason its wide twin
    /// does.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    public void Dashboard_stacks_its_four_panels_in_reading_order_below_the_switch_point(
        double width, double height)
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

            var panels = DashboardPanels(window);
            Assert.Equal(4, panels.Count);

            var dashboard = Assert.Single(window.GetVisualDescendants().OfType<DashboardView>());
            Assert.True(dashboard.Bounds.Width < DashboardView.MinPanelWidth * 2,
                $"the dashboard is {dashboard.Bounds.Width} px wide at {width}x{height}: " +
                "wide enough for the two-column form, so this case is not measuring the stacked one");

            double Top(Control c) => c.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            double Left(Control c) => c.TranslatePoint(new Point(0, 0), window)!.Value.X;

            var ordered = new[] { "SettingsPanel", "TagPanel", "DedupPanel", "UpscalePanel" }
                .Select(name => panels.Single(p => p.Name == name)).ToList();

            for (var i = 1; i < ordered.Count; i++)
                Assert.True(Top(ordered[i]) > Top(ordered[i - 1]),
                    $"{ordered[i].Name} is not below {ordered[i - 1].Name} at {width}x{height}");

            Assert.All(ordered, p => Assert.True(
                System.Math.Abs(Left(p) - Left(ordered[0])) < Tolerance,
                $"{p.Name} is not in the single column at {width}x{height}"));

            var report = Audit(window);
            Assert.True(report.Length == 0,
                $"Dashboard at {width}x{height} ({_panelsAudited} panels):\n{report}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The three operations' parameter forms, audited where they live now
    /// (ui-design D3 / §10 criterion 4): inline in the Parameters region of each
    /// mode, inside the real shell at every audited size — no step to walk to, so
    /// every case audits the mode as it opens. The dialogs were audited at the one
    /// size each was authored for; inline they have to survive all four, beside
    /// the two regions under and above them.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    public void Inline_parameters_have_no_overlapping_or_clipped_controls(
        double width, double height)
    {
        // Tagging: the full engine form is the Parameters region.
        var tagging = Shell("local", Path.GetTempPath());
        tagging.StartTaggingRouteCommand.Execute(null);
        AuditWindow(new MainWindow { DataContext = tagging }, "Tagging parameters", width, height);

        // Deduplication: the scan and keep-set rules.
        var dedup = Shell("local", Path.GetTempPath());
        dedup.StartDedupRouteCommand.Execute(null);
        AuditWindow(new MainWindow { DataContext = dedup }, "Dedup parameters", width, height);

        // Upscaling: workflow, factor, precision, output.
        var upscale = Shell("local", Path.GetTempPath());
        upscale.StartUpscaleRouteCommand.Execute(null);
        AuditWindow(new MainWindow { DataContext = upscale }, "Upscale parameters", width, height);
    }
}
