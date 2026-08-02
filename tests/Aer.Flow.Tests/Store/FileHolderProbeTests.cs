using Aer.Flow.Store;
using Aer.Tests.Shared;

namespace Aer.Flow.Tests.Store;

/// <summary>
/// <see cref="FileHolderProbe"/> is the diagnostic that enriches a sharing-violation
/// (<see cref="FlowJournalHeldException"/>, #398 class) with the name of the process actually holding
/// the file. These prove it reads a real, live handle rather than returning a canned string: probing a
/// file this test process holds exclusively must name this process's own pid, and probing a file nobody
/// holds must not.
/// </summary>
public class FileHolderProbeTests
{
    [Fact]
    public void Names_the_process_that_holds_a_file_open_exclusively()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Off Windows the class of violation cannot arise (FileShare is not enforced), so the probe
            // is a deliberate marker, not a real query.
            Assert.Contains("Windows-only", FileHolderProbe.DescribeHolders("irrelevant"));
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"holder-probe-{Guid.NewGuid():N}.tmp");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            // The only holder is this test process. Naming our own pid proves the probe read the live
            // handle table, not a placeholder — the discriminating check.
            Assert.Contains($"pid {Environment.ProcessId}", FileHolderProbe.DescribeHolders(path));
        }

        FileCleanup.Delete(path);
    }

    [Fact]
    public void Does_not_name_this_process_for_a_file_it_does_not_hold()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"holder-probe-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "x"); // written and closed — this process holds no handle to it now
        try
        {
            // Negative control: robust even if an external scanner transiently grabs the file (it would
            // name the scanner, never us). A blind probe returning a canned "held by pid <self>" fails here.
            Assert.DoesNotContain($"pid {Environment.ProcessId}", FileHolderProbe.DescribeHolders(path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
