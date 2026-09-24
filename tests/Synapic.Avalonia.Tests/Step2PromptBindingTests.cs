using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.ViewModels.Steps;
using Synapic.Avalonia.Views.Wizard;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// A mistyped binding in Avalonia is silent - the control just never updates -
/// so these tests drive the Step 2 prompt editor through the real compiled XAML:
/// the box has to round-trip with the view model and both buttons have to carry
/// the commands that maintain the instruction.
/// </summary>
public class Step2PromptBindingTests
{
    private static (Step2Engine View, Step2EngineViewModel Vm, Window Window) Build()
    {
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar());
        var view = new Step2Engine { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        return (view, vm, window);
    }

    [AvaloniaFact]
    public void PromptBox_TwoWayBindsToTheInstruction()
    {
        var (view, vm, window) = Build();
        try
        {
            var box = view.FindControl<TextBox>("UserPromptBox");
            Assert.NotNull(box);
            Assert.Equal("", box!.Text);

            // View model -> control (hydration, "use built-in", reset).
            vm.UserPrompt = "edited in the view model";
            Assert.Equal("edited in the view model", box.Text);

            // Control -> view model (what the user types).
            box.Text = "typed by hand";
            Assert.Equal("typed by hand", vm.UserPrompt);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PromptButtons_CarryTheirCommands()
    {
        var (view, vm, window) = Build();
        try
        {
            var useBuiltIn = view.FindControl<Button>("UseBuiltInPromptButton");
            var reset = view.FindControl<Button>("ResetUserPromptButton");

            Assert.NotNull(useBuiltIn);
            Assert.NotNull(reset);
            Assert.Same(vm.UseBuiltInPromptCommand, useBuiltIn!.Command);
            Assert.Same(vm.ResetUserPromptCommand, reset!.Command);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ResetButton_ThroughTheView_ClearsTheInstruction()
    {
        var (view, vm, window) = Build();
        try
        {
            vm.UserPrompt = "mine";

            view.FindControl<Button>("ResetUserPromptButton")!.Command!.Execute(null);

            Assert.Equal("", vm.UserPrompt);
            Assert.Equal("", view.FindControl<TextBox>("UserPromptBox")!.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
