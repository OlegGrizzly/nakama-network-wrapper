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
        [SerializeField] private ConnectionConfig config;
        
        private IClientService _clientService;
        private IAuthService _authService;

        private async void Start()
        {
            _clientService = new ClientService(config);
            var tokenPersistence = new PlayerPrefsTokenPersistence();
            var deviceIdProvider = new DeviceIdProvider();

            _authService = new AuthService(_clientService, tokenPersistence);
            
            var restored = await _authService.TryRestoreSessionAsync();
            if (!restored)
            {
                var deviceId = deviceIdProvider.GetDeviceId();
                await _authService.LoginAsync(AuthType.Custom, deviceId);
            }
        }
    }
}