using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Asura.App.ViewModels;

/// <summary>
/// Puts things on screen a handful at a time, handing the thread back between
/// each handful.
///
/// Anything a preview shows — rows of a table, lines of a dump, blocks of a
/// document — has to be attached on the thread that draws, and a collection
/// attached in one go holds that thread for as long as the collection is long.
/// Filling in bounded steps means the cost of showing a file stops being a
/// function of the file's size: the panel answers between every step, whatever
/// it is showing and however much of it there is.
/// </summary>
internal static class IncrementalFill
{
    /// <summary>
    /// Items attached per turn. Small enough that a turn is over before anyone
    /// notices it began, large enough that a long list does not take hundreds
    /// of turns to arrive.
    /// </summary>
    public const int DefaultStep = 128;

    public static async Task FillAsync<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> items,
        CancellationToken cancellationToken,
        int step = DefaultStep)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);

        target.Clear();
        for (var index = 0; index < items.Count; index += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(index + step, items.Count);
            for (var offset = index; offset < end; offset++)
            {
                target.Add(items[offset]);
            }

            if (end < items.Count)
            {
                // Back to the queue: whatever the reader did while this step
                // ran is served before the next one starts.
                await Task.Yield();
            }
        }
    }
}
