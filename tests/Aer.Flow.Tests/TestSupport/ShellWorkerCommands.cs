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
}
