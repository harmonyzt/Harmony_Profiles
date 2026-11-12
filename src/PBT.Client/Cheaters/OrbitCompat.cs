using System.Reflection;
using EFT;
using HarmonyLib;

namespace PBT.Client.Cheaters;

// ORBIT (soft dependency) drives cheaters between cheater moments, which keeps them looting and roaming, but it
// must never put them to sleep. ORBIT's SetBotSleepAllowed does that (and keeps the squad awake); builds without
// it get their sleep gates patched instead.
internal static class OrbitCompat
{
    private const string Source = "PBT";

    private static Harmony? harmony;
    private static bool resolved,
        patched;
    private static Func<BotOwner, bool, string, bool>? setSleepAllowed;
    private static readonly HashSet<Player> awake = new();
    private static readonly List<Player> scratch = new();

    internal static void Install(Harmony h) => harmony = h;

    // Deferred to the first raid so ORBIT is loaded whatever the plugin order.
    internal static void RaidStarted()
    {
        awake.Clear();
        if (resolved)
            return;
        resolved = true;
        if (AccessTools.TypeByName("Orbit.Api.OrbitBotControl") is not { } api)
            return;
        var method = AccessTools.Method(api, "SetBotSleepAllowed", [typeof(BotOwner), typeof(bool), typeof(string)]);
        if (method is not null)
        {
            setSleepAllowed = (Func<BotOwner, bool, string, bool>)
                Delegate.CreateDelegate(typeof(Func<BotOwner, bool, string, bool>), method);
            CheaterRoster.Log("ORBIT sleep API found, cheaters stay awake through it.");
            return;
        }
        PatchGates();
    }

    // Called every tick with the live cheaters. ORBIT only accepts the request once its manager is up, so a refusal is retried next tick.
    internal static void KeepAwake(List<Player> cheaters)
    {
        if (setSleepAllowed is null)
            return;
        foreach (var player in cheaters)
        {
            if (awake.Contains(player) || player.AIData?.BotOwner is not { } bot)
                continue;
            try
            {
                if (setSleepAllowed(bot, false, Source))
                    awake.Add(player);
            }
            catch (Exception e)
            {
                Plugin.Report($"ORBIT SetBotSleepAllowed failed: {e.Message}");
                setSleepAllowed = null;
                return;
            }
        }

        // No longer a cheater (or behaviour switched off): ORBIT may sleep it again. Dead bots ORBIT forgets by itself.
        scratch.Clear();
        foreach (var player in awake)
            if (!cheaters.Contains(player))
                scratch.Add(player);
        foreach (var player in scratch)
        {
            awake.Remove(player);
            if (player != null && player.HealthController?.IsAlive == true && player.AIData?.BotOwner is { } bot)
                setSleepAllowed(bot, true, Source);
        }
    }

    private static void PatchGates()
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
        CheaterRoster.Log($"ORBIT without the sleep API, {ok}/8 sleep gates keep cheaters awake.");
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
