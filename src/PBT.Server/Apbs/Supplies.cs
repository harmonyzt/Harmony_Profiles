namespace PBT.Server
{
    // SPT hands every PMC ten AFAKs and a Surv12 in the secure container. Players carry a kit that fits their wallet instead.
    public sealed partial class PopulationStore
    {
        private const string Ai2 = "5755356824597772cb798962",
            Car = "590c661e86f7741e566b646a",
            Salewa = "544fb45d4bdc2d34748b456a",
            Ifak = "590c678286f77426c9660122",
            Afak = "60098ad7c2240c0fe85c570a",
            Grizzly = "590c657e86f77412b013051d",
            Bandage = "5751a25924597722c463c472",
            Esmarch = "5e831507ea0a7c419c2f9bd9",
            Cat = "60098af40accd37ef2175f27",
            Calok = "5e8488fa988a8701445df1e4",
            Splint = "544fb3364bdc2d34748b456a",
            AluSplint = "5af0454c86f7746bf20992e8",
            Analgin = "544fb37f4bdc2dee738b4567",
            Ibuprofen = "5af0548586f7743a532b7e99",
            Morphine = "544fb3f34bdc2d03748b456a",
            Cms = "5d02778e86f774203e7dedbe",
            Surv12 = "5d02797c86f774203f38e30a",
            Propital = "5c0e530286f7747fa1419862",
            Etg = "5c0e534186f7747fa1419867";

        private static readonly HashSet<MongoId> SecureStash = [new("60098ad7c2240c0fe85c570a"), new("5d02797c86f774203f38e30a")];
        private static readonly HashSet<EquipmentSlots> MedSlots = [EquipmentSlots.Pockets, EquipmentSlots.TacticalVest, EquipmentSlots.Backpack];
        private static readonly HashSet<EquipmentSlots> GrenadeSlots = [EquipmentSlots.TacticalVest, EquipmentSlots.Pockets, EquipmentSlots.Backpack];

        // Index is the supply tier, 0 broke to 4 loaded.
        private static readonly string[][] Medkits = [[Ai2, Car], [Car, Salewa], [Salewa, Ifak], [Ifak, Afak], [Afak, Grizzly]];
        private static readonly string[][] Bleeds = [[Bandage, Esmarch], [Bandage, Esmarch], [Esmarch, Cat], [Cat, Calok], [Cat, Calok]];

        private static readonly (string Tpl, int Weight)[] Grenades =
        [
            ("5710c24ad2720bc3458b45a3", 30), ("5448be9a4bdc2dfd2f8b456a", 30), ("58d3db5386f77426186285a0", 20), ("5a0c27731526d80618476ac4", 12), ("619256e5f8af2c1a4e1f5d92", 8)
        ];

        private static string Pick(string[] pool)
        {
            return pool[Random.Shared.Next(pool.Length)];
        }

        private static bool Roll(double chance)
        {
            return Random.Shared.NextDouble() < chance;
        }

        private int SupplyTier(Career c)
        {
            return Math.Clamp(Level(c.Experience) / 15 + FortuneTiers(c, false), 0, 4);
        }

        private List<(string Tpl, HashSet<EquipmentSlots> Slots)> PlanSupplies(Career c, HashSet<string> carried)
        {
            int tier = SupplyTier(c);
            var plan = new List<(string, HashSet<EquipmentSlots>)>();

            void Want(IEnumerable<string> category, string tpl)
            {
                if (!category.Any(carried.Contains)) plan.Add((tpl, MedSlots));
            }

            string[] allKits = [Ai2, Car, Salewa, Ifak, Afak, Grizzly];
            Want(allKits, Pick(Medkits[tier]));
            if (tier >= 3 && Roll(.4)) plan.Add((Pick(Medkits[tier]), MedSlots));
            Want([Bandage, Esmarch, Cat, Calok], Pick(Bleeds[tier]));
            if (Roll(.7)) Want([Splint, AluSplint], tier >= 3 ? AluSplint : Splint);
            if (Roll(.8)) Want([Analgin, Ibuprofen, Morphine], tier <= 1 ? Analgin : tier == 2 ? Ibuprofen : Roll(.5) ? Morphine : Ibuprofen);
            if (tier >= 2 && Roll(tier switch
                {
                    2 => .3,
                    3 => .6,
                    _ => .9
                })) Want([Cms, Surv12], tier == 4 && Roll(.4) ? Surv12 : Cms);
            if (tier >= 3 && Roll(tier == 3 ? .15 : .35)) Want([Propital, Etg], Roll(.6) ? Propital : Etg);

            int grenades = c.Archetype switch
            {
                "Timmy" => Random.Shared.Next(0, 2),
                "Aggressive" => Random.Shared.Next(1, 4),
                _ => Random.Shared.Next(0, 3)
            };
            grenades -= carried.Count(t => Grenades.Any(g => g.Tpl == t));
            int total = Grenades.Sum(g => g.Weight);
            for (int i = 0; i < grenades; i++)
            {
                int roll = Random.Shared.Next(total);
                plan.Add((Grenades.First(g => (roll -= g.Weight) < 0).Tpl, GrenadeSlots));
            }

            return plan;
        }

        // Caller holds no locks. The AI reloads from the container ammo.
        internal void StockSupplies(Career c, BotBaseInventory inventory, MongoId botId)
        {
            if (!policy.RealisticSupplies || inventory.Items is null || inventory.Equipment is null) return;
            string root = inventory.Equipment.Value.ToString();
            var secure = inventory.Items.FirstOrDefault(i => i.ParentId == root && i.SlotId == "SecuredContainer");
            if (secure is not null)
                inventory.Items = inventory.Items.Where(i => !(i.ParentId == secure.Id.ToString() && SecureStash.Contains(i.Template))).ToList();

            var holders = inventory.Items.Where(i => i.ParentId == root && i.SlotId is "Pockets" or "TacticalVest" or "Backpack").Select(i => i.Id.ToString()).ToHashSet();
            bool grew;
            do
            {
                grew = false;
                foreach (var i in inventory.Items)
                {
                    if (i.ParentId is { } p && holders.Contains(p)) grew |= holders.Add(i.Id.ToString());
                }
            } while (grew);

            var carried = inventory.Items.Where(i => holders.Contains(i.Id.ToString())).Select(i => i.Template.ToString()).ToHashSet();

            inventoryContainers.ClearCache(botId);
            try
            {
                foreach (string slot in LootContainers)
                {
                    TrackContainer(botId, inventory, root, slot);
                }

                foreach (var (tpl, slots) in PlanSupplies(c, carried))
                {
                    if (!templates.Items.TryGetValue(new MongoId(tpl), out var template)) continue;
                    var upd = itemHelper.GenerateUpdForItem(template) ?? new Upd();
                    upd.StackObjectsCount = 1;
                    var item = new Item
                    {
                        Id = new MongoId(),
                        Template = template.Id,
                        Upd = upd
                    };
                    botGeneratorHelper.AddItemWithChildrenToEquipmentSlot(botId, slots, item.Id, item.Template, [item], inventory);
                }
            }
            finally
            {
                inventoryContainers.ClearCache(botId);
            }
        }
    }
}
