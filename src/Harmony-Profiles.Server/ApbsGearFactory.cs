using System.Reflection;
using SPTarkov.Server.Core.Utils;
using HarmonyLib;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Services.Bot;

namespace HarmonyProfiles.Server;

internal sealed class ApbsGearFactory
{
    private const string ProfileLocation = "bigmap";
    private static readonly object generationGate = new();
    private readonly JsonUtil json;
    private readonly object generator;
    private readonly object equipmentHelper;
    private readonly object tierHelper;
    private readonly MethodInfo equipment;
    private readonly MethodInfo weapons;
    private readonly MethodInfo chances;
    private readonly MethodInfo tier;

    internal ApbsGearFactory(Assembly assembly, Harmony harmony, JsonUtil serializer)
    {
        json = serializer;
        var patch = assembly.GetType("ProgressiveBotSystem.Patches.GenerateInventoryPatch")
            ?? throw new MissingMethodException("APBS inventory service unavailable.");
        object Service(Type owner, string name) => AccessTools.Field(owner, name)?.GetValue(null)
            ?? throw new InvalidOperationException("APBS service is not initialized: " + name);
        generator = Service(patch, "_customBotInventoryGenerator");
        equipmentHelper = Service(patch, "_botEquipmentHelper");
        tierHelper = Service(assembly.GetType("ProgressiveBotSystem.Patches.GenerateBotLevel")
            ?? throw new MissingMethodException("APBS level service unavailable."), "_tierHelper");
        equipment = generator.GetType().GetMethods().Single(m => m.Name == "GenerateAndAddEquipmentToBot" && m.GetParameters().Length == 8);
        weapons = generator.GetType().GetMethods().Single(m => m.Name == "GenerateAndAddWeaponsToBot" && m.GetParameters().Length == 9);
        chances = AccessTools.Method(equipmentHelper.GetType(), "GetChancesByBotRole", [typeof(string), typeof(int)])
            ?? throw new MissingMethodException("APBS equipment chance service unavailable.");
        tier = AccessTools.Method(tierHelper.GetType(), "GetTierByLevel", [typeof(int)])
            ?? throw new MissingMethodException("APBS tier service unavailable.");
        Type chanceType = assembly.GetType("ProgressiveBotSystem.Models.ApbsChances")
            ?? throw new MissingMethodException("APBS equipment model unavailable.");
        Type generationType = assembly.GetType("ProgressiveBotSystem.Models.ApbsGeneration")
            ?? throw new MissingMethodException("APBS generation model unavailable.");
        Type questType = assembly.GetType("ProgressiveBotSystem.Models.QuestData")
            ?? throw new MissingMethodException("APBS quest model unavailable.");
        Type[] equipmentArgs = [typeof(MongoId), typeof(MongoId), typeof(BotTypeInventory), chanceType,
            typeof(BotBaseInventory), typeof(BotGenerationDetails), typeof(int), questType];
        Type[] weaponArgs = [typeof(MongoId), typeof(BotTypeInventory), chanceType, typeof(MongoId),
            typeof(BotBaseInventory), typeof(BotGenerationDetails), generationType, typeof(int), questType];
        if (!equipment.GetParameters().Select(p => p.ParameterType).SequenceEqual(equipmentArgs)
            || !weapons.GetParameters().Select(p => p.ParameterType).SequenceEqual(weaponArgs)
            || chanceType.GetProperty("Generation")?.PropertyType != generationType || chances.ReturnType != chanceType
            || equipment.ReturnType != typeof(void) || weapons.ReturnType != typeof(void) || tier.ReturnType != typeof(int))
            throw new NotSupportedException("APBS gear service contract differs from the supported version.");
        ApbsGenerationContext.Install(assembly, harmony);
        // APBS inventory caches are shared. Serialize our isolated calls with its ordinary gear calls.
        foreach (var method in new[] { equipment, weapons })
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ApbsGearFactory), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(ApbsGearFactory), nameof(Leave)));
    }

    private static void Enter(out bool __state) { Monitor.Enter(generationGate); __state = true; }
    private static void Leave(bool __state) { if (__state) Monitor.Exit(generationGate); }

    internal BotBaseInventory Generate(BotInventoryGenerator native, BotInventoryContainerService containers,
        MongoId session, BotType template, Career career, int level)
    {
        lock (generationGate)
        {
            using var context = ApbsGenerationContext.Enter(ProfileLocation, level, night: false);
            int selectedTier = (int)tier.Invoke(tierHelper, [level])!;
            string role = career.Side.Equals("Bear", StringComparison.OrdinalIgnoreCase) ? "pmcbear" : "pmcusec";
            var sourceChances = chances.Invoke(equipmentHelper, [role, selectedTier])
                ?? throw new InvalidOperationException("APBS returned no equipment chances.");
            var chanceType = sourceChances.GetType();
            // Retain SPT converters, including MongoId dictionary keys in nested APBS generation data.
            var clonedChances = json.Deserialize(json.Serialize(sourceChances, chanceType)!, chanceType)
                ?? throw new InvalidDataException("Cannot isolate APBS generation chances.");
            var generation = chanceType.GetProperty("Generation")!.GetValue(clonedChances);
            var details = new BotGenerationDetails
            {
                IsPmc = true, IsPlayerScav = false, Role = role, RoleLowercase = role, Side = career.Side,
                BotLevel = level, PlayerLevel = level, BotDifficulty = "normal", Location = ProfileLocation,
                GameVersion = career.GameVersion, ClearBotContainerCacheAfterGeneration = true,
                ExtensionData = new() { ["Tier"] = selectedTier }
            };
            var id = new MongoId();
            try
            {
                var inventory = native.GenerateInventoryBase();
                equipment.Invoke(generator, [session, id, template.BotInventory, clonedChances, inventory, details, selectedTier, null]);
                weapons.Invoke(generator, [session, template.BotInventory, clonedChances, id, inventory, details, generation, selectedTier, null]);
                return inventory;
            }
            finally { containers.ClearCache(id); }
        }
    }
}
