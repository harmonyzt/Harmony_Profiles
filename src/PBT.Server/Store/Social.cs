using System.Text.Json.Nodes;

namespace PBT.Server
{
    public sealed partial class PopulationStore
    {
        private readonly HashSet<string> pendingFriendNotifications = new HashSet<string>();

        private Career? SocialCareer(MongoId profileId, MongoId session)
        {
            if (population.Careers.TryGetValue(profileId.ToString(), out Career? direct)) return direct;
            Encounter? encounter = encounters.Values.FirstOrDefault(e => e.RuntimeProfileId == profileId.ToString() && CanAccess(e, session));
            return encounter is not null && population.Careers.TryGetValue(encounter.CareerId, out Career? c) ? c : null;
        }

        private SearchFriendResponse ChatProfile(Career career)
        {
            return new()
            {
                Id = new MongoId(career.Id),
                Aid = career.AccountId,
                Info = new UserDialogDetails
                {
                    Nickname = career.Name,
                    Side = career.Side,
                    Level = Level(career.Experience),
                    MemberCategory = (MemberCategory)career.MemberCategory,
                    SelectedMemberCategory = (MemberCategory)career.SelectedMemberCategory
                }
            };
        }

        public bool TryFriendRequest(MongoId session, FriendRequestData request, out FriendRequestSendResponse response)
        {
            response = null!;
            lock (gate)
            {
                if (!Ready || !SocialReady || !settings.EnableFriendRequests || request.To is not MongoId target) return false;
                Career? career = SocialCareer(target, session);
                if (career is null) return false;
                string sessionId = session.ToString();
                bool hadLinks = population.Friendships.TryGetValue(sessionId, out Dictionary<string, Friendship>? links);
                links ??= new Dictionary<string, Friendship>();
                links.TryGetValue(career.Id, out Friendship? previous);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (previous is not null && (previous.Removed || previous.Cancelled) && now - previous.RequestedUtc < 86400)
                {
                    response = new FriendRequestSendResponse
                    {
                        Status = BackendErrorCodes.TooManyFriendRequests,
                        RequestId = previous.RequestId,
                        RetryAfter = (int)Math.Clamp(previous.RequestedUtc + 86400 - now, 1, 86400)
                    };
                    return true;
                }

                Friendship? decision = previous;
                if (decision is null || decision.Removed || decision.Cancelled)
                {
                    decision = new Friendship
                    {
                        RequestId = NewId(),
                        RequestedUtc = now,
                        Accepted = Random.Shared.NextDouble() < settings.FriendAcceptanceChance
                    };
                    links[career.Id] = decision;
                    population.Friendships[sessionId] = links;
                    try
                    {
                        SavePopulation();
                    }
                    catch
                    {
                        if (previous is null) links.Remove(career.Id);
                        else links[career.Id] = previous;
                        if (!hadLinks) population.Friendships.Remove(sessionId);
                        throw;
                    }

                    logger.Info($"[PBT] friend request target={career.Name} decision={(decision.Accepted ? "accepted" : "pending")}");
                }

                response = new FriendRequestSendResponse
                {
                    Status = BackendErrorCodes.None,
                    RequestId = decision.RequestId,
                    RetryAfter = 86400
                };
                string notificationKey = sessionId + ":" + decision.RequestId;
                if (decision.Accepted && !decision.Removed && !decision.Cancelled && pendingFriendNotifications.Add(notificationKey))
                    _ = NotifyAccepted(session, ChatProfile(career), decision.RequestId, notificationKey);
                return true;
            }
        }

        public List<UserDialogInfo> Friends(MongoId session)
        {
            lock (gate)
            {
                if (!Ready || !SocialReady || !population.Friendships.TryGetValue(session.ToString(), out Dictionary<string, Friendship>? links)) return [];
                return links.Where(p => p.Value.Accepted && !p.Value.Removed && !p.Value.Cancelled && population.Careers.ContainsKey(p.Key)).Select(p => population.Careers[p.Key])
                    .Select(c =>
                    {
                        var profile = ChatProfile(c);
                        return new UserDialogInfo
                        {
                            Id = profile.Id,
                            Aid = profile.Aid,
                            Info = profile.Info
                        };
                    }).ToList();
            }
        }

        public bool TryDeleteFriend(MongoId session, MongoId profileId)
        {
            lock (gate)
            {
                if (!Ready || !SocialReady || SocialCareer(profileId, session) is not Career career) return false;
                if (!population.Friendships.TryGetValue(session.ToString(), out Dictionary<string, Friendship>? links)
                    || !links.TryGetValue(career.Id, out Friendship? previous)) return true;
                links[career.Id] = new Friendship
                {
                    RequestId = previous.RequestId,
                    RequestedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Accepted = previous.Accepted,
                    Removed = true,
                    Cancelled = previous.Cancelled
                };
                try
                {
                    SavePopulation();
                }
                catch
                {
                    links[career.Id] = previous;
                    throw;
                }

                return true;
            }
        }

        public bool TryCancelFriendRequest(MongoId session, string target)
        {
            lock (gate)
            {
                if (!Ready || !SocialReady || !population.Friendships.TryGetValue(session.ToString(), out Dictionary<string, Friendship>? links)) return false;
                // The native client sends the target profile ID; accept our request ID too for outbox callers.
                KeyValuePair<string, Friendship> match = links.FirstOrDefault(p => p.Key == target || p.Value.RequestId == target);
                if (match.Key is null) return false;
                Friendship? previous = match.Value;
                if (previous.Accepted || previous.Removed || previous.Cancelled) return true;
                links[match.Key] = new Friendship
                {
                    RequestId = previous.RequestId,
                    RequestedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Accepted = false,
                    Cancelled = true
                };
                try
                {
                    SavePopulation();
                }
                catch
                {
                    links[match.Key] = previous;
                    throw;
                }

                return true;
            }
        }

        internal string SocialNullResponse()
        {
            return responses.NullResponse();
        }

        internal async Task<string> MergeOutbox(ValueTask<string> original, MongoId session)
        {
            string result = await original;
            try
            {
                lock (gate)
                {
                    if (!Ready || !SocialReady || !population.Friendships.TryGetValue(session.ToString(), out Dictionary<string, Friendship>? links)) return result;
                    JsonObject? envelope = JsonNode.Parse(result)?.AsObject();
                    if (envelope?["data"] is not JsonArray rows || envelope["err"]?.GetValue<int>() != 0) return result;
                    HashSet<string?> existing = rows.OfType<JsonObject>().Select(r => r["_id"]?.GetValue<string>()).ToHashSet();
                    foreach ((string careerId, Friendship request) in links)
                    {
                        if (request.Accepted || request.Removed || request.Cancelled || existing.Contains(request.RequestId)
                            || !population.Careers.TryGetValue(careerId, out Career? career)) continue;
                        rows.Add(new JsonObject
                        {
                            ["_id"] = request.RequestId,
                            ["from"] = session.ToString(),
                            ["to"] = career.Id,
                            ["date"] = request.RequestedUtc,
                            ["profile"] = JsonNode.Parse(json.Serialize(ChatProfile(career))!)
                        });
                    }

                    return envelope.ToJsonString();
                }
            }
            catch (Exception ex)
            {
                Report(ex);
                return result;
            }
        }

        internal void SocialInstalled(bool fika)
        {
            logger.Debug("[PBT] Social handlers installed for vanilla" + (fika ? " and Fika." : "."));
        }

        private async Task NotifyAccepted(MongoId session, SearchFriendResponse profile, string requestId, string key)
        {
            try
            {
                await Task.Delay(750);
                lock (gate)
                {
                    if (!Ready || !SocialReady || !population.Friendships.TryGetValue(session.ToString(), out Dictionary<string, Friendship>? links)
                        || !links.TryGetValue(profile.Id.ToString(), out Friendship? link) || link.RequestId != requestId || !link.Accepted || link.Removed
                        || link.Cancelled) return;
                }

                await notifications.SendMessageAsync(session, new WsFriendsListAccept
                {
                    EventType = NotificationEventType.friendListRequestAccept,
                    EventIdentifier = new MongoId(requestId),
                    Profile = profile
                });
            }
            catch (Exception ex)
            {
                Report(ex);
            }
            finally
            {
                lock (gate)
                {
                    pendingFriendNotifications.Remove(key);
                }
            }
        }
    }
}
