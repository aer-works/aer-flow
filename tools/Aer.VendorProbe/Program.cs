using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aer.VendorProbe;

/// <summary>
/// Re-runnable probe of what each vendor CLI can actually do (#504).
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/vendor-capabilities.md</c> has always said "re-run the probes before trusting this after a
/// vendor update — both CLIs self-update." There were no probes to re-run: they were ad-hoc shell,
/// written once and thrown away. This is them, and the point is that a negative result now has to
/// carry the list of surfaces it was established on.
/// </para>
/// <para>
/// Never runs in CI. It drives live authenticated CLIs, which is permanently a human action item
/// (CLAUDE.md). The goal is that one command produces a trustworthy matrix, not that a robot does it
/// nightly.
/// </para>
/// </remarks>
public static class Program
{
    private static readonly string[] Vendors = ["claude", "agy"];

    /// <summary>
    /// No byte-order mark. These outputs are read by other tools, and a BOM makes an otherwise valid
    /// JSON document fail to parse in several of them.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static int Main(string[] args)
    {
        var writeTo = Arg(args, "--out");
        var only = Arg(args, "--vendor");
        var lockPath = Arg(args, "--lock") ?? Staleness.DefaultLockPath;

        var vendors = only is null ? Vendors : [only];

        if (args.Contains("--check"))
        {
            return Check(lockPath, vendors);
        }

        var findings = new List<Finding>();

        foreach (var vendor in vendors)
        {
            Console.WriteLine($"probing {vendor} …");
            var installed = Cli.IsInstalled(vendor);
            if (!installed)
            {
                Console.WriteLine($"  {vendor} is not installed or not on PATH — recording that, not an absence of capabilities.");
            }

            foreach (var f in Probes.RunAll(vendor))
            {
                findings.Add(f);
                var mark = f.Evidence switch
                {
                    Evidence.Observed => "observed ",
                    Evidence.Inspected => "inspected",
                    _ => "NOT FOUND",
                };
                Console.WriteLine($"  [{mark}] {f.Capability}: {f.Value ?? "—"}");
                if (f.Evidence == Evidence.NotFound)
                {
                    Console.WriteLine($"              looked at: {string.Join(", ", f.SurfacesConsulted)}");
                }
            }
        }

        var json = JsonSerializer.Serialize(
            new ProbeRun(DateTimeOffset.Now, Environment.OSVersion.VersionString, findings),
            new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });

        if (writeTo is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(writeTo))!);
            File.WriteAllText(writeTo, json, Utf8NoBom);
            var md = Path.ChangeExtension(writeTo, ".md");
            File.WriteAllText(md, Matrix(findings), Utf8NoBom);
            Console.WriteLine($"\nwrote {writeTo}\nwrote {md}");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine(Matrix(findings));
        }

        // Recorded whether or not --out was given: the versions these findings were established
        // against are what makes the free staleness check possible later.
        Staleness.Write(lockPath, findings);
        Console.WriteLine($"recorded probed versions in {lockPath}");

        var negatives = findings.Count(f => f.Evidence == Evidence.NotFound);
        Console.WriteLine($"\n{findings.Count} findings · {negatives} negative, each carrying the surfaces it was established on.");
        return 0;
    }

    /// <summary>
    /// The free half. Spends no usage, so it can run in the ordinary dev loop — which is the point:
    /// the expensive probe should be triggered by a vendor moving, not by a calendar or by someone
    /// remembering.
    /// </summary>
    private static int Check(string lockPath, IReadOnlyList<string> vendors)
    {
        var statuses = Staleness.Check(lockPath, vendors);

        foreach (var s in statuses)
        {
            var mark = s.Verdict switch
            {
                Staleness.Verdict.Current => "ok     ",
                Staleness.Verdict.Drifted => "STALE  ",
                Staleness.Verdict.NeverProbed => "UNPROBED",
                _ => "unknown",
            };
            Console.WriteLine($"[{mark}] {s.Explain()}");
        }

        var needsProbe = statuses.Where(s =>
            s.Verdict is Staleness.Verdict.Drifted or Staleness.Verdict.NeverProbed).ToList();

        if (needsProbe.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{needsProbe.Count} vendor(s) need a probe run: pixi run vendor-probe");
            return 1;
        }

        if (statuses.All(s => s.Verdict == Staleness.Verdict.Uninspectable))
        {
            // Deliberately exit 0 while saying plainly that nothing was established. A machine with
            // no vendor CLIs cannot fail this check honestly, and it must not pass it silently
            // either — that combination is what makes CI the wrong place to run this at all.
            Console.WriteLine();
            Console.WriteLine(
                "No vendor CLI was inspectable on this machine, so nothing was verified. "
                + "This exit code means 'not applicable here', never 'up to date'.");
        }

        return 0;
    }

    private sealed record ProbeRun(DateTimeOffset RanAt, string Host, IReadOnlyList<Finding> Findings);

    /// <summary>
    /// The matrix, generated. Kept close to <c>docs/vendor-capabilities.md</c>'s shape so the doc can
    /// be gate-checked against a real run rather than hand-maintained beside one.
    /// </summary>
    private static string Matrix(IReadOnlyList<Finding> findings)
    {
        var vendors = findings.Select(f => f.Vendor).Distinct().ToList();
        var caps = findings.Select(f => f.Capability).Distinct().ToList();
        var sb = new StringBuilder();

        sb.AppendLine("| | " + string.Join(" | ", vendors.Select(v =>
        {
            var version = findings.First(f => f.Vendor == v).VendorVersion;
            return $"`{v}` {version ?? "(not installed)"}";
        })) + " |");
        sb.AppendLine("|---|" + string.Concat(vendors.Select(_ => "---|")));

        foreach (var cap in caps)
        {
            var cells = vendors.Select(v =>
                findings.FirstOrDefault(f => f.Vendor == v && f.Capability == cap)?.Rendered() ?? "—");
            sb.AppendLine($"| {cap} | {string.Join(" | ", cells)} |");
        }

        sb.AppendLine();
        sb.AppendLine("Every cell above is one of three things, and the difference matters: **observed** (a run");
        sb.AppendLine("demonstrated it), *inspected* (read from help or the binary, never executed), or **not found");
        sb.AppendLine("on** an explicit list of surfaces. A bare \"absent\" is not expressible — that is the whole");
        sb.AppendLine("point, because every wrong row this suite was built after was a negative from one surface.");
        sb.AppendLine();

        foreach (var f in findings.Where(f => f.Evidence != Evidence.Observed))
        {
            sb.AppendLine($"- **{f.Vendor} · {f.Capability}** — {f.Detail}");
        }

        return sb.ToString();
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
