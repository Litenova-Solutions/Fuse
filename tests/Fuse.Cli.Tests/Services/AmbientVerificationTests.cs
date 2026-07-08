using Fuse.Cli.Rpc;
using Fuse.Cli.Services;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// S3: the pure decision/rendering logic behind fuse check --delta (hook text) and fuse gate (block verdict).
// Baseline discipline - only introduced errors block; empty and non-resident deltas emit nothing.
public sealed class AmbientVerificationTests
{
    private static CheckDiagnosticDto Diag(string id, string severity, string message = "msg", string? path = "A.cs", int line = 5) =>
        new(id, severity, message, path, line);

    [Fact]
    public void An_empty_delta_renders_nothing()
    {
        var text = AmbientVerification.RenderDelta(new CheckDeltaDto(Resident: true, [], []));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void A_non_resident_delta_renders_nothing_even_with_diagnostics()
    {
        // Guards the silence contract: no resident workspace served the delta, so the hook stays quiet.
        var text = AmbientVerification.RenderDelta(new CheckDeltaDto(Resident: false, [Diag("CS1061", "Error")], []));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void An_introduced_diagnostic_is_rendered()
    {
        var text = AmbientVerification.RenderDelta(
            new CheckDeltaDto(Resident: true, [Diag("CS1061", "Error", "no member Foo")], []));
        Assert.Contains("1 diagnostic(s) introduced", text);
        Assert.Contains("introduced Error CS1061 A.cs:5: no member Foo", text);
    }

    [Fact]
    public void An_introduced_error_is_red()
    {
        Assert.True(AmbientVerification.IsRed(new CheckDeltaDto(Resident: true, [Diag("CS1061", "Error")], [])));
    }

    [Fact]
    public void An_introduced_warning_is_not_red()
    {
        Assert.False(AmbientVerification.IsRed(new CheckDeltaDto(Resident: true, [Diag("CS0168", "Warning")], [])));
    }

    [Fact]
    public void A_resolved_only_delta_is_not_red()
    {
        Assert.False(AmbientVerification.IsRed(new CheckDeltaDto(Resident: true, [], [Diag("CS1061", "Error")])));
    }

    [Fact]
    public void A_non_resident_delta_is_not_red()
    {
        // With no resident workspace the gate must not block editing (the fast-exit-silent contract).
        Assert.False(AmbientVerification.IsRed(new CheckDeltaDto(Resident: false, [Diag("CS1061", "Error")], [])));
    }

    [Fact]
    public void The_gate_block_summary_lists_only_introduced_errors()
    {
        var summary = AmbientVerification.RenderGateBlock(new CheckDeltaDto(
            Resident: true,
            [Diag("CS1061", "Error", "no member"), Diag("CS0168", "Warning", "unused")],
            []));
        Assert.Contains("1 error(s) introduced", summary);
        Assert.Contains("CS1061", summary);
        Assert.DoesNotContain("CS0168", summary);
    }
}
