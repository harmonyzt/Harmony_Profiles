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
    private float next, modeSince, triggerUntil, triggerNext;
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
    }

    public override void Stop() => Trigger(false);

    public override void BuildDebugText(StringBuilder text) => text.AppendLine($"PBT cheater: {mode}, looted {looted}/{capacity}");

    private void Switch(CheaterMode to)
    {
        mode = to;
        modeSince = Time.time;
        next = 0;
        target = null;
        capacity = UnityEngine.Random.Range(8, 21);
        Trigger(false);
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

    // Shifts behind you from anywhere on the map and fires full auto at your head.
    private void Hunt()
    {
        if (Me is not { } me || me.HealthController?.IsAlive != true)
        {
            Switch(CheaterMode.Vacuum);
            return;
        }

        if (Time.time >= next || (Bot.Position - me.Position).sqrMagnitude > 30 * 30)
        {
            next = Time.time + 6;
            var back = -me.LookDirection;
            back.y = 0;
            if (Near(me.Position + back.normalized * UnityEngine.Random.Range(6f, 12f)) is { } spot)
            {
                Bot.Teleport(spot);
                CheaterRoster.Log($"{Bot.Profile.Nickname} teleported behind you.");
            }
        }

        LookAt(me);
        Burst();
    }

    // Kills everyone but you, hovering over them.
    private void Clear()
    {
        if (target is null || target.HealthController?.IsAlive != true)
        {
            Trigger(false);
            target = null;
            float best = float.MaxValue;
            var me = Me;
            foreach (var p in World.AllAlivePlayersList)
            {
                if (p is null || p == Bot || p == me || !p.IsAI || CheaterRoster.IsCheater(p) || me is not null && p.GroupId == me.GroupId && !string.IsNullOrEmpty(me.GroupId)) continue;
                float d = (p.Position - Bot.Position).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    target = p;
                }
            }

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
        Burst();
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
