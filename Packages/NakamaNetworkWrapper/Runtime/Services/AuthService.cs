using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Common;
using UnityEngine;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public class AuthService : IAuthService
    {
        private readonly IClientService _clientService;
        public ISession CurrentSession { get; private set; }

        public bool IsAuthenticated => CurrentSession is { IsExpired: false };

        public event Action<ISession> AuthSucceeded;
        public event Action<ApiResponseException> AuthFailed;
        public event Action LoggedOut;

        public AuthService(IClientService clientService)
        {
            _clientService = clientService;
        }

        public async Task<ISession> LoginAsync(AuthType type, string id, string username = null, Dictionary<string, string> vars = null)
        {
            var cancellationToken = _clientService.Config.GetCancellationToken();
            var retryConfig = _clientService.Config.GetRetryConfiguration();
            
            try
            {
                ISession session;
                switch (type)
                {
                    case AuthType.Custom:
                        session = await _clientService.Client.AuthenticateCustomAsync(id, username, true, vars, retryConfig, cancellationToken);
                        break;
                    
                    default:
                        throw new ArgumentOutOfRangeException(nameof(type));
                }

                CurrentSession = session;
            
                await _clientService.ConnectAsync(session);

                AuthSucceeded?.Invoke(session);
            
                return session;
            }
            catch (ApiResponseException ex)
            {
                Debug.LogError($"Ошибка авторизации: {ex.StatusCode} - {ex.Message}");
                
                AuthFailed?.Invoke(ex);
                throw;
            }
        }

        public async Task LogoutAsync()
        {
            await _clientService.DisconnectAsync();
            CurrentSession = null;
            
            LoggedOut?.Invoke();
        }
    }
}