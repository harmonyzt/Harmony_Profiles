using System.Diagnostics;
using System.Runtime;

namespace PBT.Server;

// .NET never compacts the large object heap on its own, so the big responses and bot lists of every raid start
// leave gigabytes of free holes the server keeps committed. Compact it once things go quiet.
public sealed partial class PopulationStore
{
    private const long CompactThresholdBytes = 512L * 1024 * 1024;
    private const int CompactQuietSeconds = 90;
    private const int CompactCooldownSeconds = 900;
    private long lastBotGeneratedUtc;
    private long lastCompactUtc;
    private long largeObjectsAfterCompact;

    // Dead large objects count as heap until a full GC, and as holes after it. Both are memory we can hand back.
    private static long LargeObjectBytes(GCMemoryInfo info) =>
        info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes + info.GenerationInfo[3].FragmentationAfterBytes : 0;

    // Runs on the checkpoint thread without the gate.
    private void CompactIfQuiet()
    {
        try
        {
            if (!settings.CompactMemory) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - lastCompactUtc < CompactCooldownSeconds || now - Volatile.Read(ref lastBotGeneratedUtc) < CompactQuietSeconds) return;
            if (LargeObjectBytes(GC.GetGCMemoryInfo()) - largeObjectsAfterCompact < CompactThresholdBytes) return;
            lastCompactUtc = now;
            long committed = GC.GetGCMemoryInfo().TotalCommittedBytes, large = LargeObjectBytes(GC.GetGCMemoryInfo());
            var clock = Stopwatch.StartNew();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            clock.Stop();
            var after = GC.GetGCMemoryInfo();
            largeObjectsAfterCompact = LargeObjectBytes(after);
            logger.Info(
                $"[PBT] Compacted server memory in {clock.ElapsedMilliseconds} ms: committed {committed / 1048576} MB -> {after.TotalCommittedBytes / 1048576} MB, large objects {large / 1048576} MB -> {largeObjectsAfterCompact / 1048576} MB.");
        }
        catch (Exception ex)
        {
            logger.Warning("[PBT] Memory compaction skipped: " + ex.Message);
        }
    }
}
