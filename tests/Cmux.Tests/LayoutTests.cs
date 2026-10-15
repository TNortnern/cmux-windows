using System.IO;
using Cmux.Core.Models;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class LayoutTests
{
    // Builds A | (B | C) the same way the UI does: each Split turns a leaf into a container.
    private static SplitNode ThreePaneChain()
    {
        var root = SplitNode.CreateLeaf("A");
        var b = root.Split(SplitDirection.Vertical, "B");
        b.Split(SplitDirection.Vertical, "C");
        return root;
    }

    private static string[] LeafIds(SplitNode node) =>
        node.GetLeaves().Select(l => l.PaneId!).ToArray();

    [Fact]
    public void Equalize_ThreePaneChain_GivesEachPaneOneThird()
    {
        var root = ThreePaneChain();
        root.SplitRatio = 0.9;

        root.Equalize();

        var widths = root.GetLeafRects().Select(r => r.Width).ToList();
        widths.Should().HaveCount(3);
        foreach (var width in widths)
            width.Should().BeApproximately(1.0 / 3, 1e-9);
    }

    [Fact]
    public void Equalize_ColumnsLayout_WeightsByLeafCount()
    {
        var root = SplitNode.CreateColumns(3);
        root.SplitRatio = 0.1;

        root.Equalize();

        // The first side holds two of the three panes, so it gets two thirds of the width.
        root.SplitRatio.Should().BeApproximately(2.0 / 3, 1e-9);
    }

    [Fact]
    public void LeafCount_CountsOnlyPanes()
    {
        ThreePaneChain().LeafCount.Should().Be(3);
        SplitNode.CreateGrid().LeafCount.Should().Be(4);
    }

    [Fact]
    public void Split_WithExplicitPaneId_UsesThatIdForNewPane()
    {
        var root = SplitNode.CreateLeaf("A");

        var created = root.Split(SplitDirection.Horizontal, "NEW");

        created.PaneId.Should().Be("NEW");
        LeafIds(root).Should().Equal("A", "NEW");
    }

    [Fact]
    public void ResizePane_GrowsPaneTowardTrailingDivider()
    {
        var root = SplitNode.CreateColumns(2);
        var ids = LeafIds(root);

        root.ResizePane(ids[0], PaneDirection.Right, 0.1).Should().BeTrue();

        root.SplitRatio.Should().BeApproximately(0.6, 1e-9);
    }

    [Fact]
    public void ResizePane_GrowsPaneTowardLeadingDivider()
    {
        var root = SplitNode.CreateColumns(2);
        root.SplitRatio = 0.6;
        var ids = LeafIds(root);

        root.ResizePane(ids[1], PaneDirection.Left, 0.1).Should().BeTrue();

        root.SplitRatio.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void ResizePane_WithoutMatchingDivider_ReturnsFalse()
    {
        var root = SplitNode.CreateColumns(2);
        var ids = LeafIds(root);

        // The leftmost pane has no divider on its left.
        root.ResizePane(ids[0], PaneDirection.Left, 0.1).Should().BeFalse();
        root.SplitRatio.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void ResizePane_ClampsRatio()
    {
        var root = SplitNode.CreateColumns(2);

        root.ResizePane(LeafIds(root)[0], PaneDirection.Right, 10);

        root.SplitRatio.Should().BeApproximately(0.98, 1e-9);
    }

    [Fact]
    public void ResizePane_SaturatedDivider_ReturnsFalse()
    {
        var root = SplitNode.CreateColumns(2);
        root.SplitRatio = 0.98;

        root.ResizePane(LeafIds(root)[0], PaneDirection.Right, 0.1).Should().BeFalse();
        root.SplitRatio.Should().BeApproximately(0.98, 1e-9);
    }

    [Fact]
    public void ResizePane_InnerDividerAtLimit_FallsThroughToOuterDivider()
    {
        var root = SplitNode.CreateColumns(3); // (A | B) | C
        var ids = LeafIds(root);
        root.First!.SplitRatio = 0.98;

        root.ResizePane(ids[1], PaneDirection.Right, 0.1).Should().BeTrue();

        root.First!.SplitRatio.Should().BeApproximately(0.98, 1e-9);
        root.SplitRatio.Should().BeApproximately(2.0 / 3 + 0.1, 1e-9);
    }

    [Fact]
    public void Equalize_ManyPanes_RatioBelowOldFloor_StaysResizable()
    {
        // One pane beside 29 panes: Equalize gives the single pane 1/30 of the width.
        var root = new SplitNode
        {
            IsLeaf = false,
            Direction = SplitDirection.Vertical,
            First = SplitNode.CreateLeaf("A"),
            Second = SplitNode.CreateColumns(29),
        };

        root.Equalize();
        root.SplitRatio.Should().BeApproximately(1.0 / 30, 1e-9);

        root.ResizePane("A", PaneDirection.Right, 0.01).Should().BeTrue();
        root.SplitRatio.Should().BeApproximately(1.0 / 30 + 0.01, 1e-9);
    }

    [Fact]
    public void ResizePane_UsesNearestAncestorWithMatchingOrientation()
    {
        var grid = SplitNode.CreateGrid();
        var ids = LeafIds(grid); // a, b (top row), c, d (bottom row)

        // Right resizes the vertical split of the top row, not the root horizontal split.
        grid.ResizePane(ids[0], PaneDirection.Right, 0.1).Should().BeTrue();

        grid.First!.SplitRatio.Should().BeApproximately(0.6, 1e-9);
        grid.SplitRatio.Should().BeApproximately(0.5, 1e-9);

        // Down resizes the horizontal root split.
        grid.ResizePane(ids[0], PaneDirection.Down, 0.1).Should().BeTrue();

        grid.SplitRatio.Should().BeApproximately(0.6, 1e-9);
    }

    [Fact]
    public void FocusInDirection_2x2Grid_PicksNearestOverlappingNeighbour()
    {
        var grid = SplitNode.CreateGrid();
        var a = LeafIds(grid)[0];
        var b = LeafIds(grid)[1];
        var c = LeafIds(grid)[2];
        var d = LeafIds(grid)[3];

        grid.FindNeighborPaneId(a, PaneDirection.Right).Should().Be(b);
        grid.FindNeighborPaneId(a, PaneDirection.Down).Should().Be(c);
        grid.FindNeighborPaneId(a, PaneDirection.Left).Should().BeNull();
        grid.FindNeighborPaneId(a, PaneDirection.Up).Should().BeNull();

        grid.FindNeighborPaneId(d, PaneDirection.Left).Should().Be(c);
        grid.FindNeighborPaneId(d, PaneDirection.Up).Should().Be(b);
        grid.FindNeighborPaneId(d, PaneDirection.Right).Should().BeNull();
        grid.FindNeighborPaneId(d, PaneDirection.Down).Should().BeNull();

        grid.FindNeighborPaneId(b, PaneDirection.Down).Should().Be(d);
        grid.FindNeighborPaneId(c, PaneDirection.Right).Should().Be(d);
    }

    [Fact]
    public void FocusInDirection_UnknownPane_ReturnsNull()
    {
        SplitNode.CreateGrid().FindNeighborPaneId("missing", PaneDirection.Right).Should().BeNull();
    }

    [Fact]
    public void GetLeafRects_FollowsSplitRatios()
    {
        var root = SplitNode.CreateColumns(2);
        root.SplitRatio = 0.25;
        var ids = LeafIds(root);

        var rects = root.GetLeafRects();

        var left = rects.Single(r => r.PaneId == ids[0]);
        var right = rects.Single(r => r.PaneId == ids[1]);
        left.Width.Should().BeApproximately(0.25, 1e-9);
        right.X.Should().BeApproximately(0.25, 1e-9);
        right.Width.Should().BeApproximately(0.75, 1e-9);
    }

    [Fact]
    public void WorkingDirectoryPolicy_RejectsEmptyRelativeUncAndMissingPaths()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cmux-missing-" + Guid.NewGuid().ToString("N"));

        WorkingDirectoryPolicy.IsUsable(null).Should().BeFalse();
        WorkingDirectoryPolicy.IsUsable("   ").Should().BeFalse();
        WorkingDirectoryPolicy.IsUsable(@"relative\dir").Should().BeFalse();
        WorkingDirectoryPolicy.IsUsable(@"\\server\share").Should().BeFalse();
        WorkingDirectoryPolicy.IsUsable(missing).Should().BeFalse();
    }

    [Fact]
    public void WorkingDirectoryPolicy_AcceptsExistingLocalFolder()
    {
        var temp = Path.GetTempPath();

        WorkingDirectoryPolicy.IsUsable(temp).Should().BeTrue();
        WorkingDirectoryPolicy.Sanitize(temp).Should().Be(temp.Trim());
    }

    [Fact]
    public void WorkingDirectoryPolicy_ChooseForNewPane_PrefersLiveThenCachedThenFallback()
    {
        var usable = Path.GetTempPath();
        var missing = Path.Combine(Path.GetTempPath(), "cmux-missing-" + Guid.NewGuid().ToString("N"));

        WorkingDirectoryPolicy.ChooseForNewPane(usable, missing, usable).Should().Be(usable.Trim());
        WorkingDirectoryPolicy.ChooseForNewPane(missing, usable, null).Should().Be(usable.Trim());
        WorkingDirectoryPolicy.ChooseForNewPane(null, null, usable).Should().Be(usable.Trim());
        WorkingDirectoryPolicy.ChooseForNewPane(missing, missing, missing).Should().BeNull();
    }
}
