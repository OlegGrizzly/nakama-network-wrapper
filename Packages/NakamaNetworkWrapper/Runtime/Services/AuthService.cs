using System;
using System.Collections.Generic;
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
            _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
            _tokenPersistence = tokenPersistence;
        }
        
        public ISession CurrentSession { get; private set; }

        public bool IsAuthenticated => CurrentSession is { IsExpired: false };
        
        public IApiAccount Account { get; private set; }

        public event Action<ISession> OnAuthenticated;
        public event Action<Exception> OnAuthenticationFailed;
        public event Action OnLoggedOut;
        
        public async Task<ISession> LoginAsync(AuthType type, string id, string username = null, Dictionary<string, string> vars = null)
        {
            await _gate.WaitAsync();
            try
            {
                if (IsAuthenticated)
                {
                    if (_clientService.IsConnected)
                    {
                        return CurrentSession;
                    }

                    try
                    {
                        await RefreshSessionIfNeededAsync();
                        
                        await _clientService.ConnectAsync(CurrentSession);
                        
                        Account = await _clientService.Client.GetAccountAsync(CurrentSession, canceller: _clientService.ShutdownToken);
                        Authenticated(CurrentSession);

                        return CurrentSession;
                    }
                    catch (Exception ex)
                    {
                        AuthenticationFailed(ex);
                        throw;
                    }
                }
                
                var session = await AuthenticateAsync(type, id, username, vars);
                await _clientService.ConnectAsync(session);

                CurrentSession = session;
                SaveTokensIfPossible(session);
            
                Account = await _clientService.Client.GetAccountAsync(session, canceller: _clientService.ShutdownToken);
                Authenticated(session);
            
                return session;
            }
            catch (Exception ex)
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
                    session = await RefreshIfExpiredAsync(session);

                    if (session.IsExpired)
                    {
                        _tokenPersistence.Clear();
                        return false;
                    }

                    await _clientService.ConnectAsync(session);

                    CurrentSession = session;
                    
                    Account = await _clientService.Client.GetAccountAsync(session, canceller: _clientService.ShutdownToken);
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
        
        private void Authenticated(ISession session) => OnAuthenticated?.Invoke(session);

        private void AuthenticationFailed(Exception ex)
        {
            Debug.LogError($"Authentication Failed: {ex.GetType().Name} - {ex.Message}");
            OnAuthenticationFailed?.Invoke(ex);
        }
        
        private void LoggedOut() => OnLoggedOut?.Invoke();

        private async Task RefreshSessionIfNeededAsync()
        {
            if (CurrentSession == null) return;
            if (!CurrentSession.IsExpired) return;
            if (string.IsNullOrEmpty(CurrentSession.RefreshToken)) return;

            var refreshed = await _clientService.Client.SessionRefreshAsync(CurrentSession, canceller: _clientService.ShutdownToken);
            CurrentSession = refreshed;
            
            SaveTokensIfPossible(refreshed);
        }

        private async Task<ISession> AuthenticateAsync(AuthType type, string id, string username, Dictionary<string, string> vars)
        {
            switch (type)
            {
                case AuthType.Custom:
                    if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Custom id must be provided", nameof(id));
                    return await _clientService.Client.AuthenticateCustomAsync(id, username, true, vars, canceller: _clientService.ShutdownToken);
                
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        private async Task<ISession> RefreshIfExpiredAsync(ISession session)
        {
            if (session.IsExpired && !string.IsNullOrEmpty(session.RefreshToken))
            {
                var refreshed = await _clientService.Client.SessionRefreshAsync(session, canceller: _clientService.ShutdownToken);
                SaveTokensIfPossible(refreshed);
                
                return refreshed;
            }

            return session;
        }

        private void SaveTokensIfPossible(ISession session)
        {
            _tokenPersistence?.Save(session.AuthToken, session.RefreshToken);
        }
    }
}