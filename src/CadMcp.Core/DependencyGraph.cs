namespace CadMcp.Core;

/// <summary>Select dependent formula cells without evaluating unrelated components.</summary>
public static class DependencyGraph
{
    public sealed record Formula(string Target, IReadOnlyList<string> Sources);
    public static HashSet<string> Affected(IEnumerable<Formula> formulas, IReadOnlySet<string> changed)
    {
        var all = formulas.ToArray();
        var targets = all.Select(f => f.Target).ToHashSet(StringComparer.Ordinal);
        var dependents = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var formula in all)
            foreach (var source in formula.Sources)
            {
                if (!dependents.TryGetValue(source, out var next)) dependents[source] = next = new(StringComparer.Ordinal);
                next.Add(formula.Target);
            }
        var result = changed.Where(targets.Contains).ToHashSet(StringComparer.Ordinal);
        var visited = changed.ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(changed);
        while (pending.TryDequeue(out var source))
            if (dependents.TryGetValue(source, out var next))
                foreach (var target in next)
                {
                    result.Add(target);
                    if (visited.Add(target)) pending.Enqueue(target);
                }
        return result;
    }
}
