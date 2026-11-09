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
    // Above every SAIN layer (its highest is 99), so a cheater ignores SAIN while it cheats.
    private const int Priority = 150;

    internal static void Register() => BrainManager.AddCustomLayer(typeof(CheaterLayer), ["PmcBear", "PmcUsec", "PMC"], Priority);
}

internal sealed class CheaterLayer(BotOwner botOwner, int priority) : CustomLayer(botOwner, priority)
{
    public override string GetName() => "PbtCheater";

    public override bool IsActive() =>
        CheaterRoster.Behaviour && BotOwner?.GetPlayer is { } player && player.HealthController?.IsAlive == true && CheaterRoster.IsCheater(player);

    public override Action GetNextAction() => new(typeof(CheaterLogic), "cheating");

    public override bool IsCurrentActionEnding() => false;
}

internal sealed class CheaterLogic(BotOwner botOwner) : CustomLogic(botOwner)
{
    private CheaterMode mode;
    private float next, modeSince, triggerUntil, triggerNext, spin, nextPath, nextJump, nextSight, nextPrey;
    private bool sees, holding;
    private Player? seen;
    private Vector3 destination;
    private int looted, capacity;
    private bool firing, leaving;
    private Player? target;

    private Player Bot => BotOwner.GetPlayer;
    private static GameWorld World => Singleton<GameWorld>.Instance;
    private static Player? Me => Singleton<GameWorld>.Instantiated ? World.MainPlayer : null;

    public override void Start()
    {
        BotOwner.Mover?.Stop();
        Switch(CheaterRoster.ModeOf(Bot));
        if (CheaterRoster.Speeds(Bot)) CheaterRoster.Log($"{Bot.Profile.Nickname} is speedhacking.");
    }

    public override void Stop()
    {
        Trigger(false);
        CheaterRoster.Aim(Bot, null);
    }

    public override void BuildDebugText(StringBuilder text) => text.AppendLine($"PBT cheater: {mode}, looted {looted}/{capacity}");

    private void Switch(CheaterMode to)
    {
        mode = to;
        modeSince = Time.time;
        next = 0;
        target = null;
        capacity = UnityEngine.Random.Range(8, 21);
        Trigger(false);
        CheaterRoster.Aim(Bot, null);
        CheaterRoster.SetMode(Bot, to);
        CheaterRoster.Log($"{Bot.Profile.Nickname} now {to}.");
    }

    public override void Update(CustomLayer.ActionData data)
    {
        if (leaving || !Singleton<GameWorld>.Instantiated) return;
        switch (mode)
        {
            case CheaterMode.Vacuum:
                Vacuum();
                break;
            case CheaterMode.Stalker:
                Stalk();
                break;
            case CheaterMode.Hunter:
                Hunt();
                break;
            case CheaterMode.Hitman:
                Clear();
                break;
            case CheaterMode.Spinbot:
                Spin();
                break;
            case CheaterMode.Jumpshot:
                JumpShot();
                break;
        }
    }

    // Flies to the best loose loot on the map, takes it, and extracts once the bag is "full".
    private void Vacuum()
    {
        if (Time.time < next) return;
        next = Time.time + UnityEngine.Random.Range(.5f, 1.2f);
        if (looted >= capacity)
        {
            leaving = true;
            CheaterRoster.Log($"{Bot.Profile.Nickname} vacuumed {looted} items and extracted.");
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
            looted = capacity;
            return;
        }

        Bot.Teleport(best.transform.position + Vector3.up * .3f);
        CheaterRoster.Log($"{Bot.Profile.Nickname} took {best.Item.TemplateId} ({bestPrice:N0} RUB).");
        World.DestroyLoot(best);
        looted++;
    }

    // Pops up in front of you, stares, never shoots. Sooner or later most of them start shooting.
    private void Stalk()
    {
        if (Me is not { } me || me.HealthController?.IsAlive != true)
        {
            Switch(CheaterMode.Vacuum);
            return;
        }

        LookAt(me);
        if (Time.time - modeSince > UnityEngine.Random.Range(25f, 60f) && UnityEngine.Random.value < .4f)
        {
            Switch(CheaterMode.Hunter);
            return;
        }

        if (Time.time < next) return;
        next = Time.time + UnityEngine.Random.Range(2f, 6f);
        var forward = me.LookDirection;
        forward.y = 0;
        if (Near(me.Position + forward.normalized * UnityEngine.Random.Range(2.5f, 5f)) is { } spot) Bot.Teleport(spot);
    }

    // Shifts behind its target from anywhere on the map and fires full auto at its head. Half of them want you.
    private void Hunt()
    {
        if (target is null || target.HealthController?.IsAlive != true)
        {
            Trigger(false);
            target = Me is { } me && me.HealthController?.IsAlive == true && UnityEngine.Random.value < .5f ? me : Nearest(Bot);
            next = 0;
            if (target is null)
            {
                Switch(CheaterMode.Vacuum);
                return;
            }

            CheaterRoster.Log($"{Bot.Profile.Nickname} hunts {target.Profile.Nickname}.");
        }

        if (Time.time >= next || (Bot.Position - target.Position).sqrMagnitude > 30 * 30)
        {
            next = Time.time + 6;
            var back = -target.LookDirection;
            back.y = 0;
            if (Near(target.Position + back.normalized * UnityEngine.Random.Range(6f, 12f)) is { } spot)
            {
                Bot.Teleport(spot);
                CheaterRoster.Log($"{Bot.Profile.Nickname} teleported behind {target.Profile.Nickname}.");
            }
        }

        LookAt(target);
        CheaterRoster.Aim(Bot, target);
        Burst();
    }

    // Kills everyone but you, hovering over them.
    private void Clear()
    {
        if (target is null || target.HealthController?.IsAlive != true)
        {
            Trigger(false);
            target = Nearest(Me, false);

            if (target is null)
            {
                Switch(CheaterMode.Vacuum);
                return;
            }

            CheaterRoster.Log($"{Bot.Profile.Nickname} goes after {target.Profile.Nickname}.");
        }

        var side = target.Position - Bot.Position;
        side.y = 0;
        Bot.Teleport(target.Position - side.normalized * 6f + Vector3.up * 4f);
        LookAt(target);
        CheaterRoster.Aim(Bot, target);
        Burst();
    }

    // Walks at you spinning like a top and never misses once it sees you.
    private void Spin()
    {
        if (Prey() is not { } prey)
        {
            Switch(CheaterMode.Vacuum);
            return;
        }

        bool sees = Sees(prey);
        // Never sprints: the game drops a sprint whenever the body turns away, which made it stop and start.
        Chase(prey, false);
        spin = (spin + 1800f * Time.deltaTime) % 360f;
        BotOwner.Steering?.LookToPoint(Bot.Position + Vector3.up * 1.5f + Quaternion.Euler(0, spin, 0) * Vector3.forward * 5f, 3600f);
        Fire(prey, sees);
    }

    // Bunny hops at you. Behind cover it jumps far higher than anyone can and shoots over the wall.
    private void JumpShot()
    {
        if (Prey() is not { } prey)
        {
            Switch(CheaterMode.Vacuum);
            return;
        }

        bool sees = Sees(prey);
        float distance = Vector3.Distance(prey.Position, Bot.Position);
        // Stops once it has a shot up close and only moves again when the shot is gone or the target got away.
        if (holding && (!sees || distance > 35)) holding = false;
        else if (!holding && sees && distance < 20)
        {
            holding = true;
            BotOwner.Mover?.Stop();
        }

        if (!holding) Chase(prey, false);
        LookAt(prey);
        if (Time.time >= nextJump && distance < 90)
        {
            nextJump = Time.time + UnityEngine.Random.Range(1.1f, 2f);
            Bot.MovementContext?.TryJump();
            // The super jump is for cover right in front of it, and only with headroom, so it never pops into a ceiling.
            if (!sees && distance < 25 && !Physics.Raycast(Bot.Position + Vector3.up * 1.8f, Vector3.up, 1.6f, LayersMaskController.HighPolyWithTerrainMask))
            {
                Bot.Teleport(Bot.Position + Vector3.up * UnityEngine.Random.Range(2f, 3f));
                CheaterRoster.Log($"{Bot.Profile.Nickname} jumped over cover at {prey.Profile.Nickname}.");
            }
        }

        Fire(prey, sees);
    }

    // Whoever is nearest, you or a bot, kept for a few seconds so it does not flick between targets.
    private Player? Prey()
    {
        if (target is not null && target.HealthController?.IsAlive == true && Time.time < nextPrey) return target;
        nextPrey = Time.time + 4;
        var found = Nearest(Bot);
        if (found != target && found is not null) CheaterRoster.Log($"{Bot.Profile.Nickname} goes after {found.Profile.Nickname}.");
        return target = found;
    }

    private Player? Nearest(Player? exceptGroupOf, bool withYou = true)
    {
        Player? found = null;
        float best = float.MaxValue;
        foreach (var p in World.AllAlivePlayersList)
        {
            if (p is null || p == Bot || !p.IsAI && (!withYou || p != Me) || CheaterRoster.IsCheater(p)
                || exceptGroupOf is not null && !string.IsNullOrEmpty(exceptGroupOf.GroupId) && p.GroupId == exceptGroupOf.GroupId) continue;
            float d = (p.Position - Bot.Position).sqrMagnitude;
            if (d < best)
            {
                best = d;
                found = p;
            }
        }

        return found;
    }

    // Repaths only when the target moved, since every new path makes the bot slow down and start again.
    private void Chase(Player prey, bool sprint)
    {
        BotOwner.SetPose(1f);
        BotOwner.SetTargetMoveSpeed(1f);
        if (Time.time < nextPath && (prey.Position - destination).sqrMagnitude < 4 * 4) return;
        nextPath = Time.time + 5f;
        destination = prey.Position;
        BotOwner.GoToPoint(destination, false);
        BotOwner.Mover?.Sprint(sprint);
    }

    private void Fire(Player prey, bool sees)
    {
        if (sees && (prey.Position - Bot.Position).sqrMagnitude < 150 * 150)
        {
            CheaterRoster.Aim(Bot, prey);
            Burst();
        }
        else
        {
            CheaterRoster.Aim(Bot, null);
            Trigger(false);
        }
    }

    // Checked a few times a second; a linecast every frame flickers on foliage.
    private bool Sees(Player p)
    {
        if (p == seen && Time.time < nextSight) return sees;
        seen = p;
        nextSight = Time.time + .25f;
        return sees = !Physics.Linecast(Bot.PlayerBones.Head.position, p.PlayerBones.Head.position, LayersMaskController.HighPolyWithTerrainMask);
    }

    private void LookAt(Player p) => BotOwner.Steering?.LookToPoint(p.Position + Vector3.up * 1.55f, 3600f);

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
