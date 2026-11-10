namespace PBT.Server;

// The one place that turns the cheater settings into a type and a set of cheats. The server rolls every cheater
// career here and sends the result to the client, so what the editor shows is what the bot does in raid.
public static class CheaterRules
{
    public const string Shared = "Shared";
    public const string PerType = "PerType";

    public const string Closet = "Closet";
    public const string Looter = "Looter";
    public const string Hunter = "Hunter";
    public const string Rage = "Rage";
    public const string Troll = "Troll";

    public static readonly string[] Types = [Closet, Looter, Hunter, Rage, Troll];

    // Same names as the client's Cheat flags.
    public static readonly string[] Cheats =
    [
        "Aimbot",
        "Esp",
        "Godmode",
        "Speedhack",
        "Spinbot",
        "BunnyHop",
        "FightTeleport",
        "Fly",
        "Vacuum",
        "Stalker",
        "VoiceSpam",
    ];

    public static readonly Dictionary<string, string> CheatLabels = new()
    {
        ["Aimbot"] = "Aimbot",
        ["Esp"] = "ESP",
        ["Godmode"] = "Godmode",
        ["Speedhack"] = "Speedhack",
        ["Spinbot"] = "Spinbot",
        ["BunnyHop"] = "Bunny hop",
        ["FightTeleport"] = "Fight teleports",
        ["Fly"] = "Flying",
        ["Vacuum"] = "Loot vacuum",
        ["Stalker"] = "Stalker",
        ["VoiceSpam"] = "Voice spam",
    };

    // Shared mode: cheats a type always has, and the ones it may roll at the shared chance. The rest it never gets.
    // Looters always vacuum: without it they had nothing to do but run from people.
    private static readonly Dictionary<string, (string[] Always, string[] May)> Kits = new()
    {
        [Closet] = ([], ["Aimbot", "Esp"]),
        [Looter] = (["Vacuum"], ["Esp", "Aimbot", "Speedhack"]),
        [Hunter] = (["Esp", "Aimbot"], ["Speedhack", "BunnyHop", "FightTeleport", "Godmode", "VoiceSpam"]),
        [Rage] = (["Aimbot"], ["Esp", "Godmode", "Speedhack", "Spinbot", "BunnyHop", "FightTeleport", "Fly", "VoiceSpam"]),
        [Troll] = (["Stalker", "VoiceSpam"], ["Godmode", "FightTeleport", "Speedhack", "Spinbot", "Aimbot"]),
    };

    public enum Access
    {
        Never,
        May,
        Always,
    }

    public static Access Allowed(string type, string cheat) =>
        Kits[type].Always.Contains(cheat) ? Access.Always
        : Kits[type].May.Contains(cheat) ? Access.May
        : Access.Never;

    public static double SharedChance(CheaterPlaySettings? play, string cheat) =>
        cheat switch
        {
            "Aimbot" => play?.Aimbot ?? .6,
            "Esp" => play?.Esp ?? .5,
            "Godmode" => play?.Godmode ?? .1,
            "Speedhack" => play?.Speedhack ?? .35,
            "Spinbot" => play?.Spinbot ?? .15,
            "BunnyHop" => play?.BunnyHop ?? .15,
            "FightTeleport" => play?.FightTeleport ?? .2,
            "Fly" => play?.Fly ?? .05,
            "Vacuum" => play?.Vacuum ?? .6,
            "Stalker" => play?.Stalker ?? .1,
            "VoiceSpam" => play?.VoiceSpam ?? .4,
            _ => 0,
        };

    private static double SharedWeight(CheaterPlaySettings? play, string type) =>
        type switch
        {
            Looter => play?.Types.Looter ?? .25,
            Hunter => play?.Types.Hunter ?? .2,
            Rage => play?.Types.Rage ?? .1,
            Troll => play?.Types.Troll ?? .05,
            _ => play?.Types.Closet ?? .4,
        };

    public static double Chance(CheaterPersonality p, string cheat) =>
        cheat switch
        {
            "Aimbot" => p.Aimbot,
            "Esp" => p.Esp,
            "Godmode" => p.Godmode,
            "Speedhack" => p.Speedhack,
            "Spinbot" => p.Spinbot,
            "BunnyHop" => p.BunnyHop,
            "FightTeleport" => p.FightTeleport,
            "Fly" => p.Fly,
            "Vacuum" => p.Vacuum,
            "Stalker" => p.Stalker,
            "VoiceSpam" => p.VoiceSpam,
            _ => 0,
        };

    public static void SetChance(CheaterPersonality p, string cheat, double value)
    {
        switch (cheat)
        {
            case "Aimbot": p.Aimbot = value; break;
            case "Esp": p.Esp = value; break;
            case "Godmode": p.Godmode = value; break;
            case "Speedhack": p.Speedhack = value; break;
            case "Spinbot": p.Spinbot = value; break;
            case "BunnyHop": p.BunnyHop = value; break;
            case "FightTeleport": p.FightTeleport = value; break;
            case "Fly": p.Fly = value; break;
            case "Vacuum": p.Vacuum = value; break;
            case "Stalker": p.Stalker = value; break;
            case "VoiceSpam": p.VoiceSpam = value; break;
        }
    }

    // A personality that plays exactly like the shared settings do for this type. Null play means the shipped defaults.
    public static CheaterPersonality Seed(string type, CheaterPlaySettings? play)
    {
        var p = new CheaterPersonality
        {
            Weight = SharedWeight(play, type),
            AimAccuracy = play?.AimAccuracy ?? .7,
            ReactionTime = play?.ReactionTime ?? 1,
            SpeedMultiplier = play?.SpeedMultiplier ?? 2.2,
        };
        foreach (var cheat in Cheats)
            SetChance(
                p,
                cheat,
                Allowed(type, cheat) switch
                {
                    Access.Always => 1,
                    Access.May => SharedChance(play, cheat),
                    _ => 0,
                }
            );
        return p;
    }

    // What a type really plays with under the current mode.
    public static CheaterPersonality Effective(CheaterPlaySettings play, string type) =>
        play.Mode == PerType ? play.Personalities.Get(type) : Seed(type, play);

    public static Dictionary<string, CheaterPersonality> EffectiveAll(CheaterPlaySettings play) =>
        Types.ToDictionary(t => t, t => Effective(play, t));

    // Fixed per career and settings, so a cheater keeps its type and cheats raid after raid.
    public static (string Type, List<string> Cheats) Roll(string careerId, CheaterPlaySettings play, Func<string, double> stable)
    {
        var table = EffectiveAll(play);
        double total = Types.Sum(t => Math.Max(0, table[t].Weight));
        string type = Closet;
        if (total > 0)
        {
            double roll = stable(careerId + ":cheater-type") * total;
            foreach (var t in Types)
                if ((roll -= Math.Max(0, table[t].Weight)) < 0)
                {
                    type = t;
                    break;
                }
        }

        var p = table[type];
        var cheats = Cheats.Where(c => stable(careerId + ":cheat:" + c) < Chance(p, c)).ToList();
        // Shared mode: a closet cheater that rolled nothing still runs one of the two.
        if (play.Mode == Shared && type == Closet && cheats.Count == 0)
            cheats.Add(stable(careerId + ":closet-fallback") < .5 ? "Aimbot" : "Esp");
        return (type, cheats);
    }
}
