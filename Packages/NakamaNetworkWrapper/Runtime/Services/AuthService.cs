using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
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
        private readonly SemaphoreSlim _gate = new(1,1);
        
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
            Debug.Log($"<color=cyan>DeviceID: {id}</color>");

            await _gate.WaitAsync();
            try
            {
                if (IsAuthenticated)
                {
                    return CurrentSession;
                }
                
                ISession session;
                switch (type)
                {
                    case AuthType.Custom:
                        session = await _clientService.Client.AuthenticateCustomAsync(id, username, true, vars, canceller: _clientService.ShutdownToken);
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
            finally
            {
                _gate.Release();
            }
        }

        public async Task LogoutAsync()
        {
            if (CurrentSession == null)
            {
                return;
            }

            await _gate.WaitAsync();
            try
            {
                await _clientService.DisconnectAsync();

                CurrentSession = null;
                _tokenPersistence?.Clear();

                LoggedOut();
            }
            finally
            {
                _gate.Release();
            }
        }
        
        public async Task<bool> TryRestoreSessionAsync()
        {
            await _gate.WaitAsync();
            try
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
            finally
            {
                _gate.Release();
            }
        }
        
        private void Authenticated(ISession session)
        {
            Debug.LogWarning("Authenticated");
            
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