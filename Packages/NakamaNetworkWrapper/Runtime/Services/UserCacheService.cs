using System;
using System.Collections.Generic;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using UnityEngine;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

namespace OlegGrizzly.NakamaNetworkWrapper.Services
{
    enum LogLevel { Error, Info, Verbose }

    static class UcLog
    {
        public static LogLevel Level = LogLevel.Info;
        private static readonly System.Collections.Generic.Dictionary<string, System.DateTime> _lastLog = new();

        public static void Info(string m, string tag = "UserCache")
        {
            if (Level >= LogLevel.Info) UnityEngine.Debug.Log($"[{tag}] {m}");
        }
        public static void Error(string m, string tag = "UserCache")
        {
            UnityEngine.Debug.LogError($"[{tag}] {m}");
        }
        // Throttled info by key
        public static void InfoThrottled(string key, System.TimeSpan interval, System.Func<string> msgFactory, string tag = "UserCache")
        {
            if (Level < LogLevel.Info) return;
            var now = System.DateTime.UtcNow;
            if (_lastLog.TryGetValue(key, out var last) && (now - last) < interval) return;
            _lastLog[key] = now;
            UnityEngine.Debug.Log($"[{tag}] {msgFactory()}");
        }
    }

    public class UserCacheService : IUserCacheService
    {
        private readonly IClientService _clientService;
        private readonly IAuthService _authService;
        private readonly HashSet<string> _usersIds = new();
        private readonly Dictionary<string, IApiUser> _userCache = new();
        private readonly SemaphoreSlim _semaphore = new(MaxConcurrentRequests);
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Task _inflightLoadTask;
        private readonly object _loadStartLock = new();

        private const int MaxUsersPerRequest = 100;
        private const int MaxConcurrentRequests = 5;

        // --- logging aggregation fields ---
        private int _hitCount;
        private int _missCount;
        private DateTime _lastReportUtc = DateTime.UtcNow;
        private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);
        private readonly HashSet<string> _inflightIds = new(); // for suppressing duplicate MISS logs only
        
        public UserCacheService(IClientService clientService, IAuthService authService)
        {
            _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            
            if (_clientService.Client == null)
            {
                throw new ArgumentException("ClientService.Client is null", nameof(clientService));
            }
        }
        
        private Task EnsureLoadRunningAsync()
        {
            lock (_loadStartLock)
            {
                if (_inflightLoadTask == null || _inflightLoadTask.IsCompleted)
                {
                    UcLog.InfoThrottled("load-start", TimeSpan.FromSeconds(2), () => "LOAD START");
                    _inflightLoadTask = LoadUsersInternalAsync();
                }
                return _inflightLoadTask;
            }
        }
        
        public async Task<IApiUser> GetUserAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            if (_userCache.TryGetValue(userId, out var cached))
            {
                System.Threading.Interlocked.Increment(ref _hitCount);
                MaybeReport();
                return cached;
            }

            await _gate.WaitAsync();
            try
            {
                if (!_userCache.ContainsKey(userId))
                {
                    if (_usersIds.Add(userId))
                    {
                        // Log MISS only once per userId until it gets cached
                        if (_inflightIds.Add(userId))
                        {
                            System.Threading.Interlocked.Increment(ref _missCount);
                            UcLog.Info($"MISS userId={userId}, scheduling load");
                        }
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            await EnsureLoadRunningAsync();

            _userCache.TryGetValue(userId, out var result);
            return result;
        }

        private void MaybeReport()
        {
            var now = DateTime.UtcNow;
            if (now - _lastReportUtc >= ReportEvery)
            {
                _lastReportUtc = now;
                int hits = System.Threading.Interlocked.Exchange(ref _hitCount, 0);
                int misses = System.Threading.Interlocked.Exchange(ref _missCount, 0);

                int cacheSize, pending;
                _gate.Wait();
                try
                {
                    cacheSize = _userCache.Count;
                    pending = _usersIds.Count;
                }
                finally
                {
                    _gate.Release();
                }

                UcLog.Info($"STAT hits={hits}, misses={misses}, cache={cacheSize}, pending={pending}");
            }
        }

        public async Task CollectUserIdsAsync(HashSet<string> ids)
        {
            if (ids == null) return;

            await _gate.WaitAsync();
            try
            {
                foreach (var id in ids)
                {
                    if (!string.IsNullOrEmpty(id))
                    {
                        _usersIds.Add(id);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            await EnsureLoadRunningAsync();
        }

        private async Task LoadUsersInternalAsync()
        {
            var session = _authService.Session ?? throw new InvalidOperationException("Not authenticated");

            List<string> userIdsToFetch;
            await _gate.WaitAsync();
            try
            {
                userIdsToFetch = new List<string>(_usersIds.Where(id => !_userCache.ContainsKey(id)));
            }
            finally
            {
                _gate.Release();
            }

            var started = DateTime.UtcNow;
            if (userIdsToFetch.Count == 0) return;
            UcLog.Info($"LOAD START pendingUnique={userIdsToFetch.Count}, cache={_userCache.Count}");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tasks = new List<Task>();
            
            foreach (var chunk in ChunkBy(userIdsToFetch, MaxUsersPerRequest))
            {
                await _semaphore.WaitAsync(timeoutCts.Token);
                tasks.Add(LoadUserChunkAsync(chunk, session, timeoutCts.Token));
            }

            UcLog.Info($"LOAD DISPATCH chunks={tasks.Count}");

            await Task.WhenAll(tasks);
            
            await _gate.WaitAsync(timeoutCts.Token);
            try
            {
                foreach (var id in userIdsToFetch)
                {
                    if (_userCache.ContainsKey(id))
                    {
                        _usersIds.Remove(id);
                        _inflightIds.Remove(id); // only affects logging suppression
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            var durMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            UcLog.Info($"LOAD DONE durationMs={durMs}, cache={_userCache.Count}, pending={_usersIds.Count}");
        }

        private async Task LoadUserChunkAsync(List<string> userIds, ISession session, CancellationToken cancellationToken = default)
        {
            UcLog.Info($"CHUNK START size={userIds.Count}");
            try
            {
                var users = await _clientService.Client.GetUsersAsync(session, userIds.ToArray(), canceller: cancellationToken);

                await _gate.WaitAsync(cancellationToken);
                try
                {
                    foreach (var user in users.Users)
                    {
                        _userCache[user.Id] = user;
                    }
                }
                finally
                {
                    _gate.Release();
                }
                UcLog.Info($"CHUNK DONE fetched={users.Users.Count()}, cacheNow={_userCache.Count}");
            }
            catch (Exception ex)
            {
                UcLog.Error($"CHUNK ERROR size={userIds.Count}: {ex}");
            }
            finally
            {
                _semaphore.Release();
            }
        }
        
        private static IEnumerable<List<T>> ChunkBy<T>(List<T> source, int chunkSize)
        {
            for (var i = 0; i < source.Count; i += chunkSize)
            {
                yield return source.GetRange(i, Math.Min(chunkSize, source.Count - i));
            }
        }

        public void Dispose()
        {
            if (_gate.Wait(TimeSpan.FromSeconds(1)))
            {
                try
                {
                    _usersIds?.Clear();
                    _userCache?.Clear();
                    _inflightLoadTask = null;
                }
                finally
                {
                    _gate.Release();
                }
            }

            _semaphore?.Dispose();
            _gate.Dispose();
        }
    }
}