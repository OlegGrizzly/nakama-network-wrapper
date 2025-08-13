using System;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Common;

namespace OlegGrizzly.NakamaNetworkWrapper.Controllers
{
    public sealed class ConnectionStateMachine : IDisposable
    {
        private readonly IAuthService _authService;
        private readonly IClientService _clientService;
        private bool _disposed;

        public ConnectionStateMachine(IAuthService authService, IClientService clientService)
        {
            _authService = authService  ?? throw new ArgumentNullException(nameof(authService));
            _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
            
            _clientService.OnConnecting += Connecting;
            _clientService.OnRetrying += Retrying;
            _clientService.OnConnected += Connected;
            _clientService.OnDisconnected += Disconnected;

            _authService.OnAuthenticated += Authenticated;
            _authService.OnAuthenticationFailed += AuthFailed;
            _authService.OnLoggedOut += LoggedOut;

            CurrentState = ConnectionState.Disconnected;
        }
        
        public ConnectionState CurrentState { get; private set; }
        
        public event Action<ConnectionState> OnStateChanged;

        private void SetState(ConnectionState newState)
        {
            if (CurrentState == newState) return;
            
            CurrentState = newState;
            
            OnStateChanged?.Invoke(newState);
        }

        private void Connecting() => SetState(ConnectionState.Connecting);
        
        private void Retrying() => SetState(ConnectionState.Connecting);
        
        private void Connected() => SetState(ConnectionState.Connected);
        
        private void Disconnected() => SetState(ConnectionState.Disconnected);
        
        private void Authenticated(ISession _) => SetState(ConnectionState.Connected);
        
        private void AuthFailed(Exception _) => SetState(ConnectionState.AuthFailed);
        
        private void LoggedOut() => SetState(ConnectionState.Disconnected);

        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;

            _clientService.OnConnecting -= Connecting;
            _clientService.OnRetrying -= Retrying;
            _clientService.OnConnected -= Connected;
            _clientService.OnDisconnected -= Disconnected;

            _authService.OnAuthenticated -= Authenticated;
            _authService.OnAuthenticationFailed -= AuthFailed;
            _authService.OnLoggedOut -= LoggedOut;
        }
    }
}
