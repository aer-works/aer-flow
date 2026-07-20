using Aer.Workers.Dialogue;

namespace Aer.Workers.Dialogue.Tests;

public class DialogueWorkerConfigParserTests
{
    private const string ValidJson = """
        {
          "SeedPrompt": "Propose a design.",
          "TurnBudget": 4,
          "FinalOutputName": "transcript-summary.md",
          "StopSentinel": null,
          "Participants": [
            {
              "Role": "initiator",
              "Vendor": "claude",
              "Model": null,
              "Preamble": "You are the architect.",
              "Command": "claude",
              "Args": ["-p", "{PROMPT}"]
            },
            {
              "Role": "responder",
              "Vendor": "gemini",
              "Model": null,
              "Preamble": "You are the critic.",
              "Command": "agy",
              "Args": ["-p", "{PROMPT}"]
            }
          ]
        }
        """;

    [Fact]
    public void Parses_a_well_formed_config()
    {
        var config = DialogueWorkerConfigParser.Parse(ValidJson);

        Assert.Equal("Propose a design.", config.SeedPrompt);
        Assert.Equal(4, config.TurnBudget);
        Assert.Equal("transcript-summary.md", config.FinalOutputName);
        Assert.Null(config.StopSentinel);
        Assert.Equal(2, config.Participants.Count);
        Assert.Equal("initiator", config.Participants[0].Role);
        Assert.Equal("claude", config.Participants[0].Vendor);
        Assert.Equal("responder", config.Participants[1].Role);
        Assert.Equal("gemini", config.Participants[1].Vendor);
    }

    [Fact]
    public void Malformed_json_throws()
    {
        var ex = Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse("{ not json"));
        Assert.Contains("Malformed", ex.Message);
    }

    [Fact]
    public void An_empty_document_throws()
    {
        Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse("null"));
    }

    [Theory]
    [InlineData("SeedPrompt", "\"\"")]
    [InlineData("FinalOutputName", "\"\"")]
    [InlineData("TurnBudget", "0")]
    [InlineData("TurnBudget", "-1")]
    public void A_missing_or_invalid_top_level_field_throws(string field, string invalidValue)
    {
        var json = ReplaceField(ValidJson, field, invalidValue);

        Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse(json));
    }

    [Fact]
    public void A_participant_missing_the_prompt_placeholder_throws()
    {
        var json = ValidJson.Replace("""["-p", "{PROMPT}"]""", """["-p"]""");

        var ex = Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse(json));
        Assert.Contains("{PROMPT}", ex.Message);
    }

    [Fact]
    public void A_participant_missing_its_command_throws()
    {
        var json = ValidJson.Replace("\"Command\": \"claude\"", "\"Command\": \"\"");

        Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse(json));
    }

    [Fact]
    public void Fewer_than_two_participants_throws()
    {
        const string json = """
            {
              "SeedPrompt": "Propose a design.",
              "TurnBudget": 4,
              "FinalOutputName": "transcript-summary.md",
              "StopSentinel": null,
              "Participants": [
                {
                  "Role": "initiator",
                  "Vendor": "claude",
                  "Model": null,
                  "Preamble": "You are the architect.",
                  "Command": "claude",
                  "Args": ["-p", "{PROMPT}"]
                }
              ]
            }
            """;

        var ex = Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse(json));
        Assert.Contains("Participants", ex.Message);
    }

    private static string ReplaceField(string json, string field, string value) =>
        System.Text.RegularExpressions.Regex.Replace(json, $"\"{field}\":\\s*(\"[^\"]*\"|-?\\d+|null)", $"\"{field}\": {value}");
}
