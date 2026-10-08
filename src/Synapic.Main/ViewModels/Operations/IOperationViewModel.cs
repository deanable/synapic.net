using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Synapic.Main.ViewModels.Operations;

/// <summary>
/// One operation the shell can open — Tagging, Deduplication or Upscaling — as
/// the shared operation template sees it (docs/ui-refactor-plan.md §3, CONTEXT
/// D-04/D-05). Implementations are thin adapters over the existing step view
/// models, so nothing here duplicates run or parameter state.
///
/// The three regions of the template map to these members: Region B (parameters)
/// shows <see cref="ParametersSummary"/> and the mode's own settings view, Region C
/// (output) shows <see cref="Report"/> plus the shared run bar, and the dashboard
/// panel shows <see cref="Title"/>/<see cref="Description"/>.
///
/// Region A — the datasource — is deliberately NOT here: one shell-owned source
/// selection is bound once by the template, and a mode stays ready or not
/// through <see cref="IsRunEnabled"/> / <see cref="RunDisabledReason"/>.
/// </summary>
public interface IOperationViewModel : INotifyPropertyChanged
{
    /// <summary>"tag" | "dedup" | "upscale" — dashboard routing and the open-operation key.</summary>
    string Key { get; }

    /// <summary>The mode's display name, exactly as its route title reads it.</summary>
    string Title { get; }

    /// <summary>One-line description for the dashboard panel — the chooser copy, reused.</summary>
    string Description { get; }

    /// <summary>
    /// The mode's help topic (<c>help:HelpScope.Topic</c>) while it is on screen,
    /// taken from <see cref="Services.HelpTopics"/> so a renamed topic is a
    /// compile error rather than a blank page.
    /// </summary>
    string HelpTopic { get; }

    /// <summary>
    /// Read-back of the mode's current parameters (Region B): the same summary
    /// line the mode's own run page shows, so a run started from parameters
    /// nobody can see cannot happen.
    /// </summary>
    string ParametersSummary { get; }

    /// <summary>
    /// The mode's primary run: tagging's batch, dedup's scan (Apply stays
    /// dedup-specific), upscaling's run. It is the mode's existing start command.
    /// </summary>
    IAsyncRelayCommand Run { get; }

    /// <summary>The mode's own ready-to-run gate — mirrors that command's CanExecute.</summary>
    bool IsRunEnabled { get; }

    /// <summary>Why the run is disabled, from the same conditions the gate checks; null when enabled.</summary>
    string? RunDisabledReason { get; }

    /// <summary>
    /// The mode's result surface for Region C (tagging's results grid, dedup's
    /// reviewed groups, upscaling's run output), or null before the first run.
    /// </summary>
    object? Report { get; }
}
