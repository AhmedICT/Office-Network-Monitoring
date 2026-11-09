using Cbh.Nms.Core.Inventory;

namespace Cbh.Nms.Tests;

public class DependencyGraphTests
{
    // Router(1) → Switch(2) → Server(3), Switch(2) → AP(4)
    private static readonly Dictionary<int, List<int>> Edges = new()
    {
        [2] = [1],
        [3] = [2],
        [4] = [2],
    };

    [Fact]
    public void Self_parent_is_a_cycle() => Assert.True(DependencyGraph.WouldCreateCycle(3, 3, Edges));

    [Fact]
    public void Making_a_descendant_the_parent_is_a_cycle() =>
        Assert.True(DependencyGraph.WouldCreateCycle(childId: 1, parentId: 3, Edges));   // router under server

    [Fact]
    public void Sibling_parent_is_fine() => Assert.False(DependencyGraph.WouldCreateCycle(childId: 4, parentId: 3, Edges));

    [Fact]
    public void New_device_under_leaf_is_fine() => Assert.False(DependencyGraph.WouldCreateCycle(childId: 5, parentId: 3, Edges));

    [Fact]
    public void Existing_cycle_in_data_does_not_loop_forever()
    {
        var bad = new Dictionary<int, List<int>> { [1] = [2], [2] = [1] };
        Assert.False(DependencyGraph.WouldCreateCycle(childId: 9, parentId: 1, bad));
    }
}
