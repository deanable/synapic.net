using Avalonia;
using Avalonia.Controls;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.Views.Operation;

/// <summary>
/// The shared operation template: the three regions every mode renders through
/// (Data source → Parameters → Output). It hosts the existing panels and pages
/// and holds no mode logic — only per-mode DataTemplates.
/// </summary>
public partial class OperationLayout : UserControl
{
    /// <summary>
    /// The one shell-owned data source (docs/ui-design.md D4), handed to Region A
    /// by the host. The template still knows no mode: it renders whatever source
    /// state the shell passes in, and Region A binds that single instance — which
    /// is what makes the source read identically on every route.
    /// </summary>
    public static readonly StyledProperty<Step1DatasourceViewModel?> SharedSourceProperty =
        AvaloniaProperty.Register<OperationLayout, Step1DatasourceViewModel?>(nameof(SharedSource));

    public OperationLayout()
    {
        InitializeComponent();
    }

    public Step1DatasourceViewModel? SharedSource
    {
        get => GetValue(SharedSourceProperty);
        set => SetValue(SharedSourceProperty, value);
    }
}
