using System.Diagnostics;
using SPTarkov.Common.Models.Logging;

namespace HarmonyProfiles.Server;

// Prints done/total in 10% steps so a long first-time build doesn't look like a hung server.
internal sealed class LoadProgress(ISptLogger<PopulationStore> logger, string label, int total)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int done;
    private int lastStep;
    private bool reported;

    public void Step()
    {
        done++;
        int step = done * 10 / Math.Max(1, total);
        // Phases that finish within a couple of seconds stay silent.
        if (step == lastStep || clock.ElapsedMilliseconds < 2000) return;
        lastStep = step;
        reported = true;
        logger.Info($"[Harmony-Profiles] {label}: {done}/{total} ({step * 10}%)");
    }

    public void Finish()
    {
        if (reported) logger.Info($"[Harmony-Profiles] {label}: done in {clock.Elapsed.TotalSeconds:0.0}s");
    }
}
