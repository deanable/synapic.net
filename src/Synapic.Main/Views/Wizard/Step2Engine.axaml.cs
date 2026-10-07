using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Synapic.Avalonia.ViewModels.Steps;

namespace Synapic.Avalonia.Views.Wizard;

public partial class Step2Engine : UserControl
{
    public Step2Engine()
    {
        InitializeComponent();

        // Delete removes the highlighted system prompt from the saved history.
        // Tunnel, so the key reaches us before the combo's own list handling;
        // the combo is not editable, so Delete has no text-editing job to steal.
        SystemPromptPresetBox.AddHandler(
            KeyDownEvent, OnSystemPromptPresetKeyDown, RoutingStrategies.Tunnel);

        // Clicking away commits the prompt to the history, so the wording is in
        // the list the next time it is wanted.
        SystemPromptBox.LostFocus += (_, _) => ViewModel?.RememberSystemPrompt();
    }

    private Step2EngineViewModel? ViewModel => DataContext as Step2EngineViewModel;

    private void OnSystemPromptPresetKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || ViewModel is not { } vm) return;

        vm.DeleteSystemPromptPresetCommand.Execute(vm.SelectedSystemPromptPreset);
        e.Handled = true;
    }
}
