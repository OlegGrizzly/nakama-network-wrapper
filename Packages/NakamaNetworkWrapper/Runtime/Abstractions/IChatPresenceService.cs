using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IChatPresenceService
    {
        event Action<IChannelPresenceEvent> OnPresenceChanged;
        
        Task AddChannelPresencesAsync(IChannel channel);
        
        void RemoveChannelPresences(IChannel channel);

        void PresenceChanged(IChannelPresenceEvent presenceEvent);
        
        IReadOnlyDictionary<string, IUserPresence> GetPresences(string channelId);
    }
}