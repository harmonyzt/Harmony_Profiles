using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Bot;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Services.Bot;

namespace PBT.Server;

internal static class ApbsIntegration
{
    internal sealed class Scope(MongoId session, BotGenerationDetails details)
    {
        internal MongoId Session = session;
        internal BotGenerationDetails Details = details;
        internal Career? Career;
        internal int Level;
        internal int Experience;
        internal bool LevelConfirmed;
    }

    [ThreadStatic] internal static Scope? Current;
    private static MethodInfo? prefix;
    private static MethodInfo? relative;
    private static MethodInfo? levelMethod;
    private static MethodInfo? nameMethod;
    private static MethodInfo? editionMethod;
    private static MethodInfo? editionPrefix;
    private static Func<double, double, int, double, int>? sample;
    private const string Owner = "com.local.pbt.apbs";

    internal static void Install(Harmony harmony, Assembly apbs)
    {
        var type = apbs.GetType("ProgressiveBotSystem.Patches.GenerateBotLevel")
            ?? throw new MissingMethodException("APBS level patch type unavailable.");
        if (apbs.GetName().Version?.Major != 2) throw new NotSupportedException("APBS major version is not supported by this adapter.");
        prefix = AccessTools.Method(type, "Prefix", [typeof(RandomisedBotLevelResult).MakeByRefType(),
            typeof(MinMax<int>), typeof(BotGenerationDetails), typeof(BotBase)]);
        relative = AccessTools.Method(type, "GetRelativePmcBotLevelRange", [typeof(BotGenerationDetails), typeof(MinMax<int>), typeof(int)]);
        var choose = AccessTools.Method(type, "ChooseBotLevel", [typeof(double), typeof(double), typeof(int), typeof(double)]);
        if (prefix?.ReturnType != typeof(bool) || relative?.ReturnType != typeof(MinMax<int>) || choose?.ReturnType != typeof(int)
            || !prefix.IsStatic || !relative.IsStatic || !choose.IsStatic)
            throw new MissingMethodException("APBS synchronous level contract differs from the supported shape.");
        var calls = Calls(prefix);
        int rangeIndex = calls.FindIndex(m => m == relative), chooseIndex = calls.FindIndex(m => m == choose);
        if (rangeIndex < 0 || chooseIndex <= rangeIndex
            || !calls.Skip(chooseIndex + 1).Any(m => m.Name == "GetTierByLevel" && m.DeclaringType?.Assembly == apbs))
            throw new NotSupportedException("APBS tier calculation does not follow the supported range/level sequence.");
        levelMethod = AccessTools.Method(typeof(BotLevelGenerator), nameof(BotLevelGenerator.GenerateBotLevel));
        nameMethod = AccessTools.Method(typeof(BotNameService), nameof(BotNameService.GenerateUniqueBotNickname));
        var generate = AccessTools.Method(typeof(BotGenerator), "GenerateBot", [typeof(MongoId), typeof(BotBase), typeof(BotType), typeof(BotGenerationDetails)]);
        var prepare = AccessTools.Method(typeof(BotGenerator), nameof(BotGenerator.PrepareAndGenerateBot), [typeof(MongoId), typeof(BotGenerationDetails)]);
        if (levelMethod is null || nameMethod?.ReturnType != typeof(string) || generate is null || prepare is null)
            throw new MissingMethodException("Native generation contract unavailable.");
        var nativeCalls = Calls(generate);
        if (nativeCalls.FindIndex(m => m == levelMethod) < 0
            || nativeCalls.FindIndex(m => m == nameMethod) <= nativeCalls.FindIndex(m => m == levelMethod)
            || Harmony.GetPatchInfo(generate)?.Transpilers.Count > 0)
            throw new NotSupportedException("Native generation/name ordering differs or has a transpiler owner.");
        if (!OwnershipMatches()) throw new NotSupportedException("APBS is inactive or another mod owns the level/name contract.");
        editionMethod = AccessTools.Method(typeof(BotGenerator), "SetRandomisedGameVersionAndCategory", [typeof(Info)]);
        editionPrefix = AccessTools.Method(apbs.GetType("ProgressiveBotSystem.Patches.SetRandomisedGameVersionAndCategoryPatch"), "Prefix");
        if (editionMethod?.ReturnType != typeof(string) || editionPrefix is null
            || Harmony.GetPatchInfo(editionMethod)?.Prefixes.Any(p => p.PatchMethod == editionPrefix) != true)
            throw new MissingMethodException("APBS account-edition contract unavailable.");
        if (!OwnershipMatches()) throw new NotSupportedException("Account-edition generation has an unsupported owner.");
        sample = choose.CreateDelegate<Func<double, double, int, double, int>>();
        harmony.Patch(prepare, prefix: new HarmonyMethod(typeof(ApbsIntegration), nameof(Begin)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(ApbsIntegration), nameof(End)) { priority = Priority.Last });
        harmony.Patch(relative, postfix: new HarmonyMethod(typeof(ApbsIntegration), nameof(Range)));
        harmony.Patch(prefix, postfix: new HarmonyMethod(typeof(ApbsIntegration), nameof(LevelFinished)));
        harmony.Patch(nameMethod, prefix: new HarmonyMethod(typeof(ApbsIntegration), nameof(Name)));
        harmony.Patch(editionMethod, prefix: new HarmonyMethod(typeof(ApbsIntegration), nameof(Edition)) { priority = Priority.First });
    }

    private static bool OwnershipMatches()
    {
        var levels = Harmony.GetPatchInfo(levelMethod!);
        if (levels?.Prefixes.Any(p => p.PatchMethod == prefix) != true) return false;
        foreach (var method in new[] { levelMethod!, nameMethod!, prefix!, relative! })
        {
            var patches = Harmony.GetPatchInfo(method);
            if (patches is null) continue;
            if (patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers).Concat(patches.Finalizers)
                .Any(p => p.owner != Owner && (method != levelMethod || p.PatchMethod != prefix))) return false;
        }
        if (editionMethod is not null && Harmony.GetPatchInfo(editionMethod) is { } editionOwners
            && editionOwners.Prefixes.Concat(editionOwners.Postfixes).Concat(editionOwners.Transpilers).Concat(editionOwners.Finalizers)
                .Any(p => p.owner != Owner && p.PatchMethod != editionPrefix)) return false;
        return true;
    }

    private static void Begin(MongoId sessionId, BotGenerationDetails botGenerationDetails, out Scope? __state)
    {
        __state = Current;
        Current = null;
        var store = PopulationStore.Active;
        if (store?.Ready != true || !store.ApbsEnabled || !botGenerationDetails.IsPmc || botGenerationDetails.IsPlayerScav) return;
        try
        {
            if (!OwnershipMatches()) { store.DisableApbs("Generation owners changed after startup."); return; }
            Current = new(sessionId, botGenerationDetails);
        }
        catch (Exception ex) { store.DisableApbs(ex.Message); }
    }

    private static void End(Exception? __exception, Scope? __state)
    {
        try
        {
            if (__exception is not null && Current?.Career is { } career)
                PopulationStore.Active?.ReleaseApbsReservation(Current.Session, career.Id);
        }
        finally { Current = __state; }
    }

    private static void Range(ref MinMax<int> __result)
    {
        var scope = Current;
        var store = PopulationStore.Active;
        if (scope is null || store?.Ready != true || __result is null || __result.Min < 1 || __result.Max < __result.Min) return;
        try
        {

            if (!store.SelectApbsCareer(scope, __result.Min, __result.Max, sample!)) return;
            __result = new(scope.Level, scope.Level);
        }
        catch (Exception ex) { store.Report(ex); }
    }

    private static void LevelFinished(ref RandomisedBotLevelResult __0, bool __result)
    {
        var scope = Current;
        if (scope?.Career is null) return;
        if (__result || __0?.Level != scope.Level)
        {
            PopulationStore.Active?.ReleaseApbsReservation(scope.Session, scope.Career.Id);
            scope.Career = null;
            return;
        }
        scope.LevelConfirmed = true;
        __0 = new() { Level = scope.Level, Exp = scope.Experience };
    }

    private static bool Name(ref string __result)
    {
        if (Current is not { LevelConfirmed: true, Career: { } career }) return true;
        __result = career.Name;
        return false;
    }

    private static bool Edition(Info __0, ref string __result)
    {
        if (Current is not { LevelConfirmed: true, Career: { } career }) return true;
        PopulationStore.ApplyAccountIdentity(career, __0);
        __result = career.GameVersion;
        return false;
    }

    private static List<MethodBase> Calls(MethodInfo method)
    {
        var result = new List<MethodBase>();
        byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!).GroupBy(c => unchecked((ushort)c.Value)).ToDictionary(g => g.Key, g => g.First());
        for (int offset = 0; offset < il.Length;)
        {
            ushort key = il[offset++];
            if (key == 0xfe) key = (ushort)(0xfe00 | il[offset++]);
            var op = codes[key];
            if (op.OperandType == OperandType.InlineMethod)
            {
                var target = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset));
                if (target is not null) result.Add(target);
            }
            offset += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                _ => 4
            };
        }
        return result;
    }
}

public sealed partial class PopulationStore
{
    private Harmony? apbsHarmony;
    private bool apbsAvailable;
    public string GenerationMode { get; private set; } = "initializing";
    public long ApbsMatched { get; private set; }
    public long ApbsFallback { get; private set; }
    internal bool ApbsEnabled => apbsAvailable && policy.EnableApbsCareerIntegration;

    private void InstallApbsIntegration()
    {
        var apbs = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
            a.GetName().Name?.Contains("progressivebotsystem", StringComparison.OrdinalIgnoreCase) == true);
        if (apbs is null) throw new InvalidOperationException("Required APBS assembly is unavailable.");
        apbsHarmony = new("com.local.pbt.apbs");
        try
        {
            ApbsIntegration.Install(apbsHarmony, apbs);
            apbsAvailable = true;
            GenerationMode = "APBS career bridge available";
            logger.Info("[PBT] APBS career bridge installed; APBS owns ranges, tiers, inventory, and appearance; selected careers retain account identity and prestige. Assembly: " + apbs.GetName().Version);
        }
        catch (Exception ex)
        {
            apbsHarmony.UnpatchSelf();
            DisableApbs(ex.Message);
        }
    }

    internal void DisableApbs(string reason)
    {
        apbsAvailable = false;
        GenerationMode = "APBS observation only: " + reason;
        logger.Warning("[PBT] APBS career bridge inactive; generated profiles remain available. " + reason);
    }

    internal bool SelectApbsCareer(ApbsIntegration.Scope scope, int minimum, int maximum, Func<double, double, int, double, int> sampler)
    {
        lock (gate)
        {
            if (!Ready) return false;
            if (!reservedCareers.TryGetValue(scope.Session.ToString(), out var used))
                reservedCareers.Add(scope.Session.ToString(), used = new());
            var candidates = population.Careers.Values.Where(c => !c.Retired && c.Side.Equals(scope.Details.Side, StringComparison.OrdinalIgnoreCase)
                && !used.Contains(c.Id)).Select(c => new { Career = c, Level = Level(c.Experience) })
                .Where(c => c.Level >= minimum && c.Level <= maximum).ToArray();
            if (candidates.Length == 0) { ApbsFallback++; return false; }
            int rolled = sampler(minimum, maximum, 1, 1.15);
            int distance = candidates.Min(c => Math.Abs(c.Level - rolled));
            var nearest = candidates.Where(c => Math.Abs(c.Level - rolled) == distance).ToArray();
            var selected = nearest[Random.Shared.Next(nearest.Length)];
            scope.Career = selected.Career;
            scope.Level = selected.Level;
            scope.Experience = selected.Career.Experience;
            used.Add(selected.Career.Id);
            ApbsMatched++;
            return true;
        }
    }

    internal void ReleaseApbsReservation(MongoId session, string id)
    {
        lock (gate)
            if (reservedCareers.TryGetValue(session.ToString(), out var used)) used.Remove(id);
    }
}
