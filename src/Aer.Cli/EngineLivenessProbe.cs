using System.ComponentModel;
using System.Diagnostics;

namespace Aer.Cli;

public enum EngineLivenessStatus
{
    Alive,
    Dead,
    Unknown
}

public sealed record EngineLivenessResult(EngineLivenessStatus Status, string? Why = null);

public static class EngineLivenessProbe
{
    public static EngineLivenessResult Probe(int? pid, DateTimeOffset? startTime)
    {
        if (pid is null || startTime is null)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, "no process identity recorded");
        }

        if (pid.Value <= 0)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, "invalid process identity");
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);

            DateTimeOffset processStartTime;
            try
            {
                processStartTime = new DateTimeOffset(process.StartTime).ToUniversalTime();
            }
            catch (InvalidOperationException)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Dead);
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Unknown, ex.Message);
            }

            var recordedUtc = startTime.Value.ToUniversalTime();
            var diffMs = Math.Abs((processStartTime - recordedUtc).TotalMilliseconds);
            if (diffMs > 1000)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Dead);
            }

            bool hasExited;
            try
            {
                hasExited = process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Dead);
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Unknown, ex.Message);
            }

            if (hasExited)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Dead);
            }

            return new EngineLivenessResult(EngineLivenessStatus.Alive);
        }
        catch (ArgumentException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Dead);
        }
        catch (InvalidOperationException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Dead);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, ex.Message);
        }
    }
}
