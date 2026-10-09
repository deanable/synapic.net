using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Synapic.Main.ViewModels.Operations;

/// <summary>
/// The shell's navigation state (CONTEXT D-03): <see cref="Current"/> is null on
/// the dashboard and names the one open operation otherwise, so a second
/// navigation model cannot grow during the strangler window.
///
/// Who writes and who reads, as it stands: the route machine is still the entry
/// point — <c>MainWindowViewModel</c>'s route methods call <see cref="SetCurrent"/>,
/// and the dashboard panels bind those same route commands — while the readers
/// today are the navigation pins in the tests. <see cref="Open"/> and
/// <see cref="Home"/> are the surface Phase 3 renders the panels and the Home
/// entry through once the sidebar it duplicates is deleted; nothing binds them
/// yet, and the help scope reads <c>ContextHelpTopic</c>, not this state.
/// </summary>
public partial class ShellViewModel : ViewModelBase
{
    private readonly Dictionary<string, IOperationViewModel> _operations;
    private readonly Action<string> _openRoute;
    private readonly Action _goHome;

    /// <param name="operations">The operations the dashboard can open (the
    /// Phase 1 adapters, keyed by <see cref="IOperationViewModel.Key"/>).</param>
    /// <param name="openRoute">Enters an operation through the existing route
    /// machine; the route then reports back through <see cref="SetCurrent"/>.</param>
    /// <param name="goHome">Returns to the dashboard through the existing route
    /// machine, which clears <see cref="Current"/> the same way.</param>
    public ShellViewModel(
        IEnumerable<IOperationViewModel> operations,
        Action<string> openRoute,
        Action goHome)
    {
        _operations = operations.ToDictionary(o => o.Key, StringComparer.Ordinal);
        _openRoute = openRoute;
        _goHome = goHome;
    }

    /// <summary>
    /// The open operation, or null for the dashboard. Written only by
    /// <see cref="SetCurrent"/>, so every entry point — a dashboard panel, the
    /// sidebar, a route command invoked by a test — lands on the same state.
    /// </summary>
    [ObservableProperty]
    private IOperationViewModel? _current;

    /// <summary>The operations the dashboard can open, keyed by <c>Key</c>.</summary>
    public IReadOnlyCollection<IOperationViewModel> Operations => _operations.Values;

    /// <summary>The operation with this key, or null when the key is unknown.</summary>
    public IOperationViewModel? Operation(string key) =>
        _operations.TryGetValue(key, out var operation) ? operation : null;

    /// <summary>
    /// Opens an operation the way a dashboard panel does: through the existing
    /// route machine, which is also what sets <see cref="Current"/>. Reserved for
    /// Phase 3 — today's panels bind the route commands directly.
    /// </summary>
    public void Open(string key)
    {
        if (!_operations.ContainsKey(key)) return;
        _openRoute(key);
    }

    /// <summary>Back to the dashboard: the route machine goes home and clears Current.</summary>
    public void Home() => _goHome();

    /// <summary>What the shell's route methods report, so Current tracks the open operation.</summary>
    public void SetCurrent(string? key) =>
        Current = key is null ? null : Operation(key);
}
