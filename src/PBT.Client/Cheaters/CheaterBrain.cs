using System.Text;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace PBT.Client.Cheaters;

// Only touched once BigBrain is known to be loaded, so PBT still runs without it.
internal static class CheaterBrain
{
    // Above every SAIN layer (its highest is 99) and every patrol mod, so a cheater fights with its cheats. Patrol is left to whatever runs below.
    private const int Priority = 150;

    internal static void Register() => BrainManager.AddCustomLayer(typeof(CheaterLayer), ["PmcBear", "PmcUsec", "PMC"], Priority);
}

internal enum CheaterPhase
{
    Evade,
    Stalk,
    Fight,
    Loot,
}

// How a cheater fights. Push walks or teleports straight in, Flank swings round the side first, Hold takes an angle and pre-aims through the wall.
internal enum Tactic
{
    Push,
    Flank,
    Hold,
}

// What a cheater knows and wants, shared by its layer and its logic.
internal sealed class CheaterMind
{
    public int Looted,
        Misses;
    public float NextPull = Time.time + UnityEngine.Random.Range(2f, 6f);
    public bool Pulling,
        Full;

    // Where a looter walks when nothing worth vacuuming is in range, and the item there.
    public Vector3? LootSpot;
    public EFT.InventoryLogic.Item? LootItem;
    public int Stuck;

    // A vacuum bot's run: the loot cluster it is clearing, what it has vacuumed and wants to (RUB, its own gear never counts), and the exit it leaves by.
    public Vector3? Stop;
    public int Stops;
    public long Haul,
        HaulTarget;
    public float LeaveAt = Time.time + UnityEngine.Random.Range(900f, 1500f);
    public bool Leaving;
    public EFT.Interactive.ExfiltrationPoint? Exit;
    public Vector3 ExitSpot;
    public int ExitTries;
    public readonly HashSet<EFT.InventoryLogic.Item> Skip = new();
    public readonly HashSet<EFT.Interactive.ExfiltrationPoint> BadExits = new();

    // Has something to do on its run: loot to walk to, a cluster to clear, or an exit to reach.
    public bool OnRun => LootSpot is not null || Stop is not null || Leaving && Exit is not null;

    // A looter's reason to run: the PMC (or you) it is keeping away from, and when it last had it in sight or close.
    public Player? Threat;
    public float ThreatSeen;

    // Nobody hunts in the first couple of minutes, and a troll waits longer still before it starts visiting.
    public float HuntAfter = CheaterRoster.RaidStart + UnityEngine.Random.Range(90f, 150f);
    public float StalkAfter = CheaterRoster.RaidStart + UnityEngine.Random.Range(180f, 360f);
    public float NextVisit;
    public int Visits,
        VisitsPlanned = UnityEngine.Random.Range(3, 8);
    public bool Provoked;
    public bool Stalked,
        Wants;

    // For the stuck-bot log, and a pause after the brain threw so its own AI can move the bot meanwhile.
    public string Doing = "roaming";
    public float LastCheck,
        BrokenUntil;
    public int Failures;
    public float NextCheck,
        LastSeenTime = -100,
        FirstSeen,
        RestUntil,
        NextHunt,
        LastProgress,
        BestDistance;
    public int Kills,
        Budget = -1;
    public Player? Target;
    public Vector3 LastSeenPosition;

    // Targets it gave up on, until when.
    private readonly Dictionary<Player, float> ignored = new();

    private static readonly Dictionary<Player, CheaterMind> minds = new();

    public static CheaterMind Of(Player bot)
    {
        if (!minds.TryGetValue(bot, out var mind))
            minds[bot] = mind = new CheaterMind();
        return mind;
    }

    public static void Clear() => minds.Clear();

    // A kill moves on to the victim's nearest squadmate; only with nobody left does it rest (which just delays new hunts).
    public void Settle(Player bot)
    {
        if (Target is not { } t || t.HealthController?.IsAlive == true)
            return;
        Kills++;
        if (Squadmate(bot, t) is { } next)
        {
            CheaterRoster.Log(
                $"{bot.Profile.Nickname} got {t.Profile.Nickname} ({Kills} so far) and turns on {next.Profile.Nickname}."
            );
            Track(next, next.Position);
            return;
        }

        var aim = CheaterRoster.AimOf(bot);
        RestUntil = Time.time + UnityEngine.Random.Range(aim.RestMin, aim.RestMax);
        CheaterRoster.Log(
            $"{bot.Profile.Nickname} got {t.Profile.Nickname} ({Kills} so far), resting {RestUntil - Time.time:0}s."
        );
        EndHunt(bot);
    }

    // The dead target's nearest squadmate still alive and close. ESP finds them further off.
    private static Player? Squadmate(Player bot, Player dead)
    {
        if (string.IsNullOrEmpty(dead.GroupId) || !Singleton<GameWorld>.Instantiated)
            return null;
        float range = CheaterRoster.Has(bot, Cheat.Esp) ? 120 : 60;
        Player? best = null;
        float nearest = range * range;
        foreach (var p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
        {
            if (
                p == null
                || p == bot
                || p.GroupId != dead.GroupId
                || p.HealthController?.IsAlive != true
                || CheaterRoster.Spares(bot, p)
                || CheaterRoster.IsFriendly(bot, p)
            )
                continue;
            float d = (p.Position - bot.Position).sqrMagnitude;
            if (d < nearest)
            {
                nearest = d;
                best = p;
            }
        }

        return best;
    }

    public void Track(Player target, Vector3 position)
    {
        if (Target != target)
        {
            FirstSeen = 0;
            LastProgress = Time.time;
            BestDistance = float.MaxValue;
        }

        Target = target;
        LastSeenPosition = position;
        LastSeenTime = Time.time;
    }

    // Stops hunting and roams on its normal AI for the type's gap.
    public void EndHunt(Player bot)
    {
        var hunt = CheaterRoster.HuntOf(bot);
        NextHunt = Math.Max(NextHunt, Time.time + UnityEngine.Random.Range(hunt.GapMin, hunt.GapMax));
        Target = null;
    }

    public void GiveUp(Player bot)
    {
        if (Target is { } t)
        {
            ignored[t] = Time.time + 180;
            CheaterRoster.Log($"{bot.Profile.Nickname} gives up on {t.Profile.Nickname} and roams.");
        }

        EndHunt(bot);
    }

    public bool Ignores(Player p) => ignored.TryGetValue(p, out float until) && Time.time < until;

    // Players and cheaters always; scavs only for some, fixed per pair so it does not flip each scan.
    public bool Worth(Player bot, Player p)
    {
        if (!p.IsAI || p.Side != EPlayerSide.Savage)
            return true;
        return (uint)(bot.ProfileId + p.ProfileId).GetHashCode() % 1000 / 1000f < CheaterRoster.HuntOf(bot).Scavs;
    }

    public bool Hungry(Player bot)
    {
        if (Budget < 0)
        {
            var hunt = CheaterRoster.HuntOf(bot);
            Budget = UnityEngine.Random.Range(hunt.KillsMin, hunt.KillsMax + 1);
        }

        return Kills < Budget && Time.time >= NextHunt && Time.time >= HuntAfter;
    }
}

// Takes the bot over while it has a cheater's goal: loot to vacuum, you to stalk, a fight, or a target its ESP found. Otherwise patrol mods move it as usual.
internal sealed class CheaterLayer(BotOwner botOwner, int priority) : CustomLayer(botOwner, priority)
{
    public override string GetName() => "PbtCheater";

    public override bool IsActive()
    {
        if (
            !CheaterRoster.Behaviour
            || BotOwner?.GetPlayer is not { } bot
            || bot.HealthController?.IsAlive != true
            || !CheaterRoster.IsCheater(bot)
        )
            return false;
        var mind = CheaterMind.Of(bot);
        if (Time.time < mind.BrokenUntil)
            return false;
        // A hit, a near miss or an enemy in sight is looked at within a quarter second, not on the next one-second check.
        bool alarm =
            !mind.Wants
            && Time.time - mind.LastCheck >= .25f
            && (CheaterRoster.Attacker(bot) is not null || BotOwner.Memory?.GoalEnemy?.IsVisible == true);
        if (Time.time >= mind.NextCheck || alarm)
        {
            mind.NextCheck = Time.time + 1;
            mind.LastCheck = Time.time;
            bool wanted;
            try
            {
                wanted = CheaterLogic.Wants(BotOwner, bot, mind);
            }
            catch (Exception ex)
            {
                CheaterLogic.Broke(bot, mind, "deciding", ex);
                return false;
            }
            if (wanted != mind.Wants)
                CheaterRoster.Log($"{bot.Profile.Nickname} {(wanted ? "takes over from its AI" : "hands back to its AI")}.");
            mind.Wants = wanted;
            CheaterRoster.SetDriven(bot, wanted);
        }

        return mind.Wants;
    }

    public override Action GetNextAction() => new(typeof(CheaterLogic), "cheating");

    public override bool IsCurrentActionEnding() => false;
}

internal sealed class CheaterLogic(BotOwner botOwner) : CustomLogic(botOwner)
{
    // How long after seeing a target a cheater forgets it, unless ESP keeps telling it where the target is.
    private const float Memory = 30;

    private CheaterPhase phase;
    private Tactic tactic;
    private float phaseSince,
        stalkFor,
        next,
        nextTeleport,
        nextJump,
        nextPath,
        nextSight,
        triggerUntil,
        triggerNext,
        spin,
        tacticUntil;
    private bool firing,
        holding,
        sees,
        settled;
    private float react,
        patience,
        visitUntil,
        nextStrafe,
        beatUntil,
        tellUntil,
        settleUntil;
    private bool visiting,
        crouched,
        peeking,
        duel;
    private int strafeSide = 1,
        peeks,
        peekLimit = 2;
    private Vector3? teleportTo;
    private bool teleportPeek;
    private EFT.InventoryLogic.Item? arrivedFor;
    private float arrivedAt;
    private int arrivedLooted;
    private int blinksLeft;
    private readonly List<Vector3> used = new();
    private Player? seen,
        engaged;
    private Vector3 destination,
        stuckFrom,
        travelGoal;
    private float travelBest,
        travelSince,
        extractAt,
        nextReload,
        tacticSince;
    private float nextStuckCheck;
    private AntiYaw? antiYaw;
    private AntiPitch antiPitch;
    private float spinRate,
        nextYaw,
        nextPitch,
        nextSuperJump,
        nextGlide;
    private bool jitterLeft,
        pitchUp;
    private Vector3? angle,
        flank;

    private Player Bot => BotOwner.GetPlayer;
    private CheaterMind Mind => CheaterMind.Of(Bot);
    private static GameWorld World => Singleton<GameWorld>.Instance;
    private static Player? Me => Singleton<GameWorld>.Instantiated ? World.MainPlayer : null;

    private bool Has(Cheat cheat) => CheaterRoster.Has(Bot, cheat);

    private CheaterType Type => CheaterRoster.TypeOf(Bot);

    // The type's goal, checked about once a second by the layer.
    internal static bool Wants(BotOwner owner, Player bot, CheaterMind mind)
    {
        mind.Settle(bot);
        // A looter keeps away from PMCs and you, and only fights them when it is cornered or shot at. With godmode it has nothing to run from.
        if (CheaterRoster.TypeOf(bot) == CheaterType.Looter && !CheaterRoster.Has(bot, Cheat.Godmode))
        {
            // Shot at: turns and fights the shooter instead of running with its back to it.
            if (CheaterRoster.Attacker(bot) is { } attacker && Valid(bot, attacker) && !CheaterRoster.Spares(bot, attacker))
            {
                if (mind.Target != attacker)
                    CheaterRoster.Log($"{bot.Profile.Nickname} is shot by {attacker.Profile.Nickname} and fights back.");
                mind.Threat = null;
                mind.Track(attacker, attacker.Position);
                return true;
            }

            // A fight it already took on is finished, not dropped the moment the shooting pauses.
            if (mind.Target is not null && Knows(bot, mind))
            {
                mind.Threat = null;
                return true;
            }

            var threat = Threat(owner, bot, mind);
            if (threat != mind.Threat && threat is not null)
                CheaterRoster.Log($"{bot.Profile.Nickname} keeps away from {threat.Profile.Nickname}.");
            mind.Threat = threat;
            if (threat is not null)
            {
                float d = Vector3.Distance(threat.Position, bot.Position);
                bool shot = owner.Memory?.GoalEnemy is { } hit && Time.time - hit.LastGetHitTime < 6;
                if (!shot && d > 15 || CheaterRoster.Spares(bot, threat))
                    return true;
                mind.Threat = null;
                mind.Track(threat, threat.Position);
                return true;
            }
        }

        // Visits wait while someone shoots at it or it has a live fight, or a troll stood still between visits through every fight.
        if (Stalking(bot, mind) && CheaterRoster.Attacker(bot) is null && !(mind.Target is not null && Knows(bot, mind)))
            return true;
        var fighting = InFight(owner, bot, mind);
        // Whoever is right in front of it comes first, ahead of a fight or hunt further off.
        if (
            CheaterRoster.TypeOf(bot) != CheaterType.Looter
            && Spotted(owner, bot, mind) is { } close
            && Closer(bot, close, fighting)
            && Closer(bot, close, mind.Target is { } current && Valid(bot, current) && Clear(bot, current) ? current : null)
        )
        {
            if (mind.Target != close)
                CheaterRoster.Log($"{bot.Profile.Nickname} spots {close.Profile.Nickname}.");
            mind.Track(close, close.Position);
            return true;
        }

        // A fight its own AI walked into is fought with the cheats, not by SAIN.
        if (fighting is not null)
        {
            if (mind.Target != fighting)
                CheaterRoster.Log($"{bot.Profile.Nickname} fights {fighting.Profile.Nickname} with its cheats.");
            mind.Track(fighting, owner.Memory.GoalEnemy.EnemyLastPosition);
            return true;
        }

        // Shot by someone its own AI has not picked as an enemy yet (a squadmate of the last kill, a third party).
        if (CheaterRoster.Attacker(bot) is { } shooter && Valid(bot, shooter) && !CheaterRoster.Spares(bot, shooter))
        {
            if (mind.Target != shooter)
                CheaterRoster.Log($"{bot.Profile.Nickname} is shot by {shooter.Profile.Nickname} and fights back.");
            mind.Track(shooter, shooter.Position);
            return true;
        }

        // A squadmate's fight is its fight too, or ORBIT walks it on past the shooting.
        if (CheaterRoster.TypeOf(bot) != CheaterType.Looter && AllyFight(owner, bot, mind, out var allyEnemyAt) is { } ally)
        {
            if (mind.Target != ally)
                CheaterRoster.Log($"{bot.Profile.Nickname} joins its squad's fight with {ally.Profile.Nickname}.");
            mind.Track(ally, allyEnemyAt);
            return true;
        }

        // A vacuum bot's run (loot, next cluster, exit) is driven here start to finish, so its own AI never wanders off or leaves early.
        bool run = CheaterRoster.Has(bot, Cheat.Vacuum) && mind.OnRun;
        if (Time.time < mind.RestUntil)
            return run;
        if (Knows(bot, mind))
            return true;
        var type = CheaterRoster.TypeOf(bot);
        // Looters do not go looking for fights, a closet cheater without ESP has nothing to go on, and between hunts it roams like anyone.
        if (type == CheaterType.Looter || type == CheaterType.Closet && !CheaterRoster.Has(bot, Cheat.Esp) || !mind.Hungry(bot))
            return run;
        mind.Target = FindTarget(owner, bot, mind);
        // Nobody worth it in range: roam a little before looking again.
        if (mind.Target is null)
            mind.NextHunt = Time.time + UnityEngine.Random.Range(10f, 20f);
        return mind.Target is not null || run;
    }

    private static bool Stalking(Player bot, CheaterMind mind) =>
        CheaterRoster.Has(bot, Cheat.Stalker)
        && !CheaterRoster.Has(bot, Cheat.Vacuum)
        && !mind.Stalked
        && Time.time >= mind.StalkAfter
        && Me is { } me
        && me.HealthController?.IsAlive == true;

    // PMCs or you in sight within 80 m (ESP: any) or within 20 m; dropped after 4 s unseen and past 35 m.
    private static Player? Threat(BotOwner owner, Player bot, CheaterMind mind)
    {
        const float Sight = 80,
            Close = 20,
            Gone = 35;
        bool esp = CheaterRoster.Has(bot, Cheat.Esp);
        if (mind.Threat is { } old && Valid(bot, old))
        {
            float d = Vector3.Distance(old.Position, bot.Position);
            if (d < Gone || d < Sight && (esp || Clear(bot, old)))
                mind.ThreatSeen = Time.time;
            if (Time.time - mind.ThreatSeen < 4)
                return old;
        }

        Player? best = null;
        float nearest = Sight * Sight;
        foreach (var p in World.AllAlivePlayersList)
        {
            if (!Valid(bot, p) || p.IsAI && p.Side == EPlayerSide.Savage)
                continue;
            float d = (p.Position - bot.Position).sqrMagnitude;
            if (d >= nearest)
                continue;
            if (d > Close * Close && !esp && (object?)owner.Memory?.GoalEnemy?.Person != p && !Clear(bot, p))
                continue;
            nearest = d;
            best = p;
        }

        if (best is not null)
            mind.ThreatSeen = Time.time;
        return best;
    }

    // Only a live fight: seen lately or shot by it lately. SAIN keeps old enemies in memory long after the fight ends.
    // A target it gave up on only counts while it is actually shooting at the cheater.
    private static Player? InFight(BotOwner owner, Player bot, CheaterMind mind) =>
        owner.Memory?.GoalEnemy is { } enemy
        && enemy.Person is Player fighting
        && Valid(bot, fighting)
        && !CheaterRoster.Spares(bot, fighting)
        // TimeLastSeen is the group's: also heard, or reported by a squadmate.
        && (
            enemy.IsVisible
            || Time.time - enemy.PersonalLastSeenTime < 10
            || Time.time - enemy.TimeLastSeen < 10
            || Time.time - enemy.LastGetHitTime < 10
        )
        && (!mind.Ignores(fighting) || Time.time - enemy.LastGetHitTime < 5)
            ? fighting
            : null;

    // Nearest enemy the squad saw, heard or was shot by in the last 8 s, within 150 m.
    private static Player? AllyFight(BotOwner owner, Player bot, CheaterMind mind, out Vector3 at)
    {
        at = default;
        if (owner.BotsGroup?.Enemies is not { } enemies)
            return null;
        Player? best = null;
        float nearest = 150 * 150;
        foreach (var pair in enemies)
        {
            if (
                pair.Key is not Player p
                || Time.time - pair.Value.EnemyLastSeenTimeSense > 8
                || !Valid(bot, p)
                || CheaterRoster.Spares(bot, p)
                || mind.Ignores(p)
            )
                continue;
            float d = (p.Position - bot.Position).sqrMagnitude;
            if (d >= nearest)
                continue;
            nearest = d;
            best = p;
            at = pair.Value.EnemyLastPosition;
        }
        return best;
    }

    private static bool Closer(Player bot, Player a, Player? b) =>
        b is null || a == b || (a.Position - bot.Position).sqrMagnitude < (b.Position - bot.Position).sqrMagnitude * .5f;

    // ORBIT only yields to SAIN combat or incoming fire, and cheaters skip SAIN, so they must spot enemies themselves.
    // Nearest enemy in its 140 degree view within 70 m, or within 8 m. ESP and spinbots see all round, and anyone gunning for it counts within 15 m.
    private static Player? Spotted(BotOwner owner, Player bot, CheaterMind mind)
    {
        if (owner.BotsGroup is not { } group)
            return null;
        Player? best = null;
        float nearest = 70 * 70;
        var look = bot.LookDirection;
        bool allRound = CheaterRoster.Has(bot, Cheat.Esp | Cheat.Spinbot);
        foreach (var p in World.AllAlivePlayersList)
        {
            if (!Valid(bot, p) || CheaterRoster.Spares(bot, p) || mind.Ignores(p))
                continue;
            float d = (p.Position - bot.Position).sqrMagnitude;
            if (d >= nearest)
                continue;
            bool enemy,
                hostile;
            try
            {
                hostile = (object?)p.AIData?.BotOwner?.Memory?.GoalEnemy?.Person == bot;
                enemy =
                    hostile
                    || group.IsEnemy(p)
                    || p.IsYourPlayer
                    || p.Side is EPlayerSide.Usec or EPlayerSide.Bear
                    || p.Side == EPlayerSide.Savage && bot.Side != EPlayerSide.Savage;
            }
            catch
            {
                continue;
            }
            if (!enemy)
                continue;
            float close = hostile ? 15 : 8;
            if (
                d > close * close
                && (!allRound && !hostile && Vector3.Angle(look, p.Position - bot.Position) > 70 || !Clear(bot, p))
            )
                continue;
            nearest = d;
            best = p;
        }

        return best;
    }

    private static bool Knows(Player bot, CheaterMind mind)
    {
        if (mind.Target is not { } t || t.HealthController?.IsAlive != true)
            return false;
        if (!CheaterRoster.Has(bot, Cheat.Esp))
            return Time.time - mind.LastSeenTime < Memory;
        // ESP lets a target go once it is well out of the type's hunting range, so a closet cheater does not walk across the map to it.
        float range = Math.Max(150, CheaterRoster.HuntOf(bot).Range) * 1.25f;
        return (t.Position - bot.Position).sqrMagnitude < range * range;
    }

    // ESP sees everyone in range. Without it, only what the bot is fighting or can see right now. Other cheaters are fair game too.
    private static Player? FindTarget(BotOwner owner, Player bot, CheaterMind mind)
    {
        bool esp = CheaterRoster.Has(bot, Cheat.Esp);
        var type = CheaterRoster.TypeOf(bot);
        float range = esp ? CheaterRoster.HuntOf(bot).Range : 150;
        Player? you = null,
            other = null,
            rival = null;
        float nearest = float.MaxValue,
            nearestRival = float.MaxValue;
        var me = Me;

        if (
            !esp
            && owner.Memory?.GoalEnemy is { } enemy
            && enemy.Person is Player fighting
            && Valid(bot, fighting)
            && !CheaterRoster.Spares(bot, fighting)
            && !mind.Ignores(fighting)
            && Time.time - enemy.TimeLastSeen < Memory
        )
        {
            mind.Track(fighting, enemy.EnemyLastPosition);
            return fighting;
        }

        foreach (var p in World.AllAlivePlayersList)
        {
            // A troll only cares about you.
            if (
                !Valid(bot, p)
                || mind.Ignores(p)
                || CheaterRoster.Spares(bot, p)
                || !mind.Worth(bot, p)
                || type == CheaterType.Troll && p != me
            )
                continue;
            float d = (p.Position - bot.Position).sqrMagnitude;
            if (d > range * range || !esp && (d > 150 * 150 || !Clear(bot, p)))
                continue;
            if (p == me)
                you = p;
            else if (CheaterRoster.IsCheater(p))
            {
                if (d < nearestRival)
                {
                    nearestRival = d;
                    rival = p;
                }
            }
            else if (d < nearest)
            {
                nearest = d;
                other = p;
            }
        }

        Player? pick;
        if (rival is not null && UnityEngine.Random.value < CheaterRoster.Rivalry)
            pick = rival;
        else if (you is not null && (other is null || UnityEngine.Random.value < CheaterRoster.TargetsYou))
            pick = you;
        else
            pick = other ?? rival ?? you;
        if (pick is null)
            return null;

        mind.Track(pick, pick.Position);
        CheaterRoster.Log(
            $"{bot.Profile.Nickname} goes after {pick.Profile.Nickname}{(esp ? " (ESP)" : "")}{(pick == rival ? ", another cheater" : "")}."
        );
        if (pick == rival)
            Provoke(bot, rival);
        return pick;
    }

    // The other cheater notices and turns on it, unless it is already busy with someone it can see.
    private static void Provoke(Player bot, Player rival)
    {
        var theirs = CheaterMind.Of(rival);
        if (
            theirs.Target is { } busy
            && busy != bot
            && busy.HealthController?.IsAlive == true
            && Time.time - theirs.LastSeenTime < 5
        )
            return;
        theirs.Track(bot, bot.Position);
        theirs.NextCheck = 0;
        try
        {
            if (rival.AIData?.BotOwner?.BotsGroup is { } group && !group.IsEnemy(bot))
                group.AddEnemy(bot, EBotEnemyCause.addPlayer);
        }
        catch (Exception ex)
        {
            CheaterRoster.Log($"Rivalry enemy add failed: {ex.Message}");
        }
    }

    private static bool Valid(Player bot, Player? p) =>
        p is not null
        && p != bot
        && p.HealthController?.IsAlive == true
        && !CheaterRoster.IsFriendly(bot, p);

    private static bool Clear(Player from, Player to) =>
        !Physics.Linecast(
            from.PlayerBones.Head.position,
            to.PlayerBones.Head.position,
            LayersMaskController.HighPolyWithTerrainMask
        );

    public override void Start()
    {
        BotOwner.Mover?.Stop();
        // A pause another mover left behind (ORBIT's loot session) would hold every GoToPoint below.
        if (BotOwner.Mover is { Pause: true } mover)
            mover.Pause = false;
        // BigBrain keeps one logic per bot and only calls Start and Stop, so path timers from the last takeover would hold
        // the new path back for up to 5 s after the Stop above.
        nextPath = 0;
        travelGoal = destination = Vector3.positiveInfinity;
        stuckFrom = Bot.Position;
        nextStuckCheck = Time.time + 6;
        Unstick();
        var mind = Mind;
        Enter(
            mind.Threat is not null ? CheaterPhase.Evade
            : mind.Target is null && Stalking(Bot, mind) ? CheaterPhase.Stalk
            : Looting(mind) ? CheaterPhase.Loot
            : CheaterPhase.Fight
        );
    }

    public override void Stop()
    {
        teleportTo = null;
        arrivedFor = null;
        Trigger(false);
        Unstick();
        BotOwner.SetPose(1f);
        Bot.MovementContext?.SetTilt(0f);
        CheaterRoster.Aim(Bot, null);
        CheaterRoster.SetDriven(Bot, false);
        Mind.Doing = "roaming";
    }

    // An exception out of IsActive or Update aborts the bot's whole BotOwner.UpdateManual (mover, memory, weapons) and the game
    // swallows it, so the bot froze without a word. It hands back for a few seconds instead, and says why.
    internal static void Broke(Player bot, CheaterMind mind, string where, Exception ex)
    {
        mind.BrokenUntil = Time.time + 3;
        mind.Wants = false;
        CheaterRoster.SetDriven(bot, false);
        if (mind.Failures++ % 20 == 0)
            CheaterRoster.Diag($"{bot.Profile?.Nickname} brain failed while {where} ({mind.Failures} so far), hands back for 3 s: {ex}");
    }

    public override void BuildDebugText(StringBuilder text) =>
        text.AppendLine(
            $"PBT {Type} cheater: {phase}/{tactic}, {CheaterRoster.CheatsOf(Bot)}, target {Mind.Target?.Profile.Nickname ?? "none"}"
        );

    private void Enter(CheaterPhase to)
    {
        phase = to;
        phaseSince = Time.time;
        stalkFor = UnityEngine.Random.Range(20f, 50f);
        next = 0;
        engaged = null;
        angle = flank = null;
        // A queued teleport or arrival timer belongs to the moment it was set, not to whatever comes next.
        teleportTo = null;
        arrivedFor = null;
        settled = false;
        Trigger(false);
        CheaterRoster.Aim(Bot, null);
        Mind.Doing = to.ToString();
        CheaterRoster.Log($"{Bot.Profile.Nickname} now {to}.");
    }

    public override void Update(CustomLayer.ActionData data)
    {
        if (!Singleton<GameWorld>.Instantiated)
            return;
        try
        {
            Unstick();
            KeepGunReady();
            // The layer decides once a second whether a looter runs, walks to loot or fights. A fight pauses a troll's visits.
            var want =
                Mind.Threat is not null ? CheaterPhase.Evade
                : Looting(Mind) ? CheaterPhase.Loot
                : phase == CheaterPhase.Stalk && Mind.Target is not null ? CheaterPhase.Fight
                : phase == CheaterPhase.Fight && Mind.Target is null && Stalking(Bot, Mind) ? CheaterPhase.Stalk
                : phase is CheaterPhase.Evade or CheaterPhase.Loot ? CheaterPhase.Fight
                : phase;
            if (want != phase)
                Enter(want);
            switch (phase)
            {
                case CheaterPhase.Evade:
                    Evade();
                    break;
                case CheaterPhase.Loot:
                    WalkToLoot();
                    break;
                case CheaterPhase.Stalk:
                    Stalk();
                    break;
                default:
                    Fight();
                    break;
            }
        }
        catch (Exception ex)
        {
            Trigger(false);
            Broke(Bot, Mind, $"in {phase}", ex);
        }
    }

    // A vacuum bot with no target of its own and a run to get on with.
    private bool Looting(CheaterMind mind) => mind.Target is null && Has(Cheat.Vacuum) && mind.OnRun;

    private void WalkToLoot()
    {
        var mind = Mind;
        Trigger(false);
        CheaterRoster.Aim(Bot, null);
        if (mind.Leaving)
        {
            Leave(mind);
            return;
        }

        // At the item or between pulls: wait for the vacuum instead of handing the bot back to its AI.
        if (mind.LootSpot is not { } spot || (spot - Bot.Position).sqrMagnitude < Square(CheaterRoster.VacuumReach - 1))
        {
            // Arrived: pull soon, but at most twice a second since every pull scans the whole map.
            if (mind.LootSpot is not null)
            {
                mind.NextPull = Math.Min(mind.NextPull, Time.time + .5f);
                // Still pulling other things around it counts as progress.
                if (arrivedFor != mind.LootItem || arrivedLooted != mind.Looted)
                {
                    arrivedFor = mind.LootItem;
                    arrivedLooted = mind.Looted;
                    arrivedAt = Time.time;
                }
                // Standing at it and the pull still fails (other floor, sealed box): drop it rather than loop on it.
                else if (Time.time - arrivedAt > 4 && mind.LootItem is { } stuck)
                {
                    mind.Skip.Add(stuck);
                    mind.LootSpot = null;
                    mind.LootItem = null;
                    arrivedFor = null;
                    CheaterRoster.Log(
                        $"{Bot.Profile.Nickname} cannot pull {stuck.TemplateId} from where it stands and skips it."
                    );
                }
            }
            if (BotOwner.Mover?.IsMoving == true)
                BotOwner.Mover.Stop();
            BotOwner.SetPose(.7f);
            return;
        }

        arrivedFor = null;
        if (Travel(spot, true))
            mind.Stuck++;
    }

    // Walks to the chosen exit and leaves the raid there after a short extract, like a player would.
    private void Leave(CheaterMind mind)
    {
        if (mind.Exit is not { } exit || exit.Status == EFT.Interactive.EExfiltrationStatus.NotPresent)
        {
            if (mind.Exit is { } gone)
                mind.BadExits.Add(gone);
            CheaterRoster.PickExit(Bot, mind);
            return;
        }

        if ((mind.ExitSpot - Bot.Position).sqrMagnitude > 2.5f * 2.5f)
        {
            extractAt = 0;
            // An exit it cannot get to (blocked path, locked gate) is swapped for another.
            if (Travel(mind.ExitSpot, false) && ++mind.ExitTries >= 3)
            {
                mind.BadExits.Add(exit);
                CheaterRoster.PickExit(Bot, mind);
            }
            return;
        }

        if (extractAt <= 0)
        {
            BotOwner.Mover?.Stop();
            BotOwner.SetPose(.25f);
            extractAt = Time.time + UnityEngine.Random.Range(6f, 10f);
            CheaterRoster.Log($"{Bot.Profile.Nickname} is extracting at {exit.Settings?.Name} with {mind.Looted} items.");
        }
        else if (Time.time >= extractAt)
        {
            // The despawn freezes the body and can leave its gun hanging in the air, so nobody may be watching.
            if (Watched())
                extractAt = Time.time + 2;
            else
                CheaterRoster.Extract(Bot);
        }
    }

    private bool Watched()
    {
        foreach (var p in World.AllAlivePlayersList)
        {
            if (p.IsAI || p.HealthController?.IsAlive != true)
                continue;
            float d = (p.Position - Bot.Position).sqrMagnitude;
            if (d < 30f * 30f || d < 200f * 200f && Clear(p, Bot))
                return true;
        }
        return false;
    }

    // True after 12 s without getting closer; teleporters and flyers then blink there (into locked rooms too).
    private bool Travel(Vector3 spot, bool breakIn)
    {
        BotOwner.SetPose(1f);
        BotOwner.SetTargetMoveSpeed(1f);
        float d = Vector3.Distance(spot, Bot.Position);
        if ((spot - travelGoal).sqrMagnitude > 1)
        {
            travelGoal = spot;
            travelBest = d;
            travelSince = Time.time;
            nextPath = 0;
        }
        else if (d < travelBest - 2)
        {
            travelBest = d;
            travelSince = Time.time;
        }

        if (Time.time >= nextPath)
        {
            nextPath = Time.time + 5;
            // The loot can sit off the navmesh (on a shelf), so walk to the nearest point that is on it.
            var status = BotOwner.GoToPoint(Near(spot) ?? spot, false);
            // No way in (a locked or marked room): a teleporter just goes in, and out the same way.
            if (
                breakIn
                && status != NavMeshPathStatus.PathComplete
                && Has(Cheat.Fly | Cheat.FightTeleport)
                && d is > 3 and < 60
                && Time.time >= nextTeleport
                && Near(spot) is { } inside
            )
            {
                nextTeleport = Time.time + 20;
                Blink(inside);
                CheaterRoster.Suspect(Bot, 2);
                CheaterRoster.Log($"{Bot.Profile.Nickname} has no way there and teleports in.");
                return false;
            }
        }

        BotOwner.Mover?.Sprint(!Has(Cheat.Spinbot));
        BotOwner.Steering?.LookToMovingDirection();
        Glide(spot);
        if (Time.time - travelSince < 12)
            return false;
        travelSince = Time.time;
        travelBest = d;
        if (Has(Cheat.Fly | Cheat.FightTeleport) && Near(spot) is { } there)
        {
            Blink(there);
            CheaterRoster.Suspect(Bot, 2);
            CheaterRoster.Log($"{Bot.Profile.Nickname} got stuck and teleported to where it was going.");
        }
        return true;
    }

    private static float Square(float x) => x * x;

    // Runs to cover away from the threat, then crouches there watching it. Speedhackers get away faster.
    private void Evade()
    {
        if (Mind.Threat is not { } threat)
            return;
        Trigger(false);
        CheaterRoster.Aim(Bot, null);
        // Cover is blown once the threat can see it there, checked about once a second.
        bool blown = false;
        if (settled && Time.time >= next)
        {
            next = Time.time + 1;
            blown = Clear(threat, Bot);
        }
        if (angle is null || blown || Time.time > tacticUntil || (threat.Position - angle.Value).sqrMagnitude < 15 * 15)
        {
            angle = FleeSpot(threat);
            tacticUntil = Time.time + UnityEngine.Random.Range(10f, 20f);
            settled = false;
        }

        var spot = angle.Value;
        if ((spot - Bot.Position).sqrMagnitude > 1.5f * 1.5f)
        {
            BotOwner.SetPose(1f);
            BotOwner.SetTargetMoveSpeed(1f);
            if (destination != spot || Time.time >= nextPath)
            {
                destination = spot;
                nextPath = Time.time + 5;
                BotOwner.GoToPoint(spot, false);
            }

            BotOwner.Mover?.Sprint(true);
            BotOwner.Steering?.LookToMovingDirection();
            Glide(spot);
            return;
        }

        if (!settled)
        {
            settled = true;
            BotOwner.Mover?.Stop();
        }

        BotOwner.SetPose(.45f);
        LookAt(threat.PlayerBones.Head.position);
    }

    // Nearest spot out of the threat's sight in any direction but toward it, never closer to it than now.
    private Vector3 FleeSpot(Player threat)
    {
        var mask = LayersMaskController.HighPolyWithTerrainMask;
        var away = Bot.Position - threat.Position;
        away.y = 0;
        if (away.sqrMagnitude < .01f)
            away = -Bot.LookDirection;
        away.y = 0;
        away.Normalize();
        var eye = threat.PlayerBones.Head.position;
        float now = Vector3.Distance(Bot.Position, threat.Position);
        Vector3 best = Near(Bot.Position + away * 15) ?? Bot.Position;
        float bestScore = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float yaw = i * 22.5f;
            float turn = Mathf.Abs(Mathf.DeltaAngle(0, yaw));
            if (turn > 120)
                continue;
            var dir = Quaternion.Euler(0, yaw, 0) * away;
            foreach (float radius in new[] { 8f, 16f, 26f, 38f })
            {
                if (!NavMesh.SamplePosition(Bot.Position + dir * radius, out var hit, 3f, NavMesh.AllAreas))
                    continue;
                var p = hit.position;
                float fromThreat = Vector3.Distance(p, threat.Position);
                if (fromThreat < now - 2)
                    continue;
                float score = fromThreat * .06f - radius * .08f - turn * .01f;
                // Hidden crouched is what counts; hidden standing too is better still.
                if (Physics.Linecast(eye, p + Vector3.up * 1f, mask))
                    score += 5;
                if (Physics.Linecast(eye, p + Vector3.up * 1.6f, mask))
                    score += 2;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
        }

        return best;
    }

    // A troll does not show up until a few minutes in. Then it visits: pops up off to your side at 12 to 25 m, teabags, jumps and spams,
    // and vanishes the moment you aim at it. After a few visits, or as soon as you hit it, it sets a trap: an angle near you, pre-aimed.
    private void Stalk()
    {
        var mind = Mind;
        if (Me is not { } me || me.HealthController?.IsAlive != true || mind.Provoked || mind.Visits >= mind.VisitsPlanned)
        {
            mind.Stalked = true;
            if (visiting && Me is { } watcher)
                Vanish(watcher);
            Enter(CheaterPhase.Fight);
            if (Me is { } you && you.HealthController?.IsAlive == true)
                mind.Track(you, you.Position);
            CheaterRoster.Log($"{Bot.Profile.Nickname} is done playing{(mind.Provoked ? ", you hit it" : "")} and sets a trap.");
            return;
        }

        Trigger(false);
        CheaterRoster.Aim(Bot, null);
        if (visiting)
        {
            LookAt(me.PlayerBones.Head.position);
            // Teabags and hops while you watch.
            if (Time.time >= next)
            {
                next = Time.time + UnityEngine.Random.Range(.25f, .45f);
                crouched = !crouched;
                BotOwner.SetPose(crouched ? 0f : 1f);
                if (UnityEngine.Random.value < .15f)
                    Bot.MovementContext?.TryJump();
            }

            bool hit = CheaterRoster.SinceHit(Bot) < 1;
            if (hit)
                mind.Provoked = true;
            if (hit || AimedAt(me) || Time.time > visitUntil)
                Vanish(me);
            return;
        }

        BotOwner.Mover?.Stop();
        if (Time.time < mind.NextVisit)
            return;
        if (VisitSpot(me) is not { } spot)
        {
            mind.NextVisit = Time.time + 5;
            return;
        }

        Blink(spot);
        CheaterRoster.Suspect(Bot, 1);
        visiting = true;
        visitUntil = Time.time + UnityEngine.Random.Range(4f, 9f);
        mind.Visits++;
        CheaterRoster.Log($"{Bot.Profile.Nickname} pays you visit {mind.Visits} of {mind.VisitsPlanned}.");
    }

    // You are looking right at it with a clear line.
    private bool AimedAt(Player me)
    {
        var toward = Bot.PlayerBones.Head.position - me.PlayerBones.Head.position;
        return Vector3.Angle(me.LookDirection, toward) < 7 && Sees(me);
    }

    private void Vanish(Player me)
    {
        visiting = false;
        BotOwner.SetPose(1f);
        Mind.NextVisit = Time.time + UnityEngine.Random.Range(15f, 40f);
        // Out of sight, well away from you.
        var away = Bot.Position - me.Position;
        away.y = 0;
        var dir = away.sqrMagnitude > .01f ? away.normalized : -me.LookDirection;
        for (int i = 0; i < 8; i++)
        {
            var probe =
                me.Position
                + Quaternion.Euler(0, UnityEngine.Random.Range(-90f, 90f), 0) * dir * UnityEngine.Random.Range(40f, 60f);
            if (
                Near(probe) is not { } spot
                || !Physics.Linecast(
                    me.PlayerBones.Head.position,
                    spot + Vector3.up * 1.5f,
                    LayersMaskController.HighPolyWithTerrainMask
                )
            )
                continue;
            Blink(spot);
            CheaterRoster.Suspect(Bot, 1);
            return;
        }
    }

    // Off to one side of where you look (35 to 80 degrees), 12 to 25 m out, somewhere you can actually see it.
    private Vector3? VisitSpot(Player me)
    {
        var look = me.LookDirection;
        look.y = 0;
        if (look.sqrMagnitude < .01f)
            return null;
        look.Normalize();
        for (int i = 0; i < 10; i++)
        {
            float yaw = UnityEngine.Random.Range(35f, 80f) * (UnityEngine.Random.value < .5f ? -1 : 1);
            var probe = me.Position + Quaternion.Euler(0, yaw, 0) * look * UnityEngine.Random.Range(12f, 25f);
            if (
                Near(probe) is { } spot
                && !Physics.Linecast(
                    me.PlayerBones.Head.position,
                    spot + Vector3.up * 1.5f,
                    LayersMaskController.HighPolyWithTerrainMask
                )
            )
                return spot;
        }

        return null;
    }

    // Each type has its own way in. Closet cheaters, looters and trolls work angles and peeks. Hunters get behind you first. Rage just comes.
    private Tactic PickTactic()
    {
        float roll = UnityEngine.Random.value;
        // Two cheaters both holding angles just wait each other out, so a duel is always pushed.
        if (duel)
            return Tactic.Push;
        return Type switch
        {
            CheaterType.Closet or CheaterType.Looter or CheaterType.Troll => Tactic.Hold,
            CheaterType.Hunter => roll < .75f ? Tactic.Flank : Tactic.Hold,
            _ => Tactic.Push,
        };
    }

    private void Switch(Tactic to)
    {
        tactic = to;
        angle = flank = null;
        settled = holding = peeking = false;
        peeks = 0;
        Bot.MovementContext?.SetTilt(0f);
        tacticUntil = Time.time + UnityEngine.Random.Range(20f, 40f);
        tacticSince = Time.time;
        BotOwner.SetPose(1f);
        Mind.Doing = $"{phase}/{to}";
        CheaterRoster.Log(
            $"{Bot.Profile.Nickname} {to switch { Tactic.Hold => "works an angle on", Tactic.Flank => "circles behind", _ => "pushes" }} {Mind.Target?.Profile.Nickname}."
        );
    }

    private void Fight()
    {
        var mind = Mind;
        bool esp = Has(Cheat.Esp);
        mind.Settle(Bot);
        if (!Knows(Bot, mind) && mind.Target is not null)
        {
            CheaterRoster.Log($"{Bot.Profile.Nickname} lost track of {mind.Target.Profile.Nickname}.");
            mind.EndHunt(Bot);
            mind.NextCheck = 0;
        }

        if (mind.Target is not { } prey)
        {
            // Nothing to do. The layer hands the bot back soon, not after it stood frozen for up to a second.
            Trigger(false);
            CheaterRoster.Aim(Bot, null);
            mind.NextCheck = Math.Min(mind.NextCheck, Time.time + .2f);
            return;
        }

        duel = CheaterRoster.IsCheater(prey);

        if (prey != engaged)
        {
            engaged = prey;
            teleportTo = null;
            blinksLeft = Type == CheaterType.Rage ? UnityEngine.Random.Range(2, 4) : UnityEngine.Random.Range(1, 3);
            Switch(PickTactic());
        }

        bool clear = Sees(prey);
        if (clear)
        {
            mind.LastSeenPosition = prey.Position;
            mind.LastSeenTime = Time.time;
            if (mind.FirstSeen <= 0)
            {
                mind.FirstSeen = Time.time;
                react = Reaction();
                // ESP had it pre-aimed round the corner.
                if (esp && Vector3.Distance(prey.Position, Bot.Position) < 15)
                    react *= .5f;
                patience = UnityEngine.Random.Range(2f, 4f);
            }
        }
        // A blink of cover (a hop, a strafe past a post) does not restart its reaction.
        else if (Time.time - mind.LastSeenTime > 1)
            mind.FirstSeen = 0;

        // ESP knows where the target is now. Without it the bot goes where it last saw it, like any bot.
        var known = esp ? prey.Position : mind.LastSeenPosition;
        float distance = Vector3.Distance(known, Bot.Position);

        // Getting somewhere means a line on the target or real ground gained. An unreachable roof or a locked room ends in giving up, not an endless chase.
        if (clear || distance < mind.BestDistance - 3)
        {
            mind.LastProgress = Time.time;
            mind.BestDistance = Math.Min(mind.BestDistance, distance);
        }
        else if (Time.time - mind.LastProgress > CheaterRoster.HuntOf(Bot).GiveUp)
        {
            mind.GiveUp(Bot);
            Trigger(false);
            CheaterRoster.Aim(Bot, null);
            mind.NextCheck = 0;
            return;
        }

        bool exposed = Exposed(prey, clear, distance);

        // Teleports are moves with a reason, never a habit: out of a bad spot, or onto an angle the target is not watching.
        if (teleportTo is { } jump && Time.time >= tellUntil)
        {
            teleportTo = null;
            Blink(jump);
            Switch(Tactic.Hold);
            angle = jump;
            settleUntil = Time.time + UnityEngine.Random.Range(.4f, .8f);
            CheaterRoster.Suspect(Bot, 3);
            CheaterRoster.Log(
                $"{Bot.Profile.Nickname} teleported {(teleportPeek ? "onto an angle on" : "away from")} {prey.Profile.Nickname}."
            );
        }
        else if (teleportTo is null && Has(Cheat.FightTeleport) && Time.time >= nextTeleport)
        {
            if (exposed || Grenade())
            {
                nextTeleport = Time.time + UnityEngine.Random.Range(12f, 20f);
                teleportTo = FleeSpot(prey);
                teleportPeek = false;
                tellUntil = Time.time + .15f;
            }
            else if (
                blinksLeft > 0
                && (esp || Time.time - mind.LastSeenTime < 8)
                && (!clear && Time.time - mind.LastSeenTime > 3 || distance > 45)
                && GunReady
            )
            {
                // The search is a dozen navmesh samples, so a miss waits a moment before the next try.
                if (PeekSpot(prey) is not { } spot)
                    nextTeleport = Time.time + 1.5f;
                else
                {
                    blinksLeft--;
                    nextTeleport = Time.time + UnityEngine.Random.Range(25f, 45f);
                    teleportTo = spot;
                    teleportPeek = true;
                    tellUntil = Time.time + .6f;
                    Bot.Speaker?.Play(EPhraseTrigger.Gogogo, Bot.HealthStatus | ETagStatus.Combat, true);
                }
            }
        }
        // Without a teleport it still gets out of the open to reload or when it is losing the trade.
        else if (exposed && tactic == Tactic.Push && !duel)
        {
            Switch(Tactic.Hold);
            CheaterRoster.Log($"{Bot.Profile.Nickname} breaks for cover.");
        }

        switch (tactic)
        {
            case Tactic.Hold:
                Hold(prey, known, clear, distance);
                break;
            case Tactic.Flank:
                Flank(prey, known, clear, esp, distance);
                break;
            default:
                Push(known, clear, esp, distance);
                break;
        }

        if (Has(Cheat.BunnyHop))
            Hop(prey, clear, distance);

        Look(prey, known, clear, esp, distance);

        // Only with a clear line, once it has reacted and turned onto the target. Never at a remembered spot through a wall.
        if (
            clear
            && distance < 150
            && Time.time - mind.FirstSeen >= react
            && Time.time >= settleUntil
            && Facing(prey)
            && Moment(prey, mind)
            && !CheaterRoster.FriendlyInLine(Bot, prey)
        )
        {
            // A spinbot is useless without an aim lock, so spinners always have one.
            CheaterRoster.Aim(Bot, Has(Cheat.Aimbot | Cheat.Spinbot) ? prey : null);
            Burst();
        }
        else
        {
            CheaterRoster.Aim(Bot, null);
            Trigger(false);
        }
    }

    // Rage: walks in and, once it has a shot, strafes side to side while it sprays, jumping if it can.
    private void Push(Vector3 known, bool clear, bool esp, float distance)
    {
        if (clear && distance < 30)
        {
            // Two cheaters strafing round each other never line up a shot, so a duel is fought standing.
            if (duel)
            {
                if (BotOwner.Mover?.IsMoving == true)
                    BotOwner.Mover.Stop();
                return;
            }

            if (Time.time >= nextStrafe)
            {
                nextStrafe = Time.time + UnityEngine.Random.Range(.5f, .9f);
                strafeSide = -strafeSide;
                var toward = known - Bot.Position;
                toward.y = 0;
                var right = Vector3.Cross(Vector3.up, toward.normalized);
                if (Near(Bot.Position + right * strafeSide * UnityEngine.Random.Range(1.5f, 3f)) is { } step)
                {
                    destination = step;
                    nextPath = Time.time + .5f;
                    BotOwner.GoToPoint(step, false);
                }
            }

            return;
        }

        // At the last known spot with nothing in sight: stands and looks around until it forgets.
        if (!esp && !clear && distance < 3)
            BotOwner.Mover?.Stop();
        else
            Chase(known);
    }

    // Hunter: gets round behind the target (where it is not looking) at 20 to 30 m, then works an angle from there.
    private void Flank(Player prey, Vector3 known, bool clear, bool esp, float distance)
    {
        if (Time.time > tacticUntil || distance < 12 || clear && distance < 35 && !Watching(prey))
        {
            Switch(Tactic.Hold);
            return;
        }

        if (flank is null)
        {
            var back = esp ? -prey.LookDirection : Bot.Position - known;
            back.y = 0;
            if (back.sqrMagnitude < .01f)
                back = Vector3.forward;
            var spot =
                known
                + Quaternion.Euler(0, UnityEngine.Random.Range(-50f, 50f), 0)
                    * back.normalized
                    * UnityEngine.Random.Range(20f, 30f);
            flank = Near(spot) ?? known;
        }

        if ((flank.Value - Bot.Position).sqrMagnitude < 3 * 3)
            Switch(Tactic.Hold);
        else
            Chase(flank.Value);
    }

    // Works an angle: crouched behind cover pre-aimed at the target through the wall, then a short peek (a lean if one side has the line, else standing up),
    // back down, and again. After two or three peeks, or a hit, it moves to a fresh angle so you never know where the next one comes from.
    private void Hold(Player prey, Vector3 known, bool clear, float distance)
    {
        // Close in, or a standoff: a held angle with no line for a while turns into a push instead of sitting in cover all fight.
        if (
            !clear && distance < 10 && Type == CheaterType.Hunter
            || settled && Time.time - Mind.LastSeenTime > 12 && Time.time - tacticSince > 10
        )
        {
            Switch(Tactic.Push);
            return;
        }

        bool hit = CheaterRoster.SinceHit(Bot) < .5f;
        if (angle is null || !clear && Time.time > tacticUntil || settled && (peeks >= peekLimit || hit))
        {
            bool moveUp = angle is not null && !hit && peeks < peekLimit;
            if (angle is { } old)
                used.Add(old);
            angle = FindAngle(prey, known, moveUp);
            tacticUntil = Time.time + UnityEngine.Random.Range(20f, 40f);
            settled = peeking = false;
            peeks = 0;
            peekLimit = UnityEngine.Random.Range(2, 4);
            Bot.MovementContext?.SetTilt(0f);
            if (used.Count > 0)
                CheaterRoster.Log($"{Bot.Profile.Nickname} {(moveUp ? "moves up" : "relocates")} to a new angle.");
        }

        var spot = angle!.Value;
        if (!settled && (spot - Bot.Position).sqrMagnitude > 1.2f * 1.2f)
        {
            Chase(spot);
            return;
        }

        if (!settled)
        {
            settled = true;
            BotOwner.Mover?.Stop();
            beatUntil = Time.time + UnityEngine.Random.Range(1f, 2.5f);
        }

        // Stays out while it has a shot, up to a couple of seconds.
        if (peeking && clear && Time.time < beatUntil + 1)
            return;
        if (Time.time >= beatUntil)
        {
            peeking = !peeking;
            if (peeking)
            {
                peeks++;
                beatUntil = Time.time + UnityEngine.Random.Range(.8f, 1.6f);
                int side = PeekSide(prey);
                Bot.MovementContext?.SetTilt(side * 5f);
                BotOwner.SetPose(side == 0 ? 1f : .7f);
            }
            else
            {
                beatUntil = Time.time + UnityEngine.Random.Range(1.5f, 3.5f);
                Bot.MovementContext?.SetTilt(0f);
                BotOwner.SetPose(.45f);
            }
        }
    }

    // Which way to lean for a line on the target: -1 left, 1 right, 0 neither (it stands up instead).
    private int PeekSide(Player prey)
    {
        var mask = LayersMaskController.HighPolyWithTerrainMask;
        var head = Bot.Position + Vector3.up * 1.3f;
        var toward = prey.Position - Bot.Position;
        toward.y = 0;
        var right = Vector3.Cross(Vector3.up, toward.normalized);
        var eye = prey.PlayerBones.Head.position;
        bool r = !Physics.Linecast(head + right * .6f, eye, mask),
            l = !Physics.Linecast(head - right * .6f, eye, mask);
        return r && l ? (UnityEngine.Random.value < .5f ? 1 : -1)
            : r ? 1
            : l ? -1
            : 0;
    }

    // The target is looking roughly at the cheater.
    private bool Watching(Player prey)
    {
        var toward = Bot.PlayerBones.Head.position - prey.PlayerBones.Head.position;
        return Vector3.Angle(prey.LookDirection, toward) < 50;
    }

    // ESP cheaters pick their moment: they open up when you look away or are busy with someone else, not into your face,
    // unless you are shooting them or they have waited long enough. Rage never waits.
    private bool Moment(Player prey, CheaterMind mind)
    {
        if (!Has(Cheat.Esp) || Type == CheaterType.Rage || CheaterRoster.SinceHit(Bot) < 3)
            return true;
        if (Time.time - mind.FirstSeen > patience)
            return true;
        if (!Watching(prey))
            return true;
        return prey.AIData?.BotOwner?.Memory?.GoalEnemy is { IsVisible: true } busy && busy.Person != (IPlayer)Bot;
    }

    // 15 to 30 m off the target's side or back with a standing line on its head, hidden crouched if it can find one.
    private Vector3? PeekSpot(Player prey)
    {
        var mask = LayersMaskController.HighPolyWithTerrainMask;
        var look = prey.LookDirection;
        look.y = 0;
        if (look.sqrMagnitude < .01f)
            return null;
        look.Normalize();
        var eye = prey.PlayerBones.Head.position;
        Vector3? best = null;
        float bestScore = float.MinValue;
        for (int i = 0; i < 14; i++)
        {
            float yaw = UnityEngine.Random.Range(100f, 180f) * (UnityEngine.Random.value < .5f ? -1 : 1);
            var dir = Quaternion.Euler(0, yaw, 0) * look;
            if (
                Near(prey.Position + dir * UnityEngine.Random.Range(15f, 30f)) is not { } p
                || Physics.Linecast(eye, p + Vector3.up * 1.55f, mask)
            )
                continue;
            float score = 0;
            if (Physics.Linecast(eye, p + Vector3.up * 1f, mask))
                score += 3;
            var facing = prey.Position - p;
            facing.y = 0;
            if (Physics.Raycast(p + Vector3.up * 1f, facing.normalized, 2f, mask))
                score += 2;
            if ((p - Bot.Position).sqrMagnitude < 10 * 10)
                score -= 4;
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return best;
    }

    // Reloading or clearing a jam in the target's view, or hurt and still taking hits.
    private bool Exposed(Player prey, bool clear, float distance)
    {
        if (Has(Cheat.Godmode))
            return false;
        if (clear && distance < 60 && GunBusy && Watching(prey))
            return true;
        return CheaterRoster.SinceHit(Bot) < 1 && Health() < .45f;
    }

    private float Health() =>
        Bot.HealthController?.GetBodyPartHealth(EBodyPart.Common, false) is { Maximum: > 0 } h ? h.Current / h.Maximum : 1;

    private bool Grenade() => BotOwner.BewareGrenade?.SawGrenadeSoFar(2f) == true;

    private bool GunReady => !GunBusy;

    // Scores spots around the bot (or partway to the target when moving up): hidden from the target when crouched, a view of it when standing, cover right in front.
    private Vector3 FindAngle(Player prey, Vector3 known, bool moveUp)
    {
        var mask = LayersMaskController.HighPolyWithTerrainMask;
        var from = Bot.Position;
        var center = moveUp || (known - from).sqrMagnitude > 70 * 70 ? Vector3.Lerp(from, known, .45f) : from;
        var eye = known + Vector3.up * 1.5f;
        Vector3 best = from;
        float bestScore = float.MinValue;
        foreach (float radius in new[] { 3f, 7f, 12f })
        {
            for (int i = 0; i < 12; i++)
            {
                var probe = center + Quaternion.Euler(0, i * 30 + radius * 7, 0) * Vector3.forward * radius;
                if (!NavMesh.SamplePosition(probe, out var hit, 2f, NavMesh.AllAreas))
                    continue;
                var p = hit.position;
                float toTarget = Vector3.Distance(p, known);
                if (toTarget < 8)
                    continue;
                var low = p + Vector3.up * 1f;
                var high = p + Vector3.up * 1.55f;
                var facing = known - p;
                facing.y = 0;
                float score = 0;
                if (Physics.Linecast(eye, low, mask))
                    score += 3;
                if (!Physics.Linecast(eye, high, mask))
                    score += 3;
                if (Physics.Raycast(low, facing.normalized, 2f, mask))
                    score += 2;
                score -= Vector3.Distance(from, p) * .08f + Mathf.Abs(toTarget - 35) * .02f;
                foreach (var u in used)
                    if ((u - p).sqrMagnitude < 6 * 6)
                        score -= 5;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
        }

        return best;
    }

    // ESP or a hold: the crosshair stays on the target's head through walls, which is what gives a closet cheater away. Otherwise it watches where it last saw it.
    private void Look(Player prey, Vector3 known, bool clear, bool esp, float distance)
    {
        if (Has(Cheat.Spinbot))
            Spinbot(prey);
        else if (clear || esp)
            LookAt(prey.PlayerBones.Head.position);
        else if (distance < 3)
        {
            spin = (spin + 90f * Time.deltaTime) % 360f;
            BotOwner.Steering?.LookToPoint(
                Bot.Position + Vector3.up * 1.5f + Quaternion.Euler(0, spin, 0) * Vector3.forward * 5f,
                180f
            );
        }
        else
            LookAt(known + Vector3.up * 1.55f);
    }

    private enum AntiYaw
    {
        Spin,
        Jitter,
        Flick,
    }

    private enum AntiPitch
    {
        Down,
        Up,
        Flip,
    }

    // A rage config: yaw spins, jitters backwards from the target or flicks at random, and the head pitches
    // into the floor, at the sky or flips between them. The aimbot bends every shot, so none of it costs aim.
    private void Spinbot(Player prey)
    {
        if (antiYaw is null)
        {
            antiYaw = (AntiYaw)UnityEngine.Random.Range(0, 3);
            antiPitch = (AntiPitch)UnityEngine.Random.Range(0, 3);
            spinRate = UnityEngine.Random.Range(900f, 2400f) * (UnityEngine.Random.value < .5f ? -1 : 1);
        }

        float now = Time.time;
        switch (antiYaw)
        {
            case AntiYaw.Spin:
                // Now and then reverses, like someone tapping the toggle.
                if (now >= nextYaw)
                {
                    nextYaw = now + UnityEngine.Random.Range(2f, 6f);
                    if (UnityEngine.Random.value < .35f)
                        spinRate = -spinRate;
                }
                spin = (spin + spinRate * Time.deltaTime) % 360f;
                break;
            case AntiYaw.Jitter:
                if (now >= nextYaw)
                {
                    nextYaw = now + UnityEngine.Random.Range(.04f, .09f);
                    jitterLeft = !jitterLeft;
                    var away = Bot.Position - prey.Position;
                    spin =
                        Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg + UnityEngine.Random.Range(25f, 65f) * (jitterLeft ? -1 : 1);
                }
                break;
            default:
                if (now >= nextYaw)
                {
                    nextYaw = now + UnityEngine.Random.Range(.06f, .18f);
                    spin = UnityEngine.Random.Range(0f, 360f);
                }
                break;
        }

        if (antiPitch == AntiPitch.Flip && now >= nextPitch)
        {
            nextPitch = now + UnityEngine.Random.Range(.12f, .35f);
            pitchUp = !pitchUp;
        }
        // Past the stand pose limits on purpose; MovementContext clamps it to the furthest the body bends.
        float pitch = antiPitch == AntiPitch.Up || antiPitch == AntiPitch.Flip && pitchUp ? -89f : 89f;

        // Steering aims from the weapon root and only rate-limits yaw, so this lands exactly and the speed just has to outrun the flicks.
        var root = BotOwner.WeaponRoot != null ? BotOwner.WeaponRoot.position : Bot.Position + Vector3.up * 1.4f;
        BotOwner.Steering?.LookToPoint(root + Quaternion.Euler(pitch, spin, 0) * Vector3.forward * 5f, 20000f);
        CheaterRoster.Suspect(Bot, Time.deltaTime * .3f);
    }

    // Rolled each time the target comes into view. Pre-aimed from a held angle it reacts faster, but never instantly.
    private float Reaction()
    {
        var aim = CheaterRoster.AimOf(Bot);
        float seconds = UnityEngine.Random.Range(aim.ReactMin, aim.ReactMax) * CheaterRoster.ReactionScale(Bot);
        return tactic == Tactic.Hold && settled ? seconds * .65f : seconds;
    }

    private bool Facing(Player prey)
    {
        if (Has(Cheat.Spinbot))
            return true;
        var toward = prey.PlayerBones.Head.position - Bot.PlayerBones.Head.position;
        return Vector3.Angle(Bot.LookDirection, toward) <= Mathf.Max(CheaterRoster.AimOf(Bot).Snap, 6);
    }

    // Bunny hops. Now and then it clears a low wall right in front with a jump far higher than anyone can.
    // Only over a real wall with room above and ground behind, and rarely: every superjump used to stall the path.
    private void Hop(Player prey, bool clear, float distance)
    {
        // Hopping in place (at a last known spot, mid-peek) looked like the bot spazzing out.
        if (Time.time < nextJump || distance > 90 || tactic == Tactic.Hold && settled || BotOwner.Mover?.IsMoving != true)
            return;
        nextJump = Time.time + UnityEngine.Random.Range(1.1f, 2f);
        Bot.MovementContext?.TryJump();
        if (clear || distance > 25 || Time.time < nextSuperJump)
            return;

        var mask = LayersMaskController.HighPolyWithTerrainMask;
        var dir = prey.Position - Bot.Position;
        dir.y = 0;
        if (dir.sqrMagnitude < .01f)
            return;
        dir.Normalize();
        var from = Bot.Position;
        if (
            !Physics.Raycast(from + Vector3.up * 1f, dir, out var wall, 2.5f, mask)
            || Physics.Raycast(from + Vector3.up * 3.2f, dir, wall.distance + 1.5f, mask)
            || Physics.Raycast(from + Vector3.up * 1.8f, Vector3.up, 1.6f, mask)
            || Near(from + dir * (wall.distance + 1.2f)) is not { } landing
            || Mathf.Abs(landing.y - from.y) > 2f
        )
            return;

        nextSuperJump = Time.time + UnityEngine.Random.Range(8f, 15f);
        Blink(landing + Vector3.up * UnityEngine.Random.Range(.3f, .8f));
        CheaterRoster.Suspect(Bot, 2);
        CheaterRoster.Log($"{Bot.Profile.Nickname} jumped over cover at {prey.Profile.Nickname}.");
    }

    // Repaths only when the goal moved, since every new path makes the bot slow down and start again. Never sprints: a spinning body drops it.
    private void Chase(Vector3 goal)
    {
        BotOwner.SetPose(1f);
        BotOwner.SetTargetMoveSpeed(1f);
        Glide(goal);
        // Barely moved in 6 s: walk a few metres somewhere else, then try the path again.
        if (Time.time >= nextStuckCheck)
        {
            bool stuck = (Bot.Position - stuckFrom).sqrMagnitude < 1.5f * 1.5f && (goal - Bot.Position).sqrMagnitude > 3 * 3;
            stuckFrom = Bot.Position;
            nextStuckCheck = Time.time + 6;
            if (
                stuck
                && Near(Bot.Position + Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0) * Vector3.forward * 6)
                    is { } detour
            )
            {
                destination = detour;
                nextPath = Time.time + 3;
                BotOwner.GoToPoint(detour, false);
                return;
            }
        }

        if (Time.time < nextPath && (goal - destination).sqrMagnitude < 4 * 4)
            return;
        nextPath = Time.time + 5f;
        destination = goal;
        BotOwner.GoToPoint(destination, false);
        BotOwner.Mover?.Sprint(false);
    }

    // Checked a few times a second, out to 200 m; a linecast every frame flickers on foliage.
    private bool Sees(Player p)
    {
        if (p == seen && Time.time < nextSight)
            return sees;
        seen = p;
        nextSight = Time.time + .25f;
        return sees = (p.Position - Bot.Position).sqrMagnitude < 200 * 200 && Clear(Bot, p);
    }

    private void LookAt(Vector3 point) => BotOwner.Steering?.LookToPoint(point, CheaterRoster.AimOf(Bot).Turn);

    // Reloads and jam clearing live in the AI's own brain nodes, which never run while this layer drives.
    private void KeepGunReady()
    {
        if (CheaterRoster.ClearJam(BotOwner))
        {
            Trigger(false);
            return;
        }
        if (Time.time < nextReload)
            return;
        nextReload = Time.time + 1;
        if (CheaterRoster.Reload(BotOwner, firing))
            Trigger(false);
    }

    private bool GunBusy =>
        BotOwner.WeaponManager is { } w && (w.Reload?.Reloading == true || CheaterRoster.Jammed(BotOwner));

    private void Burst()
    {
        if (GunBusy)
        {
            Trigger(false);
            return;
        }
        float now = Time.time;
        if (firing && now >= triggerUntil)
        {
            Trigger(false);
            var aim = CheaterRoster.AimOf(Bot);
            triggerNext = now + UnityEngine.Random.Range(aim.PauseMin, aim.PauseMax);
        }
        else if (!firing && now >= triggerNext)
        {
            Trigger(true);
            var aim = CheaterRoster.AimOf(Bot);
            triggerUntil = now + UnityEngine.Random.Range(aim.BurstMin, aim.BurstMax);
        }
    }

    private void Trigger(bool pressed)
    {
        firing = pressed;
        try
        {
            (Bot.HandsController as Player.FirearmController)?.SetTriggerPressed(pressed);
        }
        catch { }
    }

    // Teleports through the bot's mover. A bare Player.Teleport leaves the mover on its old path, which snaps the bot
    // back to the last navmesh point it linked from and leaves it rubber-banding in place.
    private void Blink(Vector3 to)
    {
        BotOwner.Mover?.Teleport(to);
        nextPath = 0;
        stuckFrom = to;
    }

    // A flyer skims ahead toward where it is going in short airborne hops, landing each time so the jump state ends normally.
    private void Glide(Vector3 goal)
    {
        // Only to get somewhere. Blinking about mid-fight made it an enderman.
        if (!Has(Cheat.Fly) || phase == CheaterPhase.Fight || Time.time < nextGlide)
            return;
        var to = goal - Bot.Position;
        to.y = 0;
        float d = to.magnitude;
        if (d < 40)
            return;
        nextGlide = Time.time + UnityEngine.Random.Range(6f, 12f);
        if (Near(Bot.Position + to / d * Mathf.Min(d - 4, UnityEngine.Random.Range(6f, 12f))) is not { } spot)
            return;
        Blink(spot + Vector3.up * UnityEngine.Random.Range(.8f, 1.6f));
        CheaterRoster.Suspect(Bot, .4f);
    }

    // The bot's own AI may have gone prone (BotLay) right before this layer took over. Only that AI gets it back up,
    // so it stayed prone for good while this logic walked it around.
    private void Unstick()
    {
        if (Bot.MovementContext is not { IsInPronePose: true } ctx)
            return;
        if (BotOwner.BotLay is { } lay)
            lay.IsLay = false;
        if (ctx.IsInPronePose)
            ctx.IsInPronePose = false;
        BotOwner.SetPose(1f);
    }

    private static Vector3? Near(Vector3 point) =>
        NavMesh.SamplePosition(point, out var hit, 4f, NavMesh.AllAreas) ? hit.position : null;
}
