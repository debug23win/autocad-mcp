using System.Text.Json;

namespace CadMcp.Core;

public sealed record VerificationCheck(string Label, string Status, object? Expected = null,
    object? Actual = null, double? Tolerance = null, string? Reason = null);
public sealed record VerificationReport(string State, int EntityCount, int ErasedCount, object? Bounds,
    IReadOnlyList<VerificationCheck> Checks)
{
    public int Passed => Checks.Count(c => c.Status == "passed");
    public int Failed => Checks.Count(c => c.Status == "failed");
    public int Unverified => Checks.Count(c => c.Status == "unverified");
}

/// <summary>Acceptance checks on actual database readback, never on the requested operations.</summary>
public static class DrawingVerification
{
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    { "type", "layer", "length", "radius", "area", "volume", "measurement", "closed", "vertex_count", "face_count",
      "position", "center", "start", "end", "bounds.min", "bounds.max", "bounds.size", "distance" };
    public static JsonElement Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Wire.Element(new { });
        if (json.Length > 32768) throw new CadFault("INVALID_EXPECTATIONS", "Acceptance plan exceeds 32768 characters");
        using var doc = JsonDocument.Parse(json);
        var plan = doc.RootElement;
        if (plan.ValueKind != JsonValueKind.Object) throw new CadFault("INVALID_EXPECTATIONS", "Expected an acceptance object");
        Allowed(plan, "units", "entity_count", "checks", "enforce");
        if (plan.TryGetProperty("units", out _)) EditPlan.RequiredText(plan, "units");
        if (plan.TryGetProperty("enforce", out var enforce) && enforce.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Invalid("enforce must be Boolean");
        if (plan.TryGetProperty("entity_count", out var count) && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var n) || n < 0 || n > 1000)) Invalid("entity_count must be 0..1000");
        if (plan.TryGetProperty("checks", out var checks))
        {
            if (checks.ValueKind != JsonValueKind.Array || checks.GetArrayLength() > 100) Invalid("checks must contain at most 100 entries");
            foreach (var check in checks.EnumerateArray())
            {
                Allowed(check, "label", "handle", "target", "property", "expected", "tolerance", "first", "second");
                var property = EditPlan.RequiredText(check, "property");
                if (!Properties.Contains(property)) Invalid("Unsupported measurement: " + property);
                if (!check.TryGetProperty("expected", out var expected) || expected.ValueKind is JsonValueKind.Null or JsonValueKind.Object) Invalid("A scalar or coordinate array expected value is required");
                if (check.TryGetProperty("tolerance", out _)) { if (EditPlan.Numeric(check, "tolerance") < 0) Invalid("Tolerance must be nonnegative"); }
                if (expected.ValueKind == JsonValueKind.Number) Number(expected);
                if (expected.ValueKind == JsonValueKind.Array)
                {
                    if (expected.GetArrayLength() is < 2 or > 3) Invalid("Expected coordinates have 2 or 3 values");
                    foreach (var value in expected.EnumerateArray()) Number(value);
                }
                if (check.TryGetProperty("label", out _)) EditPlan.RequiredText(check, "label");
                if (property == "distance")
                {
                    if (!check.TryGetProperty("first", out var first) || !check.TryGetProperty("second", out var second)) Invalid("Distance needs first and second endpoints");
                    Endpoint(check.GetProperty("first")); Endpoint(check.GetProperty("second"));
                }
                else Target(check);
            }
        }
        return plan.Clone();
    }
    private static void Endpoint(JsonElement value)
    {
        Allowed(value, "handle", "target", "point");
        Target(value);
        if (value.Text("point") is not ("start" or "end" or "position" or "center")) Invalid("Distance point must be start/end/position/center");
    }
    private static void Target(JsonElement value)
    {
        if (value.TryGetProperty("handle", out _) == value.TryGetProperty("target", out _)) Invalid("Each target needs exactly one handle or target");
        EditPlan.RequiredText(value, value.TryGetProperty("handle", out _) ? "handle" : "target");
    }
    private static void Allowed(JsonElement item, params string[] fields)
    {
        if (item.ValueKind != JsonValueKind.Object) Invalid("Expected an object");
        var names = new HashSet<string>();
        foreach (var p in item.EnumerateObject()) if (!fields.Contains(p.Name) || !names.Add(p.Name)) Invalid("Unknown or duplicate field: " + p.Name);
    }
    private static void Invalid(string reason) => throw new CadFault("INVALID_EXPECTATIONS", reason);
    private static double Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) && double.IsFinite(n)
        ? n : throw new CadFault("INVALID_EXPECTATIONS", "Values must be finite numbers");
    public static VerificationReport Evaluate(IEnumerable<JsonElement> readback, string units, JsonElement plan,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        var all = readback.ToArray();
        var live = all.Where(e => !e.TryGetProperty("erased", out var erased) || erased.ValueKind != JsonValueKind.True).ToArray();
        var byHandle = all.Where(e => e.Text("handle") is not null).ToDictionary(e => e.Text("handle")!, StringComparer.OrdinalIgnoreCase);
        var checks = new List<VerificationCheck>();
        if (plan.TryGetProperty("units", out var unit)) checks.Add(new("Drawing units", unit.GetString() == units ? "passed" : "failed", unit.GetString(), units));
        if (plan.TryGetProperty("entity_count", out var count)) checks.Add(new("Live entity count", count.GetInt32() == live.Length ? "passed" : "failed", count.GetInt32(), live.Length, 0));
        JsonElement? Resolve(JsonElement check)
        {
            string? handle = check.Text("handle");
            if (handle is null && check.Text("target") is { } target && aliases is not null) aliases.TryGetValue(target, out handle);
            return handle is not null && byHandle.TryGetValue(handle, out var entity) ? entity : null;
        }
        double[]? Coordinates(JsonElement? e, string point) => e is { } value && value.TryGetProperty(point, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(Number).ToArray() : null;
        if (plan.TryGetProperty("checks", out var requested)) foreach (var check in requested.EnumerateArray())
        {
            string property = check.Text("property")!;
            string label = check.Text("label") ?? (check.Text("target") ?? check.Text("handle") ?? "Between objects") + ": " + property;
            var expected = check.GetProperty("expected");
            double tolerance = check.TryGetProperty("tolerance", out var t) ? Number(t) : 1e-6;
            object? actual = null;
            if (property == "distance")
            {
                var first = check.GetProperty("first"); var second = check.GetProperty("second");
                var a = Coordinates(Resolve(first), first.Text("point")!); var b = Coordinates(Resolve(second), second.Text("point")!);
                if (a?.Length == 3 && b?.Length == 3) actual = Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
            }
            else if (Resolve(check) is { } entity)
            {
                if (property.StartsWith("bounds.", StringComparison.Ordinal) && entity.TryGetProperty("bounds", out var bounds))
                {
                    var min = Coordinates(bounds, "min"); var max = Coordinates(bounds, "max");
                    actual = property switch { "bounds.min" => min, "bounds.max" => max,
                        "bounds.size" when min is not null && max is not null => max.Zip(min, (x, y) => x - y).ToArray(), _ => null };
                }
                else if (entity.TryGetProperty(property, out var p)) actual = p.Clone();
            }
            if (actual is null) { checks.Add(new(label, "unverified", expected.Clone(), Reason: "Entity or requested measurement is unavailable")); continue; }
            var value = Wire.Element(actual); bool pass;
            if (expected.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number) pass = Math.Abs(Number(value) - Number(expected)) <= tolerance;
            else if (expected.ValueKind == JsonValueKind.Array && value.ValueKind == JsonValueKind.Array)
                pass = expected.GetArrayLength() == value.GetArrayLength() && expected.EnumerateArray().Zip(value.EnumerateArray(), (x, y) => Math.Abs(Number(x) - Number(y)) <= tolerance).All(x => x);
            else pass = expected.ValueKind == value.ValueKind && (expected.ValueKind == JsonValueKind.String ? expected.GetString() == value.GetString() : expected.GetRawText() == value.GetRawText());
            checks.Add(new(label, pass ? "passed" : "failed", expected.Clone(), value, tolerance));
        }
        var bounded = live.Where(e => e.TryGetProperty("bounds", out _)).ToArray();
        object? aggregate = bounded.Length == 0 ? null : new {
            min = Enumerable.Range(0, 3).Select(i => bounded.Min(e => e.GetProperty("bounds").GetProperty("min")[i].GetDouble())).ToArray(),
            max = Enumerable.Range(0, 3).Select(i => bounded.Max(e => e.GetProperty("bounds").GetProperty("max")[i].GetDouble())).ToArray(),
            complete = bounded.Length == live.Length };
        string state = checks.Count == 0 ? "not_requested" : checks.Any(c => c.Status == "failed") ? "failed" : checks.Any(c => c.Status == "unverified") ? "unverified" : "passed";
        return new(state, live.Length, all.Length - live.Length, aggregate, checks);
    }
}
