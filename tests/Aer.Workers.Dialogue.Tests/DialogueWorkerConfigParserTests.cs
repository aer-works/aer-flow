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
        Assert.Equal(TimeSpan.FromMinutes(5), config.TurnTimeout);
    }

    [Fact]
    public void Parses_custom_turn_timeout()
    {
        var json = ValidJson.Replace("\"TurnBudget\": 4,", "\"TurnBudget\": 4, \"TurnTimeout\": \"00:02:00\",");

        var config = DialogueWorkerConfigParser.Parse(json);

        Assert.Equal(TimeSpan.FromMinutes(2), config.TurnTimeout);
    }

    [Theory]
    [InlineData("-00:01:00")]
    public void Negative_turn_timeout_throws(string timeoutStr)
    {
        var json = ValidJson.Replace("\"TurnBudget\": 4,", $"\"TurnBudget\": 4, \"TurnTimeout\": \"{timeoutStr}\",");

        var ex = Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse(json));
        Assert.Contains("TurnTimeout", ex.Message);
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

    /// <summary>
    /// #579 regression: DialogueParticipantPresets.For builds participants with
    /// DialogueParticipant.PromptFilePlaceholder embedded inside a longer instruction string, not the
    /// exact-match DialogueParticipant.PromptPlaceholder this class's other tests use. A config built
    /// this way — the shape aer-dialogue actually loads for any preset-based participant — must parse.
    /// </summary>
    [Fact]
    public void A_participant_using_the_prompt_file_placeholder_parses()
    {
        var config = new DialogueWorkerConfig(
            SeedPrompt: "Propose a design.",
            TurnBudget: 4,
            FinalOutputName: "transcript-summary.md",
            StopSentinel: null,
            Participants:
            [
                DialogueParticipantPresets.For("claude", "initiator", "You are the architect.", model: null),
                DialogueParticipantPresets.For("gemini", "responder", "You are the critic.", model: null),
            ]);
        var json = System.Text.Json.JsonSerializer.Serialize(config);

        var parsed = DialogueWorkerConfigParser.Parse(json);

        Assert.Contains(
            parsed.Participants[0].Args,
            a => a.Contains(DialogueParticipant.PromptFilePlaceholder, StringComparison.Ordinal));
        Assert.Contains(
            parsed.Participants[1].Args,
            a => a.Contains(DialogueParticipant.PromptFilePlaceholder, StringComparison.Ordinal));
    }

    /// <summary>
    /// The negative arm of the above: a participant with neither placeholder — not even embedded in a
    /// longer string — must still be rejected. Guards against loosening the {PROMPT_FILE} check to
    /// substring-match without keeping this failure mode covered.
    /// </summary>
    [Fact]
    public void A_participant_with_neither_placeholder_throws()
    {
        var json = ValidJson.Replace("""["-p", "{PROMPT}"]""", """["-p", "no placeholder here"]""");

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
