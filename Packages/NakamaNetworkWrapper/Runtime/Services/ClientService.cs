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
        private readonly ConnectionConfig _config;
        private readonly SemaphoreSlim _connectionGate = new(1, 1);

        private ISocket _socket;
        private UnitySocket _socketAdapter;
        private bool _disposed;
        private bool _manualDisconnect;
        private bool _notifiedConnected;
        private CancellationTokenSource _shutdownCts;

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
        public event Action OnConnectionLost;
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

                EnsureSocket();

                _manualDisconnect = false;

                Connecting();

                try
                {
                    await _socket.ConnectAsync(session, _config.AppearOnline, _config.ConnectTimeout);
                }
                catch (Exception ex)
                {
                    ReceivedError(ex);

                    // Let state listeners leave the "Connecting" state.
                    OnDisconnected?.Invoke();
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
                _manualDisconnect = true;

                if (_socket != null)
                {
                    try
                    {
                        await _socket.CloseAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Socket close failed: {ex.Message}");
                    }
                }

                // Guarantees an immediate, exactly-once notification on every platform;
                // the queued socket Closed event becomes a no-op afterwards.
                HandleSocketClosed();

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

        private void EnsureSocket()
        {
            if (_socket != null) return;

            // The socket is created once and reused for every reconnect: each
            // ClientExtensions.NewSocket(useMainThread: true) call would leak a
            // "[Nakama Socket]" DontDestroyOnLoad GameObject per connect.
#if UNITY_WEBGL && !UNITY_EDITOR
            ISocketAdapter transport = new JsWebSocketAdapter();
#else
            ISocketAdapter transport = new WebSocketStdlibAdapter();
#endif
            _socketAdapter = UnitySocket.Create(transport);
            _socket = Nakama.Socket.From(_client, _socketAdapter);

            AttachSocketEvents(_socket);
        }

        private void AttachSocketEvents(ISocket socket)
        {
            socket.Connected += HandleSocketConnected;
            socket.Closed += HandleSocketClosed;
            socket.ReceivedError += ReceivedError;
        }

        private void DetachSocketEvents(ISocket socket)
        {
            if (socket == null) return;

            socket.Connected -= HandleSocketConnected;
            socket.Closed -= HandleSocketClosed;
            socket.ReceivedError -= ReceivedError;
        }

        private void Connecting() => OnConnecting?.Invoke();

        private void HandleSocketConnected()
        {
            _notifiedConnected = true;
            OnConnected?.Invoke();
        }

        private void HandleSocketClosed()
        {
            if (!_notifiedConnected) return;

            _notifiedConnected = false;
            var manual = _manualDisconnect;

            OnDisconnected?.Invoke();

            if (!manual)
            {
                OnConnectionLost?.Invoke();
            }
        }

        private void Retrying()
        {
            // HTTP retries firing while the socket transport is actually dead but its
            // Closed event never arrived (e.g. WebGL tab suspend) — synthesize the close
            // so listeners and the reconnect loop learn about the drop.
            if (_notifiedConnected && _socket is { IsConnected: false })
            {
                HandleSocketClosed();
            }

            OnRetrying?.Invoke();
        }

        private void ReceivedError(Exception error)
        {
            Debug.LogError(error);
            OnReceivedError?.Invoke(error);
        }

        private void ResetShutdownCts()
        {
            var previous = _shutdownCts;
            _shutdownCts = new CancellationTokenSource();

            previous.Cancel();
            previous.Dispose();
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

            if (_socketAdapter != null)
            {
                UnityEngine.Object.Destroy(_socketAdapter.gameObject);
                _socketAdapter = null;
            }

            _connectionGate.Dispose();
        }
    }
}
