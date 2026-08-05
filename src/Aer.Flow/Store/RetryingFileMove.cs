namespace Aer.Flow.Store;

/// <summary>
/// Wall-clock bounded retry loop around <see cref="File.Move(string, string, bool)"/> for atomic-move sites.
/// Mirrors the pattern in <see cref="T:Aer.Adapters.AtomicLaunchConfigWriter"/>.
/// </summary>
public static class RetryingFileMove
{
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);
    private const double MaxBackoffMs = 250;

    /// <summary>
    /// Moves a file from <paramref name="source"/> to <paramref name="destination"/>, retrying on transient
    /// sharing violations (<see cref="IOException"/> and <see cref="UnauthorizedAccessException"/>)
    /// until <paramref name="budget"/> expires.
    /// </summary>
    public static void Move(string source, string destination, bool overwrite = false, TimeSpan? budget = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        var actualBudget = budget ?? DefaultBudget;
        var deadlineTicks = Environment.TickCount64 + (long)actualBudget.TotalMilliseconds;
        var backoffMs = 10.0;

        while (true)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadlineTicks)
                {
                    throw;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(backoffMs));
                backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
            }
        }
    }
}
