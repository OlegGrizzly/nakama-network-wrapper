using System;
using System.Threading;
using System.Threading.Tasks;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Common;
using OlegGrizzly.NakamaNetworkWrapper.Config;
using OlegGrizzly.NakamaNetworkWrapper.Services;
using OlegGrizzly.NakamaNetworkWrapper.Persistence;
using OlegGrizzly.NakamaNetworkWrapper.Utils;
using UnityEngine;

namespace OlegGrizzly.NakamaNetworkWrapper.Controllers
{
    public class NakamaManager : MonoBehaviour
    {
        private const int SignInRetryDelaySeconds = 5;

        [SerializeField] private ConnectionConfig config;

        private IClientService _clientService;
        private IAuthService _authService;
        private IDeviceIdProvider _deviceIdProvider;
        private ReconnectController _reconnectController;
        private CancellationTokenSource _lifetimeCts;
        private bool _signInInProgress;

        private async void Start()
        {
            _lifetimeCts = new CancellationTokenSource();

            _clientService = new ClientService(config);
            var tokenPersistence = new PlayerPrefsTokenPersistence();
            _deviceIdProvider = new DeviceIdProvider();

            _authService = new AuthService(_clientService, tokenPersistence);
            _authService.OnSessionExpired += SessionExpired;

            _reconnectController = new ReconnectController(_authService, _clientService);

            await SignInAsync(_lifetimeCts.Token);
        }

        private void SessionExpired()
        {
            if (_lifetimeCts == null || _lifetimeCts.IsCancellationRequested) return;

            _ = SignInAsync(_lifetimeCts.Token);
        }

        /// <summary>
        /// Signs in (restoring the stored session when possible) and retries on
        /// transient failures, so a network blip at startup does not leave the app
        /// permanently offline — the ReconnectController only kicks in after the
        /// first successful connect.
        /// </summary>
        private async Task SignInAsync(CancellationToken ct)
        {
            if (_signInInProgress) return;

            _signInInProgress = true;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var restored = await _authService.TryRestoreSessionAsync();
                        if (!restored)
                        {
                            await _authService.LoginAsync(AuthType.Custom, _deviceIdProvider.GetDeviceId());
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Sign-in failed, retrying in {SignInRetryDelaySeconds}s: {ex.Message}");
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(SignInRetryDelaySeconds), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
            finally
            {
                _signInInProgress = false;
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                _reconnectController?.EnsureConnection();
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused)
            {
                _reconnectController?.EnsureConnection();
            }
        }

        private void OnDestroy()
        {
            if (_lifetimeCts != null)
            {
                _lifetimeCts.Cancel();
                _lifetimeCts.Dispose();
                _lifetimeCts = null;
            }

            if (_authService != null)
            {
                _authService.OnSessionExpired -= SessionExpired;
            }

            _reconnectController?.Dispose();
            _reconnectController = null;

            _clientService?.Dispose();
            _clientService = null;
        }
    }
}
