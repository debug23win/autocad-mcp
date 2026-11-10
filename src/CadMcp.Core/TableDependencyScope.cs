namespace CadMcp.Core;

/// <summary>Only a connected table component can be affected by a source/target table change.</summary>
public static class TableDependencyScope
{
    public static HashSet<string> Connected(IEnumerable<(string Target, string Source)> links, IEnumerable<string> changed)
    {
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, source) in links)
        {
            if (!adjacency.TryGetValue(target, out var a)) adjacency[target] = a = new(StringComparer.OrdinalIgnoreCase);
            if (!adjacency.TryGetValue(source, out var b)) adjacency[source] = b = new(StringComparer.OrdinalIgnoreCase);
            a.Add(source); b.Add(target);
        }
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(changed);
        while (queue.TryDequeue(out var table))
        {
            if (!selected.Add(table) || !adjacency.TryGetValue(table, out var neighbors)) continue;
            foreach (var neighbor in neighbors) queue.Enqueue(neighbor);
        }
        return selected;
    }
}
