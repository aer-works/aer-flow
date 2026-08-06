using System.Reflection;
using System.Text.Json;

namespace Aer.Workers.Dialogue;

/// <summary>
/// The known vendors' participant invocation shapes (M19 Phase 4, issue #189) — the same
/// one-shot-text-turn flags <c>ClaudeWorkerAdapter</c>/<c>AgyWorkerAdapter</c> build for a
/// top-level dispatch, minus Flow's <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c> convention
/// (see <see cref="DialogueParticipant"/>'s remarks), previously duplicated by hand in every
/// smoke test and now owned here: the worker that invokes the participants is where their
/// invocation knowledge lives, so the UI's guided authoring can offer vendor presets without
/// re-encoding any adapter quirk (the phase's named open question).
///
/// #836: the shapes themselves (<c>Command</c>/<c>Args</c>/<c>ModelArgs</c>) live in the embedded
/// <c>DialogueParticipantPresets.json</c>, the single source also read directly by
/// <c>tools/aer-agy-loop/dispatch.py</c>'s <c>build_dialogue_participant</c> — #586 changed this
/// formula while dispatch.py hand-mirrored the old one, and every generated dialogue config
/// became parser-refused while the mirroring checker stayed green pinning dispatch.py's own
/// stale output. A shared data source makes that divergence impossible to construct: there is
/// only one place left to edit.
/// </summary>
public static class DialogueParticipantPresets
{
    private const string ModelToken = "{MODEL}";

    private sealed record PresetShape(
        string Vendor,
        string Command,
        IReadOnlyList<string> Args,
        IReadOnlyList<string> ModelArgs);

    private static readonly IReadOnlyDictionary<string, PresetShape> Shapes = LoadShapes();

    public static readonly IReadOnlyList<string> KnownVendors = Shapes.Keys.ToArray();

    /// <summary>Builds a real vendor participant; throws for a vendor no preset exists for — callers offer <see cref="KnownVendors"/>, they never free-type.</summary>
    public static DialogueParticipant For(string vendor, string role, string preamble, string? model)
    {
        ArgumentException.ThrowIfNullOrEmpty(vendor);
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentException.ThrowIfNullOrEmpty(preamble);

        if (!Shapes.TryGetValue(vendor, out var shape))
        {
            throw new ArgumentException($"No participant preset exists for vendor '{vendor}'.", nameof(vendor));
        }

        var args = shape.Args.ToList();
        if (model is not null)
        {
            args.AddRange(shape.ModelArgs.Select(a => a.Replace(ModelToken, model, StringComparison.Ordinal)));
        }

        return new DialogueParticipant(role, vendor, model, preamble, shape.Command, args);
    }

    private static IReadOnlyDictionary<string, PresetShape> LoadShapes()
    {
        var assembly = typeof(DialogueParticipantPresets).Assembly;
        const string resourceName = "Aer.Workers.Dialogue.DialogueParticipantPresets.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' is missing from {assembly.FullName} — " +
                "DialogueParticipantPresets.json must be an EmbeddedResource in Aer.Workers.Dialogue.csproj.");

        var shapes = JsonSerializer.Deserialize<List<PresetShape>>(stream)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' did not contain a JSON array.");

        // The JSON writes "{PROMPT}" as a literal, decoupled from DialogueParticipant.PromptPlaceholder;
        // this check is what re-couples them, failing at first load rather than at parse time downstream.
        foreach (var shape in shapes.Where(s => !s.Args.Contains(DialogueParticipant.PromptPlaceholder)))
        {
            throw new InvalidOperationException(
                $"Preset '{shape.Vendor}' in '{resourceName}' has no whole-element " +
                $"'{DialogueParticipant.PromptPlaceholder}' argument — the JSON's literal has drifted from the constant.");
        }

        return shapes.ToDictionary(s => s.Vendor, s => s);
    }
}
