using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Common;
using OlegGrizzly.NakamaNetworkWrapper.Config;
using OlegGrizzly.NakamaNetworkWrapper.Persistence;
using OlegGrizzly.NakamaNetworkWrapper.Services;
using OlegGrizzly.NakamaNetworkWrapper.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace Samples.Example
{
    public class ExampleManager : MonoBehaviour
    {
        [SerializeField] private ConnectionConfig config;
        [SerializeField] private Button connectButton;
        [SerializeField] private Button disconnectButton;
        [SerializeField] private ScrollRect scrollRect;
        [SerializeField] private Text loggerText;
        
        private IClientService _clientService;
        private IDeviceIdProvider _deviceIdProvider;
        private IAuthService _authService;

        private void Awake()
        {
            connectButton.interactable = false;
            disconnectButton.interactable = false;

            connectButton.onClick.AddListener(OnConnectClicked);
            disconnectButton.onClick.AddListener(OnDisconnectClicked);
        }
        
        private async void Start()
        {
            if (config == null)
            {
                loggerText.text = "ConnectionConfig not assigned";
                return;
            }

            _clientService = new ClientService(config);
            _deviceIdProvider = new DeviceIdProvider();
            var deviceId = _deviceIdProvider.GetDeviceId();
            SetStatus($"DeviceID: {deviceId}", Color.cyan);
            
            var tokenPersistence = new PlayerPrefsTokenPersistence();
            _authService = new AuthService(_clientService, tokenPersistence);
            
            _clientService.OnConnecting += () => SetStatus("Connecting…", Color.yellow);
            _clientService.OnConnected += () => SetStatus("Connected", Color.green);
            _clientService.OnDisconnected += () =>
            {
                SetStatus("Disconnected", Color.red);
                
                connectButton.interactable = true;
                disconnectButton.interactable = false;
            };
            _clientService.OnRetrying += () => SetStatus("Retrying…", Color.yellow);
            _clientService.OnReceivedError += ex => SetStatus($"Error: {ex.Message}", Color.red);

            _authService.OnAuthenticated += _ =>
            {
                SetStatus("Authenticated", Color.green);
                
                connectButton.interactable = false;
                disconnectButton.interactable = true;
            };
            
            _authService.OnLoggedOut += () =>
            {
                SetStatus("LoggedOut", Color.red);
                
                connectButton.interactable = true;
                disconnectButton.interactable = false;
            };
            
            _authService.OnAuthenticationFailed += ex => SetStatus($"Auth failed: {ex.Message}", Color.red);
            _authService.OnAuthenticationFailed += ex =>
            {
                SetStatus($"Auth failed: {ex.Message}", Color.red);
                
                connectButton.interactable = true;
                disconnectButton.interactable = false;
            };
            
            var restored = await _authService.TryRestoreSessionAsync();
            if (!restored)
            {
                connectButton.interactable = false;
                await _authService.LoginAsync(AuthType.Custom, deviceId);
            }
            else
            {
                disconnectButton.interactable = true;
            }
        }

        private async void OnConnectClicked()
        {
            connectButton.interactable = false;
            
            var id = _deviceIdProvider.GetDeviceId();
            SetStatus($"DeviceID: {id}", Color.cyan);
            await _authService.LoginAsync(AuthType.Custom, id);
        }

        private async void OnDisconnectClicked()
        {
            disconnectButton.interactable = false;
            await _authService.LogoutAsync();
        }

        private void AddLog(string text, Color color)
        {
            var html = $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";
            loggerText.text += html + "\n";
            
            if (scrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                scrollRect.verticalNormalizedPosition = 0f;
            }
        }

        private void SetStatus(string text, Color color) => AddLog(text, color);
    }
}
