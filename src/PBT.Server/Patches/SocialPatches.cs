using System.Reflection;
using HarmonyLib;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Dialog;

namespace PBT.Server;

internal static class SocialPatches
{
    internal static void Install(Harmony harmony)
    {
        Bind(harmony, typeof(DialogueController), nameof(DialogueController.SendFriendRequest),
            [typeof(MongoId), typeof(FriendRequestData)], typeof(FriendRequestSendResponse), nameof(Send));
        Bind(harmony, typeof(DialogueController), nameof(DialogueController.GetFriendList),
            [typeof(MongoId)], typeof(GetFriendListDataResponse), nameof(Friends), postfix: true);
        Bind(harmony, typeof(DialogueController), nameof(DialogueController.DeleteFriend),
            [typeof(MongoId), typeof(DeleteFriendRequest)], typeof(void), nameof(Delete));
        Bind(harmony, typeof(DialogueCallbacks), nameof(DialogueCallbacks.ListOutbox),
            [typeof(string), typeof(SPTarkov.Server.Core.Models.Eft.Common.EmptyRequestData), typeof(MongoId)],
            typeof(ValueTask<string>), nameof(Outbox), postfix: true);
        Bind(harmony, typeof(DialogueCallbacks), nameof(DialogueCallbacks.CancelFriendRequest),
            [typeof(string), typeof(CancelFriendRequestData), typeof(MongoId)], typeof(ValueTask<string>), nameof(Cancel));

        Type? fika = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("FikaServer.Controllers.FikaDialogueController", throwOnError: false))
            .FirstOrDefault(t => t is not null);
        if (fika is not null)
        {
            Bind(harmony, fika, "AddFriendRequest", [typeof(MongoId), typeof(MongoId)],
                typeof(FriendRequestSendResponse), nameof(FikaSend));
            Bind(harmony, fika, "GetFriendsList", [typeof(MongoId)], typeof(GetFriendListDataResponse), nameof(Friends), postfix: true);
            Bind(harmony, fika, "DeleteFriend", [typeof(MongoId), typeof(MongoId)], typeof(ValueTask<string>), nameof(FikaDelete));
            Bind(harmony, fika, "ListOutBox", [typeof(MongoId)], typeof(ValueTask<string>), nameof(FikaOutbox), postfix: true);
            Bind(harmony, fika, "CancelFriendRequest", [typeof(MongoId), typeof(string)], typeof(ValueTask<string>), nameof(FikaCancel));
        }
        PopulationStore.Active?.SocialInstalled(fika is not null);
    }

    private static void Bind(Harmony harmony, Type type, string name, Type[] args, Type returns, string patch, bool postfix = false)
    {
        var target = AccessTools.Method(type, name, args);
        if (target?.ReturnType != returns) throw new MissingMethodException(type.FullName, name);
        var handler = new HarmonyMethod(typeof(SocialPatches), patch) { priority = postfix ? Priority.Last : Priority.First };
        harmony.Patch(target, prefix: postfix ? null : handler, postfix: postfix ? handler : null);
    }

    private static bool Send(MongoId __0, FriendRequestData __1, ref FriendRequestSendResponse __result) =>
        HandleSend(__0, __1, ref __result);

    private static bool FikaSend(MongoId __0, MongoId __1, ref FriendRequestSendResponse __result) =>
        HandleSend(__0, new FriendRequestData { To = __1 }, ref __result);

    private static bool HandleSend(MongoId session, FriendRequestData request, ref FriendRequestSendResponse result)
    {
        var store = PopulationStore.Active;
        if (store?.Ready != true) return true;
        try
        {
            if (!store.TryFriendRequest(session, request, out var response)) return true;
            result = response;
            return false;
        }
        catch (Exception ex) { store.Report(ex); return true; }
    }

    private static void Friends(MongoId __0, ref GetFriendListDataResponse __result)
    {
        var store = PopulationStore.Active;
        if (store?.Ready != true) return;
        try
        {
            var virtualFriends = store.Friends(__0);
            var existing = (__result.Friends ?? []).Select(p => p.Id).ToHashSet();
            __result.Friends ??= [];
            __result.Friends.AddRange(virtualFriends.Where(p => !existing.Contains(p.Id)));
        }
        catch (Exception ex) { store.Report(ex); }
    }

    private static bool Delete(MongoId __0, DeleteFriendRequest __1)
    {
        var store = PopulationStore.Active;
        if (store?.Ready != true) return true;
        try { return !store.TryDeleteFriend(__0, __1.FriendId); }
        catch (Exception ex) { store.Report(ex); return true; }
    }

    private static bool FikaDelete(MongoId __0, MongoId __1, ref ValueTask<string> __result)
    {
        var store = PopulationStore.Active;
        if (store?.Ready != true) return true;
        try
        {
            if (!store.TryDeleteFriend(__0, __1)) return true;
            __result = new ValueTask<string>(store.SocialNullResponse());
            return false;
        }
        catch (Exception ex) { store.Report(ex); return true; }
    }

    private static void Outbox(MongoId __2, ref ValueTask<string> __result) => AddOutbox(__2, ref __result);
    private static void FikaOutbox(MongoId __0, ref ValueTask<string> __result) => AddOutbox(__0, ref __result);
    private static void AddOutbox(MongoId session, ref ValueTask<string> result)
    {
        if (PopulationStore.Active is { Ready: true, SocialReady: true } store)
            result = new ValueTask<string>(store.MergeOutbox(result, session));
    }

    private static bool Cancel(CancelFriendRequestData __1, MongoId __2, ref ValueTask<string> __result) =>
        HandleCancel(__2, __1.ProfileId, ref __result);
    private static bool FikaCancel(MongoId __0, string __1, ref ValueTask<string> __result) =>
        HandleCancel(__0, __1, ref __result);
    private static bool HandleCancel(MongoId session, string? target, ref ValueTask<string> result)
    {
        var store = PopulationStore.Active;
        if (store?.Ready != true) return true;
        try
        {
            if (string.IsNullOrEmpty(target) || !store.TryCancelFriendRequest(session, target)) return true;
            result = new ValueTask<string>(store.SocialNullResponse());
            return false;
        }
        catch (Exception ex) { store.Report(ex); return true; }
    }
}
