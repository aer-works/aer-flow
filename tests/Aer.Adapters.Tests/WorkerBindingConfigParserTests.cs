namespace Aer.Adapters.Tests;

public class WorkerBindingConfigParserTests
{
    private const string ValidJson = """
        {
          "architect": {
            "Adapter": "echo",
            "Contract": {
              "WorkerName": "architect",
              "RequiredInputs": [],
              "ProducedOutputs": [{ "Name": "plan" }],
              "OptionalMetadata": []
            },
            "PromptTemplate": "Draft a plan and write it to your output file.",
            "Timeout": "00:05:00",
            "Model": "claude-opus-4",
            "PermissionScope": "write-only"
          }
        }
        """;

    [Fact]
    public void A_valid_config_parses_into_one_entry_per_worker_name()
    {
        var config = WorkerBindingConfigParser.Parse(ValidJson);

        var entry = Assert.Single(config).Value;
        Assert.Equal("architect", config.Keys.Single());
        Assert.Equal("echo", entry.Adapter);
        Assert.Equal("architect", entry.Contract.WorkerName);
        Assert.Equal(["plan"], entry.Contract.ProducedOutputs.Select(o => o.Name));
        Assert.Equal("Draft a plan and write it to your output file.", entry.PromptTemplate);
        Assert.Equal(TimeSpan.FromMinutes(5), entry.Timeout);
        Assert.Equal("claude-opus-4", entry.Model);
        Assert.Equal("write-only", entry.PermissionScope);
    }

    [Fact]
    public void Model_and_permission_scope_are_optional()
    {
        const string json = """
            {
              "critic": {
                "Adapter": "echo",
                "Contract": {
                  "WorkerName": "critic",
                  "RequiredInputs": ["plan"],
                  "ProducedOutputs": [{ "Name": "review" }],
                  "OptionalMetadata": []
                },
                "PromptTemplate": "Review the plan.",
                "Timeout": "00:01:00"
              }
            }
            """;

        var config = WorkerBindingConfigParser.Parse(json);

        var entry = config["critic"];
        Assert.Null(entry.Model);
        Assert.Null(entry.PermissionScope);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    public void Malformed_json_throws(string json)
    {
        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
    }

    [Fact]
    public void Null_document_throws()
    {
        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse("null"));
    }

    [Fact]
    public void An_entry_missing_Adapter_throws()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "",
                "Contract": { "WorkerName": "architect", "RequiredInputs": [], "ProducedOutputs": [], "OptionalMetadata": [] },
                "PromptTemplate": "Draft a plan.",
                "Timeout": "00:05:00"
              }
            }
            """;

        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
    }

    [Fact]
    public void An_entry_missing_Contract_throws()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "echo",
                "PromptTemplate": "Draft a plan.",
                "Timeout": "00:05:00"
              }
            }
            """;

        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
    }

    [Fact]
    public void An_entry_missing_PromptTemplate_throws()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "echo",
                "Contract": { "WorkerName": "architect", "RequiredInputs": [], "ProducedOutputs": [], "OptionalMetadata": [] },
                "Timeout": "00:05:00"
              }
            }
            """;

        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
    }

    [Fact]
    public async Task LoadFromFileAsync_reads_and_parses_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, ValidJson);
        try
        {
            var config = await WorkerBindingConfigParser.LoadFromFileAsync(path);
            Assert.Single(config);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
