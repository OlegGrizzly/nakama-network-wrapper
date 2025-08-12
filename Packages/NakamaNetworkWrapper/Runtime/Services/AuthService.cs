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
        private readonly ITokenPersistence _tokenPersistence;
        
        public AuthService(IClientService clientService, ITokenPersistence tokenPersistence = null)
        {
            _clientService = clientService;
            _tokenPersistence = tokenPersistence;
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

                await _clientService.ConnectAsync(session);

                CurrentSession = session;
                _tokenPersistence?.Save(session.AuthToken, session.RefreshToken);
            
                Authenticated(session);
            
                return session;
            }
            catch (Exception ex) when (ex is ApiResponseException or TaskCanceledException or HttpRequestException)
            {
                CurrentSession = null;
                AuthenticationFailed(ex);
                throw;
            }
        }

        public async Task LogoutAsync()
        {
            await _clientService.DisconnectAsync();
            CurrentSession = null;
            _tokenPersistence?.Clear();

            LoggedOut();
        }
        
        public async Task<bool> TryRestoreSessionAsync()
        {
            if (_tokenPersistence is not { HasToken: true })
            {
                return false;
            }

            if (!_tokenPersistence.TryLoad(out var authToken, out var refreshToken))
            {
                return false;
            }

            try
            {
                var session = Session.Restore(authToken, refreshToken);
                
                if (session.IsExpired && !string.IsNullOrEmpty(session.RefreshToken))
                {
                    session = await _clientService.Client.SessionRefreshAsync(session);
                    _tokenPersistence.Save(session.AuthToken, session.RefreshToken);
                }

                if (session.IsExpired)
                {
                    _tokenPersistence.Clear();
                    return false;
                }

                await _clientService.ConnectAsync(session);

                CurrentSession = session;
                Authenticated(session);
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to restore session: {ex.Message}");
                _tokenPersistence.Clear();
                
                return false;
            }
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