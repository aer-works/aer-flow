using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// <see cref="VendorGate"/> and each adapter's own <c>Resolve</c> build the gate separately (#703),
/// because <c>Resolve</c> interleaves it with placeholder-bearing arguments a non-Flow caller must
/// not carry. These fail when the two drift.
/// </summary>
/// <remarks>
/// The direction matters: everything the gate claims to install must actually be in the dispatch
/// path's output. A gate arg missing from <c>Resolve</c> means one of the two spawn paths is
/// ungated, which is the entire defect #703 exists to close.
/// </remarks>
[Collection(LaunchConfigCollection.Name)]
public class VendorGateMatchesResolveTests
{
    private static readonly WorkerContract Contract = new("architect", [], [new ProducedOutput("plan.md")], []);

    /// <summary>Withholds every category, so the denied/disallowed lists are non-empty and actually compared.</summary>
    private static readonly PermissionGrant Restrictive = new(
        ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false);

    private static void AssertGateIsInstalled(VendorGate gate, CoreDispatchTarget target)
    {
        // Adjacent-pair containment, not "each token appears somewhere": a flag and its value landing
        // in the argv separately would satisfy a naive Contains while producing a different command.
        for (var i = 0; i < gate.Args.Count; i += 2)
        {
            var flag = gate.Args[i];
            var value = gate.Args[i + 1];
            var found = Enumerable.Range(0, target.Args.Count - 1)
                .Any(j => target.Args[j] == flag && target.Args[j + 1] == value);

            Assert.True(found, $"Resolve's argv is missing the gate pair '{flag} {value}'.");
        }

        var resolved = target.Environment ?? [];
        foreach (var (name, value) in gate.Environment)
        {
            Assert.Contains((name, value), resolved);
        }
    }

    [Fact]
    public void Claude_gate_is_every_gate_mechanism_Resolve_installs()
    {
        var gate = ClaudeWorkerAdapter.BuildGate(Restrictive);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: Restrictive), Contract);

        AssertGateIsInstalled(gate, target);

        // The three that are load-bearing rather than incidental, asserted by name so deleting one
        // from BuildGate fails here rather than silently shrinking what both sides agree on.
        Assert.Contains("--settings", gate.Args);
        Assert.Contains("--mcp-config", gate.Args);
        Assert.Equal("0", gate.Environment[ClaudeWorkerAdapter.SimpleModeVariable]);
    }

    [Fact]
    public void Agy_gate_is_every_gate_mechanism_Resolve_installs()
    {
        var gate = GeminiWorkerAdapter.BuildGate(Restrictive);
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: Restrictive), Contract);

        AssertGateIsInstalled(gate, target);

        // agy discovers hooks ONLY from an --add-dir path, so this pair IS the gate on this vendor.
        Assert.Equal("--add-dir", gate.Args[0]);
        Assert.Contains(GeminiWorkerAdapter.AgyWorkspaceDirectoryName, gate.Args[1]);
    }

    /// <summary>
    /// The CONTROL. An unknown vendor yields null rather than an empty gate — a caller must be able
    /// to tell "cannot be gated" from "gated, needed nothing", and only the first is fail-closed.
    /// </summary>
    [Fact]
    public void A_vendor_AER_ships_no_gate_for_yields_null_rather_than_an_empty_gate()
    {
        Assert.Null(VendorGate.For("some-future-vendor", Restrictive));
        Assert.NotNull(VendorGate.For("claude", Restrictive));
        Assert.NotNull(VendorGate.For("gemini", Restrictive));
    }
}
