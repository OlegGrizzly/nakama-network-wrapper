using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Nakama;
using NSubstitute;
using NUnit.Framework;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Common;
using OlegGrizzly.NakamaNetworkWrapper.Services;
using UnityEngine;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

namespace OlegGrizzly.NakamaNetworkWrapper.Tests.Tests.Runtime
{
    public class AuthServiceTests
    {
        private static TSub CreateSub<TSub>() where TSub : class => Substitute.For<TSub>();

        private static bool TrySetProperty(object target, string propertyName, object value)
        {
            if (target == null) return false;
            var type = target.GetType();
            var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null) return false;
            var setter = prop.GetSetMethod(true);
            if (setter == null) return false;
            setter.Invoke(target, new[] { value });
            return true;
        }

        private static void SetCurrentSession(object authService, ISession session)
        {
            if (TrySetProperty(authService, "CurrentSession", session)) return;
            
            var candidates = new[] { "_currentSession", "currentSession", "_session", "session" };
            foreach (var name in candidates)
            {
                var f = authService.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null)
                {
                    f.SetValue(authService, session);
                    return;
                }
            }
            Assert.Fail($"Neither property CurrentSession nor a known backing field was found on {authService.GetType().Name}");
        }

        private static AuthService CreateAuthService(out IClientService clientService)
        {
            clientService = CreateSub<IClientService>();
            return new AuthService(clientService);
        }

        [Test]
        public void LoginAsync_WithUnsupportedAuthType_ShouldThrow()
        {
            var svc = CreateAuthService(out _);

            Assert.Throws<ArgumentOutOfRangeException>(() => svc.LoginAsync(AuthType.Google, "id", "user").GetAwaiter().GetResult());
        }

        [Test]
        public void LoginAsync_ShouldAuthenticateAndConnect()
        {
            var svc = CreateAuthService(out var clientService);
            
            var client = CreateSub<IClient>();
            clientService.Client.Returns(client);

            var id = "id-123";
            var username = "user";
            var vars = new Dictionary<string, string> { { "k", "v" } };

            var session = CreateSub<ISession>();
            session.IsExpired.Returns(false);
            client.AuthenticateCustomAsync(id, username, true, vars).Returns(Task.FromResult(session));

            clientService.ConnectAsync(session).Returns(Task.CompletedTask);

            var authenticatedRaised = false;
            svc.OnAuthenticated += _ => authenticatedRaised = true;
            
            svc.LoginAsync(AuthType.Custom, id, username, vars).GetAwaiter().GetResult();
            
            client.Received(1).AuthenticateCustomAsync(id, username, true, vars);
            clientService.Received(1).ConnectAsync(session);
            Assert.AreSame(session, svc.CurrentSession, "CurrentSession should be set to authenticated session");
            Assert.IsTrue(svc.IsAuthenticated, "IsAuthenticated should be true when session is not expired");
            Assert.IsTrue(authenticatedRaised, "OnAuthenticated should be raised");
        }

        [Test]
        public void LoginAsync_ShouldRaiseOnAuthenticationFailedOnAuthError()
        {
            var svc = CreateAuthService(out var clientService);

            var client = CreateSub<IClient>();
            clientService.Client.Returns(client);

            var id = "id-123";
            var username = "user";
            var vars = new Dictionary<string, string>();

            var authEx = new ApiResponseException(401, "unauthorized", 0);
            client.AuthenticateCustomAsync(id, username, true, vars).Returns(_ => Task.FromException<ISession>(authEx));

            Exception observed = null;
            svc.OnAuthenticationFailed += e => observed = e;

            Exception thrown = null;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("Authentication Failed", RegexOptions.IgnoreCase));
                svc.LoginAsync(AuthType.Custom, id, username, vars).GetAwaiter().GetResult();
                Assert.Fail("Expected exception was not thrown");
            }
            catch (Exception e)
            {
                thrown = e;
            }

            Assert.AreSame(authEx, thrown);
            Assert.AreSame(authEx, observed, "OnAuthenticationFailed should be invoked with the same exception");
            Assert.IsNull(svc.CurrentSession, "CurrentSession should not be set on auth error");
            clientService.DidNotReceive().ConnectAsync(Arg.Any<ISession>());
        }

        [Test]
        public void LoginAsync_ShouldRaiseOnAuthenticationFailedOnConnectError()
        {
            var svc = CreateAuthService(out var clientService);

            var client = CreateSub<IClient>();
            clientService.Client.Returns(client);

            var id = "id-123";
            var username = "user";
            var vars = new Dictionary<string, string>();
            
            SetCurrentSession(svc, null);

            var session = CreateSub<ISession>();
            session.IsExpired.Returns(false);
            client.AuthenticateCustomAsync(id, username, true, vars).Returns(Task.FromResult(session));

            var connectEx = new System.Net.Http.HttpRequestException("connect fail");
            clientService.ConnectAsync(session).Returns(_ => Task.FromException(connectEx));

            Exception observed = null;
            svc.OnAuthenticationFailed += e => observed = e;

            Exception thrown = null;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("Authentication Failed", RegexOptions.IgnoreCase));
                svc.LoginAsync(AuthType.Custom, id, username, vars).GetAwaiter().GetResult();
                Assert.Fail("Expected exception was not thrown");
            }
            catch (Exception e)
            {
                thrown = e;
            }

            Assert.AreSame(connectEx, thrown);
            Assert.AreSame(connectEx, observed, "OnAuthenticationFailed should be invoked with the same exception");
            Assert.IsNull(svc.CurrentSession, "CurrentSession should be cleared when connect fails");
            clientService.Received(1).ConnectAsync(session);
        }

        [Test]
        public void LogoutAsync_ShouldDisconnectAndClearSession()
        {
            var svc = CreateAuthService(out var clientService);

            var session = CreateSub<ISession>();
            session.IsExpired.Returns(false);
            SetCurrentSession(svc, session);

            clientService.DisconnectAsync().Returns(Task.CompletedTask);

            var loggedOutRaised = false;
            svc.OnLoggedOut += () => loggedOutRaised = true;

            svc.LogoutAsync().GetAwaiter().GetResult();

            clientService.Received(1).DisconnectAsync();
            Assert.IsNull(svc.CurrentSession, "CurrentSession should be null after logout");
            Assert.IsFalse(svc.IsAuthenticated, "IsAuthenticated should be false after logout");
            Assert.IsTrue(loggedOutRaised, "OnLoggedOut should be raised");
        }

        [Test]
        public void IsAuthenticated_ShouldReturnFalseWhenSessionExpired()
        {
            var svc = CreateAuthService(out _);
            var session = CreateSub<ISession>();
            session.IsExpired.Returns(true);
            SetCurrentSession(svc, session);

            Assert.IsFalse(svc.IsAuthenticated);
        }
    }
}
