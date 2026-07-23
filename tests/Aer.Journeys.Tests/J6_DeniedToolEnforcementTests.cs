using Aer.Adapters;
using Aer.Flow;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Journeys.Tests;

/// <summary>
/// <b>Journey J6 — "Deny a tool and have it actually blocked" (safety).</b> Engine leg: the grant a
/// user withholds must be enforced at the dispatch boundary, not merely displayed.
/// <para>
/// This is a <b>red-spec</b>. It fails today on purpose and documents the promise: decision
/// <c>0004</c> establishes (verified against the live CLI in #331) that <c>--allowedTools</c>
/// <em>pre-approves</em> tools so they don't prompt — it is not a deny-list — and that nothing in
/// <c>src/</c> passes <c>--disallowedTools</c> or a denying <c>--permission-mode</c>, so a
/// shell-denied session ran <c>hostname</c> and returned the real value. The decision requires
/// grants to <b>fail closed</b>: a withheld capability must be actively denied at the boundary, or
/// the run must not start. This test accepts <em>either</em> resolution — an enforcing flag on the
/// dispatch, or a refusal to build one (an <see cref="AerFlowException"/>) — so it goes green
/// whichever way #331 is fixed rather than being coupled to one mechanism.
/// </para>
/// <para>
/// The end-to-end refusal — a live worker attempting the tool and being blocked — is a live-vendor
/// smoke check (a human gate), not this test; see the runbook.
/// </para>
/// </summary>
[Trait(Journeys.TraitKey, "J6")]
public class J6_DeniedToolEnforcementTests
{
    private static readonly WorkerContract ArchitectContract =
        new("architect", ["goal"], [new ProducedOutput("plan.md")], []);

    [Fact]
    public void A_shell_denied_grant_produces_a_dispatch_that_actively_denies_shell_not_merely_omits_it()
    {
        // Read and write are granted; shell is deliberately withheld — the exact shape of #331's
        // session (RunShellCommands: false).
        var invocation = new WorkerInvocation(
            "Draft a plan.",
            PermissionGrant: new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false));

        CoreDispatchTarget target;
        try
        {
            target = new ClaudeWorkerAdapter().Resolve(invocation, ArchitectContract);
        }
        catch (AerFlowException)
        {
            // Fail-closed: refusing to build a dispatch that cannot enforce the denial is the other
            // resolution decision 0004 blesses, so this satisfies J6 too. (It does not happen today
            // — Resolve returns an unenforced dispatch, and the assertions below run and fail.)
            return;
        }

        // Baseline the defect: the withheld tool is simply absent from the pre-approval list...
        var allowed = ArgValue(target.Args, "--allowedTools");
        Assert.False(allowed?.Contains("Bash") ?? false,
            "Shell should not be pre-approved when RunShellCommands is denied.");

        // ...but omission is not enforcement (decision 0004). The dispatch must carry an active
        // denial of the withheld tool: either an explicit deny-list entry, or a permission mode
        // that denies by default rather than one that bypasses or blanket-accepts.
        var disallowed = ArgValue(target.Args, "--disallowedTools");
        var permissionMode = ArgValue(target.Args, "--permission-mode");

        var enforcesDenial =
            (disallowed is not null && disallowed.Contains("Bash"))
            || (permissionMode is not null && permissionMode is not ("bypassPermissions" or "acceptEdits"));

        Assert.True(
            enforcesDenial,
            "J6 (safety): a withheld tool must be actively denied at the dispatch boundary — via "
            + "--disallowedTools or a denying --permission-mode — or the run must fail closed. The "
            + "dispatch omits Bash from --allowedTools but passes no enforcing flag, so a subscription "
            + "worker can still run it (decision 0004, #331). Args were: "
            + string.Join(' ', target.Args));
    }

    /// <summary>The value token immediately after <paramref name="flag"/> in a flat argv, or null if absent.</summary>
    private static string? ArgValue(IReadOnlyList<string> args, string flag)
    {
        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == flag)
            {
                index = i;
                break;
            }
        }

        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }
}
