using Avalonia.Controls;

namespace Synapic.Main.Views.Operation;

/// <summary>
/// Region A's limits row (ui-design §8): the shared processing limits, bound to
/// the one shell-owned <c>Step1DatasourceViewModel</c> — the same instance the
/// source form above it edits and every mode's run reads.
/// </summary>
public partial class SourceLimitsPanel : UserControl
{
    public SourceLimitsPanel()
    {
        InitializeComponent();
    }
}
