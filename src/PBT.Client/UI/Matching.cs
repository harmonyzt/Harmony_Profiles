using System;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace PBT.Client;

// Live-style matching before a raid: Matching, Looking for server, Preparing the game, Awaiting response.
// Runs after map loading and caching, before the local game is created.
internal static class Matching
{
    private static MethodInfo? create;
    private static AccessTools.FieldRef<TarkovApplication, RaidSettings>? raidSettings;
    private static bool passing;

    internal static void Install(Harmony harmony)
    {
        create = AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.LocalGameCreate));
        raidSettings = AccessTools.FieldRefAccess<TarkovApplication, RaidSettings>("_raidSettings");
        if (create?.ReturnType != typeof(Task) || AccessTools.Property(typeof(TarkovApplication), "Matchmaker") is null)
            throw new MissingMethodException("Local game creation hook unavailable.");
        harmony.Patch(create, prefix: new HarmonyMethod(typeof(Matching), nameof(Create)));
    }

    private static bool Create(TarkovApplication __instance, object[] __args, ref Task __result)
    {
        if (passing) { passing = false; return true; }
        RaidSettings settings;
        try { settings = raidSettings!(__instance); }
        catch { return true; }
        if (settings is null || settings.isInTransition) return true;
        __result = Queue(__instance, settings.LocationId ?? "", __args);
        return false;
    }

    private static async Task Queue(TarkovApplication app, string map, object[] args)
    {
        try
        {
            var response = JObject.Parse(await RequestHandler.PostJsonAsync("/pbt/world/matching",
                new JObject { ["Map"] = map }.ToString(Newtonsoft.Json.Formatting.None)));
            int seconds = response.Value<int>("ProtocolVersion") == 1 ? response.Value<int>("Seconds") : 0;
            if (seconds > 0)
            {
                var random = new Random();
                app.Matchmaker.UpdateMatchingStatus("Matching...");
                await Task.Delay(seconds * 1000);
                app.Matchmaker.UpdateMatchingStatus("Looking for server...");
                await Task.Delay(random.Next(5000, 15001));
                app.Matchmaker.UpdateMatchingStatus("Preparing the game...");
                await Task.Delay(random.Next(200, 1001));
                app.Matchmaker.UpdateMatchingStatus("Awaiting server response...");
                await Task.Delay(100);
            }
        }
        catch (Exception ex) { Plugin.Report("Matching simulation skipped: " + ex.Message); }
        passing = true;
        await (Task)create!.Invoke(app, args);
    }
}
