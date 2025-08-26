using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using UnityEngine;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public class ChatPresenceService : IChatPresenceService, IDisposable
    {
        private readonly IUserCacheService _userCacheService;
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, IUserPresence>>> _channelPresences = new();
        private readonly Dictionary<(string channelId, string userId, string sessionId), CancellationTokenSource> _pendingLeaves = new();
        private readonly TimeSpan _leaveGrace = TimeSpan.FromSeconds(5);
        private static readonly IReadOnlyDictionary<string, IUserPresence> EmptyPresences = new ReadOnlyDictionary<string, IUserPresence>(new Dictionary<string, IUserPresence>());
        private readonly Dictionary<string, TaskCompletionSource<bool>> _channelReady = new();
        private readonly SemaphoreSlim _gate = new(1, 1);

        public ChatPresenceService(IUserCacheService userCacheService = null)
        {
            _userCacheService = userCacheService;
        }
        
        public event Action<string> OnChannelReady;
        
        public event Action<IChannelPresenceEvent> OnPresenceChanged;
        
        public bool IsChannelReady(string channelId) => _channelReady.TryGetValue(channelId, out var tcs) && tcs.Task.IsCompleted;
        
        public async Task AddChannelPresencesAsync(IChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            
            var presencesSnapshot = new List<IUserPresence>(channel.Presences);

            await _gate.WaitAsync();
            try
            {
                if (!_channelPresences.ContainsKey(channel.Id))
                {
                    _channelPresences[channel.Id] = new Dictionary<string, Dictionary<string, IUserPresence>>();
                }

                var usersMap = _channelPresences[channel.Id];
                var beforeUsers = usersMap.Count;
                var beforeSessions = CountSessions(usersMap);
                foreach (var presence in presencesSnapshot)
                {
                    if (!usersMap.TryGetValue(presence.UserId, out var sessionsMap))
                    {
                        sessionsMap = new Dictionary<string, IUserPresence>();
                        usersMap[presence.UserId] = sessionsMap;
                    }

                    sessionsMap[presence.SessionId] = presence;

                    CancelPendingLeave(channel.Id, presence.UserId, presence.SessionId);
                }

                var afterUsers = usersMap.Count;
                var afterSessions = CountSessions(usersMap);
                Debug.LogWarning($"[Presence] AddPresences ch={channel.Id} snap={presencesSnapshot.Count} users {beforeUsers}->{afterUsers} sessions {beforeSessions}->{afterSessions}");
            }
            finally
            {
                _gate.Release();
            }

            await CollectUserIdsForPresence(presencesSnapshot);

            MarkChannelReady(channel.Id);
        }

        public async Task RemoveChannelPresencesAsync(IChannel channel)
        {
            if (channel == null) return;

            await _gate.WaitAsync();
            try
            {
                var hadReady = _channelReady.Remove(channel.Id);
                var hadMap = _channelPresences.TryGetValue(channel.Id, out var usersMap);
                var users = hadMap ? usersMap.Count : 0;
                var sessions = hadMap ? CountSessions(usersMap) : 0;
                _channelPresences.Remove(channel.Id);

                var toCancel = new List<(string channelId, string userId, string sessionId)>();
                foreach (var kv in _pendingLeaves)
                {
                    if (kv.Key.channelId == channel.Id)
                    {
                        toCancel.Add(kv.Key);
                    }
                }
                foreach (var k in toCancel)
                {
                    if (_pendingLeaves.Remove(k, out var cts))
                    {
                        cts.Cancel();
                        cts.Dispose();
                    }
                }

                Debug.LogWarning($"[Presence] RemoveChannel ch={channel.Id} ready={hadReady} users={users} sessions={sessions} pendingLeavesCancelled={toCancel.Count}");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task PresenceChangedAsync(IChannelPresenceEvent presenceEvent)
        {
            if (presenceEvent == null) return;
            
            var joinsSnapshot = new List<IUserPresence>(presenceEvent.Joins);
            var leavesSnapshot = new List<IUserPresence>(presenceEvent.Leaves);

            await _gate.WaitAsync();
            try
            {
                if (!_channelPresences.ContainsKey(presenceEvent.ChannelId))
                {
                    _channelPresences[presenceEvent.ChannelId] = new Dictionary<string, Dictionary<string, IUserPresence>>();
                }

                var usersMap = _channelPresences[presenceEvent.ChannelId];
                var beforeUsers = usersMap.Count;
                var beforeSessions = CountSessions(usersMap);

                foreach (var presence in joinsSnapshot)
                {
                    if (!usersMap.TryGetValue(presence.UserId, out var sessionsMap))
                    {
                        sessionsMap = new Dictionary<string, IUserPresence>();
                        usersMap[presence.UserId] = sessionsMap;
                    }
                    sessionsMap[presence.SessionId] = presence;

                    CancelPendingLeave(presenceEvent.ChannelId, presence.UserId, presence.SessionId);
                }

                foreach (var presence in leavesSnapshot)
                {
                    ScheduleLeaveRemoval(presenceEvent.ChannelId, presence.UserId, presence.SessionId);
                }

                var afterUsers = usersMap.Count;
                var afterSessions = CountSessions(usersMap);
                Debug.LogWarning($"[Presence] Change ch={presenceEvent.ChannelId} joins={joinsSnapshot.Count} leaves={leavesSnapshot.Count} users {beforeUsers}->{afterUsers} sessions {beforeSessions}->{afterSessions} (leaves scheduled with grace)");
            }
            finally
            {
                _gate.Release();
            }

            await CollectUserIdsForPresence(joinsSnapshot);

            OnPresenceChanged?.Invoke(presenceEvent);
        }

        public IReadOnlyDictionary<string, IUserPresence> GetPresences(string channelId)
        {
            _gate.Wait();
            try
            {
                if (_channelPresences.TryGetValue(channelId, out var usersMap))
                {
                    var copy = new Dictionary<string, IUserPresence>();
                    foreach (var userEntry in usersMap)
                    {
                        foreach (var sessionEntry in userEntry.Value)
                        {
                            copy[userEntry.Key] = sessionEntry.Value;
                            break;
                        }
                    }
                    return new ReadOnlyDictionary<string, IUserPresence>(copy);
                }

                return EmptyPresences;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ClearAllAsync()
        {
            await _gate.WaitAsync();
            try
            {
                foreach (var kv in _pendingLeaves)
                {
                    kv.Value.Cancel();
                    kv.Value.Dispose();
                }
                _pendingLeaves.Clear();

                _channelReady.Clear();
                _channelPresences.Clear();
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task CollectUserIdsForPresence(IEnumerable<IUserPresence> presences)
        {
            if (_userCacheService == null) return;
            
            var userIdSet = new HashSet<string>();

            foreach (var presence in presences)
            {
                if (!string.IsNullOrEmpty(presence.UserId))
                {
                    userIdSet.Add(presence.UserId);
                }
            }

            if (userIdSet.Count > 0)
            {
                await _userCacheService.CollectUserIdsAsync(userIdSet);
            }
        }

        private void MarkChannelReady(string channelId)
        {
            if (!_channelReady.TryGetValue(channelId, out var tcs))
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _channelReady[channelId] = tcs;
            }
            tcs.TrySetResult(true);
            
            OnChannelReady?.Invoke(channelId);
            Debug.LogWarning($"[Presence] ChannelReady ch={channelId}");
        }

        private void CancelPendingLeave(string channelId, string userId, string sessionId)
        {
            var key = (channelId, userId, sessionId);
            if (_pendingLeaves.TryGetValue(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _pendingLeaves.Remove(key);
                Debug.LogWarning($"[Presence] CancelPendingLeave ch={channelId} user={userId} session={sessionId}");
            }
        }

        private void ScheduleLeaveRemoval(string channelId, string userId, string sessionId)
        {
            var key = (channelId, userId, sessionId);
            if (_pendingLeaves.ContainsKey(key))
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _pendingLeaves[key] = cts;
            
            _ = RemoveSessionAfterGraceAsync(channelId, userId, sessionId, cts.Token);
            Debug.LogWarning($"[Presence] ScheduleLeave ch={channelId} user={userId} session={sessionId} grace={_leaveGrace.TotalSeconds:F0}s");
        }

        private async Task RemoveSessionAfterGraceAsync(string channelId, string userId, string sessionId, CancellationToken token)
        {
            try
            {
                await Task.Delay(_leaveGrace, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            await _gate.WaitAsync(token);
            try
            {
                if (_channelPresences.TryGetValue(channelId, out var usersMap) &&
                    usersMap.TryGetValue(userId, out var sessionsMap))
                {
                    sessionsMap.Remove(sessionId);
                    if (sessionsMap.Count == 0)
                    {
                        usersMap.Remove(userId);
                    }

                    var users = usersMap.Count;
                    var sessions = CountSessions(usersMap);
                    Debug.LogWarning($"[Presence] LeaveCommitted ch={channelId} user={userId} session={sessionId} -> users={users} sessions={sessions}");
                }
            }
            finally
            {
                _gate.Release();
            }

            var key = (channelId, userId, sessionId);
            if (_pendingLeaves.Remove(key, out var cts))
            {
                cts.Dispose();
            }
        }
        
        public void Dispose()
        {
            _gate.Wait();
            try
            {
                _channelReady.Clear();
                _channelPresences.Clear();

                OnPresenceChanged = null;
                OnChannelReady = null;
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }

        private static int CountSessions(Dictionary<string, Dictionary<string, IUserPresence>> usersMap)
        {
            var total = 0;
            foreach (var kv in usersMap)
            {
                total += kv.Value.Count;
            }
            return total;
        }
    }
}