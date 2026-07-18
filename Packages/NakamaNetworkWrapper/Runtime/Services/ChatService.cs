using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Models;
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
        private readonly Dictionary<(ChannelType type, string key), SubscribedChannel> _desiredChannels = new();
        private readonly Dictionary<string, (ChannelType type, string key)> _channelIdToDesiredKey = new();
        private string _desiredChannelsUserId;

        public ChatService(IClientService clientService, IAuthService authService, IUserCacheService userCacheService = null, IChatPresenceService chatPresenceService = null)
        {
            _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));

            _userCacheService = userCacheService;
            _chatPresenceService = chatPresenceService;

            _clientService.OnConnected += Connected;
            _clientService.OnDisconnected += Disconnected;
            _authService.OnLoggedOut += LoggedOut;
        }

        public event Action<IChannel> OnJoinedChannel;
        public event Action<IChannel> OnLeftChannel;
        public event Action<IApiChannelMessage> OnMessageReceived;

        public IReadOnlyDictionary<string, IChannel> JoinedChannels => _channels;

        public IChannel GetChannel(string channelId) => channelId != null && _channels.TryGetValue(channelId, out var ch) ? ch : null;

        public async Task<IChannel> JoinChannelAsync(ChannelType channelType, string channelId, bool persistence = true, bool hidden = false)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));

            var socket = GetConnectedSocket();
            var channel = await socket.JoinChatAsync(channelId, channelType, persistence, hidden);

            RegisterChannel(channel.Id, channel);
            _desiredChannels[(channelType, channelId)] = new SubscribedChannel(channelType, channelId, persistence, hidden);
            _channelIdToDesiredKey[channel.Id] = (channelType, channelId);
            _desiredChannelsUserId = _authService.Session?.UserId;
            
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

            Exception leaveError = null;
            try
            {
                var socket = _clientService.Socket;
                if (socket is { IsConnected: true })
                {
                    await socket.LeaveChatAsync(channel);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChatService] Error while leaving channel '{channelId}': {ex}");
                leaveError = ex;
            }

            // Local state is cleaned regardless of the network call, otherwise the
            // channel silently rejoins itself after the next reconnect.
            UnregisterChannel(channelId);

            if (_channelIdToDesiredKey.Remove(channelId, out var desiredKey))
            {
                _desiredChannels.Remove(desiredKey);
            }

            if (_chatPresenceService != null)
            {
                try
                {
                    await _chatPresenceService.RemoveChannelPresencesAsync(channel);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ChatService] Presence cleanup failed for '{channelId}': {ex.Message}");
                }
            }

            if (leaveError != null)
            {
                ExceptionDispatchInfo.Capture(leaveError).Throw();
            }
        }

        public async Task SendMessageAsync(string channelId, string content)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            
            var channel = GetChannel(channelId) ?? throw new InvalidOperationException($"Channel '{channelId}' not joined");
            var socket = GetConnectedSocket();
            await socket.WriteChatMessageAsync(channel.Id, content);
        }
        
        public async Task UpdateMessageAsync(string channelId, string messageId, string content)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("Message id required", nameof(messageId));
            
            var channel = GetChannel(channelId) ?? throw new InvalidOperationException($"Channel '{channelId}' not joined");
            var socket = GetConnectedSocket();
            await socket.UpdateChatMessageAsync(channel.Id, messageId, content);
        }

        public async Task RemoveMessageAsync(string channelId, string messageId)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("Message id required", nameof(messageId));
            
            var channel = GetChannel(channelId) ?? throw new InvalidOperationException($"Channel '{channelId}' not joined");
            var socket = GetConnectedSocket();
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

        private ISocket GetConnectedSocket()
        {
            // The socket instance is reused across reconnects, so a non-null socket
            // no longer implies a live connection — check IsConnected explicitly.
            var socket = _clientService.Socket;
            if (socket is not { IsConnected: true }) throw new InvalidOperationException("Not connected");

            return socket;
        }

        private void Connected()
        {
            AttachSocket();

            // Channels subscribed under another account must never leak into this one.
            var currentUserId = _authService.Session?.UserId;
            if (_desiredChannels.Count > 0 && _desiredChannelsUserId != currentUserId)
            {
                _desiredChannels.Clear();
            }

            _ = RejoinDesiredChannelsAsync();
        }

        private void Disconnected()
        {
            DetachSocket();

            _channels.Clear();
            _channelIdToDesiredKey.Clear();

            if (_chatPresenceService != null)
            {
                _ = _chatPresenceService.ClearAllAsync();
            }
        }

        private void LoggedOut()
        {
            // Explicit logout abandons the subscriptions entirely — they must not be
            // re-joined for whoever logs in next.
            _desiredChannels.Clear();
            _desiredChannelsUserId = null;
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
            if (!_channels.ContainsKey(msg.ChannelId)) return;

            OnMessageReceived?.Invoke(msg);
        }
        
        private void ReceivedChannelPresence(IChannelPresenceEvent presenceEvent)
        {
            if (_chatPresenceService != null)
            {
                _ = _chatPresenceService.PresenceChangedAsync(presenceEvent);
            }
        }

        private async Task RejoinDesiredChannelsAsync()
        {
            try
            {
                var socket = _clientService.Socket;
                if (socket == null || !socket.IsConnected) return;

                // Snapshot: joins/leaves during the awaits below would otherwise
                // invalidate the iterator and silently abort the whole rejoin.
                var desired = new List<SubscribedChannel>(_desiredChannels.Values);

                foreach (var entry in desired)
                {
                    // The connection may have dropped again mid-rejoin.
                    if (!socket.IsConnected) break;

                    // The user may have left the channel while we were rejoining others.
                    if (!_desiredChannels.ContainsKey((entry.Type, entry.Key))) continue;

                    try
                    {
                        var channel = await socket.JoinChatAsync(entry.Key, entry.Type, entry.Persistence, entry.Hidden);

                        RegisterChannel(channel.Id, channel);
                        _channelIdToDesiredKey[channel.Id] = (entry.Type, entry.Key);

                        if (_chatPresenceService != null)
                        {
                            await _chatPresenceService.AddChannelPresencesAsync(channel);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ChatService] Rejoin failed for {entry.Type}:{entry.Key} - {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Fired-and-forgotten from Connected — never let it die silently.
                Debug.LogError($"[ChatService] Rejoin loop failed: {ex}");
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
            _authService.OnLoggedOut -= LoggedOut;

            Disconnected();
        }
    }
}