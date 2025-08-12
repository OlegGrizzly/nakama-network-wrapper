using System;
using System.Reflection;
using System.Threading;
using Nakama;
using NUnit.Framework;
using OlegGrizzly.NakamaNetworkWrapper.Config;
using UnityEngine;

namespace OlegGrizzly.NakamaNetworkWrapper.Tests.Tests.Runtime
{
    [TestFixture]
    public class ConnectionConfigTests
    {
        private static ConnectionConfig CreateConfig()
        {
            var cfg = ScriptableObject.CreateInstance<ConnectionConfig>();
            Assert.NotNull(cfg, "Failed to create ConnectionConfig via ScriptableObject.CreateInstance");
            return cfg!;
        }
        
        private static void SetPrivateField<T>(ConnectionConfig config, string fieldName, T value)
        {
            var field = typeof(ConnectionConfig).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
            {
                Assert.Fail($"Field {fieldName} not found in ConnectionConfig");
                return;
            }
            field.SetValue(config, value);
        }
        
        [Test]
        public void Scheme_UseSSLTrue_ShouldReturnHttps()
        {
            var config = CreateConfig();
            
            SetPrivateField(config, "useSSL", true);
            Assert.AreEqual("https", config.Scheme, "When useSSL=true, Scheme should return 'https'.");
            
            SetPrivateField(config, "useSSL", false);
            Assert.AreEqual("http", config.Scheme, "When useSSL=false, Scheme should return 'http'.");
        }
        
        [Test]
        public void GetCancellationToken_ShouldCancelAfterConfiguredTimeout()
        {
            var config = CreateConfig();
            
            SetPrivateField(config, "connectTimeout", 1);

            var cts = config.GetCancellationTokenSource();
            Assert.IsFalse(cts.Token.IsCancellationRequested, "Token should be active immediately after creation.");
            
            Thread.Sleep(TimeSpan.FromSeconds(1.6));
            Assert.IsTrue(cts.Token.IsCancellationRequested, "Token should be cancelled after connectTimeout expires.");
        }
        
        [Test]
        public void GetRetryConfiguration_ShouldReturnConfigurationWithCorrectValues()
        {
            var config = CreateConfig();
            
            const int expectedBaseDelay = 1234;
            const int expectedMaxRetries = 5;
            SetPrivateField(config, "baseDelayMs", expectedBaseDelay);
            SetPrivateField(config, "maxRetries", expectedMaxRetries);
            
            var retryConfig = config.GetRetryConfiguration();
            Assert.NotNull(retryConfig, "GetRetryConfiguration() should not return null");
            var type = typeof(RetryConfiguration);

            var baseDelayProp = type.GetProperty("BaseDelay", BindingFlags.Public | BindingFlags.Instance) ?? type.GetProperty("BaseDelayMs", BindingFlags.Public | BindingFlags.Instance);
            if (baseDelayProp != null)
            {
                var actualBaseDelay = (int)baseDelayProp.GetValue(retryConfig);
                Assert.AreEqual(expectedBaseDelay, actualBaseDelay, "BaseDelay/DelayMs in RetryConfiguration does not match the setting");
            }
            else
            {
                Assert.Fail("BaseDelay/BaseDelayMs property not found in RetryConfiguration");
            }

            int actualMax;
            var maxAttemptsProp = type.GetProperty("MaxAttempts", BindingFlags.Public | BindingFlags.Instance);
            if (maxAttemptsProp != null)
            {
                actualMax = (int)maxAttemptsProp.GetValue(retryConfig);
            }
            else
            {
                var maxRetriesProp = type.GetProperty("MaxRetries", BindingFlags.Public | BindingFlags.Instance);
                if (maxRetriesProp != null)
                {
                    actualMax = (int)maxRetriesProp.GetValue(retryConfig);
                }
                else
                {
                    var maxAttemptsField = type.GetField("maxAttempts", BindingFlags.NonPublic | BindingFlags.Instance) ?? type.GetField("MaxAttempts", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public) ?? type.GetField("maxRetries", BindingFlags.NonPublic | BindingFlags.Instance) ?? type.GetField("MaxRetries", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    Assert.NotNull(maxAttemptsField, "MaxAttempts/MaxRetries member not found in RetryConfiguration (neither property nor field)");
                    actualMax = (int)maxAttemptsField!.GetValue(retryConfig);
                }
            }
            Assert.AreEqual(expectedMaxRetries, actualMax, "Max retry attempts do not match the setting");

            var jitterProp = type.GetProperty("Jitter", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(jitterProp, "Jitter property not found in RetryConfiguration");
            var jitterValue = jitterProp!.GetValue(retryConfig);
            Assert.NotNull(jitterValue, "Jitter should not be null");
        }
        
        [Test]
        public void Properties_ShouldReturnConfiguredValues()
        {
            var config = CreateConfig();
            
            const bool expectedAutoRefresh = false;
            const bool expectedAppearOnline = false;
            const int expectedTimeout = 42;
            
            SetPrivateField(config, "autoRefreshSession", expectedAutoRefresh);
            SetPrivateField(config, "appearOnline", expectedAppearOnline);
            SetPrivateField(config, "connectTimeout", expectedTimeout);
            
            Assert.AreEqual(expectedAutoRefresh, config.AutoRefreshSession, "AutoRefreshSession getter should return configured value.");
            Assert.AreEqual(expectedAppearOnline, config.AppearOnline, "AppearOnline getter should return configured value.");
            Assert.AreEqual(expectedTimeout, config.ConnectTimeout, "ConnectTimeout getter should return configured value.");
        }
        
        #if UNITY_EDITOR
        [Test]
        public void OnValidate_ShouldNormalizeHostAndClampPort()
        {
            var config = CreateConfig();
            
            SetPrivateField(config, "host", "   ");
            SetPrivateField(config, "port", -10);
            SetPrivateField<string>(config, "serverKey", null);
            SetPrivateField(config, "maxRetries", -5);
            SetPrivateField(config, "baseDelayMs", -100);
            
            var method = typeof(ConnectionConfig).GetMethod("OnValidate", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method, "OnValidate method not found");
            method!.Invoke(config, null);
            
            var host = config.Host;
            Assert.AreEqual("127.0.0.1", host, "host should be replaced with '127.0.0.1' when empty string is provided");
            
            var port = config.Port;
            Assert.GreaterOrEqual(port, 1, "port should be at least 1 after normalization");
            Assert.LessOrEqual(port, 65535, "port should be at most 65535 after normalization");
            
            Assert.AreEqual("defaultkey", config.ServerKey, "serverKey should be replaced with 'defaultkey' when value is empty");
            
            Assert.GreaterOrEqual(config.MaxRetries, 0, "maxRetries should be non-negative after normalization");
            Assert.GreaterOrEqual(config.BaseDelayMs, 0, "baseDelayMs should be non-negative after normalization");
            
            SetPrivateField(config, "host", "  127. 0.0 .1  ");
            method.Invoke(config, null);
            Assert.AreEqual("127.0.0.1", config.Host, "host should have internal whitespace removed");
        }
        #endif
    }
}