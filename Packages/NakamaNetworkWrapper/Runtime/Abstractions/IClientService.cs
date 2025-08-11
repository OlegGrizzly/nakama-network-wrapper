using System;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Config;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IClientService : IDisposable
    {
        Task ConnectAsync(ISession session);
        
        Task DisconnectAsync();
        
        event Action OnConnecting;
        
        event Action OnConnected;
        
        event Action OnDisconnected;
        
        event Action<Exception> OnReceivedError;
        
        IClient Client { get; }
        
        ISocket Socket { get; }
        
        ConnectionConfig Config { get; }
        
        bool IsConnected { get; }
    }
}