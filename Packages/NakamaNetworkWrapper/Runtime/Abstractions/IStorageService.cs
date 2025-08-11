using System.Threading;
using System.Threading.Tasks;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface IStorageService
    {
        Task WriteAsync(string collection, string key, object value, CancellationToken ct = default);
        
        Task<T> ReadAsync<T>(string collection, string key, CancellationToken ct = default);
    }
}