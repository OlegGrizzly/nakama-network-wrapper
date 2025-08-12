using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Nakama;
using NSubstitute;
using NUnit.Framework;
using OlegGrizzly.NakamaNetworkWrapper.Config;
using OlegGrizzly.NakamaNetworkWrapper.Services;
using UnityEngine;
using UnityEngine.TestTools;

namespace OlegGrizzly.NakamaNetworkWrapper.Tests.Tests.Runtime
{
    public class ClientServiceTests
    {
        private static ConnectionConfig CreateConfig()
        {
            var cfg = ScriptableObject.CreateInstance<ConnectionConfig>();
            Assert.NotNull(cfg);
            return cfg!;
        }

        private static TSub CreateSub<TSub>() where TSub : class => Substitute.For<TSub>();

        private static void SetPrivateField<T>(object target, string fieldName, T value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Field {fieldName} not found on {target.GetType().Name}");
            field!.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string methodName, params object[] args)
        {
            var mi = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mi, $"Method {methodName} not found on {target.GetType().Name}");
            mi!.Invoke(target, args);
        }

        private static ClientService CreateService(out ConnectionConfig config)
        {
            config = CreateConfig();
            var svc = new ClientService(config);
            Assert.NotNull(svc);
            return svc;
        }
        
        [Test]
        public void ConnectAsync_ShouldThrowWhenDisposed()
        {
            var svc = CreateService(out _);
            var session = CreateSub<ISession>();
            
            SetPrivateField(svc, "_disposed", true);

            Assert.Throws<ObjectDisposedException>(() => svc.ConnectAsync(session).GetAwaiter().GetResult());
        }
        
        [Test]
        public void ConnectAsync_ShouldThrowOnNullSession()
        {
            var svc = CreateService(out _);
            Assert.Throws<ArgumentNullException>(() => svc.ConnectAsync(null).GetAwaiter().GetResult());
        }
        
        [Test]
        public void ConnectAsync_ShouldNotReconnectWhenAlreadyConnected()
        {
            var svc = CreateService(out _);

            var socket = CreateSub<ISocket>();
            socket.IsConnected.Returns(true);
            
            SetPrivateField(svc, "_socket", socket);

            var client = CreateSub<IClient>();
            SetPrivateField(svc, "_client", client);

            var session = CreateSub<ISession>();
            svc.ConnectAsync(session).GetAwaiter().GetResult();
            
            socket.DidNotReceive().ConnectAsync(Arg.Any<ISession>(), Arg.Any<bool>(), Arg.Any<int>());
        }
        
        [Test]
        public void ConnectAsync_ShouldCreateSocketAndConnectWithCorrectParameters()
        {
            var svc = CreateService(out var config);

            var client = CreateSub<IClient>();
            var socket = CreateSub<ISocket>();
            
            SetPrivateField(svc, "_client", client);
            SetPrivateField(svc, "_socket", socket);

            var connectingRaised = false;
            svc.OnConnecting += () => connectingRaised = true;

            var session = CreateSub<ISession>();
            socket.ConnectAsync(session, config.AppearOnline, config.ConnectTimeout).Returns(Task.CompletedTask);

            svc.ConnectAsync(session).GetAwaiter().GetResult();

            socket.Received(1).ConnectAsync(session, config.AppearOnline, config.ConnectTimeout);
            Assert.IsTrue(connectingRaised, "OnConnecting should be raised");
            Assert.AreSame(socket, svc.Socket, "Socket property should point to created socket");
        }
        
        [Test]
        public void ConnectAsync_ShouldRaiseOnConnectedWhenSocketConnectedEventFires()
        {
            var svc = CreateService(out var config);

            var client = CreateSub<IClient>();
            var socket = CreateSub<ISocket>();
            SetPrivateField(svc, "_client", client);
            SetPrivateField(svc, "_socket", socket);

            var session = CreateSub<ISession>();
            socket.ConnectAsync(session, config.AppearOnline, config.ConnectTimeout).Returns(Task.CompletedTask);

            var connectedRaised = false;
            svc.OnConnected += () => connectedRaised = true;

            svc.ConnectAsync(session).GetAwaiter().GetResult();
            
            socket.Connected += Raise.Event<Action>();

            Assert.IsTrue(connectedRaised, "OnConnected should be raised when socket.Connected fires");
        }
        
        [Test]
        public void ConnectAsync_ShouldHandleConnectError()
        {
            var svc = CreateService(out var config);

            var client = CreateSub<IClient>();
            var socket = CreateSub<ISocket>();
            SetPrivateField(svc, "_client", client);
            SetPrivateField(svc, "_socket", socket);

            var session = CreateSub<ISession>();
            var ex = new Exception("connect failed");
            socket.ConnectAsync(session, config.AppearOnline, config.ConnectTimeout).Returns(_ => Task.FromException(ex));

            Exception observed = null;
            svc.OnReceivedError += e => observed = e;

            Exception thrown = null;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("connect failed"));
                svc.ConnectAsync(session).GetAwaiter().GetResult();
                Assert.Fail("Expected exception was not thrown");
            }
            catch (Exception te) { thrown = te; }
            Assert.AreSame(ex, thrown);
            Assert.AreSame(ex, observed, "OnReceivedError should be invoked with the same exception");
            Assert.IsNull(svc.Socket, "Socket should be cleared when connect fails");
        }
        
        [Test]
        public void DisconnectAsync_ShouldCloseSocketAndDetachEvents()
        {
            var svc = CreateService(out _);

            var socket = CreateSub<ISocket>();
            socket.CloseAsync().Returns(Task.CompletedTask);
            SetPrivateField(svc, "_socket", socket);

            var onConnectedRaised = false;
            var onDisconnectedRaised = false;
            var onErrorRaised = false;
            svc.OnConnected += () => onConnectedRaised = true;
            svc.OnDisconnected += () => onDisconnectedRaised = true;
            svc.OnReceivedError += _ => onErrorRaised = true;

            svc.DisconnectAsync().GetAwaiter().GetResult();

            socket.Received(1).CloseAsync();
            Assert.IsNull(svc.Socket, "Socket should be null after DisconnectAsync");
            
            socket.Connected += Raise.Event<Action>();
            socket.Closed += Raise.Event<Action>();
            socket.ReceivedError += Raise.Event<Action<Exception>>(new Exception("boom"));

            Assert.IsFalse(onConnectedRaised, "OnConnected should not fire after detaching");
            Assert.IsFalse(onDisconnectedRaised, "OnDisconnected should not fire after detaching");
            Assert.IsFalse(onErrorRaised, "OnReceivedError should not fire after detaching");
        }
        
        [Test]
        public void Dispose_ShouldDetachAndCloseSocket()
        {
            var svc = CreateService(out _);

            var socket = CreateSub<ISocket>();
            socket.CloseAsync().Returns(Task.CompletedTask);
            SetPrivateField(svc, "_socket", socket);

            svc.Dispose();
            socket.Received(1).CloseAsync();
            Assert.IsNull(svc.Socket, "Socket should be null after Dispose");
            
            var onConnectedRaised = false;
            var onDisconnectedRaised = false;
            var onErrorRaised = false;
            svc.OnConnected += () => onConnectedRaised = true;
            svc.OnDisconnected += () => onDisconnectedRaised = true;
            svc.OnReceivedError += _ => onErrorRaised = true;

            socket.Connected += Raise.Event<Action>();
            socket.Closed += Raise.Event<Action>();
            socket.ReceivedError += Raise.Event<Action<Exception>>(new Exception("boom"));

            Assert.IsFalse(onConnectedRaised, "OnConnected should not fire after Dispose");
            Assert.IsFalse(onDisconnectedRaised, "OnDisconnected should not fire after Dispose");
            Assert.IsFalse(onErrorRaised, "OnReceivedError should not fire after Dispose");
            
            svc.Dispose();
            socket.Received(1).CloseAsync();
        }
        
        [Test]
        public void ReceivedError_ShouldInvokeOnReceivedError()
        {
            var svc = CreateService(out _);
            Exception captured = null;
            svc.OnReceivedError += e => captured = e;

            var ex = new Exception("boom");
            LogAssert.Expect(LogType.Error, new Regex("boom"));
            InvokePrivate(svc, "ReceivedError", ex);

            Assert.AreSame(ex, captured, "OnReceivedError should be invoked with the same exception instance");
        }
        
        [Test]
        public void Connected_ShouldInvokeOnConnected()
        {
            var svc = CreateService(out _);
            var raised = false;
            svc.OnConnected += () => raised = true;
            InvokePrivate(svc, "Connected");
            Assert.IsTrue(raised);
        }

        [Test]
        public void Disconnected_ShouldInvokeOnDisconnected()
        {
            var svc = CreateService(out _);
            var raised = false;
            svc.OnDisconnected += () => raised = true;
            InvokePrivate(svc, "Disconnected");
            Assert.IsTrue(raised);
        }
    }
}