using System;
using System.Threading;
using System.Threading.Tasks;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using UnityEngine;

namespace OlegGrizzly.NakamaNetworkWrapper.Controllers
{
    /// <summary>
    /// Restores the socket connection after unexpected drops using exponential
    /// backoff with jitter. Stops on success, manual logout or session expiry.
    /// </summary>
    public sealed class ReconnectController : IDisposable
    {
        private readonly IAuthService _authService;
        private readonly IClientService _clientService;
        private readonly System.Random _random = new();

        private CancellationTokenSource _loopCts;
        private bool _disposed;

        public ReconnectController(IAuthService authService, IClientService clientService)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));

            _clientService.OnConnectionLost += ConnectionLost;
            _clientService.OnConnected += Connected;
            _authService.OnLoggedOut += LoggedOut;
            _authService.OnSessionExpired += SessionExpired;
        }

        public bool IsReconnecting => _loopCts != null;

        public event Action<int> OnReconnectAttempt;
        public event Action OnReconnectFailed;

        /// <summary>
        /// Kicks off an immediate reconnect if there is a session but no live socket.
        /// Call from OnApplicationFocus/OnApplicationPause(false).
        /// </summary>
        public void EnsureConnection()
        {
            if (_disposed) return;
            if (_clientService.IsConnected) return;
            if (_authService.Session == null) return;

            // Discard any in-flight backoff delay (it can be up to ReconnectMaxDelayMs)
            // so a resume-from-background reconnects immediately.
            StopLoop();
            StartLoop(true);
        }

        private void ConnectionLost() => StartLoop(false);

        private void Connected() => StopLoop();

        private void LoggedOut() => StopLoop();

        private void SessionExpired() => StopLoop();

        private void StartLoop(bool immediate)
        {
            if (_disposed) return;
            if (_loopCts != null) return;

            var config = _clientService.Config;
            if (config != null && !config.AutoReconnect) return;

            var cts = new CancellationTokenSource();
            _loopCts = cts;

            _ = RunLoopAsync(immediate, cts);
        }

        private void StopLoop()
        {
            var cts = _loopCts;
            if (cts == null) return;

            _loopCts = null;

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The loop's finally already disposed it — nothing left to cancel.
            }

            cts.Dispose();
        }

        private async Task RunLoopAsync(bool immediate, CancellationTokenSource ownCts)
        {
            var token = ownCts.Token;
            var config = _clientService.Config;
            var baseDelay = Math.Max(100, config != null ? config.ReconnectBaseDelayMs : 1000);
            var maxDelay = Math.Max(baseDelay, config != null ? config.ReconnectMaxDelayMs : 30000);
            var maxAttempts = config != null ? config.ReconnectMaxAttempts : 0;

            var attempt = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    attempt++;
                    if (maxAttempts > 0 && attempt > maxAttempts)
                    {
                        Debug.LogWarning($"[Reconnect] Given up after {maxAttempts} attempts");
                        RaiseSafe(OnReconnectFailed);
                        break;
                    }

                    if (!immediate || attempt > 1)
                    {
                        await Task.Delay(ComputeDelayMs(attempt, baseDelay, maxDelay), token);
                    }

                    if (_clientService.IsConnected) break;
                    if (_authService.Session == null) break;

                    RaiseSafe(OnReconnectAttempt, attempt);

                    var reconnected = await _authService.TryReconnectAsync(token);
                    if (reconnected) break;
                }
            }
            catch (OperationCanceledException)
            {
                // Loop stopped: connected, logged out, session expired or disposed.
            }
            catch (Exception ex)
            {
                // The loop runs fire-and-forget — never let it die silently.
                Debug.LogError($"[Reconnect] Loop crashed: {ex}");
            }
            finally
            {
                if (ReferenceEquals(_loopCts, ownCts))
                {
                    _loopCts = null;
                }

                ownCts.Dispose();
            }
        }

        private static void RaiseSafe(Action handler)
        {
            try
            {
                handler?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Reconnect] Event handler threw: {ex}");
            }
        }

        private static void RaiseSafe(Action<int> handler, int arg)
        {
            try
            {
                handler?.Invoke(arg);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Reconnect] Event handler threw: {ex}");
            }
        }

        private int ComputeDelayMs(int attempt, int baseDelay, int maxDelay)
        {
            // Equal jitter: half fixed exponential part + half random.
            var exponential = Math.Min(maxDelay, baseDelay * Math.Pow(2, attempt - 1));
            var half = (int)(exponential / 2);

            return half + _random.Next(half + 1);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            _clientService.OnConnectionLost -= ConnectionLost;
            _clientService.OnConnected -= Connected;
            _authService.OnLoggedOut -= LoggedOut;
            _authService.OnSessionExpired -= SessionExpired;

            StopLoop();
        }
    }
}
