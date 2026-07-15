using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Ui.Tests.TestSupport;

/// <summary>
/// M15 Phase 4 (issue #140): the UI-layer counterpart to <c>Aer.Flow.Tests.TestSupport.CrashTestHostLauncher.WaitForLogConditionAsync</c>
/// — every "is a real dispatch genuinely still running" wait a targeted-cancel/host-stop test needs
/// polls the log for <see cref="CoreEvent.ExecutionStarted"/> rather than a fixed delay, so these
/// tests are exactly as fast as the real dispatch underneath them, not slower or flaky under load.
/// </summary>
internal static class FlowLogWaiter
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static Task WaitForCoreExecutionStartedAsync(string logPath, TimeSpan? timeout = null) =>
        WaitForConditionAsync(logPath, snapshot => snapshot.CoreEvents.OfType<CoreEvent.ExecutionStarted>().Any(), timeout);

    public static async Task WaitForConditionAsync(
        string logPath, Func<EventLogSnapshot, bool> predicate, TimeSpan? timeout = null)
    {
        var reader = new FlowEventLogReader(logPath);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (true)
        {
            var snapshot = await reader.ReadSnapshotAsync().ConfigureAwait(false);
            if (predicate(snapshot))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for the expected condition in '{logPath}'.");
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    public static async Task<T> AwaitWithTimeoutAsync<T>(Task<T> task, TimeSpan? timeout = null)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout ?? DefaultTimeout)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException("Timed out waiting for the task to complete.");
        }

        return await task.ConfigureAwait(false);
    }

    public static async Task AwaitWithTimeoutAsync(Task task, TimeSpan? timeout = null)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout ?? DefaultTimeout)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException("Timed out waiting for the task to complete.");
        }

        await task.ConfigureAwait(false);
    }

    public static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the expected condition.");
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }
}
