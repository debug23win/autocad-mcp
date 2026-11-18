namespace CadMcp.Core;

/// <summary>Bounds for the native selected-scope review. Heavy native work has separate budgets.</summary>
public static class StandardReviewLimits
{
    // At 500 entities there are at most 124750 managed box comparisons across the text/solid groups.
    // The native adapter caches each box once; it does not retrieve native extents per pair.
    public const int MaxEntities = 500, MaxSolidChecks = 2000, MaxTableCells = 20000,
        MaxCurvePoints = 250000, MaxFindings = 1000;

    public static void CheckScope(int entities)
    {
        if (entities > MaxEntities)
            throw new CadFault("REVIEW_TOO_LARGE", "At least " + entities + " entities in scope; standard review accepts up to " + MaxEntities +
                " together. Narrow the scope with handles or layers, or use checks:[\"topology\"] for linework. Separate scopes do not check interactions between them.");
    }
}

public readonly record struct ReviewBounds(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    public bool IsValid => double.IsFinite(MinX) && double.IsFinite(MinY) && double.IsFinite(MinZ) &&
        double.IsFinite(MaxX) && double.IsFinite(MaxY) && double.IsFinite(MaxZ) && MinX <= MaxX && MinY <= MaxY && MinZ <= MaxZ;

    public bool Overlaps(ReviewBounds other, bool volume) =>
        MaxX > other.MinX + 1e-7 && other.MaxX > MinX + 1e-7 && MaxY > other.MinY + 1e-7 && other.MaxY > MinY + 1e-7 &&
        (!volume || MaxZ > other.MinZ + 1e-7 && other.MaxZ > MinZ + 1e-7);
}

public sealed record ReviewBox<T>(T Item, ReviewBounds Bounds, object? Group = null);
public sealed record ReviewPairCoverage(int PairComparisons, int Candidates, int ChecksAttempted, int SkippedByLimit);

/// <summary>
/// Scans the entire selected scope, including pairs across any former page boundary. After the native check
/// budget is spent it still counts remaining candidate pairs using cached boxes, so coverage stays explicit.
/// </summary>
public static class StandardReviewPairs
{
    public static ReviewPairCoverage Scan<T>(IReadOnlyList<ReviewBox<T>> items, bool volume, int maxChecks,
        Action<T, T> check, CancellationToken ct = default)
    {
        StandardReviewLimits.CheckScope(items.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChecks);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (!item.Bounds.IsValid) throw new ArgumentException("Unreadable bounds must be reported before scanning pairs", nameof(items));
        }
        int comparisons = 0, candidates = 0, attempted = 0;
        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int j = i + 1; j < items.Count; j++)
            {
                ct.ThrowIfCancellationRequested();
                comparisons++;
                if (!Equals(items[i].Group, items[j].Group) || !items[i].Bounds.Overlaps(items[j].Bounds, volume)) continue;
                candidates++;
                if (attempted >= maxChecks) continue;
                attempted++;
                check(items[i].Item, items[j].Item);
            }
        }
        return new(comparisons, candidates, attempted, candidates - attempted);
    }
}

/// <summary>Reserves a whole table before its expensive check; rejected items are never called complete.</summary>
public sealed class ReviewWorkBudget(long maximum)
{
    public long Used { get; private set; }
    public long Skipped { get; private set; }
    public long Remaining => Math.Max(0, maximum - Used);

    public bool TryReserve(long units)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        if (units > Remaining) { Skipped += units; return false; }
        Used += units;
        return true;
    }
}

/// <summary>Bounded response storage; omitted findings still count and still determine the overall severity.</summary>
public sealed class ReviewFindings<T>(Func<T, (string Code, string Severity)> classify, int maximum = StandardReviewLimits.MaxFindings)
{
    private readonly List<T> items = [];
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private bool warning, unverified;
    public IReadOnlyList<T> Items => items;
    public IReadOnlyDictionary<string, int> Counts => counts;
    public bool Truncated { get; private set; }
    public string State => unverified ? "unverified" : warning ? "review_required" : "passed";

    public void Add(T item)
    {
        var (code, severity) = classify(item);
        Count(code, severity, 1);
        if (items.Count < maximum) items.Add(item); else Truncated = true;
    }

    public void AddOmitted(string code, string severity, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0) return;
        Count(code, severity, count);
        Truncated = true;
    }

    private void Count(string code, string severity, int count)
    {
        counts[code] = counts.GetValueOrDefault(code) + count;
        warning |= severity == "warning";
        unverified |= severity == "unverified";
    }
}
