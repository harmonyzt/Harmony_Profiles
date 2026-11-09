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
    Vacuum,
    Stalk,
    Fight
}

// How a cheater fights. Push walks or teleports straight in, Flank swings round the side first, Hold takes an angle and pre-aims through the wall.
internal enum Tactic
{
    Push,
    Flank,
    Hold
}

// What a cheater knows and wants. Outlives the BigBrain logic, which is rebuilt each time the layer takes over.
internal sealed class CheaterMind
{
    public int Looted, Capacity = UnityEngine.Random.Range(8, 21);
    public bool Stalked, Left, Wants;
    public float NextCheck, LastSeenTime = -100, FirstSeen;
    public Player? Target;
    public Vector3 LastSeenPosition;

    private static readonly Dictionary<Player, CheaterMind> minds = new();

    public static CheaterMind Of(Player bot)
    {
        if (!minds.TryGetValue(bot, out var mind)) minds[bot] = mind = new CheaterMind();
        return mind;
    }

    public static void Clear() => minds.Clear();

    public void Track(Player target, Vector3 position)
    {
        if (Target != target) FirstSeen = 0;
        Target = target;
        LastSeenPosition = position;
        LastSeenTime = Time.time;
    }
}

// Takes the bot over while it has a cheater's goal: loot to vacuum, you to stalk, a fight, or a target its ESP found. Otherwise patrol mods move it as usual.
internal sealed class CheaterLayer(BotOwner botOwner, int priority) : CustomLayer(botOwner, priority)
{
    public override string GetName() => "PbtCheater";

    public override bool IsActive()
    {
        if (!CheaterRoster.Behaviour || BotOwner?.GetPlayer is not { } bot || bot.HealthController?.IsAlive != true || !CheaterRoster.IsCheater(bot)) return false;
        var mind = CheaterMind.Of(bot);
        if (Time.time >= mind.NextCheck)
        {
            mind.NextCheck = Time.time + 1;
            bool wanted = CheaterLogic.Wants(BotOwner, bot, mind);
            if (wanted != mind.Wants) CheaterRoster.Log($"{bot.Profile.Nickname} {(wanted ? "takes over from its AI" : "hands back to its AI")}.");
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

    // A closet cheater only sets up on targets this close, so it still patrols most of the raid.
    private const float ClosetRange = 120;

    private CheaterPhase phase;
    private Tactic tactic;
    private float phaseSince, stalkFor, next, nextTeleport, nextJump, nextPath, nextSight, triggerUntil, triggerNext, spin, tacticUntil, nextPeek, peekUntil;
    private bool firing, holding, sees, settled;
    private Player? seen, engaged;
    private Vector3 destination;
    private Vector3? angle, flank;

    private Player Bot => BotOwner.GetPlayer;
    private CheaterMind Mind => CheaterMind.Of(Bot);
    private static GameWorld World => Singleton<GameWorld>.Instance;
    private static Player? Me => Singleton<GameWorld>.Instantiated ? World.MainPlayer : null;
    private bool Has(Cheat cheat) => CheaterRoster.Has(Bot, cheat);
    private CheaterType Type => CheaterRoster.TypeOf(Bot);

    // The type's goal, checked about once a second by the layer.
    internal static bool Wants(BotOwner owner, Player bot, CheaterMind mind)
    {
        if (mind.Left) return false;
        if (CheaterRoster.Has(bot, Cheat.Vacuum) && mind.Looted < mind.Capacity) return true;
        if (CheaterRoster.Has(bot, Cheat.Stalker) && !mind.Stalked && Me is { } me && me.HealthController?.IsAlive == true) return true;
        // A fight its own AI walked into is fought with the cheats, not by SAIN.
        if (InFight(owner, bot) is { } fighting)
        {
            if (mind.Target != fighting) CheaterRoster.Log($"{bot.Profile.Nickname} fights {fighting.Profile.Nickname} with its cheats.");
            mind.Track(fighting, owner.Memory.GoalEnemy.EnemyLastPosition);
            return true;
        }

        var type = CheaterRoster.TypeOf(bot);
        // Looters do not go looking for fights, and a closet cheater without ESP has nothing to go on.
        if (type == CheaterType.Looter || type == CheaterType.Closet && !CheaterRoster.Has(bot, Cheat.Esp)) return false;
        if (Knows(bot, mind)) return true;
        mind.Target = FindTarget(owner, bot, mind);
        return mind.Target is not null;
    }

    // Only a live fight: seen lately or shot by it lately. SAIN keeps old enemies in memory long after the fight ends.
    private static Player? InFight(BotOwner owner, Player bot) =>
        owner.Memory?.GoalEnemy is { } enemy && enemy.Person is Player fighting && Valid(bot, fighting)
        && (enemy.IsVisible || Time.time - enemy.PersonalLastSeenTime < 10 || Time.time - enemy.LastGetHitTime < 10)
            ? fighting
            : null;

    private static bool Knows(Player bot, CheaterMind mind)
    {
        if (mind.Target is not { } t || t.HealthController?.IsAlive != true) return false;
        if (!CheaterRoster.Has(bot, Cheat.Esp)) return Time.time - mind.LastSeenTime < Memory;
        // A closet cheater lets a target go once it is out of range, so it does not walk across the map to it.
        return CheaterRoster.TypeOf(bot) != CheaterType.Closet || (t.Position - bot.Position).sqrMagnitude < ClosetRange * ClosetRange * 1.5f;
    }

    // ESP sees everyone in range. Without it, only what the bot is fighting or can see right now. Other cheaters are fair game too.
    private static Player? FindTarget(BotOwner owner, Player bot, CheaterMind mind)
    {
        bool esp = CheaterRoster.Has(bot, Cheat.Esp);
        var type = CheaterRoster.TypeOf(bot);
        float range = type switch
        {
            CheaterType.Hunter => 500,
            CheaterType.Closet => ClosetRange,
            _ => 300
        };
        Player? you = null, other = null, rival = null;
        float nearest = float.MaxValue, nearestRival = float.MaxValue;
        var me = Me;

        if (!esp && owner.Memory?.GoalEnemy is { } enemy && enemy.Person is Player fighting && Valid(bot, fighting) && Time.time - enemy.TimeLastSeen < Memory)
        {
            mind.Track(fighting, enemy.EnemyLastPosition);
            return fighting;
        }

        foreach (var p in World.AllAlivePlayersList)
        {
            if (!Valid(bot, p)) continue;
            float d = (p.Position - bot.Position).sqrMagnitude;
            if (d > range * range || !esp && (d > 150 * 150 || !Clear(bot, p))) continue;
            if (p == me) you = p;
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
        if (rival is not null && UnityEngine.Random.value < CheaterRoster.Rivalry) pick = rival;
        else if (you is not null && (other is null || UnityEngine.Random.value < CheaterRoster.TargetsYou)) pick = you;
        else pick = other ?? rival ?? you;
        if (pick is null) return null;

        mind.Track(pick, pick.Position);
        CheaterRoster.Log($"{bot.Profile.Nickname} goes after {pick.Profile.Nickname}{(esp ? " (ESP)" : "")}{(pick == rival ? ", another cheater" : "")}.");
        if (pick == rival) Provoke(bot, rival);
        return pick;
    }

    // The other cheater notices and turns on it, unless it is already busy with someone it can see.
    private static void Provoke(Player bot, Player rival)
    {
        var theirs = CheaterMind.Of(rival);
        if (theirs.Target is { } busy && busy != bot && busy.HealthController?.IsAlive == true && Time.time - theirs.LastSeenTime < 5) return;
        theirs.Track(bot, bot.Position);
        theirs.NextCheck = 0;
        try
        {
            if (rival.AIData?.BotOwner?.BotsGroup is { } group && !group.IsEnemy(bot)) group.AddEnemy(bot, EBotEnemyCause.addPlayer);
        }
        catch (Exception ex)
        {
            CheaterRoster.Log($"Rivalry enemy add failed: {ex.Message}");
        }
    }

    private static bool Valid(Player bot, Player? p) =>
        p is not null && p != bot && p.HealthController?.IsAlive == true && (string.IsNullOrEmpty(bot.GroupId) || p.GroupId != bot.GroupId);

    private static bool Clear(Player from, Player to) =>
        !Physics.Linecast(from.PlayerBones.Head.position, to.PlayerBones.Head.position, LayersMaskController.HighPolyWithTerrainMask);

    public override void Start()
    {
        BotOwner.Mover?.Stop();
        var mind = Mind;
        Enter(Has(Cheat.Vacuum) && mind.Looted < mind.Capacity ? CheaterPhase.Vacuum : Has(Cheat.Stalker) && !mind.Stalked ? CheaterPhase.Stalk : CheaterPhase.Fight);
    }

    public override void Stop()
    {
        Trigger(false);
        BotOwner.SetPose(1f);
        CheaterRoster.Aim(Bot, null);
        CheaterRoster.SetDriven(Bot, false);
    }

    public override void BuildDebugText(StringBuilder text) =>
        text.AppendLine($"PBT {Type} cheater: {phase}/{tactic}, {CheaterRoster.CheatsOf(Bot)}, target {Mind.Target?.Profile.Nickname ?? "none"}");

    private void Enter(CheaterPhase to)
    {
        phase = to;
        phaseSince = Time.time;
        stalkFor = UnityEngine.Random.Range(20f, 50f);
        next = 0;
        engaged = null;
        Trigger(false);
        CheaterRoster.Aim(Bot, null);
        CheaterRoster.Log($"{Bot.Profile.Nickname} now {to}.");
    }

    public override void Update(CustomLayer.ActionData data)
    {
        if (Mind.Left || !Singleton<GameWorld>.Instantiated) return;
        switch (phase)
        {
            case CheaterPhase.Vacuum:
                Vacuum();
                break;
            case CheaterPhase.Stalk:
                Stalk();
                break;
            default:
                Fight();
                break;
        }
    }

    // Teleports to the best loose loot on the map, takes it, and extracts once the bag is "full".
    private void Vacuum()
    {
        if (Time.time < next) return;
        next = Time.time + UnityEngine.Random.Range(.5f, 1.2f);
        var mind = Mind;
        if (mind.Looted >= mind.Capacity)
        {
            mind.Left = true;
            CheaterRoster.Log($"{Bot.Profile.Nickname} vacuumed {mind.Looted} items and extracted.");
            BotOwner.LeaveData?.RemoveFromMap();
            return;
        }

        LootItem? best = null;
        int bestPrice = 0;
        var loot = World.LootItems;
        for (int i = 0; i < loot.Count; i++)
        {
            var item = loot.GetByIndex(i);
            if (item?.Item is null || item.Item.QuestItem) continue;
            int price = item.Item.Template.CreditsPrice * Math.Max(1, item.Item.StackObjectsCount);
            if (price > bestPrice)
            {
                best = item;
                bestPrice = price;
            }
        }

        if (best is null)
        {
            mind.Looted = mind.Capacity;
            return;
        }

        Bot.Teleport(best.transform.position + Vector3.up * .3f);
        CheaterRoster.Suspect(Bot, 1.5f);
        CheaterRoster.Log($"{Bot.Profile.Nickname} took {best.Item.TemplateId} ({bestPrice:N0} RUB).");
        World.DestroyLoot(best);
        mind.Looted++;
    }

    // Pops up in front of you and stares, never shoots, then turns on you.
    private void Stalk()
    {
        var mind = Mind;
        if (Me is not { } me || me.HealthController?.IsAlive != true || Time.time - phaseSince > stalkFor)
        {
            mind.Stalked = true;
            Enter(CheaterPhase.Fight);
            if (Me is { } you && you.HealthController?.IsAlive == true) mind.Track(you, you.Position);
            return;
        }

        LookAt(me.PlayerBones.Head.position);
        if (Time.time < next) return;
        next = Time.time + UnityEngine.Random.Range(2f, 6f);
        var forward = me.LookDirection;
        forward.y = 0;
        if (Near(me.Position + forward.normalized * UnityEngine.Random.Range(2.5f, 5f)) is { } spot)
        {
            Bot.Teleport(spot);
            CheaterRoster.Suspect(Bot, 1);
        }
    }

    // Rage and trolls just come at you. Hunters mostly swing round first. Closet cheaters and looters hold an angle like a careful player, except their crosshair follows you through the wall.
    private Tactic PickTactic()
    {
        float roll = UnityEngine.Random.value;
        return Type switch
        {
            CheaterType.Closet or CheaterType.Looter => roll < .85f ? Tactic.Hold : Tactic.Flank,
            CheaterType.Hunter => roll < .5f ? Tactic.Flank : roll < .8f ? Tactic.Push : Tactic.Hold,
            _ => Tactic.Push
        };
    }

    private void Switch(Tactic to)
    {
        tactic = to;
        angle = flank = null;
        settled = holding = false;
        tacticUntil = Time.time + UnityEngine.Random.Range(20f, 40f);
        BotOwner.SetPose(1f);
        CheaterRoster.Log($"{Bot.Profile.Nickname} {to switch { Tactic.Hold => "holds an angle on", Tactic.Flank => "flanks", _ => "pushes" }} {Mind.Target?.Profile.Nickname}.");
    }

    private void Fight()
    {
        var mind = Mind;
        bool esp = Has(Cheat.Esp);
        if (!Knows(Bot, mind))
        {
            if (mind.Target is not null) CheaterRoster.Log($"{Bot.Profile.Nickname} lost track of {mind.Target.Profile.Nickname}.");
            mind.Target = FindTarget(BotOwner, Bot, mind);
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
            if (mind.FirstSeen <= 0) mind.FirstSeen = Time.time;
        }
        else mind.FirstSeen = 0;

        // ESP knows where the target is now. Without it the bot goes where it last saw it, like any bot.
        var known = esp ? prey.Position : mind.LastSeenPosition;
        float distance = Vector3.Distance(known, Bot.Position);

        if (Has(Cheat.Fly))
        {
            // Hovers off to the side and above, out of reach.
            var side = known - Bot.Position;
            side.y = 0;
            if (side.sqrMagnitude > .01f) Bot.Teleport(known - side.normalized * 6f + Vector3.up * 4f);
            CheaterRoster.Suspect(Bot, Time.deltaTime);
        }
        else
        {
            if (Has(Cheat.FightTeleport) && (clear || esp) && distance < 80 && Time.time >= nextTeleport)
            {
                nextTeleport = Time.time + UnityEngine.Random.Range(8f, 20f);
                var around = Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0) * Vector3.forward;
                if (Near(known + around * UnityEngine.Random.Range(4f, 8f)) is { } spot)
                {
                    Bot.Teleport(spot);
                    if (tactic != Tactic.Push) Switch(Tactic.Push);
                    CheaterRoster.Suspect(Bot, 3);
                    CheaterRoster.Log($"{Bot.Profile.Nickname} teleported next to {prey.Profile.Nickname}.");
                }
            }

            switch (tactic)
            {
                case Tactic.Hold:
                    Hold(prey, known, clear, distance);
                    break;
                case Tactic.Flank:
                    Flank(known, clear, distance);
                    break;
                default:
                    Push(known, clear, esp, distance);
                    break;
            }

            if (Has(Cheat.BunnyHop)) Hop(prey, clear, distance);
        }

        Look(prey, known, clear, esp, distance);

        // Only with a clear line, and only once it has had a moment to react. Never at a remembered spot through a wall.
        if (clear && distance < 150 && Time.time - mind.FirstSeen >= Reaction())
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

    // Stops once it has a shot up close and only moves again when the shot is gone or the target got away.
    private void Push(Vector3 known, bool clear, bool esp, float distance)
    {
        if (holding && (!clear || distance > 35)) holding = false;
        else if (!holding && clear && distance < 20)
        {
            holding = true;
            BotOwner.Mover?.Stop();
        }

        if (holding) return;
        // At the last known spot with nothing in sight: stands and looks around until it forgets.
        if (!esp && !clear && distance < 3) BotOwner.Mover?.Stop();
        else Chase(known);
    }

    // Swings out to one side of the target, then pushes from there.
    private void Flank(Vector3 known, bool clear, float distance)
    {
        if (clear && distance < 30 || Time.time > tacticUntil || distance < 12)
        {
            Switch(Tactic.Push);
            return;
        }

        if (flank is null)
        {
            var toward = known - Bot.Position;
            toward.y = 0;
            var side = Vector3.Cross(Vector3.up, toward.normalized) * (UnityEngine.Random.value < .5f ? 1 : -1);
            flank = Near(known + side * Mathf.Clamp(distance * .6f, 15, 35) - toward.normalized * 5) ?? known;
        }

        if ((flank.Value - Bot.Position).sqrMagnitude < 3 * 3) Switch(Tactic.Push);
        else Chase(flank.Value);
    }

    // Takes cover with a view of where the target will come from, crouches and waits pre-aimed, standing now and then to peek. Moves up if nobody comes.
    private void Hold(Player prey, Vector3 known, bool clear, float distance)
    {
        // Too close to sit still, or a target in plain view that it is not going to get a better shot at.
        if (!clear && distance < 10)
        {
            Switch(Tactic.Push);
            return;
        }

        if (angle is null || !clear && Time.time > tacticUntil)
        {
            bool moveUp = angle is not null;
            angle = FindAngle(prey, known, moveUp);
            tacticUntil = Time.time + UnityEngine.Random.Range(20f, 40f);
            settled = false;
            if (moveUp) CheaterRoster.Log($"{Bot.Profile.Nickname} moves up to a closer angle.");
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
            nextPeek = Time.time + UnityEngine.Random.Range(3f, 6f);
        }

        // Stands to shoot or peek, crouched behind the cover otherwise.
        if (!clear && Time.time >= nextPeek)
        {
            peekUntil = Time.time + UnityEngine.Random.Range(1f, 2f);
            nextPeek = peekUntil + UnityEngine.Random.Range(4f, 8f);
        }

        BotOwner.SetPose(clear || Time.time < peekUntil ? 1f : .45f);
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
                if (!NavMesh.SamplePosition(probe, out var hit, 2f, NavMesh.AllAreas)) continue;
                var p = hit.position;
                float toTarget = Vector3.Distance(p, known);
                if (toTarget < 8) continue;
                var low = p + Vector3.up * 1f;
                var high = p + Vector3.up * 1.55f;
                var facing = known - p;
                facing.y = 0;
                float score = 0;
                if (Physics.Linecast(eye, low, mask)) score += 3;
                if (!Physics.Linecast(eye, high, mask)) score += 3;
                if (Physics.Raycast(low, facing.normalized, 2f, mask)) score += 2;
                score -= Vector3.Distance(from, p) * .08f + Mathf.Abs(toTarget - 35) * .02f;
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
        {
            spin = (spin + 1800f * Time.deltaTime) % 360f;
            BotOwner.Steering?.LookToPoint(Bot.Position + Vector3.up * 1.5f + Quaternion.Euler(0, spin, 0) * Vector3.forward * 5f, 3600f);
            CheaterRoster.Suspect(Bot, Time.deltaTime * .3f);
        }
        else if (clear || esp) LookAt(prey.PlayerBones.Head.position);
        else if (distance < 3)
        {
            spin = (spin + 90f * Time.deltaTime) % 360f;
            BotOwner.Steering?.LookToPoint(Bot.Position + Vector3.up * 1.5f + Quaternion.Euler(0, spin, 0) * Vector3.forward * 5f, 180f);
        }
        else LookAt(known + Vector3.up * 1.55f);
    }

    // Pre-aimed from a held angle there is almost nothing to react to.
    private float Reaction()
    {
        if (tactic == Tactic.Hold && settled) return .1f;
        return Type switch
        {
            CheaterType.Rage => .05f,
            CheaterType.Hunter => .2f,
            _ => .35f
        };
    }

    // Bunny hops. Behind close cover with headroom it jumps far higher than anyone can, so it never pops into a ceiling.
    private void Hop(Player prey, bool clear, float distance)
    {
        if (Time.time < nextJump || distance > 90 || tactic == Tactic.Hold && settled) return;
        nextJump = Time.time + UnityEngine.Random.Range(1.1f, 2f);
        Bot.MovementContext?.TryJump();
        if (!clear && distance < 25 && !Physics.Raycast(Bot.Position + Vector3.up * 1.8f, Vector3.up, 1.6f, LayersMaskController.HighPolyWithTerrainMask))
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
        if (Time.time < nextPath && (goal - destination).sqrMagnitude < 4 * 4) return;
        nextPath = Time.time + 5f;
        destination = goal;
        BotOwner.GoToPoint(destination, false);
        BotOwner.Mover?.Sprint(false);
    }

    // Checked a few times a second, out to 200 m; a linecast every frame flickers on foliage.
    private bool Sees(Player p)
    {
        if (p == seen && Time.time < nextSight) return sees;
        seen = p;
        nextSight = Time.time + .25f;
        return sees = (p.Position - Bot.Position).sqrMagnitude < 200 * 200 && Clear(Bot, p);
    }

    private void LookAt(Vector3 point) => BotOwner.Steering?.LookToPoint(point, 3600f);

    private void Burst()
    {
        float now = Time.time;
        if (firing && now >= triggerUntil)
        {
            Trigger(false);
            triggerNext = now + UnityEngine.Random.Range(.2f, .5f);
        }
        else if (!firing && now >= triggerNext)
        {
            Trigger(true);
            triggerUntil = now + UnityEngine.Random.Range(.35f, .7f);
        }
    }

    private void Trigger(bool pressed)
    {
        firing = pressed;
        try
        {
            (Bot.HandsController as Player.FirearmController)?.SetTriggerPressed(pressed);
        }
        catch
        {
        }
    }

    private static Vector3? Near(Vector3 point) => NavMesh.SamplePosition(point, out var hit, 4f, NavMesh.AllAreas) ? hit.position : null;
}
