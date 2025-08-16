using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        public IReadOnlyDictionary<string, IChannel> JoinedChannels => new ReadOnlyDictionary<string, IChannel>(new Dictionary<string, IChannel>(_channels));

        public IChannel GetChannel(string channelId) => channelId != null && _channels.TryGetValue(channelId, out var ch) ? ch : null;

        public async Task<IChannel> JoinChannelAsync(ChannelType channelType, string channelId, bool persistence = true, bool hidden = false)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));
            
            var socket = _clientService.Socket ?? throw new InvalidOperationException("Not connected");
            var channel = await socket.JoinChatAsync(channelId, channelType, persistence, hidden);
            
            RegisterChannel(channel);
            _chatPresenceService?.AddChannelPresences(channel);
            
            return channel;
        }
        
        public async Task LeaveChannelAsync(string channelId)
        {
            if (string.IsNullOrWhiteSpace(channelId)) return;
            if (!_channels.TryGetValue(channelId, out var channel)) return;

            var socket = _clientService.Socket;
            try
            {
                if (socket != null)
                {
                    await socket.LeaveChatAsync(channel);
                }
                
                UnregisterChannel(channelId);
                _chatPresenceService?.RemoveChannelPresences(channel);
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
            
            var socket = _clientService.Socket ?? throw new InvalidOperationException("Not connected");
            
            await socket.WriteChatMessageAsync(channelId, content);
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

        public async Task<IApiChannelMessageList> ListMessagesAsync(string channelId, int limit = 50, string cursor = null, bool forward = true, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel id required", nameof(channelId));

            var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");
            
            var messageList = await _clientService.Client.ListChannelMessagesAsync(session, channelId, limit, forward, cursor, canceller: ct);
            /*var userIdSet = new HashSet<string>();
            
            foreach (var m in messageList.Messages)
            {
                if (!string.IsNullOrEmpty(m.SenderId))
                {
                    userIdSet.Add(m.SenderId);
                }
            }
            
            if (userIdSet.Count > 0)
            {
                await UpdateHistoryUsers(userIdSet);
            }*/
            
            return messageList;
        }

        private void RegisterChannel(IChannel channel)
        {
            if (channel == null) return;
            
            if (!_channels.TryAdd(channel.Id, channel)) return;

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
            if (!_channels.ContainsKey(msg.ChannelId)) return;
            
            OnMessageReceived?.Invoke(msg);
        }
        
        private void ReceivedChannelPresence(IChannelPresenceEvent presenceEvent)
        {
            _chatPresenceService?.PresenceChanged(presenceEvent);
        }
        
        /*private async Task UpdatePresenceUsers(UserPresenceAction userPresenceAction, string channelId, IEnumerable<IUserPresence> presences)
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
                    var toFetch = new HashSet<string>();
                    foreach (var presence in presences)
                    {
                        var id = presence.UserId;
                        if (_userCacheService != null && !_userCacheService.ContainsUser(id) && !_pendingUsers.Contains(id))
                        {
                            toFetch.Add(id);
                        }
                    }
                    if (toFetch.Count > 0)
                    {
                        foreach (var id in toFetch) _pendingUsers.Add(id);
                        try
                        {
                            var batched = new List<string>(toFetch);
                            foreach (var chunk in ChunkBy(batched, MaxUsersPerRequest))
                            {
                                try
                                {
                                    var users = await _clientService.Client.GetUsersAsync(session, chunk.ToArray());
                                    foreach (var user in users.Users)
                                    {
                                        _userCacheService?.AddUser(user);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogError($"[ChatService] Error fetching users: {ex.Message}");
                                }
                            }
                        }
                        finally
                        {
                            foreach (var id in toFetch) _pendingUsers.Remove(id);
                        }
                    }
                    break;

                case UserPresenceAction.Remove:
                    foreach (var presence in presences)
                    {
                        var isUserInAnyChannel = _channelPresences.Values.Any(prc => prc.ContainsKey(presence.UserId));
                        if (!isUserInAnyChannel)
                        {
                            _userCacheService?.RemoveUser(presence.UserId);
                        }
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(userPresenceAction), userPresenceAction, null);
            }
        }
        
        private async Task UpdateHistoryUsers(IEnumerable<string> userIds)
        {
            var toFetch = new HashSet<string>();
            foreach (var id in userIds)
            {
                if (!_allUsers.ContainsKey(id) && !_pendingUsers.Contains(id))
                {
                    toFetch.Add(id);
                }
            }
            
            if (toFetch.Count == 0) return;
            
            foreach (var id in toFetch)
            {
                _pendingUsers.Add(id);
            }

            var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");

            try
            {
                var listToFetch = new List<string>(toFetch);
                foreach (var chunk in ChunkBy(listToFetch, MaxUsersPerRequest))
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
            finally
            {
                foreach (var id in toFetch) _pendingUsers.Remove(id);
            }
        }

        private static IEnumerable<List<T>> ChunkBy<T>(List<T> source, int chunkSize)
        {
            for (var i = 0; i < source.Count; i += chunkSize)
            {
                yield return source.GetRange(i, Math.Min(chunkSize, source.Count - i));
            }
        }*/
        
        public void Dispose()
        {
            _clientService.OnConnected -= Connected;
            _clientService.OnDisconnected -= Disconnected;
            
            Disconnected();
        }
    }
}