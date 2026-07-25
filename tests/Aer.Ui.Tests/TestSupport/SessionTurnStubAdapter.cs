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

    /// <summary>
    /// Sentinel forcing the turn to SUCCEED while writing no output file, printing its answer only
    /// on stdout as a <c>type: result</c> object (#534).
    /// </summary>
    /// <remarks>
    /// This is not a hypothetical. It is what the real <c>claude</c> CLI does on every
    /// directory-less chat session: <see cref="InteractiveSessionMaterializer.DefaultGrantForWorkingDirectory"/>
    /// returns an all-deny grant for a session with no working directory (fail-closed, #321), which
    /// becomes <c>--disallowedTools Edit,Write,NotebookEdit,Bash</c> — so the model genuinely cannot
    /// write <c>response.md</c>, says so, and exits <c>is_error: false</c> with the answer in
    /// <c>result</c>. Measured identically on claude-opus-5 and claude-haiku-4-5.
    /// <para>
    /// Every pre-existing stub wrote the output file, so no test covered the case the product
    /// actually hits. The sentinel exists to make that case deterministic and CI-safe.
    /// </para>
    /// </remarks>
    public const string NoOutputFileSentinel = "STUB_NO_OUTPUT_FILE";

    /// <summary>The answer text the no-output-file turn puts on stdout, and nowhere else.</summary>
    public const string StdoutOnlyAnswer = "stub answer that only ever reached stdout";

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

        if (invocation.PromptTemplate.Contains(NoOutputFileSentinel, StringComparison.Ordinal))
        {
            // Shaped like the real thing: exit 0, `is_error: false`, `subtype: success`, the answer
            // in `result`, and NO output file.
            //
            // The payload is written to a file and printed, rather than passed as a JSON literal on
            // the command line. Quoting a JSON literal differs between cmd and sh, `cmd` does not
            // treat backslash as an escape, and an earlier version of this stub emitted malformed
            // JSON as a result -- which made the stub look exactly like the product defect it is
            // supposed to reproduce. A test double that can fail the same way as the thing under
            // test cannot discriminate, so the quoting is removed from the problem entirely.
            var payload = Path.Combine(Path.GetTempPath(), $"aer-stub-result-{Guid.NewGuid():N}.json");
            File.WriteAllText(payload,
                "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\""
                + StdoutOnlyAnswer + "\"}");
            return OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "type", payload])
                : new CoreDispatchTarget("sh", ["-c", $"cat \"{payload}\""]);
        }

        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", $"echo stub-turn-response>%AER_OUTPUT_DIR%\\{outputName}"])
            : new CoreDispatchTarget("sh", ["-c", $"echo stub-turn-response > \"$AER_OUTPUT_DIR/{outputName}\""]);
    }
}
