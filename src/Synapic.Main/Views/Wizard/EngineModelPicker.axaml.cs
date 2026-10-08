using Avalonia.Controls;

namespace Synapic.Main.Views.Wizard;

/// <summary>
/// The compact model + device picker ("1 · Source & model"). It renders the
/// wizard's own <see cref="ViewModels.Steps.Step2EngineViewModel"/>, so a model
/// chosen here is the model the settings dialog and the run use — there is one
/// settings view model behind every entry point.
/// </summary>
public partial class EngineModelPicker : UserControl
{
    public EngineModelPicker()
    {
        InitializeComponent();
    }
}
