using Aer.Daemon;
using Xunit;

namespace Aer.Daemon.Tests;

public class RoomTurnThrottlesTests
{
    [Fact]
    public void Load_AbsentFile_ReturnsDefaultsAndNullError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aer_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var (values, error) = RoomTurnThrottles.Load(tempDir);
            Assert.Equal(RoomTurnThrottles.Defaults, values);
            Assert.Null(error);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Load_ValidFile_ReturnsCustomValuesAndNullError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aer_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var json = """
            {
              "machineTurnMinimumGapSeconds": 30,
              "machineTurnsPerHour": 5,
              "consecutiveFailureLimit": 2
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "throttles.json"), json);

            var (values, error) = RoomTurnThrottles.Load(tempDir);
            Assert.Equal(TimeSpan.FromSeconds(30), values.MachineTurnMinimumGap);
            Assert.Equal(5, values.MachineTurnsPerHour);
            Assert.Equal(2, values.ConsecutiveFailureLimit);
            Assert.Null(error);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Load_MalformedFile_ReturnsDefaultsAndNonNullError()
    {
        // Red arm note: If Load threw or returned null error on invalid JSON, this assertion would fail.
        var tempDir = Path.Combine(Path.GetTempPath(), "aer_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "throttles.json"), "{ invalid json }}}");

            var (values, error) = RoomTurnThrottles.Load(tempDir);
            Assert.Equal(RoomTurnThrottles.Defaults, values);
            Assert.NotNull(error);
            Assert.Contains("Malformed", error);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
