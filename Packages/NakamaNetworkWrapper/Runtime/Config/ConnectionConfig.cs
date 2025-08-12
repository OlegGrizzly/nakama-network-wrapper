using System;
using System.Threading;
using Nakama;
using UnityEngine;

#if UNITY_EDITOR
using System.Text.RegularExpressions;
#endif

namespace OlegGrizzly.NakamaNetworkWrapper.Config
{
    [CreateAssetMenu(menuName = "Nakama/Connection Config", fileName = "NakamaConnectionConfig")]
    public sealed class ConnectionConfig : ScriptableObject
    {
        [Header("Connection Settings")]
        [SerializeField]
        private string host = "127.0.0.1";

        [SerializeField]
        private int port = 7350;

        [SerializeField]
        private bool useSSL;

        [SerializeField]
        private string serverKey = "defaultkey";

        [SerializeField]
        private bool autoRefreshSession = true;

        [SerializeField]
        private bool appearOnline = true;

        [SerializeField]
        private int connectTimeout = 10;

        [Header("Retry Settings")]
        [SerializeField]
        private int maxRetries = 3;

        [SerializeField]
        private int baseDelayMs = 1000;
        
        public string Host => host;
        
        public int Port => port;
        
        public string Scheme => useSSL ? "https" : "http";
        
        public string ServerKey => serverKey;
        
        public bool AutoRefreshSession => autoRefreshSession;
        
        public bool AppearOnline => appearOnline;
        
        public int ConnectTimeout => connectTimeout;

        public int MaxRetries => maxRetries;
        
        public int BaseDelayMs => baseDelayMs;

        public CancellationToken GetCancellationToken()
        {
            return new CancellationTokenSource(TimeSpan.FromSeconds(connectTimeout)).Token;
        }
        
        public RetryConfiguration GetRetryConfiguration()
        {
            return new RetryConfiguration(baseDelayMs, maxRetries, delegate
            {
                Debug.Log("<color=orange>Retrying...</color>");
            });
        }

        #if UNITY_EDITOR
        private void OnValidate()
        {
            host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : Regex.Replace(host.Trim(), "\\s+", "");
            port = Mathf.Clamp(port, 1, 65535);
            
            if (string.IsNullOrWhiteSpace(serverKey))
            {
                serverKey = "defaultkey";
            }

            maxRetries = Mathf.Max(0, maxRetries);
            baseDelayMs = Mathf.Max(0, baseDelayMs);
        }
        #endif
    }
}