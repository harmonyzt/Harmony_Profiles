namespace PBT.Server
{
    internal static class ChatPatches
    {
        public static void Install(Harmony harmony)
        {
            var victims = AccessTools.Method(typeof(PmcChatResponseService), nameof(PmcChatResponseService.SendVictimResponse),
                [typeof(MongoId), typeof(IEnumerable<Victim>), typeof(PmcData)]);
            var killer = AccessTools.Method(typeof(PmcChatResponseService), nameof(PmcChatResponseService.SendKillerResponse),
                [typeof(MongoId), typeof(PmcData), typeof(Aggressor)]);
            if (victims is null || killer is null) throw new MissingMethodException("SPT PMC chat response contract changed.");
            harmony.Patch(victims, prefix: new HarmonyMethod(typeof(ChatPatches), nameof(Victims)));
            harmony.Patch(killer, prefix: new HarmonyMethod(typeof(ChatPatches), nameof(Killer)));
        }

        private static bool Victims(MongoId sessionId, ref IEnumerable<Victim> pmcVictims, PmcData pmcData)
        {
            PopulationStore? store = PopulationStore.Active;
            if (store?.Ready != true || pmcVictims is null) return true;
            try
            {
                var rest = store.QueueVictimMessages(sessionId, pmcVictims.ToList(), pmcData);
                pmcVictims = rest;
                return rest.Count > 0;
            }
            catch (Exception ex)
            {
                store.Report(ex);
                return true;
            }
        }

        private static bool Killer(MongoId sessionId, PmcData pmcData, Aggressor killer)
        {
            PopulationStore? store = PopulationStore.Active;
            if (store?.Ready != true || killer is null) return true;
            try
            {
                return !store.QueueKillerMessage(sessionId, pmcData, killer);
            }
            catch (Exception ex)
            {
                store.Report(ex);
                return true;
            }
        }
    }
}
