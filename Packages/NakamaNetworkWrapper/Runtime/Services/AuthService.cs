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
        
        public ISession Session { get; private set; }

        public bool IsAuthenticated => Session is { IsExpired: false };
        
        public IApiAccount Account { get; private set; }
        
        public event Action<ISession> OnAuthenticated;
        public event Action<IApiAccount> OnAccountChanged;
        public event Action<Exception> OnAuthenticationFailed;
        public event Action OnLoggedOut;
        public event Action OnSessionExpired;
        
        public async Task<ISession> LoginAsync(AuthType type, string id, string username = null, Dictionary<string, string> vars = null)
        {
            await _gate.WaitAsync();
            try
            {
                if (IsAuthenticated)
                {
                    if (_clientService.IsConnected)
                    {
                        return Session;
                    }

                    try
                    {
                        await RefreshSessionIfNeededAsync();

                        await _clientService.ConnectAsync(Session);

                        Account = await _clientService.Client.GetAccountAsync(Session, canceller: _clientService.ShutdownToken);
                        Authenticated(Session);

                        return Session;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Existing session path failed, falling back to fresh auth: {ex.Message}");
                        
                        _tokenPersistence?.Clear();
                        Session = null;
                    }
                }
                
                var session = await AuthenticateAsync(type, id, username, vars);
                await _clientService.ConnectAsync(session);

                Session = session;
                SaveTokensIfPossible(session);
            
                Account = await _clientService.Client.GetAccountAsync(session, canceller: _clientService.ShutdownToken);
                Authenticated(session);
            
                return session;
            }
            catch (Exception ex)
            {
                Session = null;
                Account = null;
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
            await _gate.WaitAsync();
            try
            {
                if (Session == null)
                {
                    return;
                }

                await _clientService.DisconnectAsync();

                Session = null;
                Account = null;
                _tokenPersistence?.Clear();

                LoggedOut();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (Session == null) return false;
                if (_clientService.IsConnected) return true;

                try
                {
                    await RefreshSessionIfNeededAsync();
                }
                catch (ApiResponseException ex) when (ex.StatusCode == 401 || ex.StatusCode == 403)
                {
                    // Refresh token is no longer accepted — the session is unrecoverable.
                    ExpireSessionLocked();
                    return false;
                }

                if (Session.IsExpired)
                {
                    // Still expired after the refresh attempt — there is no usable refresh
                    // token, so connecting would just fail forever. Escalate instead of
                    // letting the reconnect loop spin with a dead token.
                    ExpireSessionLocked();
                    return false;
                }

                await _clientService.ConnectAsync(Session);

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Reconnect attempt failed: {ex.Message}");
                return false;
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
                    var session = Nakama.Session.Restore(authToken, refreshToken);
                    session = await RefreshIfExpiredAsync(session);

                    if (session.IsExpired)
                    {
                        _tokenPersistence.Clear();
                        return false;
                    }

                    await _clientService.ConnectAsync(session);

                    Session = session;
                    
                    Account = await _clientService.Client.GetAccountAsync(session, canceller: _clientService.ShutdownToken);
                    Authenticated(session);
                    
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to restore session: {ex.Message}");
                    
                    _tokenPersistence.Clear();
                    Session = null;
                    
                    return false;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        
        public async Task UpdateAccountAsync(string username = null, string displayName = null, string avatarUrl = null, string langTag = null, string location = null, string timezone = null, CancellationToken ct = default)
        {
            if (Session == null) throw new InvalidOperationException("Not authenticated");

            await _gate.WaitAsync(ct);
            try
            {
                await _clientService.Client.UpdateAccountAsync(Session, username, displayName, avatarUrl, langTag, location, timezone, canceller: ct);
                Account = await _clientService.Client.GetAccountAsync(Session, canceller: ct);
            }
            catch (ApiResponseException ex)
            {
                Debug.LogError($"UpdateAccount failed: {ex.StatusCode} {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogError($"UpdateAccount failed: {ex.Message}");
                throw;
            }
            finally
            {
                _gate.Release();
            }

            // Only reached on success — a failed update must not re-announce the stale account.
            OnAccountChanged?.Invoke(Account);
        }
        
        private void Authenticated(ISession session) => OnAuthenticated?.Invoke(session);

        private void AuthenticationFailed(Exception ex)
        {
            Debug.LogError($"Authentication Failed: {ex.GetType().Name} - {ex.Message}");
            OnAuthenticationFailed?.Invoke(ex);
        }
        
        private void LoggedOut() => OnLoggedOut?.Invoke();

        private void SessionExpired() => OnSessionExpired?.Invoke();

        /// <summary>Drops the unrecoverable session and announces it. Must be called while holding the gate.</summary>
        private void ExpireSessionLocked()
        {
            _tokenPersistence?.Clear();
            Session = null;
            Account = null;

            SessionExpired();
        }

        private async Task RefreshSessionIfNeededAsync()
        {
            if (Session == null) return;
            if (string.IsNullOrEmpty(Session.RefreshToken)) return;
            // Refresh slightly ahead of expiry so the socket never connects with a token
            // that dies mid-handshake.
            if (!Session.HasExpired(DateTime.UtcNow.AddMinutes(1))) return;

            var refreshed = await _clientService.Client.SessionRefreshAsync(Session, canceller: _clientService.ShutdownToken);
            Session = refreshed;

            SaveTokensIfPossible(refreshed);
        }

        private async Task<ISession> AuthenticateAsync(AuthType type, string id, string username, Dictionary<string, string> vars)
        {
            switch (type)
            {
                case AuthType.Custom:
                    if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Custom id must be provided", nameof(id));
                    
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_clientService.ShutdownToken))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(10));
                        return await _clientService.Client.AuthenticateCustomAsync(id, username, true, vars, canceller: cts.Token);
                    }
                
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