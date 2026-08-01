using System.Text.Json;

namespace Aer.Adapters;

/// <summary>
/// The volatile half of a worker role (#888): which vendor/model/effort actually runs it. Lives in
/// <c>WorkerTiers.json</c>, separate from the roles, so swapping a model is one edit that every role
/// on the tier inherits — and, because the catalog is read at runtime rather than embedded, that edit
/// needs no rebuild (drop a <c>worker-tiers.json</c> under <see cref="AerPaths.Root"/>, or point
/// <see cref="WorkerRoleCatalog.TiersPathEnvironmentVariable"/> at one).
/// </summary>
public sealed record WorkerTier(string Adapter, string? Model, string? Effort);

/// <summary>
/// A composable worker-role profile — the building block the front door (#887) composes into
/// workflows. The <b>stable</b> half (grant, timeout, verdict, purpose) is authored in
/// <c>WorkerRoles.json</c>; the <b>volatile</b> half (<see cref="Adapter"/>/<see cref="Model"/>/
/// <see cref="Effort"/>) is resolved from the role's <see cref="Tier"/> in <c>WorkerTiers.json</c>.
/// A role never names a vendor or model directly, so a model swap never touches a role's capability.
/// </summary>
public sealed record WorkerRole(
    string Id,
    string Tier,
    string Adapter,
    string? Model,
    string? Effort,
    PermissionGrant Grant,
    TimeSpan Timeout,
    bool ProducesVerdict,
    string Purpose);

/// <summary>
/// The single, shared worker-role catalog — the same <c>WorkerRoles.json</c>/<c>WorkerTiers.json</c>
/// that <c>tools/aer-agy-loop/dispatch.py</c> reads (#888, the #836 shared-source pattern). Read at
/// runtime, never embedded, so the operator can retune tiers without a rebuild.
/// </summary>
/// <remarks>
/// Resolution order per file, evaluated fresh on every access (the same "resolve, never capture"
/// discipline <see cref="AerPaths"/> keeps, so a test or a live edit is honoured immediately):
/// <list type="number">
/// <item>the <c>AER_WORKER_*_PATH</c> environment override, when set — for a one-off experiment;</item>
/// <item><c>{AerPaths.Root}/worker-tiers.json</c> (or <c>worker-roles.json</c>) when it exists — the
///   operator's durable, rebuild-free override;</item>
/// <item>the default shipped next to the assembly (<see cref="AppContext.BaseDirectory"/>).</item>
/// </list>
/// Tiers and roles resolve independently, so overriding a model does not freeze the role definitions.
/// </remarks>
public static class WorkerRoleCatalog
{
    public const string TiersPathEnvironmentVariable = "AER_WORKER_TIERS_PATH";
    public const string RolesPathEnvironmentVariable = "AER_WORKER_ROLES_PATH";

    private const string TiersDefaultFileName = "WorkerTiers.json";
    private const string RolesDefaultFileName = "WorkerRoles.json";
    private const string TiersOverrideFileName = "worker-tiers.json";
    private const string RolesOverrideFileName = "worker-roles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Every role in the catalog, resolved against the current tiers, in file order.</summary>
    public static IReadOnlyList<WorkerRole> All => Load();

    /// <summary>The role with <paramref name="id"/>, or throws if the catalog has no such role.</summary>
    public static WorkerRole For(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"No worker role '{id}' in the catalog. Known roles: {string.Join(", ", All.Select(r => r.Id))}.");
    }

    private static IReadOnlyList<WorkerRole> Load()
    {
        var tiers = ReadJson<Dictionary<string, WorkerTier>>(
            ResolvePath(TiersPathEnvironmentVariable, TiersOverrideFileName, TiersDefaultFileName), "tier map");
        var rawRoles = ReadJson<List<RawRole>>(
            ResolvePath(RolesPathEnvironmentVariable, RolesOverrideFileName, RolesDefaultFileName), "role list");

        if (rawRoles.Count == 0)
        {
            throw new InvalidOperationException("The worker-role catalog is empty.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roles = new List<WorkerRole>(rawRoles.Count);
        foreach (var raw in rawRoles)
        {
            if (!seen.Add(raw.Id))
            {
                throw new InvalidOperationException($"Duplicate worker role id '{raw.Id}' in the catalog.");
            }

            if (!tiers.TryGetValue(raw.Tier, out var tier))
            {
                throw new InvalidOperationException(
                    $"Worker role '{raw.Id}' names tier '{raw.Tier}', which is not defined in the tier map. " +
                    $"Known tiers: {string.Join(", ", tiers.Keys)}.");
            }

            roles.Add(new WorkerRole(
                Id: raw.Id,
                Tier: raw.Tier,
                Adapter: tier.Adapter,
                Model: tier.Model,
                Effort: tier.Effort,
                Grant: new PermissionGrant(
                    ReadFiles: raw.ReadFiles,
                    WriteFiles: raw.WriteFiles,
                    RunShellCommands: raw.RunShellCommands,
                    NetworkAccess: raw.NetworkAccess),
                Timeout: TimeSpan.FromMinutes(raw.TimeoutMinutes),
                ProducesVerdict: raw.VerdictSchema,
                Purpose: raw.Purpose));
        }

        return roles;
    }

    private static string ResolvePath(string envVar, string overrideFileName, string defaultFileName)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var configOverride = Path.Combine(AerPaths.Root, overrideFileName);
        return File.Exists(configOverride)
            ? configOverride
            : Path.Combine(AppContext.BaseDirectory, defaultFileName);
    }

    private static T ReadJson<T>(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The worker-role catalog's {what} was not found at '{path}'. The default ships next to " +
                "the engine; an override lives under AER_HOME or the AER_WORKER_*_PATH env var.", path);
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"The worker-role catalog's {what} at '{path}' parsed to null.");
    }

    private sealed record RawRole(
        string Id,
        string Tier,
        bool ReadFiles,
        bool WriteFiles,
        bool RunShellCommands,
        bool NetworkAccess,
        int TimeoutMinutes,
        bool VerdictSchema,
        string Purpose);
}
