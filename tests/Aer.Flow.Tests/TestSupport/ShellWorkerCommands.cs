using Aer.Flow.Dispatch;

namespace Aer.Flow.Tests.TestSupport;

/// <summary>
/// Tiny cross-platform shell <see cref="CoreDispatchTarget"/>s standing in for real workers in
/// integration tests that dispatch through the real aer-core M5 binding (no mocking of Aer.Core
/// itself, per M7 Phase 7's acceptance criteria).
/// </summary>
internal static class ShellWorkerCommands
{
    public static CoreDispatchTarget WriteFile(string outputName, string content) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\""]);

    public static CoreDispatchTarget CopyFirstInputTo(string outputName) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\""]);

    /// <summary>Concatenates both resolved inputs (declaration order) into one output — the diamond DAG's join step.</summary>
    public static CoreDispatchTarget ConcatBothInputsTo(string outputName) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"copy /b %AER_INPUT_0%+%AER_INPUT_1% %AER_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"cat \"$AER_INPUT_0\" \"$AER_INPUT_1\" > \"$AER_OUTPUT_DIR/{outputName}\""]);

    public static CoreDispatchTarget ExitCleanlyWithoutWriting() => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", "exit 0"])
        : new CoreDispatchTarget("sh", ["-c", "exit 0"]);

    /// <summary>
    /// Fails its first invocation and succeeds every one after, keyed off a marker file at a fixed
    /// path outside <c>AER_OUTPUT_DIR</c> — each attempt's output directory is fresh by design
    /// (§16), so durable state across attempts has to live somewhere else.
    /// </summary>
    public static CoreDispatchTarget FailOnFirstAttemptThenSucceed(string markerFilePath, string outputName, string content) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget(
            // No quotes around markerFilePath: embedding a literal '"' in a single cmd argument
            // does not survive aer-core's Windows process-spawn re-quoting intact, and a
            // GUID-based temp path never contains spaces, so quoting buys nothing here — matches
            // this file's other Windows commands, none of which quote a path either.
            "cmd",
            ["/c", $"if exist {markerFilePath} (echo {content}>%AER_OUTPUT_DIR%\\{outputName}) else (echo marker>{markerFilePath} & exit 1)"])
        : new CoreDispatchTarget(
            "sh",
            ["-c", $"if [ -f \"{markerFilePath}\" ]; then echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"; else touch \"{markerFilePath}\"; exit 1; fi"]);

    /// <summary>
    /// Writes <paramref name="body"/> to a script file under <paramref name="scriptDirectory"/> and
    /// returns a target that runs it, instead of inlining the body into a process argument. Two of
    /// the workers below need to emit literal <c>"</c> characters (JSON), which the single-cmd-argument
    /// approach above deliberately avoids (see <see cref="FailOnFirstAttemptThenSucceed"/>'s comment) —
    /// a script file sidesteps that entirely, since its content is written directly via
    /// <see cref="File.WriteAllText(string, string)"/> and never re-parsed as a command line.
    /// </summary>
    private static CoreDispatchTarget FromScript(string scriptDirectory, string body)
    {
        Directory.CreateDirectory(scriptDirectory);
        var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var scriptPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}{extension}");
        File.WriteAllText(scriptPath, body);

        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", scriptPath])
            : new CoreDispatchTarget("sh", [scriptPath]);
    }

    /// <summary>
    /// Spec §10.1's bounded self-iteration pattern: writes <paramref name="verdictFileName"/> with
    /// <c>{"status":"needs_revision"}</c> on its first invocation and <c>{"status":"approved"}</c>
    /// on every one after, keyed off a marker file outside <c>AER_OUTPUT_DIR</c> — each attempt's
    /// output directory is fresh by design (§16), so durable state across attempts has to live
    /// elsewhere, same as <see cref="FailOnFirstAttemptThenSucceed"/>. Exits 0 both times: only the
    /// caller's declared <c>OutputCondition</c> on the produced output distinguishes the two attempts.
    /// </summary>
    public static CoreDispatchTarget WriteVerdictNeedsRevisionThenApproved(
        string scriptDirectory, string markerFilePath, string verdictFileName)
    {
        var outputPath = OperatingSystem.IsWindows()
            ? $"%AER_OUTPUT_DIR%\\{verdictFileName}"
            : $"$AER_OUTPUT_DIR/{verdictFileName}";

        var body = OperatingSystem.IsWindows()
            ? "@echo off\n" +
              $"if exist \"{markerFilePath}\" (\n" +
              $"  echo {{\"status\":\"approved\"}}>\"{outputPath}\"\n" +
              ") else (\n" +
              $"  echo marker>\"{markerFilePath}\"\n" +
              $"  echo {{\"status\":\"needs_revision\"}}>\"{outputPath}\"\n" +
              ")\n"
            : "#!/bin/sh\n" +
              $"if [ -f \"{markerFilePath}\" ]; then\n" +
              $"  echo '{{\"status\":\"approved\"}}' > \"{outputPath}\"\n" +
              "else\n" +
              $"  touch \"{markerFilePath}\"\n" +
              $"  echo '{{\"status\":\"needs_revision\"}}' > \"{outputPath}\"\n" +
              "fi\n";

        return FromScript(scriptDirectory, body);
    }

    /// <summary>
    /// Spec §8.1's worker-reported short-circuit: always fails, self-reporting
    /// <see cref="Domain.FailureClassification.Permanent"/> through <paramref name="metadataFileName"/>
    /// regardless of remaining retry budget.
    /// </summary>
    public static CoreDispatchTarget FailPermanently(string scriptDirectory, string metadataFileName)
    {
        var outputPath = OperatingSystem.IsWindows()
            ? $"%AER_OUTPUT_DIR%\\{metadataFileName}"
            : $"$AER_OUTPUT_DIR/{metadataFileName}";

        var body = OperatingSystem.IsWindows()
            ? "@echo off\n" +
              $"echo {{\"FailureClassification\":\"Permanent\"}}>\"{outputPath}\"\n" +
              "exit /b 1\n"
            : "#!/bin/sh\n" +
              $"echo '{{\"FailureClassification\":\"Permanent\"}}' > \"{outputPath}\"\n" +
              "exit 1\n";

        return FromScript(scriptDirectory, body);
    }
}
