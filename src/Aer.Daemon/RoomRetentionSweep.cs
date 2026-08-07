using Aer.Adapters;
using Aer.Flow.Projection;
using Microsoft.Extensions.Hosting;

namespace Aer.Daemon;

/// <summary>
/// Background daemon service that periodically sweeps resident rooms to compact completed room journals (#1025).
/// </summary>
public sealed class RoomRetentionSweep : BackgroundService
{
    public const string EnabledEnvironmentVariable = "AER_RETENTION_SWEEP_ENABLED";
    public const string IntervalSecondsEnvironmentVariable = "AER_RETENTION_SWEEP_INTERVAL_SECONDS";
    public const string ThresholdBytesEnvironmentVariable = "AER_RETENTION_SWEEP_THRESHOLD_BYTES";

    public static readonly TimeSpan PlaceholderDefaultInterval = TimeSpan.FromMinutes(5);
    public const long PlaceholderDefaultThresholdBytes = 1_048_576; // 1 MB placeholder

    // Bounds on the parsed interval, both ends load-bearing:
    //  - Upper: without it a pathological value (e.g. "1e300", "Infinity") reaches TimeSpan.FromSeconds,
    //    which throws OverflowException — and GetInterval() is called from ExecuteAsync's delay, whose only
    //    catch is OperationCanceledException, so the overflow would fault the BackgroundService and stop the
    //    whole daemon on a typo. A retention sweep never legitimately waits >1 day.
    //  - Lower: a sub-second typo (e.g. "1e-9") floors TimeSpan.FromSeconds to ~Zero, and Task.Delay(Zero)
    //    returns immediately, hot-looping ExecuteAsync so it re-enumerates every room continuously. One
    //    second is far below the placeholder cadence yet keeps the loop a loop, not a spin.
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    public static bool IsEnabled()
    {
        var val = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);
        return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1";
    }

    public static TimeSpan GetInterval()
    {
        var val = Environment.GetEnvironmentVariable(IntervalSecondsEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(val) &&
            double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            // Clamp before FromSeconds: honors intent within [Min, Max], collapses Infinity/huge finite to
            // Max (no overflow) and sub-second values to Min (no hot-loop). NaN fails seconds > 0 above, so
            // Math.Clamp never sees it.
            return TimeSpan.FromSeconds(Math.Clamp(seconds, MinInterval.TotalSeconds, MaxInterval.TotalSeconds));
        }

        return PlaceholderDefaultInterval;
    }

    public static long GetThresholdBytes()
    {
        var val = Environment.GetEnvironmentVariable(ThresholdBytesEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(val) &&
            long.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var bytes) &&
            bytes >= 0)
        {
            return bytes;
        }

        return PlaceholderDefaultThresholdBytes;
    }

    public async Task<int> ExecuteSingleSweepAsync(
        string? roomsDirectoryOverride = null,
        long? thresholdBytesOverride = null,
        CancellationToken cancellationToken = default)
    {
        var roomsDir = roomsDirectoryOverride ?? AerPaths.Rooms;
        if (!Directory.Exists(roomsDir))
        {
            return 0;
        }

        string[] roomDirs;
        try
        {
            roomDirs = Directory.GetDirectories(roomsDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoomRetentionSweep: Failed to enumerate rooms directory '{roomsDir}': {ex.Message}");
            return 0;
        }

        var thresholdBytes = thresholdBytesOverride ?? GetThresholdBytes();
        var compactedCount = 0;

        foreach (var roomDir in roomDirs)
        {
            try
            {
                if (await SweepRoomAsync(roomDir, thresholdBytes, cancellationToken).ConfigureAwait(false))
                {
                    compactedCount++;
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown (or a caller-cancelled token) must unwind the whole sweep, not be logged as a
                // per-room compaction error and swallowed so the loop marches to the next room.
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoomRetentionSweep: Error compacting room at '{roomDir}': {ex.Message}");
            }
        }

        return compactedCount;
    }

    internal static async Task<bool> SweepRoomAsync(
        string roomDirectoryPath,
        long thresholdBytes,
        CancellationToken cancellationToken = default)
    {
        var roomLogPath = Path.Combine(roomDirectoryPath, "room.jsonl");
        if (!File.Exists(roomLogPath))
        {
            return false;
        }

        var fileInfo = new FileInfo(roomLogPath);
        if (fileInfo.Length < thresholdBytes)
        {
            return false;
        }

        return await RoomJournalCompactor.CompactAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsEnabled())
            {
                try
                {
                    await ExecuteSingleSweepAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoomRetentionSweep sweep iteration failed: {ex.Message}");
                }
            }

            try
            {
                await Task.Delay(GetInterval(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
