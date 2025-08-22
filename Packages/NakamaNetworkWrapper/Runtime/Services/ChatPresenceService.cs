using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public class ChatPresenceService : IChatPresenceService, IDisposable
    {
        private readonly IUserCacheService _userCacheService;
        private readonly Dictionary<string, Dictionary<string, IUserPresence>> _channelPresences = new();
        private static readonly IReadOnlyDictionary<string, IUserPresence> EmptyPresences = new ReadOnlyDictionary<string, IUserPresence>(new Dictionary<string, IUserPresence>());
        private readonly Dictionary<string, TaskCompletionSource<bool>> _channelReady = new();
        private readonly System.Threading.SemaphoreSlim _gate = new(1, 1);

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
                    _channelPresences[channel.Id] = new Dictionary<string, IUserPresence>();
                }

                var map = _channelPresences[channel.Id];
                foreach (var presence in presencesSnapshot)
                {
                    map[presence.UserId] = presence;
                }
            }
            finally
            {
                _gate.Release();
            }

            await CollectUserIdsForPresence(presencesSnapshot);

            MarkChannelReady(channel.Id);
        }

        public void RemoveChannelPresences(IChannel channel)
        {
            if (channel == null) return;

            _gate.Wait();
            try
            {
                _channelReady.Remove(channel.Id);
                _channelPresences.Remove(channel.Id);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async void PresenceChanged(IChannelPresenceEvent presenceEvent)
        {
            if (presenceEvent == null) return;
            
            var joinsSnapshot = new List<IUserPresence>(presenceEvent.Joins);
            var leavesSnapshot = new List<IUserPresence>(presenceEvent.Leaves);

            await _gate.WaitAsync();
            try
            {
                if (!_channelPresences.ContainsKey(presenceEvent.ChannelId))
                {
                    _channelPresences[presenceEvent.ChannelId] = new Dictionary<string, IUserPresence>();
                }

                var map = _channelPresences[presenceEvent.ChannelId];

                foreach (var presence in joinsSnapshot)
                {
                    map[presence.UserId] = presence;
                }

                foreach (var presence in leavesSnapshot)
                {
                    map.Remove(presence.UserId);
                }
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
                if (_channelPresences.TryGetValue(channelId, out var presences))
                {
                    var copy = new Dictionary<string, IUserPresence>(presences);
                    return new ReadOnlyDictionary<string, IUserPresence>(copy);
                }

                return EmptyPresences;
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