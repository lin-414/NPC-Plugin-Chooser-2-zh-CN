using System.Threading;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Dev-only A/B escape hatch for the render harness (see
/// <see cref="RenderHarnessRunner"/>'s click-simulation mode): when
/// <see cref="ForceLegacy"/> is set, mugshot renders run the pre-2.8.0
/// resolution algorithm — no engine-order mode (the request-level
/// <c>AllowLoadOrderFallback</c> stays off), wig/worn-headgear overrides are
/// not fallback-stamped, and the broadcast tier's lazy load-order index widen
/// is suppressed — so the harness can measure the old and new algorithms in
/// otherwise-identical processes.
///
/// <para>Never set outside the harness. Normal renders must stay on
/// engine-order resolution: legacy mode reintroduces the out-of-scope-BSA
/// misses (hair/skin textures in another mod's archive) the 2.8.0 work
/// exists to fix.</para>
/// </summary>
public static class RenderResolutionMode
{
    /// <summary>True = render with the pre-2.8.0 strict resolution. Volatile:
    /// flipped by the harness between phases while render workers run.</summary>
    public static volatile bool ForceLegacy;
}

/// <summary>
/// Lightweight counters around the broadcast archive tier
/// (<see cref="Adapters.NpcChooserBsaProviderAdapter.TryLocateInBsa"/>), the
/// prime suspect for render-time regressions: every engine-order miss lands
/// there, and its per-call cost scales with the index size. Collected always
/// (a few Interlocked ops per lookup — noise next to the disk/GL work) and
/// reported by the render harness per phase.
/// </summary>
public static class BroadcastLookupStats
{
    private static long _calls;
    private static long _hits;
    private static long _ticks;
    private static long _widenMs;

    public static void RecordLookup(long elapsedTicks, bool hit)
    {
        Interlocked.Increment(ref _calls);
        if (hit) Interlocked.Increment(ref _hits);
        Interlocked.Add(ref _ticks, elapsedTicks);
    }

    public static void RecordWiden(long elapsedMs) => Interlocked.Add(ref _widenMs, elapsedMs);

    public static void Reset()
    {
        Interlocked.Exchange(ref _calls, 0);
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _ticks, 0);
        Interlocked.Exchange(ref _widenMs, 0);
    }

    public static string Report()
    {
        long calls = Interlocked.Read(ref _calls);
        long hits = Interlocked.Read(ref _hits);
        long ticks = Interlocked.Read(ref _ticks);
        long widenMs = Interlocked.Read(ref _widenMs);
        double ms = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return $"broadcast lookups={calls} (hits={hits}, misses={calls - hits}) totalMs={ms:F1} widenMs={widenMs}";
    }
}
