using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using UnityEngine;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public sealed class ChatService : IChatService
    {
        private readonly IClientService _clientService;
        private readonly IAuthService _authService;
        private readonly IUserCacheService _userCacheService;
        private readonly IChatPresenceService _chatPresenceService;
        private readonly Dictionary<string, IChannel> _channels = new();

        public ChatService(IClientService clientService, IAuthService authService, IUserCacheService userCacheService = null, IChatPresenceService chatPresenceService = null)
        {
            _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            
            _userCacheService = userCacheService;
            _chatPresenceService = chatPresenceService;

            _clientService.OnConnected += Connected;
            _clientService.OnDisconnected += Disconnected;
        }

        public event Action<IChannel> OnJoinedChannel;
        public event Action<IChannel> OnLeftChannel;
        public event Action<IApiChannelMessage> OnMessageReceived;

        public IReadOnlyDictionary<string, IChannel> JoinedChannels => _channels;

        public IChannel GetChannel(string channelId) => channelId != null && _channels.TryGetValue(channelId, out var ch) ? ch : null;

        public async Task<IChannel> JoinChannelAsync(ChannelType channelType, string channelId, bool persistence = true, bool hidden = false)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            
            var socket = _clientService.Socket ?? throw new InvalidOperationException("Not connected");
            var channel = await socket.JoinChatAsync(channelId, channelType, persistence, hidden);
            
            RegisterChannel(channel.Id, channel);
            
            if (_chatPresenceService != null)
            {
                await _chatPresenceService.AddChannelPresencesAsync(channel);
            }
            
            return channel;
        }
        
        public async Task LeaveChannelAsync(string channelId)
        {
            if (string.IsNullOrWhiteSpace(channelId)) return;
            var channel = GetChannel(channelId);
            if (channel == null) return;

            var socket = _clientService.Socket;
            try
            {
                if (socket != null)
                {
                    await socket.LeaveChatAsync(channel);
                }
                
                UnregisterChannel(channelId);
                if (_chatPresenceService != null)
                {
                    await _chatPresenceService.RemoveChannelPresencesAsync(channel);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChatService] Error while leaving channel '{channelId}': {ex}");
                throw;
            }
        }

        public async Task SendMessageAsync(string channelId, string content)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            
            var channel = GetChannel(channelId) ?? throw new InvalidOperationException($"Channel '{channelId}' not joined");
            var socket = _clientService.Socket ?? throw new InvalidOperationException("Not connected");
            await socket.WriteChatMessageAsync(channel.Id, content);
        }
        
        public async Task UpdateMessageAsync(string channelId, string messageId, string content)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("Message id required", nameof(messageId));
            
            var channel = GetChannel(channelId) ?? throw new InvalidOperationException($"Channel '{channelId}' not joined");
            var socket = _clientService.Socket ?? throw new InvalidOperationException("Not connected");
            await socket.UpdateChatMessageAsync(channel.Id, messageId, content);
        }

        public async Task RemoveMessageAsync(string channelId, string messageId)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("Message id required", nameof(messageId));
            
            var channel = GetChannel(channelId) ?? throw new InvalidOperationException($"Channel '{channelId}' not joined");
            var socket = _clientService.Socket ?? throw new InvalidOperationException("Not connected");
            await socket.RemoveChatMessageAsync(channel.Id, messageId);
        }

        public async Task<IApiChannelMessageList> ListMessagesAsync(string channelId, int limit = 50, string cursor = null, bool forward = true, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));

            var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");
            
            var channel = GetChannel(channelId) ?? throw new InvalidOperationException($"Channel '{channelId}' not joined");
            var messageList = await _clientService.Client.ListChannelMessagesAsync(session, channel.Id, limit, forward, cursor, canceller: ct);

            await CollectUserIdsForMessages(messageList);
            
            return messageList;
        }

        private void RegisterChannel(string key, IChannel channel)
        {
            if (channel == null) return;
            
            if (!_channels.TryAdd(key, channel)) return;

            OnJoinedChannel?.Invoke(channel);
        }

        private void UnregisterChannel(string channelId)
        {
            if (string.IsNullOrEmpty(channelId)) return;
            
            if (_channels.Remove(channelId, out var channel))
            {
                OnLeftChannel?.Invoke(channel);
            }
        }

        private void Connected()
        {
            AttachSocket();
        }

        private void Disconnected()
        {
            DetachSocket();
            
            _channels.Clear();
        }

        private void AttachSocket()
        {
            var socket = _clientService.Socket;
            if (socket == null) return;
            
            DetachSocket();
            
            socket.ReceivedChannelMessage += ReceivedChannelMessage;
            socket.ReceivedChannelPresence += ReceivedChannelPresence;
        }

        private void DetachSocket()
        {
            var socket = _clientService.Socket;
            if (socket == null) return;
            
            socket.ReceivedChannelMessage -= ReceivedChannelMessage;
            socket.ReceivedChannelPresence -= ReceivedChannelPresence;
        }

        private void ReceivedChannelMessage(IApiChannelMessage msg)
        {
            var isJoined = false;
            foreach (var ch in _channels.Values)
            {
                if (ch.Id == msg.ChannelId)
                {
                    isJoined = true;
                    break;
                }
            }
            
            if (!isJoined) return;
            
            OnMessageReceived?.Invoke(msg);
        }
        
        private void ReceivedChannelPresence(IChannelPresenceEvent presenceEvent)
        {
            if (_chatPresenceService != null)
            {
                _ = _chatPresenceService.PresenceChangedAsync(presenceEvent);
            }
        }
        
        private async Task CollectUserIdsForMessages(IApiChannelMessageList messageList)
        {
            if (_userCacheService == null) return;
            
            var userIdSet = new HashSet<string>();

            foreach (var m in messageList.Messages)
            {
                if (!string.IsNullOrEmpty(m.SenderId))
                {
                    userIdSet.Add(m.SenderId);
                }
            }

            if (userIdSet.Count > 0)
            {
                await _userCacheService.CollectUserIdsAsync(userIdSet);
            }
        }
        
        public void Dispose()
        {
            _clientService.OnConnected -= Connected;
            _clientService.OnDisconnected -= Disconnected;
            
            Disconnected();
        }
    }
}