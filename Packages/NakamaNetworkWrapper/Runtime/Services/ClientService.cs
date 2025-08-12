using System;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Config;
using UnityEngine;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public sealed class ClientService : IClientService
    {
        private readonly IClient _client;
        private ISocket _socket;
        private readonly ConnectionConfig _config;
        private bool _disposed;
        
        public ClientService(ConnectionConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _client = new Client(config.Scheme, config.Host, config.Port, config.ServerKey, UnityWebRequestAdapter.Instance, config.AutoRefreshSession);
            _client.GlobalRetryConfiguration = _config.GetRetryConfiguration();
        }
        
        public IClient Client => _client;
        public ISocket Socket => _socket;
        public ConnectionConfig Config => _config;
        
        public bool IsConnected => _socket?.IsConnected == true;
        
        public event Action OnConnecting;
        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<Exception> OnReceivedError;
        
        public async Task ConnectAsync(ISession session)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientService));
            if (session == null) throw new ArgumentNullException(nameof(session));

            if (_socket is { IsConnected: true })
            {
                return;
            }

            _socket ??= _client.NewSocket(true);
            
            DetachSocketEvents(_socket);
            AttachSocketEvents(_socket);

            Connecting();
            
            try
            {
                await _socket.ConnectAsync(session, _config.AppearOnline, _config.ConnectTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DetachSocketEvents(_socket);
                _socket = null;
                 
                ReceivedError(ex);
                throw;
            }
        }
        
        public async Task DisconnectAsync()
        {
            if (_disposed) return;

            if (_socket != null)
            {
                try
                {
                    await _socket.CloseAsync();
                }
                finally
                {
                    DetachSocketEvents(_socket);
                    _socket = null;
                }
            }
        }
        
        private void AttachSocketEvents(ISocket socket)
        {
            socket.Connected += Connected;
            socket.Closed += Disconnected;
            socket.ReceivedError += ReceivedError;
        }

        private void DetachSocketEvents(ISocket socket)
        {
            if (socket == null) return;
            
            socket.Connected -= Connected;
            socket.Closed -= Disconnected;
            socket.ReceivedError -= ReceivedError;
        }

        private void Connecting()
        {
            Debug.Log("<color=orange>Connecting...</color>");
            OnConnecting?.Invoke();
        }

        private void Connected()
        {
            Debug.Log("<color=green>Connected</color>");
            OnConnected?.Invoke();
        }

        private void Disconnected()
        {
            Debug.Log("<color=red>Disconnected</color>");
            OnDisconnected?.Invoke();
        }

        private void ReceivedError(Exception error)
        {
            Debug.LogError(error);
            OnReceivedError?.Invoke(error);
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_socket != null)
            {
                DetachSocketEvents(_socket);
                
                _socket.CloseAsync().ConfigureAwait(false);
                _socket = null;
            }
        }
    }
}