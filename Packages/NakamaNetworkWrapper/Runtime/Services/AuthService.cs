using System;
using System.Collections.Generic;
using System.Net.Http;
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
        
        public AuthService(IClientService clientService)
        {
            _clientService = clientService;
        }
        
        public ISession CurrentSession { get; private set; }

        public bool IsAuthenticated => CurrentSession is { IsExpired: false };

        public event Action<ISession> OnAuthenticated;
        public event Action<Exception> OnAuthenticationFailed;
        public event Action OnLoggedOut;
        
        public async Task<ISession> LoginAsync(AuthType type, string id, string username = null, Dictionary<string, string> vars = null)
        {
            try
            {
                ISession session;
                switch (type)
                {
                    case AuthType.Custom:
                        session = await _clientService.Client.AuthenticateCustomAsync(id, username, true, vars);
                        break;
                    
                    default:
                        throw new ArgumentOutOfRangeException(nameof(type));
                }

                CurrentSession = session;
            
                await _clientService.ConnectAsync(session);

                Authenticated(session);
            
                return session;
            }
            catch (Exception ex) when (ex is ApiResponseException or TaskCanceledException or HttpRequestException)
            {
                AuthenticationFailed(ex);
                throw;
            }
        }

        public async Task LogoutAsync()
        {
            await _clientService.DisconnectAsync();
            CurrentSession = null;

            LoggedOut();
        }
        
        private void Authenticated(ISession session)
        {
            Debug.Log("<color=green>Authenticated</color>");
            OnAuthenticated?.Invoke(session);
        }
        
        private void AuthenticationFailed(Exception ex)
        {
            Debug.LogError($"Authentication Failed: {ex.GetType().Name} - {ex.Message}");
            OnAuthenticationFailed?.Invoke(ex);
        }
        
        private void LoggedOut()
        {
            Debug.Log("<color=red>LoggedOut</color>");
            OnLoggedOut?.Invoke();
        }
    }
}