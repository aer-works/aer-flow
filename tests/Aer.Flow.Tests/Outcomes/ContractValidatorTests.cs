using Aer.Flow.Tests.TestSupport;
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

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"contract-validator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
