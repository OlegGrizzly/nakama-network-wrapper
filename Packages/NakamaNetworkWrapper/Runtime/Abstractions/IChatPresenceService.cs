using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IChatPresenceService
    {
        event Action<string> OnChannelReady;
        
        event Action<IChannelPresenceEvent> OnPresenceChanged;
        
        bool IsChannelReady(string channelId);
        
        Task AddChannelPresencesAsync(IChannel channel);
        
        Task RemoveChannelPresencesAsync(IChannel channel);

        Task PresenceChangedAsync(IChannelPresenceEvent presenceEvent);
        
        IReadOnlyDictionary<string, IUserPresence> GetPresences(string channelId);

        Task ClearAllAsync();
    }
}