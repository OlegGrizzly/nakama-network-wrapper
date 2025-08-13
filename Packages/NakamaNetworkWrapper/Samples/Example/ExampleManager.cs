using System;
using System.Threading.Tasks;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Common;
using OlegGrizzly.NakamaNetworkWrapper.Config;
using OlegGrizzly.NakamaNetworkWrapper.Controllers;
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
        private IStorageService _storageService;
        private ConnectionStateMachine _stateMachine;

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
            
            var tokenPersistence = new PlayerPrefsTokenPersistence();
            _authService = new AuthService(_clientService, tokenPersistence);
            _storageService = new StorageService(_clientService, _authService);
            
            _stateMachine  = new ConnectionStateMachine(_authService, _clientService);
            _stateMachine.OnStateChanged += UpdateUiByState;

            UpdateUiByState(_stateMachine.CurrentState);
            
            var restored = await _authService.TryRestoreSessionAsync();
            if (!restored)
            {
                var deviceId = _deviceIdProvider.GetDeviceId();
                await _authService.LoginAsync(AuthType.Custom, deviceId);
            }
            
            await TestStorageAsync();
        }

        private async void OnConnectClicked()
        {
            var id = _deviceIdProvider.GetDeviceId();
            SetStatus($"DeviceID: {id}", Color.cyan);
            await _authService.LoginAsync(AuthType.Custom, id);
            
            await TestStorageAsync();
        }

        private async void OnDisconnectClicked()
        {
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
        
        private void UpdateUiByState(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Connecting:
                    SetStatus("Connecting…", Color.yellow);
                    break;

                case ConnectionState.Connected:
                    SetStatus("Connected", Color.green);
                    break;

                case ConnectionState.Disconnected:
                    SetStatus("Disconnected", Color.red);
                    break;

                case ConnectionState.AuthFailed:
                    SetStatus("Auth failed", Color.red);
                    break;
            }
            
            connectButton.interactable = state is ConnectionState.Disconnected or ConnectionState.AuthFailed;
            disconnectButton.interactable = state == ConnectionState.Connected;
        }
        
        private void SetStatus(string text, Color color) => AddLog(text, color);

        private void OnDestroy()
        {
            _stateMachine?.Dispose();
        }

        private async Task TestStorageAsync()
        {
            try
            {
                var collection = "inventory";
                var key = "v1";
                var json = "{\"coins\":123,\"items\":[\"sword\",\"potion\"]}";

                await _storageService.WriteAsync(collection, key, json);
                AddLog("Storage write OK", Color.white);

                var readBack = await _storageService.ReadAsync<string>(collection, key);
                AddLog($"Storage read: {readBack}", Color.white);
            }
            catch (Exception ex)
            {
                AddLog($"Storage error: {ex.Message}", Color.red);
            }
        }
    }
}
