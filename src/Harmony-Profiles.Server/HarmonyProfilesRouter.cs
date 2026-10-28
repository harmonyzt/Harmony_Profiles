using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace HarmonyProfiles.Server;

[Injectable(TypePriority = OnLoadOrder.Routers + 20)]
public sealed class HarmonyProfilesRouter(JsonUtil json, PopulationStore store) : StaticRouter(json,
[
    new RouteAction<HideoutAccessRequest>("/harmony-profiles/hideout/access",
        (url, request, session, output, token) => new ValueTask<string>(json.Serialize(store.HideoutAccess(request, session))!)),
    new RouteAction<KillerLookup>("/harmony-profiles/killer/resolve",
        (url, request, session, output, token) => new ValueTask<string>(json.Serialize(new { AccountId = store.ResolveKiller(request, session) })!)),
    new RouteAction<ActivityReport>("/harmony-profiles/activity",
        (url, request, session, output, token) =>
        {
            try { return new ValueTask<string>(json.Serialize(new { ProtocolVersion = 1, Accepted = store.Observe(request, session) })!); }
            catch (Exception ex)
            {
                store.Report(ex);
                return new ValueTask<string>(json.Serialize(new { ProtocolVersion = 1, Accepted = 0 })!);
            }
        }),
    new RouteAction<KillerReport>("/harmony-profiles/killer",
        (url, request, session, output, token) =>
        {
            try { return new ValueTask<string>(json.Serialize(new { ProtocolVersion = 1, Accepted = store.Capture(request, session) })!); }
            catch (Exception ex)
            {
                store.Report(ex);
                return new ValueTask<string>(json.Serialize(new { ProtocolVersion = 1, Accepted = false })!);
            }
        }),
    new RouteAction("/client/match/local/end",
        (url, request, session, output, token) =>
        {
            store.ResetReservations(session);
            return new ValueTask<object>(output ?? "");
        }),
    new RouteAction("/harmony-profiles/status",
        (url, request, session, output, token) =>
            new ValueTask<object>(json.Serialize(new { ProtocolVersion = 1, ActivityProtocolVersion = 1, HideoutAccessProtocolVersion = 1, Ready = store.Ready, GenerationMode = store.GenerationMode, ApbsMatched = store.ApbsMatched, ApbsFallback = store.ApbsFallback })!))
]) { }
