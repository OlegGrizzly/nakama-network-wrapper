using System;
using System.Collections.Generic;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public class ChatPresenceService : IChatPresenceService, IDisposable
    {
        private readonly Dictionary<string, Dictionary<string, IUserPresence>> _channelPresences = new();
        private static readonly IReadOnlyDictionary<string, IUserPresence> EmptyPresences = new Dictionary<string, IUserPresence>();
        
        public event Action<IChannelPresenceEvent> OnPresenceChanged;

        public void AddChannelPresences(IChannel channel)
        {
            if (!_channelPresences.ContainsKey(channel.Id))
            {
                _channelPresences[channel.Id] = new Dictionary<string, IUserPresence>();
            }

            foreach (var presence in channel.Presences)
            {
                _channelPresences[channel.Id][presence.UserId] = presence;
            }
        }

        public void RemoveChannelPresences(IChannel channel)
        {
            _channelPresences.Remove(channel.Id);
        }

        public void PresenceChanged(IChannelPresenceEvent presenceEvent)
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

            OnPresenceChanged?.Invoke(presenceEvent);
        }

        public IReadOnlyDictionary<string, IUserPresence> GetPresences(string channelId)
        {
            return _channelPresences.TryGetValue(channelId, out var presences) ? presences : EmptyPresences;
        }

        public void Dispose()
        {
            _channelPresences.Clear();
            OnPresenceChanged = null;
        }
    }
}