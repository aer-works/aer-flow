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
    /// returns an all-deny grant for a session with no working directory (fail-closed, #321). When
    /// this was measured that grant became <c>--disallowedTools Edit,Write,NotebookEdit,Bash</c>, so
    /// the model genuinely could not write <c>response.md</c>, said so, and exited
    /// <c>is_error: false</c> with the answer in <c>result</c>. Measured identically on claude-opus-5
    /// and claude-haiku-4-5.
    /// <para>
    /// <b>#649 changed the primary path, and this stub deliberately still reproduces the old one.</b>
    /// The write tools now leave <c>--disallowedTools</c> and ride the <c>PreToolUse</c> hook, which
    /// allows a write landing in <c>AER_OUTPUT_DIR</c> — and <c>response.md</c> is addressed there
    /// (<see cref="InteractiveSessionMaterializer.ResponseFileInstruction"/>), so a directory-less
    /// session can now produce the file. What this stub covers is the case where it does not: a
    /// vendor that refuses for its own reasons, a hook that denied, a model that simply answered
    /// without writing. That path must keep working, which is why the stub stays — but it is no
    /// longer what "every directory-less chat session" does.
    /// </para>
    /// <para>
    /// Every pre-existing stub wrote the output file, so no test covered the case the product
    /// actually hits. The sentinel exists to make that case deterministic and CI-safe.
    /// </para>
    /// </remarks>
    public const string NoOutputFileSentinel = "STUB_NO_OUTPUT_FILE";

    /// <summary>
    /// Sentinel forcing an agy turn to SUCCEED while writing no output file, writing a fake agy log
    /// file containing <c>conversation=&lt;id&gt;</c> to <see cref="WorkerInvocation.LogFilePath"/> instead (#545).
    /// </summary>
    public const string AgyNoOutputFileSentinel = "STUB_AGY_NO_OUTPUT_FILE";

    /// <summary>The agy conversation id written to the log file by an agy no-output-file turn.</summary>
    public const string StubAgyConversationId = "stub-agy-conv-123";

    /// <summary>
    /// Sentinel forcing an agy turn to exit cleanly while producing absolutely nothing -- no
    /// output file, no log line, nothing <see cref="AgyNoOutputFileSentinel"/> would leave behind.
    /// Distinct from <see cref="FailureSentinel"/> (exit 1): this is a turn that exits 0 but still
    /// genuinely produced no answer and established nothing (#545, found by review).
    /// </summary>
    public const string AgySilentSuccessSentinel = "STUB_AGY_SILENT_SUCCESS";

    /// <summary>The answer text the no-output-file turn puts on stdout, and nowhere else.</summary>
    public const string StdoutOnlyAnswer = "stub answer that only ever reached stdout";

    /// <summary>
    /// The payload the no-output-file turn prints. Written once per test run rather than once per
    /// dispatch — the content is constant, and a fresh Guid-named file per dispatch left one small
    /// file behind in <c>%TEMP%</c> for every turn any test in the suite ran.
    /// </summary>
    private static readonly Lazy<string> ResultPayloadFile = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"aer-stub-result-{Environment.ProcessId}.json");
        File.WriteAllText(path,
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\""
            + StdoutOnlyAnswer + "\"}");
        return path;
    });

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
            var payload = ResultPayloadFile.Value;
            return OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "type", payload])
                : new CoreDispatchTarget("sh", ["-c", $"cat \"{payload}\""]);
        }

        if (invocation.PromptTemplate.Contains(AgyNoOutputFileSentinel, StringComparison.Ordinal))
        {
            // Written directly from C#, not via a dispatched shell redirect: an embedded `>` inside
            // a single combined "cmd /c \"...\"" argv element silently produced no file at all --
            // measured, not assumed (this is a test stub simulating agy's log file, not the real
            // CLI, so there is no requirement that a subprocess be the one to write it). This is the
            // same lesson NoOutputFileSentinel's own comment above already recorded for this exact
            // file: cmd's quoting is unusually finicky, and the working fix there was to remove
            // quoting from the problem entirely rather than get the escaping right.
            var logPath = invocation.LogFilePath ?? Path.Combine(Path.GetTempPath(), "agy-log.txt");
            if (Path.GetDirectoryName(logPath) is { } dir && !string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(logPath, $"conversation={StubAgyConversationId}\n");
            return OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 0"])
                : new CoreDispatchTarget("sh", ["-c", "exit 0"]);
        }

        if (invocation.PromptTemplate.Contains(AgySilentSuccessSentinel, StringComparison.Ordinal))
        {
            // Exits 0, writes nothing at all -- no output file, no log line. Reproduces a genuinely
            // failed/no-op agy turn that nonetheless exits cleanly, distinct from FailureSentinel
            // (exit 1, caught earlier as a workflow-level run failure before establishment logic
            // ever runs). This is the case #545's review found: on turn 2+, `vendorSessionId` is
            // already non-null (carried over from an earlier established turn), so a turn producing
            // nothing at all was still wrongly reported as established.
            return OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 0"])
                : new CoreDispatchTarget("sh", ["-c", "exit 0"]);
        }

        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", $"echo stub-turn-response>%AER_OUTPUT_DIR%\\{outputName}"])
            : new CoreDispatchTarget("sh", ["-c", $"echo stub-turn-response > \"$AER_OUTPUT_DIR/{outputName}\""]);
    }
}
