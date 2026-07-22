using Aer.Flow.Tests.TestSupport;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Outcomes;

namespace Aer.Flow.Tests.Outcomes;

public class OutcomeClassifierTests
{
    [Fact]
    public void Classify_returns_Succeeded_for_a_clean_exit_with_all_outputs_present()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
            Assert.Null(classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_returns_Failed_when_exit_code_is_zero_but_a_required_output_is_missing()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_returns_Failed_for_a_non_zero_exit_code()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_returns_Failed_for_a_timeout_regardless_of_exit_code_or_outputs()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_returns_Cancelled_for_a_cancel_requested_exit_even_with_a_non_zero_code()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(137, CoreExitReason.CancelRequested), contract, directory);

            Assert.Equal(OutcomeVerdict.Cancelled, classification.Verdict);
            Assert.Null(classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_reads_a_self_reported_Permanent_FailureClassification_from_OptionalMetadata()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "outcome.json"), """{"FailureClassification": "Permanent"}""");
            var contract = new WorkerContract("worker", [], [], OptionalMetadata: ["outcome.json"]);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.Permanent, classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_treats_a_missing_or_unrecognized_FailureClassification_as_null()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], OptionalMetadata: ["outcome.json"]);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Null(classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"outcome-classifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
