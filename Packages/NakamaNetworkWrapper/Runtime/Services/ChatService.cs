using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public sealed class ChatService : IChatService
    {
        private readonly IClientService _clientService;
        private readonly IAuthService _authService;
        private readonly Dictionary<string, IChannel> _channels = new();
        private readonly Dictionary<string, Dictionary<string, IUserPresence>> _participantsByChannel = new();
        private readonly Dictionary<string, IApiUser> _userCache = new();
        private readonly SemaphoreSlim _lookupGate = new(1,1);

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
            
            return await _clientService.Client.ListChannelMessagesAsync(session, channelId, limit, forward, cursor, canceller: ct);
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

        private void RegisterChannel(IChannel channel)
        {
            if (channel == null) return;
            
            _channels[channel.Id] = channel;
            
            OnJoinedChannel?.Invoke(channel);
        }

        private void UnregisterChannel(string channelId)
        {
            if (string.IsNullOrEmpty(channelId)) return;
            
            if (_channels.Remove(channelId, out var channel))
            {
                OnLeavedChannel?.Invoke(channel);
            }

            _participantsByChannel.Remove(channelId);
        }

        private void Connected()
        {
            AttachSocket();
        }

        private void Disconnected()
        {
            DetachSocket();
            _channels.Clear();
            _participantsByChannel.Clear();
            _userCache.Clear();
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

        private void ReceivedChannelPresence(IChannelPresenceEvent e)
        {
            if (!_channels.ContainsKey(e.ChannelId)) return;
            if (!_participantsByChannel.TryGetValue(e.ChannelId, out var dict))
            {
                dict = new Dictionary<string, IUserPresence>();
                _participantsByChannel[e.ChannelId] = dict;
            }
            
            foreach (var p in e.Joins)
            {
                dict[p.UserId] = p;
            }
            
            foreach (var p in e.Leaves)
            {
                if (dict.ContainsKey(p.UserId)) dict.Remove(p.UserId);
            }

            OnPresenceChanged?.Invoke(e);
        }

        public IReadOnlyCollection<IUserPresence> GetParticipants(string channelId)
        {
            if (string.IsNullOrWhiteSpace(channelId)) return Array.Empty<IUserPresence>();
            if (_participantsByChannel.TryGetValue(channelId, out var dict))
            {
                var arr = new IUserPresence[dict.Count];
                dict.Values.CopyTo(arr, 0);
                
                return arr;
            }

            if (_channels.TryGetValue(channelId, out var ch))
            {
                var seeded = new Dictionary<string, IUserPresence>();
                
                if (ch.Self != null) seeded[ch.Self.UserId] = ch.Self;
                if (ch.Presences != null)
                {
                    foreach (var p in ch.Presences) seeded[p.UserId] = p;
                }
                
                _participantsByChannel[channelId] = seeded;
                
                var arr = new IUserPresence[seeded.Count];
                seeded.Values.CopyTo(arr, 0);
                
                return arr;
            }

            return Array.Empty<IUserPresence>();
        }

        public async Task PrefetchUsersAsync(string channelId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(channelId)) return;
            var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");
            
            var participants = GetParticipants(channelId);
            if (participants.Count == 0) return;
            if (!_participantsByChannel.TryGetValue(channelId, out var dict)) return;

            var toFetch = new List<string>();
            foreach (var userId in dict.Keys)
            {
                if (_userCache.ContainsKey(userId)) continue;
                toFetch.Add(userId);
            }

            if (toFetch.Count == 0) return;

            await _lookupGate.WaitAsync(ct);
            try
            {
                var batch = new List<string>();
                foreach (var userId in toFetch)
                {
                    if (_userCache.ContainsKey(userId)) continue;
                    batch.Add(userId);
                }

                if (batch.Count == 0) return;

                var users = await _clientService.Client.GetUsersAsync(session, batch, canceller: ct);
                if (users?.Users != null)
                {
                    foreach (var u in users.Users)
                    {
                        _userCache[u.Id] = u;
                    }
                }
            }
            finally
            {
                _lookupGate.Release();
            }
        }

        public IApiUser GetUser(string userId)
        {
            return string.IsNullOrEmpty(userId) ? null : _userCache.GetValueOrDefault(userId);
        }
        
        public void Dispose()
        {
            _clientService.OnConnected -= Connected;
            _clientService.OnDisconnected -= Disconnected;
            
            Disconnected();
        }
    }
}