using Avalonia.Media;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels.Steps;
using Xunit;

namespace Synapic.Avalonia.Tests;

public class Step1DatasourceTests
{
    private static Step1DatasourceViewModel NewViewModel() => new(new Session());

    [Fact]
    public void ConnectCommand_DisabledUntilAllCredentialsEntered()
    {
        var vm = NewViewModel();
        Assert.False(vm.ConnectCommand.CanExecute(null));

        vm.DaminionUrl = "http://dam.local/daminion";
        Assert.False(vm.ConnectCommand.CanExecute(null));
        vm.DaminionUser = "bob";
        Assert.False(vm.ConnectCommand.CanExecute(null));
        vm.DaminionPass = "secret";
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    /// <summary>
    /// The button only re-evaluates when CanExecuteChanged fires. Calling
    /// CanExecute directly (as above) passes even when the ViewModel forgets to
    /// notify, so assert the notifications the WPF/Avalonia command machinery
    /// actually depends on.
    /// </summary>
    [Fact]
    public void ConnectCommand_RaisesCanExecuteChanged_AsCredentialsChange()
    {
        var vm = NewViewModel();
        var raised = 0;
        vm.ConnectCommand.CanExecuteChanged += (_, _) => raised++;

        vm.DaminionUrl = "http://dam.local/daminion";
        vm.DaminionUser = "bob";
        vm.DaminionPass = "secret";

        Assert.True(raised >= 3, $"expected a re-query per field, saw {raised}");

        // Clearing a required field must disable the button again.
        vm.DaminionPass = "";
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void ConnectCommand_NotificationsAlsoFireWhileConnecting()
    {
        var vm = NewViewModel();
        vm.DaminionUrl = "http://dam.local/daminion";
        vm.DaminionUser = "bob";
        vm.DaminionPass = "secret";
        Assert.True(vm.ConnectCommand.CanExecute(null));

        vm.IsConnecting = true;
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    // ── Source readiness: what the start screen gates the workflows on ──────

    [Fact]
    public void Local_source_is_usable_only_once_the_folder_resolves()
    {
        var vm = NewViewModel();
        Assert.False(vm.HasUsableSource);
        Assert.Equal("Folder: not selected", vm.LocalSourceText);
        Assert.Equal(Colors.Red, Brush(vm.LocalSourceBrush));

        vm.LocalPath = Path.Combine(Path.GetTempPath(), "synapic-nowhere-" + Guid.NewGuid().ToString("N"));
        Assert.False(vm.HasUsableSource);            // typed, but nothing is there
        Assert.StartsWith("Folder: not found", vm.LocalSourceText);

        vm.LocalPath = Path.GetTempPath();
        Assert.True(vm.HasUsableSource);
        Assert.Contains(Path.GetTempPath(), vm.LocalSourceText);
        Assert.Equal(Colors.ForestGreen, Brush(vm.LocalSourceBrush));
    }

    [Fact]
    public void Daminion_source_needs_a_live_session_not_just_typed_credentials()
    {
        var vm = NewViewModel();
        vm.DatasourceType = "daminion";
        vm.DaminionUrl = "http://dam.local/daminion";
        vm.DaminionUser = "bob";
        vm.DaminionPass = "secret";

        Assert.False(vm.HasUsableSource);            // filled in, but nobody is signed in
        Assert.Equal("Daminion: not connected", vm.DaminionSourceText);
        Assert.Equal(Colors.Red, Brush(vm.DaminionSourceBrush));

        vm.IsDaminionConnected = true;               // what a successful connect sets
        Assert.True(vm.HasUsableSource);
        Assert.Contains("http://dam.local/daminion", vm.DaminionSourceText);
        Assert.Equal(Colors.ForestGreen, Brush(vm.DaminionSourceBrush));
    }

    /// <summary>
    /// The shell's three workflow cards bind to HasUsableSource; a silent
    /// binding is exactly the bug this guards (the gate would never lift).
    /// </summary>
    [Fact]
    public void Source_readiness_notifies_every_binding_it_feeds()
    {
        var vm = NewViewModel();
        var seen = new List<string>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        vm.LocalPath = Path.GetTempPath();

        Assert.Contains(nameof(Step1DatasourceViewModel.HasUsableSource), seen);
        Assert.Contains(nameof(Step1DatasourceViewModel.LocalSourceBrush), seen);
        Assert.Contains(nameof(Step1DatasourceViewModel.LocalSourceText), seen);
    }

    // ── The record count is automatic: launch, and every change that moves it ──

    /// <summary>A temp folder holding <paramref name="images"/> .jpg files plus one file a scan ignores.</summary>
    private static string TempImageFolder(int images)
    {
        var dir = Path.Combine(Path.GetTempPath(), "synapic-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        for (var i = 0; i < images; i++) File.WriteAllText(Path.Combine(dir, $"img{i}.jpg"), "x");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
        return dir;
    }

    /// <summary>Poll until a debounced background count lands.</summary>
    private static async Task WaitForCountAsync(Step1DatasourceViewModel vm, string expected)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (vm.CountText == expected) return;
            await Task.Delay(25);
        }
        Assert.Fail($"expected count '{expected}', saw '{vm.CountText}'");
    }

    private static Color Brush(IBrush brush) => ((ISolidColorBrush)brush).Color;

    [Fact]
    public async Task Choosing_a_folder_counts_its_images_without_a_button_press()
    {
        var folder = TempImageFolder(3);
        try
        {
            var vm = NewViewModel();
            vm.LocalPath = folder;

            await WaitForCountAsync(vm, "3 image files");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Recursive_toggle_recounts_the_folder_it_changes()
    {
        var root = TempImageFolder(1);
        var nested = Path.Combine(root, "sub");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "nested.jpg"), "x");
        try
        {
            var vm = NewViewModel();
            vm.LocalPath = root;
            await WaitForCountAsync(vm, "1 image file");

            vm.LocalRecursive = true;   // a scope change: the count follows on its own

            await WaitForCountAsync(vm, "2 image files");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Launch_counts_the_stored_folder_without_signing_anywhere()
    {
        var folder = TempImageFolder(2);
        try
        {
            var session = new Session();
            session.Datasource.ApplyStoredSource(new DatasourceSettings
            {
                Type = "local",
                LocalPath = folder,
            });

            var vm = new Step1DatasourceViewModel(session);
            Assert.Equal(folder, vm.LocalPath);          // the stored folder comes back
            Assert.True(vm.HasUsableSource);             // so the workflows are ready

            await vm.InitializeAsync();                  // what the shell runs at startup

            Assert.Equal("2 image files", vm.CountText); // counted on launch, no button
            Assert.False(vm.IsDaminionConnected);
            Assert.Null(vm.ConnectionMessage);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Launch_does_not_sign_in_when_no_connection_was_stored()
    {
        var session = new Session();
        session.Datasource.ApplyStoredSource(new DatasourceSettings { Type = "daminion" });

        var vm = new Step1DatasourceViewModel(session);   // no registry store: nothing to reuse
        await vm.InitializeAsync();

        Assert.False(vm.IsDaminionConnected);
        Assert.False(vm.HasUsableSource);
        Assert.Null(vm.CountText);
    }
}
