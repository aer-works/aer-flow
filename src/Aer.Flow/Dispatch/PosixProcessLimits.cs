using System.Runtime.InteropServices;

namespace Aer.Flow.Dispatch;

/// <summary>
/// The kernel limits a POSIX <c>exec</c> enforces on the command line it is handed, isolated here so
/// the one <c>libc</c> P/Invoke in <c>Aer.Flow</c> has a single home (#612). Two independent caps,
/// unlike Windows' single <c>lpCommandLine</c> ceiling:
/// <list type="bullet">
/// <item><b>MAX_ARG_STRLEN</b> — a per-argument byte cap. A hard Linux kernel constant of 32 pages
/// (<c>include/uapi/linux/binfmts.h</c>), which the single-inline-prompt shape of both adapters is
/// exactly what runs into first. macOS has no per-argument cap, so this is Linux-only.</item>
/// <item><b>ARG_MAX</b> — a total byte cap across argv <em>and</em> envp, queried at runtime because
/// on Linux it tracks <c>RLIMIT_STACK</c> rather than being constant.</item>
/// </list>
/// </summary>
/// <remarks>
/// Nothing here is P/Invoked on Windows: <see cref="ArgMaxBytes"/> returns before <c>sysconf</c> when
/// the OS is neither Linux nor macOS, so <c>libc</c> is never resolved there and the declaration below
/// stays inert. <see cref="LinuxMaxArgStrlen"/> needs no native call at all.
/// </remarks>
internal static class PosixProcessLimits
{
    // sysconf's _SC_ARG_MAX name is NOT portable: glibc/Linux defines it as 0, the BSD/macOS libc
    // headers as 1. Sourced from <bits/confname.h> (glibc) and <sys/unistd.h> (Apple libc). Only the
    // Linux value runs in PR CI; where the macOS value is checked, and why a wrong one fails safe, are
    // covered at MinPlausibleArgMax below and its named test.
    private const int LinuxScArgMax = 0;
    private const int MacScArgMax = 1;

    // The smallest ARG_MAX we treat as real. NOT POSIX's _POSIX_ARG_MAX floor (4096): that is 32-256x
    // below any actual Linux/macOS ARG_MAX (glibc's legacy floor is 131072; macOS kern.argmax is 262144
    // or 1048576), so a mis-resolved _SC_* symbol that happened to return some other mid-size resource
    // limit would clear 4096 and arm the total guard with a bogus small ceiling — the exact false
    // refusal of a healthy dispatch this fallback exists to prevent. 131072 is the glibc floor every
    // real target clears by 2-8x: a sub-floor answer therefore means the symbol resolved wrong, so fall
    // back to aer-core's own E2BIG (status quo) rather than guard against a fiction. A wrong macOS
    // _SC_ARG_MAX is caught loudly by ArgMaxBytes_returns_a_plausible_limit, which asserts this floor.
    private const long MinPlausibleArgMax = 131072;

    // DllImport, not LibraryImport: the source-generated marshaller requires <AllowUnsafeBlocks> and
    // Aer.Flow deliberately does not enable unsafe code project-wide for one blittable libc call. The
    // aer-core binding project enables it for its own reasons; the engine layer keeps that posture off.
    [DllImport("libc", EntryPoint = "sysconf", CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
    private static extern long sysconf(int name);

    /// <summary>
    /// Linux's MAX_ARG_STRLEN — the per-argument byte ceiling, 32 pages, computed from
    /// <see cref="Environment.SystemPageSize"/> so it needs no native call. Only meaningful on Linux;
    /// callers gate on <see cref="OperatingSystem.IsLinux"/>.
    /// </summary>
    internal static int LinuxMaxArgStrlen => 32 * Environment.SystemPageSize;

    /// <summary>
    /// The kernel's ARG_MAX for the running POSIX OS — the byte ceiling on argv+envp for one
    /// <c>exec</c> — or <see langword="null"/> when it cannot be determined: a non-POSIX OS, or a
    /// <c>sysconf</c> answer below <see cref="MinPlausibleArgMax"/> (which covers both a -1 "no definite
    /// limit" and a mis-resolved symbol). A <see langword="null"/> skips the total guard by design — see
    /// <see cref="MinPlausibleArgMax"/> for why that is the safe direction.
    /// </summary>
    internal static long? ArgMaxBytes()
    {
        int name;
        if (OperatingSystem.IsLinux())
        {
            name = LinuxScArgMax;
        }
        else if (OperatingSystem.IsMacOS())
        {
            name = MacScArgMax;
        }
        else
        {
            return null;
        }

        var value = sysconf(name);
        return value >= MinPlausibleArgMax ? value : null;
    }
}
