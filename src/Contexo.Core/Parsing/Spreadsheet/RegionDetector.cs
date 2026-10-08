namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>A block of connected cells on a sheet, with the lone cells that act as its title.</summary>
internal sealed class DetectedRegion
{
    public DetectedRegion(CellRect rect, long realCells, (int Row, int Column)? singleCell)
    {
        Rect = rect;
        RealCells = realCells;
        SingleCell = singleCell;
    }

    public CellRect Rect { get; set; }

    /// <summary>Cells that hold a value (merged-area fillers excluded).</summary>
    public long RealCells { get; set; }

    /// <summary>Position of the only value cell when <see cref="RealCells"/> is 1.</summary>
    public (int Row, int Column)? SingleCell { get; set; }

    /// <summary>Single cells just above the region, top to bottom. Their text is the table caption.</summary>
    public List<(int Row, int Column)> Captions { get; } = [];
}

/// <summary>
/// Finds the table-like blocks of a sheet: 4-neighbour flood fill over non-empty cells, then merging of regions whose
/// bounding boxes overlap, then attaching lone cells just above a region as its caption.
/// </summary>
internal static class RegionDetector
{
    public static List<DetectedRegion> Detect(SheetScan scan)
    {
        var regions = FloodFill(scan);
        MergeOverlapping(regions);
        AttachCaptions(regions);
        regions.Sort(CompareByPosition);
        return regions;
    }

    /// <summary>One region covering every non-empty cell, for files that are a single table by definition (CSV).</summary>
    public static List<DetectedRegion> WholeSheet(SheetScan scan) =>
        scan.Bounds is { } bounds ? [new DetectedRegion(bounds, scan.RealCellCount, null)] : [];

    private static int CompareByPosition(DetectedRegion a, DetectedRegion b)
    {
        var byTop = a.Rect.Top.CompareTo(b.Rect.Top);
        return byTop != 0 ? byTop : a.Rect.Left.CompareTo(b.Rect.Left);
    }

    private static List<DetectedRegion> FloodFill(SheetScan scan)
    {
        var rowCount = scan.RowNumbers.Length;
        var offsets = new int[rowCount + 1];
        for (var i = 0; i < rowCount; i++)
        {
            offsets[i + 1] = offsets[i] + (scan.Runs[i].Length / 2);
        }

        var runCount = offsets[rowCount];
        var starts = new int[runCount];
        var ends = new int[runCount];
        var parent = new int[runCount];
        for (var i = 0; i < rowCount; i++)
        {
            var runs = scan.Runs[i];
            for (var k = 0; k < runs.Length / 2; k++)
            {
                var id = offsets[i] + k;
                starts[id] = runs[2 * k];
                ends[id] = runs[(2 * k) + 1];
                parent[id] = id;
            }
        }

        // Runs in vertically adjacent rows that share a column belong to the same region.
        for (var i = 0; i + 1 < rowCount; i++)
        {
            if (scan.RowNumbers[i + 1] != scan.RowNumbers[i] + 1)
            {
                continue;
            }

            int a = offsets[i], aEnd = offsets[i + 1];
            int b = offsets[i + 1], bEnd = offsets[i + 2];
            while (a < aEnd && b < bEnd)
            {
                if (starts[a] <= ends[b] && starts[b] <= ends[a])
                {
                    Union(parent, a, b);
                }

                if (ends[a] < ends[b])
                {
                    a++;
                }
                else
                {
                    b++;
                }
            }
        }

        var componentOfRoot = new Dictionary<int, int>();
        var componentOfRun = new int[runCount];
        var regions = new List<DetectedRegion>();
        for (var i = 0; i < rowCount; i++)
        {
            var row = scan.RowNumbers[i];
            for (var id = offsets[i]; id < offsets[i + 1]; id++)
            {
                var root = Find(parent, id);
                if (!componentOfRoot.TryGetValue(root, out var component))
                {
                    component = regions.Count;
                    componentOfRoot[root] = component;
                    regions.Add(new DetectedRegion(new CellRect(row, starts[id], row, ends[id]), 0, null));
                }
                else
                {
                    regions[component].Rect = regions[component].Rect.Union(new CellRect(row, starts[id], row, ends[id]));
                }

                componentOfRun[id] = component;
            }
        }

        for (var i = 0; i < rowCount; i++)
        {
            var run = offsets[i];
            foreach (var column in scan.Columns[i])
            {
                while (ends[run] < column)
                {
                    run++;
                }

                var region = regions[componentOfRun[run]];
                region.RealCells++;
                region.SingleCell = region.RealCells == 1 ? (scan.RowNumbers[i], column) : null;
            }
        }

        return regions;
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }

        return x;
    }

    private static void Union(int[] parent, int a, int b)
    {
        var rootA = Find(parent, a);
        var rootB = Find(parent, b);
        if (rootA != rootB)
        {
            parent[rootB] = rootA;
        }
    }

    private static void MergeOverlapping(List<DetectedRegion> regions)
    {
        bool changed;
        do
        {
            changed = false;
            regions.Sort(CompareByPosition);
            for (var i = 0; i < regions.Count; i++)
            {
                for (var j = i + 1; j < regions.Count && regions[j].Rect.Top <= regions[i].Rect.Bottom; j++)
                {
                    if (!regions[i].Rect.Intersects(regions[j].Rect))
                    {
                        continue;
                    }

                    regions[i].Rect = regions[i].Rect.Union(regions[j].Rect);
                    regions[i].RealCells += regions[j].RealCells;
                    regions[i].SingleCell = null;
                    regions.RemoveAt(j);
                    j--;
                    changed = true;
                }
            }
        }
        while (changed);
    }

    private static void AttachCaptions(List<DetectedRegion> regions)
    {
        var targetsByTop = new Dictionary<int, List<DetectedRegion>>();
        foreach (var region in regions.Where(r => r.RealCells > 1))
        {
            if (!targetsByTop.TryGetValue(region.Rect.Top, out var list))
            {
                targetsByTop[region.Rect.Top] = list = [];
            }

            list.Add(region);
        }

        var captions = new List<DetectedRegion>();
        foreach (var candidate in regions.Where(r => r.RealCells == 1 && r.SingleCell is not null))
        {
            DetectedRegion? best = null;
            for (var gap = 1; gap <= 2 && best is null; gap++)
            {
                if (!targetsByTop.TryGetValue(candidate.Rect.Bottom + gap, out var targets))
                {
                    continue;
                }

                foreach (var target in targets)
                {
                    if (candidate.Rect.Left >= target.Rect.Left && candidate.Rect.Left <= target.Rect.Right)
                    {
                        best = target;
                        break;
                    }
                }
            }

            if (best is not null)
            {
                best.Captions.Add(candidate.SingleCell!.Value);
                captions.Add(candidate);
            }
        }

        var captionSet = new HashSet<DetectedRegion>(captions, ReferenceEqualityComparer.Instance);
        regions.RemoveAll(captionSet.Contains);

        foreach (var region in regions.Where(r => r.Captions.Count > 1))
        {
            region.Captions.Sort((a, b) => a.Row.CompareTo(b.Row));
        }
    }
}
