using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views.Operation;
using Xunit;

namespace Synapic.Main.Tests;

public sealed class Step3ProcessBehaviorTests
{
    [AvaloniaFact]
    public async Task Abort_during_tagging_reports_cancelled_and_restores_idle_state()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-tag-cancel-").FullName;
        try
        {
            File.Copy(FindSampleImage(), Path.Combine(folder, "one.jpg"));
            var enteredTag = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sidecar = new FakeSidecar
            {
                TagHandler = async (_, ct) =>
                {
                    enteredTag.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return new Synapic.Shared.Contracts.TagResponse();
                },
            };
            var session = LocalSession(folder);
            var source = new Step1DatasourceViewModel(session);
            var vm = new Step3ProcessViewModel(session, sidecar, source);
            var bar = new RunStateBar { DataContext = vm };
            var window = new Window { Content = bar, Width = 900, Height = 600 };
            window.Show();
            try
            {
                var run = vm.StartCommand.ExecuteAsync(null);
                await enteredTag.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(vm.IsRunning);
                Assert.True(vm.AbortCommand.CanExecute(null));

                vm.AbortCommand.Execute(null);
                await run;
                Dispatcher.UIThread.RunJobs();

                Assert.False(vm.IsRunning);
                Assert.True(vm.IsIdle);
                Assert.False(vm.AbortCommand.CanExecute(null));
                Assert.Contains(vm.LogLines, line => line.Contains("Batch aborted by user", StringComparison.Ordinal));
                Assert.Contains(bar.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == vm.ProgressText && text.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Tag_fetch_failure_is_reported_and_restores_idle_state()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"synapic-tag-missing-{Guid.NewGuid():N}");
        var session = LocalSession(missing);
        var source = new Step1DatasourceViewModel(session);
        var vm = new Step3ProcessViewModel(session, new FakeSidecar(), source);
        var bar = new RunStateBar { DataContext = vm };
        var window = new Window { Content = bar, Width = 900, Height = 600 };
        window.Show();
        try
        {
            await vm.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(vm.IsRunning);
            Assert.True(vm.IsIdle);
            Assert.Contains(vm.LogLines, line => line.Contains($"Batch failed: Folder not found: {missing}", StringComparison.Ordinal));
            Assert.Contains(bar.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == vm.ProgressText && text.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static Session LocalSession(string folder)
    {
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = folder;
        return session;
    }

    private static string FindSampleImage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Synapic.Main.Tests", "TestData", "sample.jpg");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("TestData/sample.jpg not found");
    }
}
