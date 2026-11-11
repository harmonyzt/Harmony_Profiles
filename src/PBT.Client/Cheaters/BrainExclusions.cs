using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;

namespace PBT.Client.Cheaters;

// Other AI mods fight the cheater brain for the same bot (SAIN steers and aims every frame, layer mods grab it whenever
// the cheater layer hands back), which makes cheaters twitch and freeze. Cheaters run only our layer or vanilla AI.
internal static class BrainExclusions
{
    private static Harmony? harmony;
    private static bool patched;

    internal static void Install(Harmony h) => harmony = h;

    // Deferred to the first raid so every plugin's layer types are loaded.
    internal static void RaidStarted()
    {
        if (patched || harmony is null)
            return;
        patched = true;

        var sain = AccessTools.Method("SAIN.SAINEnableClass:IsBotExcluded", new[] { typeof(BotOwner) });
        if (sain is not null)
        {
            harmony.Patch(sain, prefix: new HarmonyMethod(typeof(BrainExclusions), nameof(SainExcluded)));
            CheaterRoster.Log("SAIN found, cheaters are left out of it.");
        }

        int layers = 0;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t is not null).ToArray()!;
            }
            catch
            {
                continue;
            }
            foreach (var type in types)
            {
                if (type.IsAbstract || type == typeof(CheaterLayer) || !typeof(CustomLayer).IsAssignableFrom(type))
                    continue;
                var active = AccessTools.DeclaredMethod(type, nameof(CustomLayer.IsActive));
                if (active is null)
                    continue;
                try
                {
                    harmony.Patch(active, prefix: new HarmonyMethod(typeof(BrainExclusions), nameof(LayerActive)));
                    layers++;
                }
                catch (Exception e)
                {
                    CheaterRoster.Log($"Could not keep {type.FullName} off cheaters: {e.Message}");
                }
            }
        }
        CheaterRoster.Log($"{layers} other brain layers kept off cheaters.");
    }

    private static bool IsCheaterBot(BotOwner? bot) =>
        CheaterRoster.Behaviour && bot?.GetPlayer is { } player && CheaterRoster.IsCheater(player);

    private static bool SainExcluded(BotOwner botOwner, ref bool __result)
    {
        if (!IsCheaterBot(botOwner))
            return true;
        __result = true;
        return false;
    }

    private static bool LayerActive(CustomLayer __instance, ref bool __result)
    {
        if (!IsCheaterBot(__instance.BotOwner))
            return true;
        __result = false;
        return false;
    }
}
