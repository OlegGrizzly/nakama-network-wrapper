using System;
using System.Threading;
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
        private CancellationTokenSource _shutdownCts;
        private readonly SemaphoreSlim _connectionGate = new(1, 1);
        
        public ClientService(ConnectionConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _client = new Client(config.Scheme, config.Host, config.Port, config.ServerKey, UnityWebRequestAdapter.Instance, config.AutoRefreshSession);
            _client.GlobalRetryConfiguration = _config.GetRetryConfiguration(Retrying);
            _client.Timeout = config.ConnectTimeout;
            
            _shutdownCts = new CancellationTokenSource();
        }
        
        public IClient Client => _client;
        public ISocket Socket => _socket;
        public ConnectionConfig Config => _config;
        public bool IsConnected => _socket?.IsConnected == true;
        public CancellationToken ShutdownToken => _shutdownCts.Token;
        
        public event Action OnConnecting;
        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action OnRetrying;
        public event Action<Exception> OnReceivedError;
        
        public async Task ConnectAsync(ISession session)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientService));
            if (session == null) throw new ArgumentNullException(nameof(session));

            await _connectionGate.WaitAsync(ShutdownToken);
            try
            {
                if (_socket is { IsConnected: true }) return;
                
                if (_socket != null)
                {
                    await CloseAndClearSocketAsync();
                }

                _socket = _client.NewSocket(true);
                AttachSocketEvents(_socket);

                Connecting();

                try
                {
                    await _socket.ConnectAsync(session, _config.AppearOnline, _config.ConnectTimeout);
                }
                catch (Exception ex)
                {
                    DetachSocketEvents(_socket);
                    _socket = null;

                    ReceivedError(ex);
                    throw;
                }
            }
            finally
            {
                _connectionGate.Release();
            }
        }
        
        public async Task DisconnectAsync()
        {
            if (_disposed) return;

            await _connectionGate.WaitAsync(ShutdownToken);
            try
            {
                if (_socket != null)
                {
                    await CloseAndClearSocketAsync();

                    #if UNITY_WEBGL && !UNITY_EDITOR
                    Disconnected();
                    #endif
                }

                ResetShutdownCts();
            }
            finally
            {
                _connectionGate.Release();
            }
        }
        
        public async Task<IApiRpc> RpcAsync(string rpcName, string payload = null)
        {
            if (string.IsNullOrWhiteSpace(rpcName)) throw new ArgumentException("RPC name required", nameof(rpcName));
            if (_socket is not { IsConnected: true }) throw new InvalidOperationException("Socket is not connected");
            
            try
            {
                return await _socket.RpcAsync(rpcName, payload ?? string.Empty);
            }
            catch (ApiResponseException ex)
            {
                Debug.LogError($"RPC '{rpcName}' failed: {ex.StatusCode} {ex.Message}");
                throw;
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

        private void Connecting() => OnConnecting?.Invoke();

        private void Connected() => OnConnected?.Invoke();

        private void Disconnected() => OnDisconnected?.Invoke();

        private void Retrying()
        {
            #if UNITY_WEBGL && !UNITY_EDITOR
            ResetShutdownCts();
            Disconnected();
            #else
            OnRetrying?.Invoke();
            #endif
        }

        private void ReceivedError(Exception error)
        {
            Debug.LogError(error);
            OnReceivedError?.Invoke(error);
        }

        private void ResetShutdownCts()
        {
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();
            _shutdownCts = new CancellationTokenSource();
        }

        private async Task CloseAndClearSocketAsync()
        {
            var socketToClose = _socket;
            if (socketToClose == null) return;

            try
            {
                DetachSocketEvents(socketToClose);
                await socketToClose.CloseAsync();
            }
            finally
            {
                if (ReferenceEquals(_socket, socketToClose))
                {
                    _socket = null;
                }
            }
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();

            if (_socket != null)
            {
                DetachSocketEvents(_socket);

                try
                {
                    _ = _socket.CloseAsync();
                }
                catch
                {
                    // ignored
                }

                _socket = null;
            }

            _connectionGate.Dispose();
        }
    }
}