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

    [Fact]
    public void Classify_includes_exit_code_in_Reason_for_non_zero_exit()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [], []);

            var class1 = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);
            var class42 = OutcomeClassifier.Classify(
                new CoreDispatchResult(42, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, class1.Verdict);
            Assert.NotNull(class1.Reason);
            Assert.Contains("1", class1.Reason);

            Assert.Equal(OutcomeVerdict.Failed, class42.Verdict);
            Assert.NotNull(class42.Reason);
            Assert.Contains("42", class42.Reason);

            // Polarity: distinct exit codes produce distinct reasons
            Assert.NotEqual(class1.Reason, class42.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_includes_timeout_diagnostic_in_Reason_for_timed_out_execution()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "content");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            var classTimeout = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.TimedOut), contract, directory);
            var classExitCode = OutcomeClassifier.Classify(
                new CoreDispatchResult(1, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classTimeout.Verdict);
            Assert.NotNull(classTimeout.Reason);

            // Polarity: timeout reason differs from exit code failure reason
            Assert.NotEqual(classTimeout.Reason, classExitCode.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_lists_all_unsatisfied_outputs_in_Reason_when_multiple_fail()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contractBothMissing = new WorkerContract(
                "worker", [], [new ProducedOutput("alpha.txt"), new ProducedOutput("beta.json")], []);
            var contractSingleMissing = new WorkerContract(
                "worker", [], [new ProducedOutput("alpha.txt")], []);

            var classBoth = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contractBothMissing, directory);
            var classSingle = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contractSingleMissing, directory);

            Assert.Equal(OutcomeVerdict.Failed, classBoth.Verdict);
            Assert.NotNull(classBoth.Reason);
            Assert.Contains("alpha.txt", classBoth.Reason);
            Assert.Contains("beta.json", classBoth.Reason);

            Assert.Equal(OutcomeVerdict.Failed, classSingle.Verdict);
            Assert.NotNull(classSingle.Reason);
            Assert.Contains("alpha.txt", classSingle.Reason);
            Assert.DoesNotContain("beta.json", classSingle.Reason);

            // Polarity: missing two outputs produces a different reason string than missing one output
            Assert.NotEqual(classBoth.Reason, classSingle.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// The 500-character cap cuts at a fixed index, and a non-BMP character occupies two UTF-16
    /// chars, so a cut can land between them and leave a lone high surrogate — malformed UTF-16
    /// written into an append-only journal. Reachable rather than theoretical: a contract-failure
    /// reason renders values from the worker's own JSON output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The offsets place a single emoji at, on either side of, and across the cut, so exactly one
    /// row lands mid-pair and the neighbours are its controls. An earlier version of this test built
    /// the overlong reason from 35 emoji-laden names and <b>passed with the fix removed</b> — the
    /// per-name padding shifted the cut by a multiple of itself rather than by one char, so no row
    /// ever straddled a pair. Computing the boundary rather than hoping to hit it is the difference
    /// between this test and that one.
    /// </para>
    /// <para>
    /// Asserted as a UTF-8 round trip rather than by inspecting chars, because that is the actual
    /// harm: encoding a lone surrogate substitutes U+FFFD, so a reason that survives the round trip
    /// unchanged is exactly one that reaches <c>flow.jsonl</c> intact.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(469)]
    [InlineData(470)]
    [InlineData(471)]
    [InlineData(472)]
    public void Classify_never_truncates_Reason_through_the_middle_of_a_surrogate_pair(int emojiOffset)
    {
        var directory = CreateTempDirectory();
        try
        {
            // "Contract not satisfied: '" is 25 chars, so name index k sits at reason index 25 + k,
            // and the cut is at 500 - "...".Length = 497. Offset 471 therefore puts the pair's high
            // surrogate at 496 and its low surrogate at 497 — straddling the cut exactly.
            var name = new string('a', emojiOffset) + "\U0001F600" + new string('a', 100);
            var contract = new WorkerContract("worker", [], [new ProducedOutput(name)], []);

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.NotNull(classification.Reason);
            Assert.True(classification.Reason.Length <= 500);

            var roundTripped = System.Text.Encoding.UTF8.GetString(
                System.Text.Encoding.UTF8.GetBytes(classification.Reason));

            Assert.Equal(classification.Reason, roundTripped);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void Classify_truncates_Reason_to_500_characters_with_ellipsis_when_pathological()
    {
        var directory = CreateTempDirectory();
        try
        {
            var outputs = Enumerable.Range(1, 35)
                .Select(i => new ProducedOutput($"pathological_long_output_filename_entry_number_{i:D2}_forcing_truncation.json"))
                .ToList();
            var contract = new WorkerContract("worker", [], outputs, []);

            var classTruncated = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), contract, directory);

            Assert.Equal(OutcomeVerdict.Failed, classTruncated.Verdict);
            Assert.NotNull(classTruncated.Reason);
            Assert.True(classTruncated.Reason.Length <= 500, $"Reason length {classTruncated.Reason.Length} exceeded 500 characters cap.");
            Assert.True(
                classTruncated.Reason.EndsWith("...") || classTruncated.Reason.EndsWith("…"),
                "Reason should end with an ellipsis when truncated.");

            // Polarity arm: non-pathological short reason is not truncated and does not end with ellipsis
            var shortContract = new WorkerContract("worker", [], [new ProducedOutput("short.txt")], []);
            var classShort = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural), shortContract, directory);

            Assert.NotNull(classShort.Reason);
            Assert.True(classShort.Reason.Length < 500);
            Assert.False(classShort.Reason.EndsWith("..."));
            Assert.False(classShort.Reason.EndsWith("…"));
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

