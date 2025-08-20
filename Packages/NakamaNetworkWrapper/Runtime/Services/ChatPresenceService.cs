using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public class ChatPresenceService : IChatPresenceService, IDisposable
    {
        private readonly IUserCacheService _userCacheService;
        private readonly Dictionary<string, Dictionary<string, IUserPresence>> _channelPresences = new();
        private static readonly IReadOnlyDictionary<string, IUserPresence> EmptyPresences = new Dictionary<string, IUserPresence>();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _channelReady = new();

        public ChatPresenceService(IUserCacheService userCacheService = null)
        {
            _userCacheService = userCacheService;
        }
        
        public event Action<string> OnChannelReady;
        
        public event Action<IChannelPresenceEvent> OnPresenceChanged;
        
        public bool IsChannelReady(string channelId) => _channelReady.TryGetValue(channelId, out var tcs) && tcs.Task.IsCompleted;
        
        public async Task AddChannelPresencesAsync(IChannel channel)
        {
            if (!_channelPresences.ContainsKey(channel.Id))
            {
                _channelPresences[channel.Id] = new Dictionary<string, IUserPresence>();
            }

            foreach (var presence in channel.Presences)
            {
                _channelPresences[channel.Id][presence.UserId] = presence;
            }

            await CollectUserIdsForPresence(channel.Presences);
            
            MarkChannelReady(channel.Id);
        }

        public void RemoveChannelPresences(IChannel channel)
        {
            _channelReady.Remove(channel.Id);
            _channelPresences.Remove(channel.Id);
        }

        public async void PresenceChanged(IChannelPresenceEvent presenceEvent)
        {
            if (!_channelPresences.ContainsKey(presenceEvent.ChannelId))
            {
                _channelPresences[presenceEvent.ChannelId] = new Dictionary<string, IUserPresence>();
            }

            foreach (var presence in presenceEvent.Joins)
            {
                _channelPresences[presenceEvent.ChannelId][presence.UserId] = presence;
            }

            foreach (var presence in presenceEvent.Leaves)
            {
                _channelPresences[presenceEvent.ChannelId].Remove(presence.UserId);
            }

            await CollectUserIdsForPresence(presenceEvent.Joins);

            OnPresenceChanged?.Invoke(presenceEvent);
        }

        public IReadOnlyDictionary<string, IUserPresence> GetPresences(string channelId)
        {
            return _channelPresences.TryGetValue(channelId, out var presences) ? presences : EmptyPresences;
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
        
        public void Dispose()
        {
            _channelReady.Clear();
            _channelPresences.Clear();
            
            OnPresenceChanged = null;
            OnChannelReady = null;
        }
    }
}