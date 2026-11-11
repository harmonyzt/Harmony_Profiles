using System.Reflection;
using EFT;
using HarmonyLib;

namespace PBT.Client.Cheaters;

// ORBIT (soft dependency) drives cheaters between cheater moments, which keeps them looting and roaming, but it
// must never put them to sleep. Its API only offers full exclusion, so its sleep gates are patched instead.
// Written against ORBIT 3.1.1; if a gate is renamed it is logged and that path is left alone.
internal static class OrbitCompat
{
    private static Harmony? harmony;
    private static bool patched;

    internal static void Install(Harmony h) => harmony = h;

    // Deferred to the first raid so ORBIT is loaded whatever the plugin order.
    internal static void RaidStarted()
    {
        if (patched || harmony is null)
            return;
        patched = true;
        var dormancy = AccessTools.TypeByName("Orbit.Systems.DormancySystem");
        var ghosts = AccessTools.TypeByName("Orbit.Systems.NativeGhostSystem");
        if (dormancy is null || ghosts is null)
            return;

        int ok = 0;
        ok += Gate(dormancy, "CanSleep", nameof(SquadCannot));
        ok += Gate(dormancy, "SleepSquad", nameof(SquadSkip));
        ok += Gate(dormancy, "SleepAgent", nameof(AgentSkip));
        ok += Gate(dormancy, "CanVanillaSleep", nameof(GroupCannot));
        ok += Gate(dormancy, "SleepVanillaGroup", nameof(GroupSkip));
        ok += Gate(dormancy, "IsNativeGhostEligible", nameof(BotCannot));
        ok += Gate(ghosts, "CanSleep", nameof(BotCannot));
        ok += Gate(ghosts, "Add", nameof(BotSkip), typeof(BotOwner));
        CheaterRoster.Log($"ORBIT found, {ok}/8 sleep gates keep cheaters awake.");
    }

    private static int Gate(Type type, string name, string prefix, params Type[] args)
    {
        var method = args.Length > 0 ? AccessTools.Method(type, name, args) : AccessTools.Method(type, name);
        if (method is null)
        {
            Plugin.Report($"ORBIT {type.Name}.{name} not found, cheaters may sleep on that path.");
            return 0;
        }
        try
        {
            harmony!.Patch(method, prefix: new HarmonyMethod(typeof(OrbitCompat), prefix));
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Report($"ORBIT {type.Name}.{name} patch failed: {e.Message}");
            return 0;
        }
    }

    private static FieldInfo? agentBot, squadMembers;

    private static bool Cheater(BotOwner? bot) =>
        CheaterRoster.Behaviour && bot?.GetPlayer is { } player && CheaterRoster.IsCheater(player);

    private static bool AgentCheats(object? agent)
    {
        if (agent is null)
            return false;
        agentBot ??= AccessTools.Field(agent.GetType(), "Bot");
        return Cheater(agentBot?.GetValue(agent) as BotOwner);
    }

    private static bool SquadCheats(object? squad)
    {
        if (squad is null)
            return false;
        squadMembers ??= AccessTools.Field(squad.GetType(), "Members");
        if (squadMembers?.GetValue(squad) is System.Collections.IEnumerable members)
            foreach (var agent in members)
                if (AgentCheats(agent))
                    return true;
        return false;
    }

    private static bool GroupCheats(List<BotOwner>? group) => group is not null && group.Exists(Cheater);

    // A cheater keeps its whole squad awake, so the squad never splits into sleeping and awake halves.
    private static bool SquadCannot(object squad, ref bool __result) => Deny(SquadCheats(squad), ref __result);

    private static bool SquadSkip(object squad) => !SquadCheats(squad);

    private static bool AgentSkip(object agent) => !AgentCheats(agent);

    private static bool GroupCannot(List<BotOwner> group, ref bool __result) => Deny(GroupCheats(group), ref __result);

    private static bool GroupSkip(List<BotOwner> group) => !GroupCheats(group);

    private static bool BotCannot(BotOwner bot, ref bool __result) => Deny(Cheater(bot), ref __result);

    private static bool BotSkip(BotOwner bot) => !Cheater(bot);

    private static bool Deny(bool cheater, ref bool result)
    {
        if (!cheater)
            return true;
        result = false;
        return false;
    }
}
