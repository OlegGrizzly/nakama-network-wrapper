using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nakama;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IChatService : IDisposable
    {
        event Action<IChannel> OnJoinedChannel;
        
        event Action<IChannel> OnLeavedChannel;
        
        event Action<IApiChannelMessage> OnMessageReceived;
        
        event Action<IChannelPresenceEvent> OnPresenceChanged;
        
        IReadOnlyDictionary<string, IChannel> JoinedChannels { get; }
        
        IChannel GetChannel(string channelId);
        
        Task<IChannel> JoinChannelAsync(ChannelType channelType, string channelId, bool persistence = true, bool hidden = false);
        
        Task LeaveChannelAsync(string channelId);
        
        Task SendMessageAsync(string channelId, string content);
        
		Task<IApiChannelMessageList> ListMessagesAsync(string channelId, int limit = 50, string cursor = null, bool forward = true, CancellationToken ct = default);
        
        Task UpdateMessageAsync(string channelId, string messageId, string content);
        
        Task RemoveMessageAsync(string channelId, string messageId);
    }
}


