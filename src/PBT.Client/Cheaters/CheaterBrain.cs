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
}

// How a cheater fights. Push walks or teleports straight in, Flank swings round the side first, Hold takes an angle and pre-aims through the wall.
internal enum Tactic
{
    Push,
    Flank,
    Hold,
}

// What a cheater knows and wants. Outlives the BigBrain logic, which is rebuilt each time the layer takes over.
internal sealed class CheaterMind
{
    public int Looted,
        Capacity = UnityEngine.Random.Range(8, 21);
    public float NextPull = Time.time + UnityEngine.Random.Range(30f, 90f);

    // A looter's reason to run: the PMC (or you) it is keeping away from.
    public Player? Threat;

    // Nobody hunts in the first couple of minutes, and a troll waits longer still before it starts visiting.
    public float HuntAfter = CheaterRoster.RaidStart + UnityEngine.Random.Range(90f, 150f);
    public float StalkAfter = CheaterRoster.RaidStart + UnityEngine.Random.Range(180f, 360f);
    public float NextVisit;
    public int Visits,
        VisitsPlanned = UnityEngine.Random.Range(3, 8);
    public bool Provoked;
    public bool Stalked,
        Left,
        Wants;
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

    // A dead target ends the fight. The cheater takes a breather (loots, heals, walks on) on its normal AI, then roams a while before it hunts again.
    public void Settle(Player bot)
    {
        if (Target is not { } t || t.HealthController?.IsAlive == true)
            return;
        Kills++;
        var aim = CheaterRoster.AimOf(bot);
        RestUntil = Time.time + UnityEngine.Random.Range(aim.RestMin, aim.RestMax);
        CheaterRoster.Log(
            $"{bot.Profile.Nickname} got {t.Profile.Nickname} ({Kills} so far), resting {RestUntil - Time.time:0}s."
        );
        EndHunt(bot);
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
        if (Time.time >= mind.NextCheck)
        {
            mind.NextCheck = Time.time + 1;
            bool wanted = CheaterLogic.Wants(BotOwner, bot, mind);
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
        peeking;
    private int strafeSide = 1,
        peeks,
        peekLimit = 2;
    private Vector3? teleportTo;
    private readonly List<Vector3> used = new();
    private Player? seen,
        engaged;
    private Vector3 destination,
        stuckFrom;
    private float nextStuckCheck;
    private AntiYaw? antiYaw;
    private AntiPitch antiPitch;
    private float spinRate,
        nextYaw,
        nextPitch;
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
        if (mind.Left)
            return false;
        mind.Settle(bot);
        // A looter keeps away from PMCs and you, and only fights them when it is cornered or shot at.
        if (CheaterRoster.TypeOf(bot) == CheaterType.Looter)
        {
            var threat = Threat(owner, bot, mind);
            if (threat != mind.Threat && threat is not null)
                CheaterRoster.Log($"{bot.Profile.Nickname} keeps away from {threat.Profile.Nickname}.");
            mind.Threat = threat;
            if (threat is not null)
            {
                float d = Vector3.Distance(threat.Position, bot.Position);
                bool shot = owner.Memory?.GoalEnemy is { } hit && Time.time - hit.LastGetHitTime < 6;
                if (!shot && d > 20)
                    return true;
                mind.Threat = null;
                mind.Track(threat, threat.Position);
                return true;
            }
        }

        if (Stalking(bot, mind))
            return true;
        // A fight its own AI walked into is fought with the cheats, not by SAIN.
        if (InFight(owner, bot, mind) is { } fighting)
        {
            if (mind.Target != fighting)
                CheaterRoster.Log($"{bot.Profile.Nickname} fights {fighting.Profile.Nickname} with its cheats.");
            mind.Track(fighting, owner.Memory.GoalEnemy.EnemyLastPosition);
            return true;
        }

        if (Time.time < mind.RestUntil)
            return false;
        if (Knows(bot, mind))
            return true;
        var type = CheaterRoster.TypeOf(bot);
        // Looters do not go looking for fights, a closet cheater without ESP has nothing to go on, and between hunts it roams like anyone.
        if (type == CheaterType.Looter || type == CheaterType.Closet && !CheaterRoster.Has(bot, Cheat.Esp) || !mind.Hungry(bot))
            return false;
        mind.Target = FindTarget(owner, bot, mind);
        // Nobody worth it in range: roam a little before looking again.
        if (mind.Target is null)
            mind.NextHunt = Time.time + UnityEngine.Random.Range(10f, 20f);
        return mind.Target is not null;
    }

    private static bool Stalking(Player bot, CheaterMind mind) =>
        CheaterRoster.Has(bot, Cheat.Stalker)
        && !mind.Stalked
        && Time.time >= mind.StalkAfter
        && Me is { } me
        && me.HealthController?.IsAlive == true;

    // The nearest PMC or human it knows about within 70 m (95 m once it is already keeping away). Scavs are not worth running from.
    private static Player? Threat(BotOwner owner, Player bot, CheaterMind mind)
    {
        bool esp = CheaterRoster.Has(bot, Cheat.Esp);
        float range = mind.Threat is not null ? 95 : 70;
        Player? best = null;
        float nearest = range * range;
        foreach (var p in World.AllAlivePlayersList)
        {
            if (!Valid(bot, p) || p.IsAI && p.Side == EPlayerSide.Savage)
                continue;
            float d = (p.Position - bot.Position).sqrMagnitude;
            if (d >= nearest)
                continue;
            if (!esp && p != mind.Threat && (object?)owner.Memory?.GoalEnemy?.Person != p && !Clear(bot, p))
                continue;
            nearest = d;
            best = p;
        }

        return best;
    }

    // Only a live fight: seen lately or shot by it lately. SAIN keeps old enemies in memory long after the fight ends.
    // A target it gave up on only counts while it is actually shooting at the cheater.
    private static Player? InFight(BotOwner owner, Player bot, CheaterMind mind) =>
        owner.Memory?.GoalEnemy is { } enemy
        && enemy.Person is Player fighting
        && Valid(bot, fighting)
        && (enemy.IsVisible || Time.time - enemy.PersonalLastSeenTime < 10 || Time.time - enemy.LastGetHitTime < 10)
        && (!mind.Ignores(fighting) || Time.time - enemy.LastGetHitTime < 5)
            ? fighting
            : null;

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
            if (!Valid(bot, p) || mind.Ignores(p) || !mind.Worth(bot, p) || type == CheaterType.Troll && p != me)
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
        && (string.IsNullOrEmpty(bot.GroupId) || p.GroupId != bot.GroupId);

    private static bool Clear(Player from, Player to) =>
        !Physics.Linecast(
            from.PlayerBones.Head.position,
            to.PlayerBones.Head.position,
            LayersMaskController.HighPolyWithTerrainMask
        );

    public override void Start()
    {
        BotOwner.Mover?.Stop();
        var mind = Mind;
        Enter(
            mind.Threat is not null ? CheaterPhase.Evade
            : Stalking(Bot, mind) ? CheaterPhase.Stalk
            : CheaterPhase.Fight
        );
    }

    public override void Stop()
    {
        Trigger(false);
        BotOwner.SetPose(1f);
        Bot.MovementContext?.SetTilt(0f);
        CheaterRoster.Aim(Bot, null);
        CheaterRoster.SetDriven(Bot, false);
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
        settled = false;
        Trigger(false);
        CheaterRoster.Aim(Bot, null);
        CheaterRoster.Log($"{Bot.Profile.Nickname} now {to}.");
    }

    public override void Update(CustomLayer.ActionData data)
    {
        if (Mind.Left || !Singleton<GameWorld>.Instantiated)
            return;
        // The layer decides once a second whether a looter runs or fights.
        if (Mind.Threat is not null != (phase == CheaterPhase.Evade))
            Enter(Mind.Threat is not null ? CheaterPhase.Evade : CheaterPhase.Fight);
        switch (phase)
        {
            case CheaterPhase.Evade:
                Evade();
                break;
            case CheaterPhase.Stalk:
                Stalk();
                break;
            default:
                Fight();
                break;
        }
    }

    // Runs to cover away from the threat, then crouches there watching it. Speedhackers get away faster.
    private void Evade()
    {
        if (Mind.Threat is not { } threat)
            return;
        Trigger(false);
        CheaterRoster.Aim(Bot, null);
        if (angle is null || Time.time > tacticUntil || (threat.Position - angle.Value).sqrMagnitude < 25 * 25)
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

    // 30 to 50 m away from the threat, preferring spots it cannot see.
    private Vector3 FleeSpot(Player threat)
    {
        var mask = LayersMaskController.HighPolyWithTerrainMask;
        var away = Bot.Position - threat.Position;
        away.y = 0;
        if (away.sqrMagnitude < .01f)
            away = Bot.LookDirection;
        away.Normalize();
        var eye = threat.PlayerBones.Head.position;
        Vector3 best = Bot.Position + away * 10;
        float bestScore = float.MinValue;
        for (int i = -3; i <= 3; i++)
        {
            foreach (float radius in new[] { 30f, 50f })
            {
                if (
                    !NavMesh.SamplePosition(
                        Bot.Position + Quaternion.Euler(0, i * 25, 0) * away * radius,
                        out var hit,
                        4f,
                        NavMesh.AllAreas
                    )
                )
                    continue;
                var p = hit.position;
                float score = Vector3.Distance(p, threat.Position) * .05f - Mathf.Abs(i) * .3f;
                if (Physics.Linecast(eye, p + Vector3.up * 1.2f, mask))
                    score += 4;
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

        Bot.Teleport(spot);
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
            Bot.Teleport(spot);
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
        BotOwner.SetPose(1f);
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
            // Nothing to do. The layer hands the bot back to its normal AI at its next check.
            Trigger(false);
            CheaterRoster.Aim(Bot, null);
            return;
        }

        if (prey != engaged)
        {
            engaged = prey;
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
        else
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

        if (Has(Cheat.Fly))
        {
            // Hovers off to the side and above, out of reach.
            var side = known - Bot.Position;
            side.y = 0;
            if (side.sqrMagnitude > .01f)
                Bot.Teleport(known - side.normalized * 6f + Vector3.up * 4f);
            CheaterRoster.Suspect(Bot, Time.deltaTime);
        }
        else
        {
            // A teleport has a tell: a shout first, then it lands behind the target and needs a moment before it can shoot.
            if (teleportTo is { } jump && Time.time >= tellUntil)
            {
                teleportTo = null;
                Bot.Teleport(jump);
                settleUntil = Time.time + UnityEngine.Random.Range(.6f, 1f);
                if (tactic != Tactic.Push)
                    Switch(Tactic.Push);
                CheaterRoster.Suspect(Bot, 3);
                CheaterRoster.Log($"{Bot.Profile.Nickname} teleported behind {prey.Profile.Nickname}.");
            }
            else if (
                teleportTo is null
                && Has(Cheat.FightTeleport)
                && (clear || esp)
                && distance is > 10 and < 80
                && Time.time >= nextTeleport
            )
            {
                nextTeleport = Time.time + UnityEngine.Random.Range(10f, 22f);
                if (BehindSpot(prey) is { } spot)
                {
                    teleportTo = spot;
                    tellUntil = Time.time + .6f;
                    Bot.Speaker?.Play(EPhraseTrigger.Gogogo, Bot.HealthStatus | ETagStatus.Combat, true);
                }
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
        }

        Look(prey, known, clear, esp, distance);

        // Only with a clear line, once it has reacted and turned onto the target. Never at a remembered spot through a wall.
        if (
            clear
            && distance < 150
            && Time.time - mind.FirstSeen >= react
            && Time.time >= settleUntil
            && Facing(prey)
            && Moment(prey, mind)
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
        if (!clear && distance < 10 && Type == CheaterType.Hunter)
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

    // Lands behind the target, out of its view, a few metres off.
    private Vector3? BehindSpot(Player prey)
    {
        var back = -prey.LookDirection;
        back.y = 0;
        if (back.sqrMagnitude < .01f)
            return null;
        return Near(
            prey.Position
                + Quaternion.Euler(0, UnityEngine.Random.Range(-40f, 40f), 0) * back.normalized * UnityEngine.Random.Range(5f, 9f)
        );
    }

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
                    spin = Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg + UnityEngine.Random.Range(25f, 65f) * (jitterLeft ? -1 : 1);
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
        float seconds = UnityEngine.Random.Range(aim.ReactMin, aim.ReactMax) * CheaterRoster.ReactionScale;
        return tactic == Tactic.Hold && settled ? seconds * .65f : seconds;
    }

    private bool Facing(Player prey)
    {
        if (Has(Cheat.Spinbot))
            return true;
        var toward = prey.PlayerBones.Head.position - Bot.PlayerBones.Head.position;
        return Vector3.Angle(Bot.LookDirection, toward) <= Mathf.Max(CheaterRoster.AimOf(Bot).Snap, 6);
    }

    // Bunny hops. Behind close cover with headroom it jumps far higher than anyone can, so it never pops into a ceiling.
    private void Hop(Player prey, bool clear, float distance)
    {
        if (Time.time < nextJump || distance > 90 || tactic == Tactic.Hold && settled)
            return;
        nextJump = Time.time + UnityEngine.Random.Range(1.1f, 2f);
        Bot.MovementContext?.TryJump();
        if (
            !clear
            && distance < 25
            && !Physics.Raycast(Bot.Position + Vector3.up * 1.8f, Vector3.up, 1.6f, LayersMaskController.HighPolyWithTerrainMask)
        )
        {
            Bot.Teleport(Bot.Position + Vector3.up * UnityEngine.Random.Range(2f, 3f));
            CheaterRoster.Suspect(Bot, 2);
            CheaterRoster.Log($"{Bot.Profile.Nickname} jumped over cover at {prey.Profile.Nickname}.");
        }
    }

    // Repaths only when the goal moved, since every new path makes the bot slow down and start again. Never sprints: a spinning body drops it.
    private void Chase(Vector3 goal)
    {
        BotOwner.SetPose(1f);
        BotOwner.SetTargetMoveSpeed(1f);
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

    private void Burst()
    {
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

    private static Vector3? Near(Vector3 point) =>
        NavMesh.SamplePosition(point, out var hit, 4f, NavMesh.AllAreas) ? hit.position : null;
}
