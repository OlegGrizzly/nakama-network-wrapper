using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using UnityEngine;
using System.Linq;
using OlegGrizzly.NakamaNetworkWrapper.Common;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public sealed class ChatService : IChatService
    {
        private readonly IClientService _clientService;
        private readonly IAuthService _authService;
        private readonly Dictionary<string, IChannel> _channels = new();
        private readonly Dictionary<string, Dictionary<string, IUserPresence>> _channelPresences = new();
        private readonly Dictionary<string, IApiUser> _allUsers = new();
        
        private const int MaxUsersPerRequest = 100;

        public ChatService(IClientService clientService, IAuthService authService)
        {
            _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));

            _clientService.OnConnected += Connected;
            _clientService.OnDisconnected += Disconnected;
        }

        public event Action<IChannel> OnJoinedChannel;
        public event Action<IChannel> OnLeavedChannel;
        public event Action<IApiChannelMessage> OnMessageReceived;
        public event Action<IChannelPresenceEvent> OnPresenceChanged;

        public IReadOnlyDictionary<string, IChannel> JoinedChannels => _channels;

        public IChannel GetChannel(string channelId) => channelId != null && _channels.TryGetValue(channelId, out var ch) ? ch : null;

        public async Task<IChannel> JoinChannelAsync(ChannelType channelType, string channelId, bool persistence = true, bool hidden = false)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            
            var socket = _clientService.Socket ?? throw new InvalidOperationException("Not connected");
            var channel = await socket.JoinChatAsync(channelId, channelType, persistence, hidden);
            
            RegisterChannel(channel);
            
            return channel;
        }

        public async Task LeaveChannelAsync(string channelId)
        {
            if (string.IsNullOrWhiteSpace(channelId)) return;
            if (!_channels.TryGetValue(channelId, out var channel)) return;
            
            var socket = _clientService.Socket;
            if (socket != null)
            {
                await socket.LeaveChatAsync(channel);
            }
            
            UnregisterChannel(channelId);
        }

        public async Task SendMessageAsync(string channelId, string content)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            
            var socket = _clientService.Socket ?? throw new InvalidOperationException("Not connected");
            
            await socket.WriteChatMessageAsync(channelId, content);
        }

        public async Task<IApiChannelMessageList> ListMessagesAsync(string channelId, int limit = 50, string cursor = null, bool forward = true, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));

            var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");
            
            var messageList = await _clientService.Client.ListChannelMessagesAsync(session, channelId, limit, forward, cursor, canceller: ct);
            var userIds = messageList.Messages.Select(m => m.SenderId).Distinct().ToList();
            await UpdateHistoryUsers(userIds);
            
            return messageList;
        }

        public async Task UpdateMessageAsync(string channelId, string messageId, string content)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("Message id required", nameof(messageId));
            
            var socket = _clientService.Socket ?? throw new InvalidOperationException("Not connected");
            
            await socket.UpdateChatMessageAsync(channelId, messageId, content);
        }

        public async Task RemoveMessageAsync(string channelId, string messageId)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("Message id required", nameof(messageId));
            
            var socket = _clientService.Socket ?? throw new InvalidOperationException("Not connected");
            
            await socket.RemoveChatMessageAsync(channelId, messageId);
        }
        
        public IApiUser GetUser(string userId)
        {
            return _allUsers.GetValueOrDefault(userId);
        }

        private async void RegisterChannel(IChannel channel)
        {
            if (channel == null) return;
            
            _channels[channel.Id] = channel;
            await UpdatePresenceUsers(UserPresenceAction.Append, channel.Id, channel.Presences);
            
            OnJoinedChannel?.Invoke(channel);
        }

        private void UnregisterChannel(string channelId)
        {
            if (string.IsNullOrEmpty(channelId)) return;
            
            if (_channels.Remove(channelId, out var channel))
            {
                _channelPresences.Remove(channel.Id);
                
                OnLeavedChannel?.Invoke(channel);
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
        
        private async void ReceivedChannelPresence(IChannelPresenceEvent channelPresenceEvent)
        {
            await UpdatePresenceUsers(UserPresenceAction.Append, channelPresenceEvent?.ChannelId, channelPresenceEvent?.Joins);
            await UpdatePresenceUsers(UserPresenceAction.Remove, channelPresenceEvent?.ChannelId, channelPresenceEvent?.Leaves);
            
            OnPresenceChanged?.Invoke(channelPresenceEvent);
        }
        
        private async Task UpdatePresenceUsers(UserPresenceAction userPresenceAction, string channelId, IEnumerable<IUserPresence> presences)
        {
            if (channelId == null || presences == null) return;

            if (!_channelPresences.TryGetValue(channelId, out var prc))
            {
                prc = new Dictionary<string, IUserPresence>();
                _channelPresences[channelId] = prc;
            }

            switch (userPresenceAction)
            {
                case UserPresenceAction.Append:
                    foreach (var presence in presences)
                    {
                        prc.TryAdd(presence.UserId, presence);
                    }
                    break;
                
                case UserPresenceAction.Remove:
                    foreach (var presence in presences)
                    {
                        prc.Remove(presence.UserId);
                    }
                    break;
                
                default:
                    throw new ArgumentOutOfRangeException(nameof(userPresenceAction), userPresenceAction, null);
            }

            await UpdatePresenceUsers(userPresenceAction, prc.Values);
        }

        private async Task UpdatePresenceUsers(UserPresenceAction userPresenceAction, IEnumerable<IUserPresence> presences)
        {
            if (presences == null) return;

            var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");

            switch (userPresenceAction)
            {
                case UserPresenceAction.Append:
                    var userIds = presences.Where(presence => !_allUsers.ContainsKey(presence.UserId)).Select(presence => presence.UserId).Distinct().ToList();
                    foreach (var chunk in ChunkBy(userIds, MaxUsersPerRequest))
                    {
                        try
                        {
                            var users = await _clientService.Client.GetUsersAsync(session, chunk.ToArray());
                            foreach (var user in users.Users)
                            {
                                _allUsers[user.Id] = user;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[ChatService] Error fetching users: {ex.Message}");
                        }
                    }
                    break;

                case UserPresenceAction.Remove:
                    foreach (var presence in presences)
                    {
                        var isUserInAnyChannel = _channelPresences.Values.Any(prc => prc.ContainsKey(presence.UserId));
                        if (!isUserInAnyChannel)
                        {
                            _allUsers.Remove(presence.UserId);
                        }
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(userPresenceAction), userPresenceAction, null);
            }
        }
        
        private async Task UpdateHistoryUsers(IEnumerable<string> userIds)
        {
            var missingIds = userIds.Where(id => !_allUsers.ContainsKey(id)).Distinct().ToList();
            if (missingIds.Count == 0) return;

            var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");

            foreach (var chunk in ChunkBy(missingIds, MaxUsersPerRequest))
            {
                try
                {
                    var users = await _clientService.Client.GetUsersAsync(session, chunk.ToArray());
                    foreach (var user in users.Users)
                    {
                        _allUsers[user.Id] = user;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ChatService] Error fetching users: {ex.Message}");
                }
            }
        }

        private static IEnumerable<List<T>> ChunkBy<T>(List<T> source, int chunkSize)
        {
            for (var i = 0; i < source.Count; i += chunkSize)
            {
                yield return source.GetRange(i, Math.Min(chunkSize, source.Count - i));
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