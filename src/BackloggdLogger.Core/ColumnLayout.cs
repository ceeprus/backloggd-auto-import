namespace BackloggdLogger.Core;

// One list column, in pixels. Fixed keeps Width; flexible shares the room left by Weight, at least Min while
// there is room, and gives up width down to Floor when there isn't, the lowest ShrinkOrder first.
public readonly record struct ColumnSpec(int Width, int Weight, int Min, int Floor, int ShrinkOrder)
{
    public bool Flexible => Weight > 0;

    public static ColumnSpec Fixed(int width) => new(Math.Max(0, width), 0, 0, 0, 0);

    public static ColumnSpec Flex(int weight, int min, int floor, int shrinkOrder) =>
        new(0, Math.Max(1, weight), Math.Max(0, min), Math.Clamp(floor, 0, Math.Max(0, min)), shrinkOrder);
}

public static class ColumnLayout
{
    // Column widths that add up to width exactly, as long as width holds the fixed columns and
    // the flexible columns' floors. Under that, the columns are wider than the list.
    public static int[] Fit(int width, IReadOnlyList<ColumnSpec> columns)
    {
        var widths = columns.Select(c => c.Flexible ? c.Min : c.Width).ToArray();
        var flexible = Enumerable.Range(0, columns.Count).Where(i => columns[i].Flexible).ToList();
        var room = width - columns.Where(c => !c.Flexible).Sum(c => c.Width);
        if (flexible.Count == 0)
        {
            if (widths.Length > 0 && room > 0) widths[^1] += room;
            return widths;
        }

        var short_ = flexible.Sum(i => columns[i].Min) - room;
        if (short_ > 0)
        {
            foreach (var i in flexible.OrderBy(i => columns[i].ShrinkOrder))
            {
                var take = Math.Min(short_, columns[i].Min - columns[i].Floor);
                widths[i] -= take;
                short_ -= take;
            }
            return widths;
        }

        // By weight; a column whose share is under its minimum keeps the minimum and leaves the
        // sharing, until every share left is at least its column's minimum.
        var sharing = flexible.ToList();
        while (true)
        {
            var weight = sharing.Sum(i => columns[i].Weight);
            var under = sharing.Where(i => (long)room * columns[i].Weight / weight < columns[i].Min).ToList();
            if (under.Count == 0 || under.Count == sharing.Count) break;
            foreach (var i in under)
            {
                room -= columns[i].Min;
                sharing.Remove(i);
            }
        }
        var total = sharing.Sum(i => columns[i].Weight);
        var used = 0;
        foreach (var i in sharing)
        {
            widths[i] = Math.Max(columns[i].Min, (int)((long)room * columns[i].Weight / total));
            used += widths[i];
        }
        // What rounding left over goes to the heaviest column (the first of them).
        var heaviest = sharing.OrderByDescending(i => columns[i].Weight).First();
        widths[heaviest] += room - used;
        return widths;
    }
}
