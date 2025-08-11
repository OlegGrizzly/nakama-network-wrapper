using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Common;
using OlegGrizzly.NakamaNetworkWrapper.Config;
using OlegGrizzly.NakamaNetworkWrapper.Services;
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
            _authService = new AuthService(_clientService);
            
            await _authService.LoginAsync(AuthType.Custom, "OlegGrizzly");
        }
    }
}