using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using UnityEngine;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    public class StorageService : IStorageService
    {
        private readonly IClientService _clientService;
        private readonly IAuthService _authService;

        public StorageService(IClientService clientService, IAuthService authService)
        {
            _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        }

        public async Task WriteAsync(string collection, string key, object value, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("Collection is required", nameof(collection));
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required", nameof(key));

            var session = _authService.CurrentSession ?? throw new InvalidOperationException("Not authenticated");

            string json;
            if (value is string s)
            {
                json = s;
            }
            else
            {
                json = JsonUtility.ToJson(value);
            }

            var write = new WriteStorageObject
            {
                Collection = collection,
                Key = key,
                Value = json,
                PermissionRead = 1,
                PermissionWrite = 1
            };

            await _clientService.Client.WriteStorageObjectsAsync(session, new IApiWriteStorageObject[] { write }, canceller: ct);
        }

        public async Task<T> ReadAsync<T>(string collection, string key, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("Collection is required", nameof(collection));
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required", nameof(key));

            var session = _authService.CurrentSession ?? throw new InvalidOperationException("Not authenticated");

            var id = new StorageObjectId
            {
                Collection = collection,
                Key = key,
                UserId = session.UserId
            };

            var result = await _clientService.Client.ReadStorageObjectsAsync(session, new IApiReadStorageObjectId[] { id }, canceller: ct);
            if (result?.Objects == null || !result.Objects.Any())
            {
                return default;
            }

            var json = result.Objects.First().Value;
            if (typeof(T) == typeof(string))
            {
                return (T)(object)json;
            }

            return JsonUtility.FromJson<T>(json);
        }
    }
}