using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Synapic.Avalonia.Views;

/// <summary>
/// Reveals the newest row of a tailing list without ever asking Avalonia to lay
/// out a control that is on its way out.
///
/// Scrolling straight from a <c>CollectionChanged</c> handler is what produced
/// "InvalidOperationException: Invalid Arrange rectangle" when the app closed.
/// The UI log sink is process-wide and Serilog keeps writing during shutdown, so
/// an already-queued UI-thread update could still append to the log collection
/// after the window was gone and then ask a detached list — with no valid
/// viewport left — to reveal the new row. Every request is therefore deferred to
/// the dispatcher and dropped unless the control is still attached with rows to
/// show.
/// </summary>
internal static class ListAutoScroll
{
    /// <summary>Reveal the final item once the current layout pass has finished.</summary>
    public static void ToEnd(ItemsControl list) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!CanReveal(list, list.ItemCount)) return;
            list.ScrollIntoView(list.ItemCount - 1);
        });

    /// <summary>
    /// Whether a control may be asked to reveal a row right now: it must have
    /// rows, still be attached to a visual tree, and still have a visual root.
    /// A closing window fails the last two.
    /// </summary>
    public static bool CanReveal(Control control, int itemCount) =>
        itemCount > 0
        && control.GetVisualRoot() is not null
        && control.IsAttachedToVisualTree();
}
