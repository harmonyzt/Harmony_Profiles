using System.Reflection;
using HarmonyLib;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Dialog;
using SPTarkov.Server.Core.Models.Spt.Bots;

namespace PBT.Server;

internal static class ServerPatches
{
    public static void Install(Harmony harmony)
    {
        var generation = AccessTools.Method(typeof(BotGenerator), nameof(BotGenerator.PrepareAndGenerateBot),
            [typeof(MongoId), typeof(BotGenerationDetails)]);
        var view = AccessTools.Method(typeof(ProfileController), nameof(ProfileController.GetOtherProfile),
            [typeof(MongoId), typeof(GetOtherProfileRequest)]);
        var search = AccessTools.Method(typeof(ProfileController), nameof(ProfileController.SearchProfiles),
            [typeof(SearchProfilesRequestData), typeof(MongoId)]);
        if (generation?.ReturnType != typeof(BotBase) || view?.ReturnType != typeof(GetOtherProfileResponse)
            || search?.ReturnType != typeof(List<SearchFriendResponse>))
            throw new MissingMethodException("PBT requires the SPT 4.1 synchronous generation/view/search methods.");
        harmony.Patch(generation, postfix: new HarmonyMethod(typeof(ServerPatches), nameof(Generated)) { priority = Priority.Last });
        harmony.Patch(view, prefix: new HarmonyMethod(typeof(ServerPatches), nameof(View)));
        harmony.Patch(search, postfix: new HarmonyMethod(typeof(ServerPatches), nameof(Search)));
        var completion = AccessTools.Method(typeof(AchievementController), nameof(AchievementController.GetAchievementStatics), [typeof(MongoId)]);
        if (completion?.ReturnType == typeof(CompletedAchievementsResponse))
            harmony.Patch(completion, postfix: new HarmonyMethod(typeof(ServerPatches), nameof(Completion)));

    }

    private static void Generated(BotBase __result, MongoId sessionId, BotGenerationDetails botGenerationDetails)
    {
        var store = PopulationStore.Active;
        if (store?.Ready != true) return;
        int? originalAid = __result?.Aid;
        string? originalNickname = __result?.Info?.Nickname;
        var originalAppearance = __result?.Customization is null ? null : __result.Customization with { };
        try { if (__result is not null) store.RecordGenerated(__result, botGenerationDetails, sessionId); }
        catch (Exception ex)
        {
            if (__result is not null)
            {
                __result.Aid = originalAid;
                if (__result.Info is not null) __result.Info.Nickname = originalNickname;
                __result.Customization = originalAppearance;
            }
            store.Report(ex);
        }
    }

    private static bool View(MongoId sessionId, GetOtherProfileRequest request, ref GetOtherProfileResponse __result)
    {
        var store = PopulationStore.Active;
        if (store?.Ready != true || !int.TryParse(request.AccountId, out int accountId)) return true;
        try
        {
            var profile = store.View(accountId, sessionId);
            if (profile is null) return true;
            __result = profile;
            return false;
        }
        catch (Exception ex) { store.Report(ex); return true; }
    }

    private static void Completion(ref CompletedAchievementsResponse __result)
    {
        var store = PopulationStore.Active;
        if (store?.Ready != true || __result?.Elements is null) return;
        try
        {
            foreach (var (id, percent) in store.AchievementPercentages()) __result.Elements[id] = percent;
        }
        catch (Exception ex) { store.Report(ex); }
    }

    private static void Search(SearchProfilesRequestData __0, MongoId __1, ref List<SearchFriendResponse> __result)
    {
        var store = PopulationStore.Active;
        if (store?.Ready != true) return;
        try
        {
            var matches = store.Search(__0.Nickname ?? "", __1);
            var existing = (__result ?? []).Select(x => x.Aid).ToHashSet();
            // Fika's search lists the searcher too. Nobody can friend themselves.
            __result = (__result ?? []).Where(x => x.Id != __1)
                .Concat(matches.Where(x => !existing.Contains(x.Aid))).ToList();
        }
        catch (Exception ex) { store.Report(ex); }
    }
}
