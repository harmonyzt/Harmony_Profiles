using System.Reflection;
using HarmonyLib;

namespace PBT.Server;

internal static class ApbsGenerationContext
{
    [ThreadStatic] private static Scope? current;

    internal static void Install(Assembly apbs, Harmony owner)
    {
        var type = apbs.GetType("ProgressiveBotSystem.Models.RaidInformation")
            ?? throw new MissingMemberException("APBS raid context unavailable.");
        var contracts = new[]
        {
            ("RaidLocation", typeof(string), nameof(Location)),
            ("CurrentRaidLevel", typeof(int), nameof(Level)),
            ("NightTime", typeof(bool), nameof(Night))
        };
        foreach (var (name, returnType, patch) in contracts)
        {
            var getter = AccessTools.PropertyGetter(type, name);
            if (getter is null || !getter.IsStatic || getter.ReturnType != returnType || getter.GetParameters().Length != 0)
                throw new MissingMethodException("APBS context getter contract changed: " + name);
            owner.Patch(getter, prefix: new HarmonyMethod(typeof(ApbsGenerationContext), patch) { priority = Priority.First });
        }
    }

    internal static IDisposable Enter(string location, int level, bool night)
    {
        if (string.IsNullOrWhiteSpace(location) || level < 1) throw new ArgumentException("Invalid isolated APBS context.");
        var scope = new Scope(location, level, night, current);
        current = scope;
        return scope;
    }

    private static bool Location(ref string __result)
    {
        if (current is null) return true;
        __result = current.Location;
        return false;
    }

    private static bool Level(ref int __result)
    {
        if (current is null) return true;
        __result = current.Level;
        return false;
    }

    private static bool Night(ref bool __result)
    {
        if (current is null) return true;
        __result = current.Night;
        return false;
    }

    // APBS generation is synchronous. Scoped reads never write its shared raid fields or flow to other threads.
    private sealed class Scope(string location, int level, bool night, Scope? previous) : IDisposable
    {
        internal readonly string Location = location;
        internal readonly int Level = level;
        internal readonly bool Night = night;
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            current = previous;
            disposed = true;
        }
    }
}
