using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class DependencyGraphTests
{
    [Fact]
    public void A_source_change_reaches_chains_and_branches_but_not_unrelated_broken_components()
    {
        DependencyGraph.Formula[] formulas = [new("B", ["A"]), new("C", ["B"]), new("D", ["A"]), new("unrelated", ["deleted-cell"])];
        var affected = DependencyGraph.Affected(formulas, new HashSet<string> { "A" });
        Assert.True(affected.SetEquals(["B", "C", "D"]));
    }
    [Fact]
    public void Editing_a_formula_cell_recalculates_it_and_its_dependents_without_its_unchanged_sources()
    {
        DependencyGraph.Formula[] formulas = [new("A", ["source"]), new("B", ["A"]), new("C", ["B"])];
        Assert.True(DependencyGraph.Affected(formulas, new HashSet<string> { "B" }).SetEquals(["B", "C"]));
    }
    [Fact]
    public void Unrelated_geometry_does_not_recalculate_any_formula_and_cycles_do_not_hang_selection()
    {
        DependencyGraph.Formula[] formulas = [new("A", ["B"]), new("B", ["A"])];
        Assert.Empty(DependencyGraph.Affected(formulas, new HashSet<string> { "line" }));
        Assert.True(DependencyGraph.Affected(formulas, new HashSet<string> { "A" }).SetEquals(["A", "B"]));
    }
}
