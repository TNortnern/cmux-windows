namespace Cmux.Core.Models;

public enum SplitDirection
{
    Horizontal,
    Vertical,
}

/// <summary>Cardinal direction for pane focus and resize.</summary>
public enum PaneDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>A leaf pane's rectangle in normalized (0..1) layout coordinates.</summary>
public readonly record struct PaneRect(string PaneId, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public class SplitNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public bool IsLeaf { get; set; } = true;
    public SplitDirection Direction { get; set; } = SplitDirection.Vertical;
    public SplitNode? First { get; set; }
    public SplitNode? Second { get; set; }
    public double SplitRatio { get; set; } = 0.5;
    public string? PaneId { get; set; }

    public static SplitNode CreateLeaf(string? paneId = null)
    {
        return new SplitNode
        {
            IsLeaf = true,
            PaneId = paneId ?? Guid.NewGuid().ToString(),
        };
    }

    /// <summary>
    /// Splits this leaf node into a container with two children.
    /// Returns the new second child (the newly created pane).
    /// </summary>
    public SplitNode Split(SplitDirection direction, string? newPaneId = null)
    {
        if (!IsLeaf)
            throw new InvalidOperationException("Cannot split a non-leaf node.");

        var firstChild = new SplitNode
        {
            IsLeaf = true,
            PaneId = PaneId,
        };

        var secondChild = CreateLeaf(newPaneId);

        IsLeaf = false;
        Direction = direction;
        First = firstChild;
        Second = secondChild;
        PaneId = null;
        SplitRatio = 0.5;

        return secondChild;
    }

    /// <summary>
    /// Recursively finds the node containing the given pane ID.
    /// </summary>
    public SplitNode? FindNode(string paneId)
    {
        if (IsLeaf)
            return PaneId == paneId ? this : null;

        return First?.FindNode(paneId) ?? Second?.FindNode(paneId);
    }

    /// <summary>
    /// Finds the parent of a node with the given ID.
    /// </summary>
    public SplitNode? FindParent(string nodeId)
    {
        if (IsLeaf) return null;

        if (First?.Id == nodeId || Second?.Id == nodeId)
            return this;

        return First?.FindParent(nodeId) ?? Second?.FindParent(nodeId);
    }

    /// <summary>
    /// Returns all leaf nodes (terminal panes) in traversal order.
    /// </summary>
    public IEnumerable<SplitNode> GetLeaves()
    {
        if (IsLeaf)
        {
            yield return this;
            yield break;
        }

        if (First != null)
        {
            foreach (var leaf in First.GetLeaves())
                yield return leaf;
        }

        if (Second != null)
        {
            foreach (var leaf in Second.GetLeaves())
                yield return leaf;
        }
    }

    /// <summary>Number of leaf panes under this node.</summary>
    public int LeafCount => IsLeaf ? 1 : (First?.LeafCount ?? 0) + (Second?.LeafCount ?? 0);

    /// <summary>
    /// Removes a leaf node with the given pane ID.
    /// The surviving sibling replaces the parent container.
    /// Returns true if the removal succeeded.
    /// </summary>
    public bool Remove(string paneId)
    {
        if (IsLeaf) return false;

        // Check if one of our direct children is the target
        SplitNode? target = null;
        SplitNode? survivor = null;

        if (First is { IsLeaf: true, PaneId: not null } && First.PaneId == paneId)
        {
            target = First;
            survivor = Second;
        }
        else if (Second is { IsLeaf: true, PaneId: not null } && Second.PaneId == paneId)
        {
            target = Second;
            survivor = First;
        }

        if (target != null && survivor != null)
        {
            // Replace this node's content with the survivor's content
            IsLeaf = survivor.IsLeaf;
            Direction = survivor.Direction;
            First = survivor.First;
            Second = survivor.Second;
            SplitRatio = survivor.SplitRatio;
            PaneId = survivor.PaneId;
            return true;
        }

        // Recurse into children
        if (First?.Remove(paneId) == true) return true;
        if (Second?.Remove(paneId) == true) return true;

        return false;
    }

    /// <summary>
    /// Gets the next leaf node after the given pane ID (for focus navigation).
    /// </summary>
    public SplitNode? GetNextLeaf(string paneId)
    {
        var leaves = GetLeaves().ToList();
        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].PaneId == paneId && i + 1 < leaves.Count)
                return leaves[i + 1];
        }
        return leaves.Count > 0 ? leaves[0] : null;
    }

    /// <summary>
    /// Gets the previous leaf node before the given pane ID.
    /// </summary>
    public SplitNode? GetPreviousLeaf(string paneId)
    {
        var leaves = GetLeaves().ToList();
        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].PaneId == paneId && i - 1 >= 0)
                return leaves[i - 1];
        }
        return leaves.Count > 0 ? leaves[^1] : null;
    }

    /// <summary>Creates a layout with the given number of equal vertical columns.</summary>
    public static SplitNode CreateColumns(int count)
    {
        if (count <= 1) return CreateLeaf();
        var node = CreateLeaf();
        for (int i = 1; i < count; i++)
            node = new SplitNode
            {
                IsLeaf = false,
                Direction = SplitDirection.Vertical,
                SplitRatio = (double)i / (i + 1),
                First = node,
                Second = CreateLeaf(),
            };
        return node;
    }

    /// <summary>Creates a layout with the given number of equal horizontal rows.</summary>
    public static SplitNode CreateRows(int count)
    {
        if (count <= 1) return CreateLeaf();
        var node = CreateLeaf();
        for (int i = 1; i < count; i++)
            node = new SplitNode
            {
                IsLeaf = false,
                Direction = SplitDirection.Horizontal,
                SplitRatio = (double)i / (i + 1),
                First = node,
                Second = CreateLeaf(),
            };
        return node;
    }

    /// <summary>Creates a 2x2 grid layout.</summary>
    public static SplitNode CreateGrid()
    {
        return new SplitNode
        {
            IsLeaf = false,
            Direction = SplitDirection.Horizontal,
            SplitRatio = 0.5,
            First = new SplitNode
            {
                IsLeaf = false,
                Direction = SplitDirection.Vertical,
                SplitRatio = 0.5,
                First = CreateLeaf(),
                Second = CreateLeaf(),
            },
            Second = new SplitNode
            {
                IsLeaf = false,
                Direction = SplitDirection.Vertical,
                SplitRatio = 0.5,
                First = CreateLeaf(),
                Second = CreateLeaf(),
            },
        };
    }

    /// <summary>Creates a main+stack layout (large pane left, stacked panes right).</summary>
    public static SplitNode CreateMainStack(int stackCount = 2)
    {
        var stack = CreateLeaf();
        for (int i = 1; i < stackCount; i++)
            stack = new SplitNode
            {
                IsLeaf = false,
                Direction = SplitDirection.Horizontal,
                SplitRatio = (double)i / (i + 1),
                First = stack,
                Second = CreateLeaf(),
            };
        return new SplitNode
        {
            IsLeaf = false,
            Direction = SplitDirection.Vertical,
            SplitRatio = 0.6,
            First = CreateLeaf(),
            Second = stack,
        };
    }

    /// <summary>
    /// Sets every split ratio so each pane gets an equal share of the space. Each side is
    /// weighted by the number of panes it contains (ratio = leaves(first) / leaves(all)).
    /// </summary>
    public void Equalize()
    {
        if (IsLeaf) return;

        int first = First?.LeafCount ?? 0;
        int total = first + (Second?.LeafCount ?? 0);
        SplitRatio = total > 0 ? (double)first / total : 0.5;

        First?.Equalize();
        Second?.Equalize();
    }

    // Equalize can leave a ratio well below 0.1 when many panes share one side, so the bounds are wide.
    private const double MinRatio = 0.02;
    private const double MaxRatio = 0.98;

    /// <summary>
    /// Grows the given pane toward <paramref name="direction"/> by moving the nearest divider
    /// with matching orientation. Right/Down move the pane's trailing divider; Left/Up move its
    /// leading divider. Returns false when no divider could move (none exists, or it is already at
    /// its limit), so an outer divider of the same orientation gets the resize instead.
    /// </summary>
    public bool ResizePane(string paneId, PaneDirection direction, double delta)
    {
        if (IsLeaf) return false;

        bool vertical = direction is PaneDirection.Left or PaneDirection.Right;
        var orientation = vertical ? SplitDirection.Vertical : SplitDirection.Horizontal;
        bool trailing = direction is PaneDirection.Right or PaneDirection.Down;

        bool inFirst = First?.FindNode(paneId) != null;
        bool inSecond = Second?.FindNode(paneId) != null;
        if (!inFirst && !inSecond) return false;

        // Prefer the nearest (deepest) matching divider.
        if (inFirst && First!.ResizePane(paneId, direction, delta)) return true;
        if (inSecond && Second!.ResizePane(paneId, direction, delta)) return true;

        if (Direction != orientation) return false;

        if (trailing && inFirst)
            return TrySetRatio(SplitRatio + delta);

        if (!trailing && inSecond)
            return TrySetRatio(SplitRatio - delta);

        return false;
    }

    /// <summary>Applies the clamped ratio. Returns false when it did not change.</summary>
    private bool TrySetRatio(double ratio)
    {
        double clamped = Math.Clamp(ratio, MinRatio, MaxRatio);
        if (Math.Abs(clamped - SplitRatio) < 1e-9)
            return false;

        SplitRatio = clamped;
        return true;
    }

    /// <summary>
    /// Computes each leaf's rectangle inside the unit square from the split ratios.
    /// </summary>
    public IReadOnlyList<PaneRect> GetLeafRects()
    {
        var result = new List<PaneRect>();
        CollectLeafRects(0, 0, 1, 1, result);
        return result;
    }

    private void CollectLeafRects(double x, double y, double w, double h, List<PaneRect> result)
    {
        if (IsLeaf)
        {
            if (PaneId != null)
                result.Add(new PaneRect(PaneId, x, y, w, h));
            return;
        }

        double r = Math.Clamp(SplitRatio, 0.0, 1.0);
        if (Direction == SplitDirection.Vertical)
        {
            First?.CollectLeafRects(x, y, w * r, h, result);
            Second?.CollectLeafRects(x + w * r, y, w * (1 - r), h, result);
        }
        else
        {
            First?.CollectLeafRects(x, y, w, h * r, result);
            Second?.CollectLeafRects(x, y + h * r, w, h * (1 - r), result);
        }
    }

    /// <summary>
    /// Finds the pane nearest to <paramref name="paneId"/> in the given direction that overlaps it
    /// on the perpendicular axis. Returns null when there is none.
    /// </summary>
    public string? FindNeighborPaneId(string paneId, PaneDirection direction)
    {
        const double eps = 1e-6;
        var rects = GetLeafRects();
        var from = rects.FirstOrDefault(r => r.PaneId == paneId);
        if (from.PaneId == null) return null;

        string? best = null;
        double bestGap = double.MaxValue;
        double bestOverlap = -1;

        foreach (var candidate in rects)
        {
            if (candidate.PaneId == paneId) continue;

            double gap;
            double overlap;
            switch (direction)
            {
                case PaneDirection.Right:
                    if (candidate.X < from.Right - eps) continue;
                    gap = candidate.X - from.Right;
                    overlap = Math.Min(from.Bottom, candidate.Bottom) - Math.Max(from.Y, candidate.Y);
                    break;
                case PaneDirection.Left:
                    if (candidate.Right > from.X + eps) continue;
                    gap = from.X - candidate.Right;
                    overlap = Math.Min(from.Bottom, candidate.Bottom) - Math.Max(from.Y, candidate.Y);
                    break;
                case PaneDirection.Down:
                    if (candidate.Y < from.Bottom - eps) continue;
                    gap = candidate.Y - from.Bottom;
                    overlap = Math.Min(from.Right, candidate.Right) - Math.Max(from.X, candidate.X);
                    break;
                case PaneDirection.Up:
                    if (candidate.Bottom > from.Y + eps) continue;
                    gap = from.Y - candidate.Bottom;
                    overlap = Math.Min(from.Right, candidate.Right) - Math.Max(from.X, candidate.X);
                    break;
                default:
                    return null;
            }

            if (overlap <= eps) continue;

            if (gap < bestGap - eps || (Math.Abs(gap - bestGap) <= eps && overlap > bestOverlap))
            {
                best = candidate.PaneId;
                bestGap = gap;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    /// <summary>Swaps two panes by exchanging their PaneId values.</summary>
    public bool SwapPanes(string paneId1, string paneId2)
    {
        var node1 = FindNode(paneId1);
        var node2 = FindNode(paneId2);
        if (node1 == null || node2 == null || !node1.IsLeaf || !node2.IsLeaf) return false;
        (node1.PaneId, node2.PaneId) = (node2.PaneId, node1.PaneId);
        return true;
    }
}
