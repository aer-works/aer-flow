using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Ui.Tests.TestSupport;

/// <summary>
/// Retroactive M24 Phase 1/2 test-gap-fill (#262/#263): the deterministic, CI-safe
/// <see cref="IWorkerAdapter"/> session-turn branching tests need. Unlike
/// <see cref="ShellCommandWorkerAdapter"/>, this ignores <c>WorkerInvocation.PromptTemplate</c>
/// entirely rather than running it as a shell command — a vendor-handoff or compact turn's
/// <c>PromptTemplate</c> is <c>InteractiveSessionMaterializer.SynthesizeContextSummary</c>'s
/// natural-language output, not a valid command line, so a literal-command adapter would fail
/// dispatch and silently swallow the failure before <c>ExecuteSessionTurnAsync</c>'s metadata write
/// ever runs. This adapter always succeeds, writing a fixed response file regardless of what the
/// prompt template says, so every turn — handoff, ceiling, or ordinary — reaches and exercises the
/// observable metadata (<c>VendorHandoffSynthesized</c>, <c>NativeSessionResumed</c>,
/// <c>CurrentAdapter</c>, <c>TurnCount</c>).
/// </summary>
internal sealed class SessionTurnStubAdapter : IWorkerAdapter
{
    /// <summary>
    /// Sentinel a test's message text can embed to force this turn to fail closed (#285's resume-
    /// gating regression tests need a deterministic, CI-safe way to simulate "the vendor rejected
    /// this turn" -- e.g. a real `claude --resume` of an unestablished id -- without a live CLI).
    /// </summary>
    public const string FailureSentinel = "STUB_FORCE_FAILURE";

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        var outputName = contract.ProducedOutputs.Count > 0
            ? contract.ProducedOutputs[0].Name
            : InteractiveSessionMaterializer.DefaultOutputFileName;

        if (invocation.PromptTemplate.Contains(FailureSentinel, StringComparison.Ordinal))
        {
            return OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 1"])
                : new CoreDispatchTarget("sh", ["-c", "exit 1"]);
        }

        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", $"echo stub-turn-response>%AER_OUTPUT_DIR%\\{outputName}"])
            : new CoreDispatchTarget("sh", ["-c", $"echo stub-turn-response > \"$AER_OUTPUT_DIR/{outputName}\""]);
    }
}
