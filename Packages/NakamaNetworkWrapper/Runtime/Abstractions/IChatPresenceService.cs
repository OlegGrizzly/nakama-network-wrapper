using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Models;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IChatPresenceService : IDisposable
    {
        event Action<string> OnChannelReady;
        
        event Action<LocalPresenceEvent> OnPresenceChanged;
        
        bool IsChannelReady(string channelId);
        
        Task AddChannelPresencesAsync(IChannel channel);
        
        Task RemoveChannelPresencesAsync(IChannel channel);

        Task PresenceChangedAsync(IChannelPresenceEvent presenceEvent);
        
        IReadOnlyDictionary<string, IUserPresence> GetPresences(string channelId);

        Task ClearAllAsync();
    }
}