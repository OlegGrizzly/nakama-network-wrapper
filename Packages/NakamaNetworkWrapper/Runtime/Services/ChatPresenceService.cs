using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Models;
using UnityEngine;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public class ChatPresenceService : IChatPresenceService, IDisposable
    {
        private readonly ICoroutineRunner _coroutineRunner;
        private readonly IUserCacheService _userCacheService;
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, IUserPresence>>> _channelPresences = new();
        private readonly Dictionary<(string channelId, string userId, string sessionId), CancellationTokenSource> _pendingLeaves = new();
        private readonly TimeSpan _leaveGrace = TimeSpan.FromSeconds(5);
        private static readonly IReadOnlyDictionary<string, IUserPresence> EmptyPresences = new ReadOnlyDictionary<string, IUserPresence>(new Dictionary<string, IUserPresence>());
        private readonly Dictionary<string, TaskCompletionSource<bool>> _channelReady = new();
        private readonly SemaphoreSlim _gate = new(1, 1);

        public ChatPresenceService(ICoroutineRunner coroutineRunner, IUserCacheService userCacheService = null)
        {
            _coroutineRunner = coroutineRunner;
            _userCacheService = userCacheService;
        }
        
        public event Action<string> OnChannelReady;
        
        public event Action<LocalPresenceEvent> OnPresenceChanged;
        
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

            List<IUserPresence> userLevelJoins = new();

            await _gate.WaitAsync();
            try
            {
                if (!_channelPresences.ContainsKey(presenceEvent.ChannelId))
                {
                    _channelPresences[presenceEvent.ChannelId] = new Dictionary<string, Dictionary<string, IUserPresence>>();
                }

                var usersMap = _channelPresences[presenceEvent.ChannelId];
                
                foreach (var presence in joinsSnapshot)
                {
                    bool isNewUser = false;
                    if (!usersMap.TryGetValue(presence.UserId, out var sessionsMap))
                    {
                        sessionsMap = new Dictionary<string, IUserPresence>();
                        usersMap[presence.UserId] = sessionsMap;
                        isNewUser = true;
                    }
                    else if (sessionsMap.Count == 0)
                    {
                        isNewUser = true;
                    }
                    sessionsMap[presence.SessionId] = presence;

                    CancelPendingLeave(presenceEvent.ChannelId, presence.UserId, presence.SessionId);

                    if (isNewUser)
                    {
                        userLevelJoins.Add(presence);
                    }
                }

                foreach (var presence in leavesSnapshot)
                {
                    ScheduleLeaveRemoval(presenceEvent.ChannelId, presence.UserId, presence.SessionId);
                }
            }
            finally
            {
                _gate.Release();
            }

            await CollectUserIdsForPresence(joinsSnapshot);

            if (userLevelJoins.Count > 0)
            {
                OnPresenceChanged?.Invoke(new LocalPresenceEvent(presenceEvent.ChannelId, userLevelJoins, Array.Empty<IUserPresence>()));
            }
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
        }

        private void CancelPendingLeave(string channelId, string userId, string sessionId)
        {
            var key = (channelId, userId, sessionId);
            if (_pendingLeaves.TryGetValue(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _pendingLeaves.Remove(key);
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
        }

        private async Task RemoveSessionAfterGraceAsync(string channelId, string userId, string sessionId, CancellationToken token)
        {
            IUserPresence removedPresence = null;
            try
            {
                await RunDelayCoroutine(token);
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
                    if (sessionsMap.TryGetValue(sessionId, out var removedPresenceCandidate))
                    {
                        removedPresence = removedPresenceCandidate;
                    }
                    sessionsMap.Remove(sessionId);
                    if (sessionsMap.Count == 0)
                    {
                        usersMap.Remove(userId);
                    }
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

            if (removedPresence != null)
            {
                OnPresenceChanged?.Invoke(new LocalPresenceEvent(channelId, Array.Empty<IUserPresence>(), new[] { removedPresence }));
            }
        }

        private Task RunDelayCoroutine(CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var coroutine = DelayRoutine(tcs, token);
            
            _coroutineRunner.StartCoroutine(coroutine);
            
            return tcs.Task;
        }

        private IEnumerator DelayRoutine(TaskCompletionSource<bool> tcs, CancellationToken token)
        {
            var elapsed = 0f;
            var duration = (float) _leaveGrace.TotalSeconds;

            while (elapsed < duration)
            {
                if (token.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(token);
                    yield break;
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            tcs.TrySetResult(true);
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
    }
}