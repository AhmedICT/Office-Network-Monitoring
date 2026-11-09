namespace Cbh.Nms.Core.Inventory;

public static class DependencyGraph
{
    /// <summary>
    /// True if making <paramref name="parentId"/> a parent of <paramref name="childId"/> would create a
    /// cycle (SCR-04 validation), i.e. the child is already an ancestor of the proposed parent.
    /// </summary>
    /// <param name="parentsOf">Existing edges: device id → its parent ids.</param>
    public static bool WouldCreateCycle(int childId, int parentId, IReadOnlyDictionary<int, List<int>> parentsOf)
    {
        if (childId == parentId) return true;
        var seen = new HashSet<int>();
        var stack = new Stack<int>([parentId]);
        while (stack.TryPop(out var id))
        {
            if (id == childId) return true;
            if (!seen.Add(id) || !parentsOf.TryGetValue(id, out var parents)) continue;
            foreach (var p in parents) stack.Push(p);
        }
        return false;
    }
}
