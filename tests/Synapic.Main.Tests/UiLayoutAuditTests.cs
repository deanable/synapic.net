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

    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    public void Home_screen_has_no_overlapping_or_clipped_controls(double width, double height)
    {
        AuditWindow(new MainWindow { DataContext = Shell("daminion") },
            "Home (Daminion form)", width, height);
    }

    /// <summary>The four tagging steps: source &amp; model, settings, process, results.</summary>
    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    public void Tagging_steps_have_no_overlapping_or_clipped_controls(double width, double height)
    {
        foreach (var (name, goTo) in new (string, Action<WizardViewModel>)[]
                 {
                     ("step 1 source & model", w => w.GoToStep1Command.Execute(null)),
                     ("step 2 settings", w => w.GoToStep2Command.Execute(null)),
                     ("step 3 process", w => w.GoToStep3Command.Execute(null)),
                     ("step 4 results", w => w.GoToStep4Command.Execute(null)),
                 })
        {
            var shell = Shell("local", Path.GetTempPath());
            shell.StartTaggingRouteCommand.Execute(null);
            goTo(shell.Wizard);

            AuditWindow(new MainWindow { DataContext = shell },
                $"Tagging {name}", width, height);
        }
    }

    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 700)]
    [InlineData(900, 600)]
    public async Task Dedup_and_upscale_steps_have_no_overlapping_or_clipped_controls(
        double width, double height)
    {
        var dedup = Shell("local", Path.GetTempPath());
        dedup.StartDedupRouteCommand.Execute(null);
        await dedup.Wizard.NextCommand.ExecuteAsync(null);
        AuditWindow(new MainWindow { DataContext = dedup }, "Deduplication step", width, height);

        var upscale = Shell("local", Path.GetTempPath());
        upscale.StartUpscaleRouteCommand.Execute(null);
        await upscale.Wizard.NextCommand.ExecuteAsync(null);
        AuditWindow(new MainWindow { DataContext = upscale }, "Upscaling step", width, height);
    }

    /// <summary>3 · Settings: every operation's dialog is a dense form of its own.</summary>
    [AvaloniaFact]
    public void Operation_settings_dialogs_have_no_overlapping_or_clipped_controls()
    {
        var shell = Shell("daminion", Path.GetTempPath());

        AuditWindow(new EngineSettingsDialog { DataContext = shell.Wizard.Step2 },
            "Tagging settings dialog", 980, 760);
        AuditWindow(new DedupSettingsDialog { DataContext = shell.Wizard.Dedup },
            "Deduplication settings dialog", 720, 560);
        AuditWindow(new UpscaleSettingsDialog { DataContext = shell.Wizard.Upscale },
            "Upscaling settings dialog", 760, 420);
    }
}
