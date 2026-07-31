using Aer.Workers.Dialogue;

namespace Aer.Workers.Dialogue.Tests;

public class DialogueWorkerConfigParserTests
{
    private const string ValidJson = """
        {
          "SeedPrompt": "Propose a design.",
          "TurnBudget": 4,
          "FinalOutputName": "transcript-summary.md",
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
        Assert.Equal(2, config.Participants.Count);
        Assert.Equal("initiator", config.Participants[0].Role);
        Assert.Equal("claude", config.Participants[0].Vendor);
        Assert.Equal("responder", config.Participants[1].Role);
        Assert.Equal("gemini", config.Participants[1].Vendor);
        Assert.Equal(TimeSpan.FromMinutes(5), config.TurnTimeout);
    }

    /// <summary>
    /// #820: an old config persisted before StopSentinel was retired from the record still parses —
    /// System.Text.Json's default UnmappedMemberHandling.Skip drops the unknown key rather than
    /// throwing, so nothing needs a compat shim on the reading side.
    /// </summary>
    [Fact]
    public void A_config_carrying_the_retired_StopSentinel_key_still_parses()
    {
        var json = ValidJson.Replace(
            "\"FinalOutputName\": \"transcript-summary.md\",",
            "\"FinalOutputName\": \"transcript-summary.md\", \"StopSentinel\": \"STOP\",");

        var config = DialogueWorkerConfigParser.Parse(json);

        Assert.Equal("transcript-summary.md", config.FinalOutputName);
        Assert.Equal(2, config.Participants.Count);
    }

    [Fact]
    public void Parses_custom_turn_timeout()
    {
        var json = ValidJson.Replace("\"TurnBudget\": 4,", "\"TurnBudget\": 4, \"TurnTimeout\": \"00:02:00\",");

        var config = DialogueWorkerConfigParser.Parse(json);

        Assert.Equal(TimeSpan.FromMinutes(2), config.TurnTimeout);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:01:00")]
    public void Non_positive_turn_timeout_throws(string timeoutStr)
    {
        var json = ValidJson.Replace("\"TurnBudget\": 4,", $"\"TurnBudget\": 4, \"TurnTimeout\": \"{timeoutStr}\",");

        var ex = Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse(json));
        Assert.Contains("TurnTimeout", ex.Message);
    }

    [Fact]
    public void Absent_FinalOutputMode_defaults_to_FinalTurn()
    {
        var config = DialogueWorkerConfigParser.Parse(ValidJson);

        Assert.Equal(FinalOutputMode.FinalTurn, config.FinalOutputMode);
    }

    [Theory]
    [InlineData("FinalTurn", FinalOutputMode.FinalTurn)]
    [InlineData("Transcript", FinalOutputMode.Transcript)]
    public void Each_valid_FinalOutputMode_value_parses_to_itself(string value, FinalOutputMode expected)
    {
        var json = ValidJson.Replace(
            "\"TurnBudget\": 4,", $"\"TurnBudget\": 4, \"FinalOutputMode\": \"{value}\",");

        var config = DialogueWorkerConfigParser.Parse(json);

        Assert.Equal(expected, config.FinalOutputMode);
    }

    [Fact]
    public void An_unknown_FinalOutputMode_value_throws_naming_the_value_and_the_valid_set()
    {
        var json = ValidJson.Replace(
            "\"TurnBudget\": 4,", "\"TurnBudget\": 4, \"FinalOutputMode\": \"Bogus\",");

        var ex = Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse(json));

        Assert.DoesNotContain("Malformed", ex.Message);
        Assert.Contains("FinalOutputMode", ex.Message);
        Assert.Contains("Bogus", ex.Message);
        Assert.Contains("FinalTurn", ex.Message);
        Assert.Contains("Transcript", ex.Message);
    }

    [Fact]
    public void Malformed_json_throws()
    {
        var ex = Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse("{ not json"));
        Assert.Contains("Malformed", ex.Message);
    }

    /// <summary>
    /// The #761 review's off-list gap: a wrong-TYPE value for the enum (number, boolean, object)
    /// was correct only by code inspection. Same contract as the unknown-string arm — the message
    /// names the field, never claims the JSON is malformed.
    /// </summary>
    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("{}")]
    public void A_wrong_typed_FinalOutputMode_value_names_the_field_not_malformed_JSON(string wrongTyped)
    {
        var json = ValidJson.Replace(
            "\"TurnBudget\": 4,", $"\"TurnBudget\": 4, \"FinalOutputMode\": {wrongTyped},");

        var ex = Assert.Throws<DialogueWorkerConfigException>(() => DialogueWorkerConfigParser.Parse(json));

        Assert.DoesNotContain("Malformed", ex.Message);
        Assert.Contains("FinalOutputMode", ex.Message);
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
    /// Decision 0039 retired {PROMPT_FILE}: DialogueParticipantPresets.For now builds every preset
    /// participant with the exact-match DialogueParticipant.PromptPlaceholder, the same shape this
    /// class's other tests use — a preset-built config must parse the same way an authored one does.
    /// </summary>
    [Fact]
    public void A_participant_built_from_a_preset_parses()
    {
        var config = new DialogueWorkerConfig(
            SeedPrompt: "Propose a design.",
            TurnBudget: 4,
            FinalOutputName: "transcript-summary.md",
            Participants:
            [
                DialogueParticipantPresets.For("claude", "initiator", "You are the architect.", model: null),
                DialogueParticipantPresets.For("gemini", "responder", "You are the critic.", model: null),
            ]);
        var json = System.Text.Json.JsonSerializer.Serialize(config);

        var parsed = DialogueWorkerConfigParser.Parse(json);

        Assert.Contains(parsed.Participants[0].Args, a => a == DialogueParticipant.PromptPlaceholder);
        Assert.Contains(parsed.Participants[1].Args, a => a == DialogueParticipant.PromptPlaceholder);
    }

    /// <summary>
    /// #836: the test above only ever passes model: null, so it never parser-judges ModelArgs --
    /// the half of DialogueParticipantPresets.json that tools/aer-agy-loop/dispatch.py always uses
    /// (its --participant flag makes a model mandatory). Without this arm, a broken ModelArgs
    /// entry in the shared JSON could stay green here while every generated dialogue config with a
    /// model still fails at parse time -- the #586 failure mode, relocated rather than closed.
    /// </summary>
    [Fact]
    public void A_participant_built_from_a_preset_with_a_model_parses()
    {
        var config = new DialogueWorkerConfig(
            SeedPrompt: "Propose a design.",
            TurnBudget: 4,
            FinalOutputName: "transcript-summary.md",
            Participants:
            [
                DialogueParticipantPresets.For("claude", "initiator", "You are the architect.", model: "sonnet"),
                DialogueParticipantPresets.For("gemini", "responder", "You are the critic.", model: "gemini-3.6-flash-high"),
            ]);
        var json = System.Text.Json.JsonSerializer.Serialize(config);

        var parsed = DialogueWorkerConfigParser.Parse(json);

        Assert.Contains(parsed.Participants[0].Args, a => a == DialogueParticipant.PromptPlaceholder);
        Assert.Contains(parsed.Participants[1].Args, a => a == DialogueParticipant.PromptPlaceholder);
        Assert.Contains("sonnet", parsed.Participants[0].Args);
        Assert.Contains("gemini-3.6-flash-high", parsed.Participants[1].Args);
    }

    /// <summary>The negative arm: a participant with no {PROMPT} element at all — not even embedded in a longer string — must be rejected.</summary>
    [Fact]
    public void A_participant_with_no_placeholder_throws()
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
