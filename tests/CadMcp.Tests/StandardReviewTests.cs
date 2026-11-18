using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class StandardReviewTests
{
    private sealed record Finding(string Code, string Severity);
    private static ReviewBounds Box(double x, double z = 0) => new(x, 0, z, x + 1, 1, z + 1);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void All_470_entities_are_scanned_together_including_pairs_across_the_former_250_boundary(bool volume)
    {
        var items = Enumerable.Range(0, 470).Select(i => new ReviewBox<int>(i, Box(i * 10), "model")).ToArray();
        items[469] = items[469] with { Bounds = items[0].Bounds };
        items[250] = items[250] with { Bounds = items[249].Bounds };
        var checkedPairs = new List<(int A, int B)>();

        var coverage = StandardReviewPairs.Scan(items, volume, StandardReviewLimits.MaxSolidChecks, (a, b) => checkedPairs.Add((a, b)));

        Assert.Equal(110215, coverage.PairComparisons);
        Assert.Equal(new[] { (0, 469), (249, 250) }, checkedPairs);
        Assert.Equal(2, coverage.Candidates);
        Assert.Equal(2, coverage.ChecksAttempted);
        Assert.Equal(0, coverage.SkippedByLimit);
    }

    [Fact]
    public void Dense_solids_stop_native_work_at_2000_but_count_every_remaining_candidate()
    {
        var items = Enumerable.Range(0, 500).Select(i => new ReviewBox<int>(i, Box(0))).ToArray();
        int nativeCalls = 0;
        var coverage = StandardReviewPairs.Scan(items, true, StandardReviewLimits.MaxSolidChecks, (_, _) => nativeCalls++);

        Assert.Equal(124750, coverage.PairComparisons);
        Assert.Equal(124750, coverage.Candidates);
        Assert.Equal(2000, nativeCalls);
        Assert.Equal(2000, coverage.ChecksAttempted);
        Assert.Equal(122750, coverage.SkippedByLimit);
        Assert.Equal(coverage.Candidates, coverage.ChecksAttempted + coverage.SkippedByLimit);
    }

    [Fact]
    public void Dense_text_completes_the_scope_with_bounded_output_and_full_finding_counts()
    {
        var items = Enumerable.Range(0, 470).Select(i => new ReviewBox<int>(i, Box(0), "model")).ToArray();
        var findings = new ReviewFindings<Finding>(f => (f.Code, f.Severity));
        var coverage = StandardReviewPairs.Scan(items, false, int.MaxValue, (_, _) => findings.Add(new("TEXT_OVERLAP", "warning")));

        Assert.Equal(110215, coverage.ChecksAttempted);
        Assert.Equal(0, coverage.SkippedByLimit);
        Assert.Equal(1000, findings.Items.Count);
        Assert.Equal(110215, findings.Counts["TEXT_OVERLAP"]);
        Assert.True(findings.Truncated);
        Assert.Equal("review_required", findings.State);

        // A later native failure must not be hidden just because text overlaps filled the output list.
        findings.Add(new("TABLE_FIT_UNVERIFIED", "unverified"));
        Assert.Equal(1, findings.Counts["TABLE_FIT_UNVERIFIED"]);
        Assert.Equal("unverified", findings.State);
        Assert.Equal(1000, findings.Items.Count);
    }

    [Fact]
    public void Texts_on_other_layouts_are_not_overlaps_and_solid_height_is_respected()
    {
        ReviewBox<int>[] items = [new(0, Box(0), "model"), new(1, Box(0, 10), "model"), new(2, Box(0), "sheet")];
        var textPairs = new List<(int, int)>();
        var textCoverage = StandardReviewPairs.Scan(items, false, int.MaxValue, (a, b) => textPairs.Add((a, b)));
        var solidCoverage = StandardReviewPairs.Scan(items, true, 2000, (_, _) => Assert.Fail("Separated solids must not reach the native check"));

        Assert.Equal(new[] { (0, 1) }, textPairs);
        Assert.Equal(3, textCoverage.PairComparisons);
        Assert.Equal(0, solidCoverage.Candidates);
    }

    [Fact]
    public void Invalid_bounds_are_rejected_before_any_pair_can_be_treated_as_checked()
    {
        ReviewBox<int>[] items = [new(0, Box(0)), new(1, Box(0)), new(2, new(0, 0, 0, double.NaN, 1, 1))];
        int calls = 0;
        Assert.Throws<ArgumentException>(() => StandardReviewPairs.Scan(items, true, 2000, (_, _) => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Cancellation_stops_between_native_checks()
    {
        using var cancel = new CancellationTokenSource();
        var items = Enumerable.Range(0, 470).Select(i => new ReviewBox<int>(i, Box(0))).ToArray();
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() => StandardReviewPairs.Scan(items, true, 2000, (_, _) =>
        {
            if (++calls == 3) cancel.Cancel();
        }, cancel.Token));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Scope_above_500_is_rejected_before_work_instead_of_silently_truncated()
    {
        var items = Enumerable.Range(0, 501).Select(i => new ReviewBox<int>(i, Box(0))).ToArray();
        int calls = 0;
        var error = Assert.Throws<CadFault>(() => StandardReviewPairs.Scan(items, true, 2000, (_, _) => calls++));
        Assert.Equal("REVIEW_TOO_LARGE", error.Code);
        Assert.Contains("500", error.Message);
        Assert.Contains("layers", error.Message);
        Assert.Contains("Separate scopes do not check interactions", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Table_budget_reserves_whole_tables_and_reports_the_omitted_cells()
    {
        var budget = new ReviewWorkBudget(StandardReviewLimits.MaxTableCells);
        Assert.True(budget.TryReserve(12000));
        Assert.False(budget.TryReserve(9000));
        Assert.Equal(12000, budget.Used);
        Assert.Equal(9000, budget.Skipped);
        Assert.True(budget.TryReserve(8000));
        Assert.Equal(20000, budget.Used);
        Assert.Equal(0, budget.Remaining);
        Assert.False(budget.TryReserve(1));
        Assert.Equal(9001, budget.Skipped);
    }

    [Fact]
    public void Omitted_topology_findings_keep_total_counts_and_unverified_severity()
    {
        var findings = new ReviewFindings<Finding>(f => (f.Code, f.Severity), maximum: 1);
        findings.Add(new("TOPOLOGY_3D_SKIPPED", "info"));
        Assert.Equal("passed", findings.State);
        findings.AddOmitted("DUPLICATE_GEOMETRY", "warning", 110215);
        Assert.Equal("review_required", findings.State);
        findings.AddOmitted("TOPOLOGY_UNVERIFIED", "unverified", 1);
        Assert.Equal("unverified", findings.State);
        Assert.Equal(110215, findings.Counts["DUPLICATE_GEOMETRY"]);
        Assert.Equal(1, findings.Counts["TOPOLOGY_UNVERIFIED"]);
        Assert.True(findings.Truncated);
        Assert.Single(findings.Items);
    }

    [Fact]
    public void Topology_of_470_curves_detects_both_pairs_across_the_old_page_boundary()
    {
        TopologyCurve Line(int handle, int location) => new(handle.ToString(), "model", [[location * 10, 0], [location * 10 + 1, 0]], false);
        var curves = Enumerable.Range(0, 470).Select(i => Line(i, i)).ToArray();
        curves[469] = Line(469, 0);
        curves[250] = Line(250, 249);
        var report = Topology.Analyze(curves, new(1e-6, Endpoints: false, Crossings: false, MaxFindings: 100));

        Assert.Equal(470, report.Curves);
        Assert.Equal("review_required", report.State);
        Assert.Contains(report.Findings, f => f.Code == "DUPLICATE_GEOMETRY" && f.Handles.SequenceEqual(new[] { "0", "469" }));
        Assert.Contains(report.Findings, f => f.Code == "DUPLICATE_GEOMETRY" && f.Handles.SequenceEqual(new[] { "249", "250" }));
        Assert.DoesNotContain(report.Findings, f => f.Severity == "unverified");
    }
}
