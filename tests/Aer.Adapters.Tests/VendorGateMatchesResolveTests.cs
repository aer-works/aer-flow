using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// <see cref="VendorGate"/> and each adapter's own <c>Resolve</c> build the gate separately (#703),
/// because <c>Resolve</c> interleaves it with placeholder-bearing arguments a non-Flow caller must
/// not carry. These fail when the two drift.
/// </summary>
/// <remarks>
/// <para>
/// The direction matters: everything the gate claims to install must actually be in the dispatch
/// path's output. A gate arg missing from <c>Resolve</c> means one of the two spawn paths is
/// ungated, which is the entire defect #703 exists to close.
/// </para>
/// <para>
/// <b>For the ENVIRONMENT the check runs in both directions, and the first version did not.</b>
/// Checking only <c>gate ⊆ Resolve</c> cannot see an omission, and one was there — see
/// <see cref="VendorGate.For"/>, which records what was missing and what it cost. A reviewer found
/// it by reading; nothing here could have. Set equality rather than containment, because anything
/// weaker leaves room for the same shape to recur — with one pinned exception: the #442 home
/// redirect is dispatch-path state isolation, not a gate mechanism, and cannot ride the gate (the
/// reason lives at <c>AgyWorkerAdapter.Resolve</c>'s own redirect clause).
/// </para>
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

        // The reverse. Anything Resolve sets that the gate does not is a mechanism a non-Flow caller
        // silently does without -- which is how the workspace bound went missing. HOME/USERPROFILE
        // are the pinned exception (state isolation, not a gate mechanism -- see the class remark).
        foreach (var (name, value) in resolved)
        {
            if (name is "HOME" or "USERPROFILE")
            {
                continue;
            }

            Assert.True(
                gate.Environment.TryGetValue(name, out var fromGate) && fromGate == value,
                $"Resolve sets '{name}' but the gate does not, so a caller installing only the gate "
                + "gets a worker configured differently from a dispatched one.");
        }
    }

    /// <summary>A workspace, because its ABSENCE from the gate is the omission these tests missed.</summary>
    private static readonly string Workspace = OperatingSystem.IsWindows() ? @"C:\rooms\r1" : "/rooms/r1";

    [Fact]
    public void Claude_gate_is_every_gate_mechanism_Resolve_installs()
    {
        var gate = ClaudeWorkerAdapter.BuildGate(Restrictive, Workspace);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: Restrictive, WorkingDirectory: Workspace), Contract);

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
        var gate = AgyWorkerAdapter.BuildGate(Restrictive, Workspace);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: Restrictive, WorkingDirectory: Workspace), Contract);

        AssertGateIsInstalled(gate, target);

        // agy discovers hooks ONLY from an --add-dir path, so this pair IS the gate on this vendor.
        Assert.Equal("--add-dir", gate.Args[0]);
        Assert.Contains(AgyWorkerAdapter.AgyWorkspaceDirectoryName, gate.Args[1]);
    }

    /// <summary>
    /// Polarity on the workspace bound specifically, in both directions, because the omission this
    /// records was invisible to a one-directional check.
    /// </summary>
    /// <remarks>
    /// The null arm asserts a REAL NARROWING rather than a harmless default: per
    /// <c>HookCheckCommand.Execute</c>, no workspace means a granted write is confined to the outbox.
    /// It is pinned here so that a caller passing null is doing so knowingly — the direction is
    /// fail-closed, which is why it is permitted at all, but silent is what made it a defect.
    /// </remarks>
    [Theory]
    [InlineData("claude")]
    [InlineData("agy")]
    public void The_workspace_bound_reaches_the_gate_when_given_and_is_absent_when_not(string vendor)
    {
        var withWorkspace = VendorGate.For(vendor, Restrictive, Workspace);
        Assert.NotNull(withWorkspace);
        Assert.Equal(Workspace, withWorkspace.Environment[WorkerEnvironment.WorkspaceVariable]);

        var without = VendorGate.For(vendor, Restrictive);
        Assert.NotNull(without);
        Assert.DoesNotContain(WorkerEnvironment.WorkspaceVariable, without.Environment.Keys);
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
        Assert.NotNull(VendorGate.For("agy", Restrictive));
    }
}
