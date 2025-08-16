using System;
using System.Collections.Generic;
using Nakama;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IChatPresenceService
    {
        event Action<IChannelPresenceEvent> OnPresenceChanged;
        
        void AddChannelPresences(IChannel channel);
        
        void RemoveChannelPresences(IChannel channel);

        void PresenceChanged(IChannelPresenceEvent presenceEvent);
        
        IReadOnlyDictionary<string, IUserPresence> GetPresences(string channelId);
    }
}