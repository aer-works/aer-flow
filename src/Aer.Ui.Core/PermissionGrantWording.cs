using Aer.Adapters;

namespace Aer.Ui.Core;

/// <summary>
/// How a permission-grant refusal is worded for a person, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Three authoring surfaces refuse the same grant — the bindings editor's inline warning, its Save,
/// and the guided wizard's validation — and each needs its own opening clause because each interrupts
/// the operator at a different moment. What they must not each own is the <em>explanation</em>: three
/// hand-written versions of "a shell command reaches them anyway" is three things to keep true when
/// the rule changes, and the rule already moved once.
/// </para>
/// <para>
/// The rule itself is not here. It lives on
/// <see cref="PermissionGrant.CategoriesDefeatedByTheShell"/>, which every surface asks; this type
/// only renders the answer.
/// </para>
/// </remarks>
internal static class PermissionGrantWording
{
    /// <summary>
    /// Why a grant whose shell defeats <paramref name="defeated"/> is refused, with no leading
    /// context — each caller supplies the clause that says where the operator is.
    /// </summary>
    internal static string ShellDefeats(IReadOnlyList<string> defeated) =>
        $"the shell is granted while {NaturalList(defeated)} "
        + $"{(defeated.Count == 1 ? "is" : "are")} withheld, and a shell command reaches "
        + $"{(defeated.Count == 1 ? "it" : "them")} anyway. The engine refuses this at bind time.";

    /// <summary>Renders the category names as prose — this line is read by a person, not parsed.</summary>
    internal static string NaturalList(IReadOnlyList<string> names)
    {
        var spaced = names.Select(SpaceOutPascalCase).ToList();
        return spaced.Count switch
        {
            0 => string.Empty,
            1 => spaced[0],
            2 => $"{spaced[0]} and {spaced[1]}",
            _ => $"{string.Join(", ", spaced.Take(spaced.Count - 1))} and {spaced[^1]}",
        };
    }

    /// <summary>
    /// <c>ReadFiles</c> → <c>read files</c>. The categories are named for the code; the warning is
    /// named for the checkbox beside it.
    /// </summary>
    private static string SpaceOutPascalCase(string name) =>
        string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : $"{char.ToLowerInvariant(c)}"));
}
