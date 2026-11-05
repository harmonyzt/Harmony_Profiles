using System.Reflection;

namespace PBT.Server
{
    internal sealed class ApbsGearFactory
    {
        private const string ProfileLocation = "bigmap";
        private static readonly object generationGate = new object();

        private static int topTier;
        private readonly MethodInfo chances;
        private readonly MethodInfo equipment;
        private readonly object equipmentHelper;
        private readonly object generator;
        private readonly JsonUtil json;
        private readonly MethodInfo loot;
        private readonly object lootGenerator;
        private readonly MethodInfo tier;
        private readonly object tierHelper;
        private readonly MethodInfo weapons;

        internal ApbsGearFactory(Assembly assembly, Harmony harmony, JsonUtil serializer)
        {
            json = serializer;
            Type patch = assembly.GetType("ProgressiveBotSystem.Patches.GenerateInventoryPatch") ??
                         throw new MissingMethodException("APBS inventory service unavailable.");

            object Service(Type owner, string name)
            {
                return AccessTools.Field(owner, name)?.GetValue(null) ??
                       throw new InvalidOperationException("APBS service is not initialized: " + name);
            }

            generator = Service(patch, "_customBotInventoryGenerator");
            equipmentHelper = Service(patch, "_botEquipmentHelper");
            lootGenerator = Service(patch, "_customBotLootGenerator");
            loot = lootGenerator.GetType().GetMethod("GenerateLoot", [
                typeof(MongoId), typeof(MongoId), typeof(BotType),
                typeof(BotGenerationDetails), typeof(BotBaseInventory), typeof(int), typeof(int)
            ]) ?? throw new MissingMethodException("APBS loot service unavailable.");
            
            equipment = generator.GetType().GetMethods().Single(m => m.Name == "GenerateAndAddEquipmentToBot" && m.GetParameters().Length == 8);
            weapons = generator.GetType().GetMethods().Single(m => m.Name == "GenerateAndAddWeaponsToBot" && m.GetParameters().Length == 9);
            
            tierHelper = Service(assembly.GetType("ProgressiveBotSystem.Patches.GenerateBotLevel") ??
                                 throw new MissingMethodException("APBS level service unavailable."), "_tierHelper");
            chances = AccessTools.Method(equipmentHelper.GetType(), "GetChancesByBotRole", [typeof(string), typeof(int)])
                      ?? throw new MissingMethodException("APBS equipment chance service unavailable.");
            tier = AccessTools.Method(tierHelper.GetType(), "GetTierByLevel", [typeof(int)]) ??
                   throw new MissingMethodException("APBS tier service unavailable.");
            Type chanceType = assembly.GetType("ProgressiveBotSystem.Models.ApbsChances") ?? 
                              throw new MissingMethodException("APBS equipment model unavailable.");
            Type generationType = assembly.GetType("ProgressiveBotSystem.Models.ApbsGeneration") ?? 
                                  throw new MissingMethodException("APBS generation model unavailable.");
            Type questType = assembly.GetType("ProgressiveBotSystem.Models.QuestData") ?? 
                             throw new MissingMethodException("APBS quest model unavailable.");
            Type[] equipmentArgs =
            [
                typeof(MongoId), typeof(MongoId), typeof(BotTypeInventory), chanceType,
                typeof(BotBaseInventory), typeof(BotGenerationDetails), typeof(int), questType
            ];
            Type[] weaponArgs =
            [
                typeof(MongoId), typeof(BotTypeInventory), chanceType, typeof(MongoId),
                typeof(BotBaseInventory), typeof(BotGenerationDetails), generationType, typeof(int), questType
            ];
            if (!equipment.GetParameters().Select(p => p.ParameterType).SequenceEqual(equipmentArgs)
                || !weapons.GetParameters().Select(p => p.ParameterType).SequenceEqual(weaponArgs) || chanceType.GetProperty("Generation")?.PropertyType != generationType
                || chances.ReturnType != chanceType || equipment.ReturnType != typeof(void) || weapons.ReturnType != typeof(void) || tier.ReturnType != typeof(int))
            {
                throw new NotSupportedException("APBS gear service contract differs from the supported version.");
            }
            
            ApbsGenerationContext.Install(assembly, harmony);
            for (int level = 1; level <= 100; level++)
            {
                try
                {
                    topTier = Math.Max(topTier, (int)tier.Invoke(tierHelper, [level])!);
                }
                catch (TargetInvocationException)
                {
                }
            }

            if (topTier < 1)
            {
                throw new NotSupportedException("APBS has no gear tiers.");
            }
            
            harmony.Patch(tier, postfix: new HarmonyMethod(typeof(ApbsGearFactory), nameof(BoostRaidTier)));
            
            // APBS inventory caches are shared. Serialize our isolated calls with its ordinary gear calls.
            foreach (MethodInfo method in new[] { equipment, weapons })
            {
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ApbsGearFactory), nameof(Enter)), finalizer: new HarmonyMethod(typeof(ApbsGearFactory), nameof(Leave)));
            }
        }

        // In-raid spawns: APBS asks for the tier of the level PBT just picked for this career.
        private static void BoostRaidTier(int level, ref int __result)
        {
            if (ApbsIntegration.Current is not { Career: { } career } scope || scope.Level != level)
            {
                return;
            }
            
            int shift = PopulationStore.Active?.TierShift(career, true) ?? 0;

            if (shift != 0)
            {
                __result = Math.Clamp(__result + shift, 1, topTier);
            }
        }

        private static void Enter(out bool __state)
        {
            Monitor.Enter(generationGate);
            __state = true;
        }

        private static void Leave(bool __state)
        {
            if (__state) Monitor.Exit(generationGate);
        }

        internal BotBaseInventory Generate(BotInventoryGenerator native, BotInventoryContainerService containers, MongoId session, BotType template, Career career, int level,
            int tierBoost, bool night)
        {
            lock (generationGate)
            {
                using IDisposable context = ApbsGenerationContext.Enter(ProfileLocation, level, night);
                
                int selectedTier = Math.Clamp((int)tier.Invoke(tierHelper, [level])! + tierBoost, 1, topTier);
                string role = career.Side.Equals("Bear", StringComparison.OrdinalIgnoreCase) ? "pmcbear" : "pmcusec";
                object sourceChances = chances.Invoke(equipmentHelper, [role, selectedTier]) ?? throw new InvalidOperationException("APBS returned no equipment chances.");
                Type chanceType = sourceChances.GetType();
                
                // Retain SPT converters, including MongoId dictionary keys in nested APBS generation data.
                var clonedChances = json.Deserialize(json.Serialize(sourceChances, chanceType)!, chanceType)
                                    ?? throw new InvalidDataException("Cannot isolate APBS generation chances.");
                object? generation = chanceType.GetProperty("Generation")!.GetValue(clonedChances);
                
                var details = new BotGenerationDetails
                {
                    IsPmc = true,
                    IsPlayerScav = false,
                    Role = role,
                    RoleLowercase = role,
                    Side = career.Side,
                    BotLevel = level,
                    PlayerLevel = level,
                    BotDifficulty = "normal",
                    Location = ProfileLocation,
                    GameVersion = career.GameVersion,
                    ClearBotContainerCacheAfterGeneration = true,
                    ExtensionData = new()
                    {
                        ["Tier"] = selectedTier
                    }
                };
                var id = new MongoId();
                try
                {
                    var inventory = native.GenerateInventoryBase();
                    
                    equipment.Invoke(generator, [session, id, template.BotInventory, clonedChances, inventory, details, selectedTier, null]);
                    weapons.Invoke(generator, [session, template.BotInventory, clonedChances, id, inventory, details, generation, selectedTier, null]);
                    
                    return inventory;
                }
                finally
                {
                    containers.ClearCache(id);
                }
            }
        }

        internal void AddLoot(MongoId botId, MongoId session, BotType template, BotGenerationDetails details, BotBaseInventory inventory)
        {
            int selectedTier = details.ExtensionData?.TryGetValue("Tier", out var value) == true && value is int t ? t : 1;
            
            lock (generationGate)
            {
                loot.Invoke(lootGenerator, [botId, session, template, details, inventory, details.BotLevel, selectedTier]);
            }
        }
    }
}
