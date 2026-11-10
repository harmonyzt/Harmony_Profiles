using EFT;
using HarmonyLib;

namespace PBT.Client.Cheaters;

// ORBIT (soft dependency) puts far bots to sleep and drives them itself, which would stop cheaters hunting. Its API hands them back.
internal static class OrbitCompat
{
    private const string Source = "PBT";

    private static bool resolved;
    private static Func<BotOwner, string, bool>? exclude;
    private static Func<BotOwner, string, bool>? enable;
    private static readonly HashSet<BotOwner> excluded = new();

    private static bool Available()
    {
        if (!resolved)
        {
            resolved = true;
            var api = AccessTools.TypeByName("Orbit.Api.OrbitBotControl");
            var ex = api is null ? null : AccessTools.Method(api, "ExcludeBot", new[] { typeof(BotOwner), typeof(string) });
            var en = api is null ? null : AccessTools.Method(api, "EnableBot", new[] { typeof(BotOwner), typeof(string) });
            if (ex is not null && en is not null)
            {
                exclude = (Func<BotOwner, string, bool>)Delegate.CreateDelegate(typeof(Func<BotOwner, string, bool>), ex);
                enable = (Func<BotOwner, string, bool>)Delegate.CreateDelegate(typeof(Func<BotOwner, string, bool>), en);
                CheaterRoster.Log("ORBIT found, cheaters will be kept outside it.");
            }
        }
        return exclude is not null;
    }

    internal static void RaidStarted() => excluded.Clear();

    // Only the host has a BotOwner. Orbit forgets dead bots on its own.
    internal static void Exclude(Player player)
    {
        if (player.AIData?.BotOwner is not { } bot || excluded.Contains(bot) || !Available())
            return;
        try
        {
            if (exclude!(bot, Source))
                excluded.Add(bot);
        }
        catch (Exception e)
        {
            CheaterRoster.Log("ORBIT exclude failed: " + e.Message);
        }
    }

    // Cheater behaviour got switched off mid-raid, so ORBIT may run them again.
    internal static void ReleaseAll()
    {
        if (excluded.Count == 0)
            return;
        foreach (var bot in excluded)
        {
            try
            {
                if (bot != null && !bot.IsDead)
                    enable!(bot, Source);
            }
            catch (Exception e)
            {
                CheaterRoster.Log("ORBIT enable failed: " + e.Message);
            }
        }
        excluded.Clear();
    }
}
