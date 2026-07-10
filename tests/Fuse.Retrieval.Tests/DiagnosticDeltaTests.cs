using Fuse.Indexing;
using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// S2 delta check: introduced/resolved diagnostics between a baseline and a current set, matched by (file, id,
// message) so line-only shifts (span drift) are not phantom changes.
public sealed class DiagnosticDeltaTests
{
    private static CheckDiagnostic Diag(string id, string message, int line, string file = "A.cs") =>
        new(id, "Error", message, file, line);

    [Fact]
    public void A_new_diagnostic_is_introduced()
    {
        var result = DiagnosticDelta.Compute([], [Diag("CS1061", "no member Foo", 5)]);
        var introduced = Assert.Single(result.Introduced);
        Assert.Equal("CS1061", introduced.Id);
        Assert.Empty(result.Resolved);
    }

    [Fact]
    public void A_disappeared_diagnostic_is_resolved()
    {
        var result = DiagnosticDelta.Compute([Diag("CS1061", "no member Foo", 5)], []);
        var resolved = Assert.Single(result.Resolved);
        Assert.Equal("CS1061", resolved.Id);
        Assert.Empty(result.Introduced);
    }

    [Fact]
    public void A_line_only_shift_is_not_a_change_span_drift()
    {
        // The same diagnostic moved from line 5 to line 12 (an edit inserted lines above it): no delta.
        var result = DiagnosticDelta.Compute(
            [Diag("CS1061", "no member Foo", 5)],
            [Diag("CS1061", "no member Foo", 12)]);
        Assert.Empty(result.Introduced);
        Assert.Empty(result.Resolved);
    }

    [Fact]
    public void One_of_two_resolving_reports_exactly_one_resolved()
    {
        var result = DiagnosticDelta.Compute(
            [Diag("CS0246", "type X not found", 3), Diag("CS0246", "type X not found", 9)],
            [Diag("CS0246", "type X not found", 3)]);
        Assert.Single(result.Resolved);
        Assert.Empty(result.Introduced);
    }

    [Fact]
    public void Mixed_introduced_and_resolved_are_separated()
    {
        var result = DiagnosticDelta.Compute(
            [Diag("CS0246", "type X not found", 3)],
            [Diag("CS1061", "no member Foo", 4)]);
        Assert.Equal("CS1061", Assert.Single(result.Introduced).Id);
        Assert.Equal("CS0246", Assert.Single(result.Resolved).Id);
    }

    [Fact]
    public void An_unchanged_set_yields_no_delta()
    {
        var set = new[] { Diag("CS1061", "no member Foo", 5), Diag("CS0246", "type X not found", 9) };
        var result = DiagnosticDelta.Compute(set, set);
        Assert.Empty(result.Introduced);
        Assert.Empty(result.Resolved);
    }
}
