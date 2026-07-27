using Aer.Flow.Tests.TestSupport;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Outcomes;

namespace Aer.Flow.Tests.Outcomes;

public class ContractValidatorTests
{
    [Fact]
    public void IsSatisfied_true_when_the_contract_declares_no_outputs()
    {
        var contract = new WorkerContract("worker", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []);

        Assert.True(ContractValidator.IsSatisfied(contract, "/does-not-matter"));
    }

    [Fact]
    public void IsSatisfied_false_when_a_required_output_file_is_missing()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            Assert.False(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_true_when_the_output_file_exists_and_declares_no_condition()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "anything");
            var contract = new WorkerContract("worker", [], [new ProducedOutput("plan")], []);

            Assert.True(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_true_when_the_declared_condition_is_met()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), """{"status": "approved"}""");
            var condition = new OutputCondition("/status", new JsonScalar.String("approved"));
            var contract = new WorkerContract("worker", [], [new ProducedOutput("verdict.json", condition)], []);

            Assert.True(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_false_when_the_declared_condition_value_does_not_match()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), """{"status": "needs_revision"}""");
            var condition = new OutputCondition("/status", new JsonScalar.String("approved"));
            var contract = new WorkerContract("worker", [], [new ProducedOutput("verdict.json", condition)], []);

            Assert.False(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_false_when_the_output_file_is_not_valid_json()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), "not json");
            var condition = new OutputCondition("/status", new JsonScalar.String("approved"));
            var contract = new WorkerContract("worker", [], [new ProducedOutput("verdict.json", condition)], []);

            Assert.False(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_false_when_the_pointer_does_not_resolve()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "verdict.json"), """{"other": "field"}""");
            var condition = new OutputCondition("/status", new JsonScalar.String("approved"));
            var contract = new WorkerContract("worker", [], [new ProducedOutput("verdict.json", condition)], []);

            Assert.False(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_compares_numbers_by_value_not_by_representation()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "score.json"), """{"value": 80}""");
            var condition = new OutputCondition("/value", new JsonScalar.Number(80.0));
            var contract = new WorkerContract("worker", [], [new ProducedOutput("score.json", condition)], []);

            Assert.True(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void IsSatisfied_requires_all_outputs_when_multiple_are_declared()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "plan"), "anything");
            var contract = new WorkerContract(
                "worker", [], [new ProducedOutput("plan"), new ProducedOutput("review")], []);

            Assert.False(ContractValidator.IsSatisfied(contract, directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// The four ways an output goes unsatisfied must be told apart in the reason, since collapsing
    /// them into one <c>false</c> is the defect #597 exists to fix.
    /// </summary>
    /// <remarks>
    /// <b>Every arm uses the same output name, in its own directory.</b> The first version of this
    /// test gave each arm a different filename — <c>missing.json</c>, <c>invalid.json</c>,
    /// <c>mismatch.json</c> — which made its pairwise <c>NotEqual</c> assertions satisfiable by the
    /// filename alone: an implementation rendering all four cases as <c>'X' is missing</c> passed it
    /// in full, which is exactly the collapse the test is named for. Holding the name constant is
    /// what forces the strings to differ by *kind*. Caught by an independent reviewer.
    /// <para>
    /// The resolved-to-wrong-value and pointer-did-not-resolve arms share a
    /// <see cref="UnsatisfiedOutputReason"/> value, so they are the pair most likely to collapse and
    /// the one the earlier version never compared. They are compared here.
    /// </para>
    /// </remarks>
    [Fact]
    public void ContractValidator_distinguishes_missing_file_invalid_json_and_both_condition_failures()
    {
        const string outputName = "result.json";
        var condition = new OutputCondition("/status", new JsonScalar.String("approved"));
        var contract = new WorkerContract("worker", [], [new ProducedOutput(outputName, condition)], []);
        var missingContract = new WorkerContract("worker", [], [new ProducedOutput(outputName)], []);

        var directories = new List<string>();
        try
        {
            string ClassifyIn(WorkerContract usedContract, string? fileContent)
            {
                var directory = CreateTempDirectory();
                directories.Add(directory);
                if (fileContent is not null)
                {
                    File.WriteAllText(Path.Combine(directory, outputName), fileContent);
                }

                var classification = OutcomeClassifier.Classify(
                    new CoreDispatchResult(0, CoreExitReason.Natural), usedContract, directory);

                Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
                Assert.NotNull(classification.Reason);
                Assert.Contains(outputName, classification.Reason);
                return classification.Reason;
            }

            var missing = ClassifyIn(missingContract, null);
            var notJson = ClassifyIn(contract, "not json");
            var wrongValue = ClassifyIn(contract, """{"status": "needs_revision"}""");
            var didNotResolve = ClassifyIn(contract, """{"other": "value"}""");

            // Each kind says its own thing. These are what make the NotEqual assertions below mean
            // "distinguished by kind" rather than "distinguished by some incidental difference".
            Assert.Contains("is missing", missing);
            Assert.Contains("is not valid JSON", notJson);
            Assert.Contains("resolved to", wrongValue);
            Assert.Contains("did not resolve", didNotResolve);

            // The mismatch arm names both sides of the comparison — the delta is the diagnostic.
            Assert.Contains("needs_revision", wrongValue);
            Assert.Contains("approved", wrongValue);

            Assert.Equal(4, new HashSet<string> { missing, notJson, wrongValue, didNotResolve }.Count);
        }
        finally
        {
            foreach (var directory in directories)
            {
                DirectoryCleanup.DeleteRecursively(directory);
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"contract-validator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}

