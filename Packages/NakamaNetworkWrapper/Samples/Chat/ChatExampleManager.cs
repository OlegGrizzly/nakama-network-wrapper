using System;
using System.Collections.Generic;
using System.Linq;
using Nakama;
using OlegGrizzly.NakamaNetworkWrapper.Abstractions;
using OlegGrizzly.NakamaNetworkWrapper.Common;
using OlegGrizzly.NakamaNetworkWrapper.Config;
using OlegGrizzly.NakamaNetworkWrapper.Controllers;
using OlegGrizzly.NakamaNetworkWrapper.Persistence;
using OlegGrizzly.NakamaNetworkWrapper.Services;
using OlegGrizzly.NakamaNetworkWrapper.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace Samples.Chat
{
	public class ChatExampleManager : MonoBehaviour
	{
		[SerializeField] private ConnectionConfig connectionConfig;
		[SerializeField] private Button connectButton;
		[SerializeField] private Button disconnectButton;
		[SerializeField] private ScrollRect scrollRect;
		[SerializeField] private Text logText;

		[SerializeField] private InputField roomNameInput;
		[SerializeField] private InputField groupIdInput;
		[SerializeField] private InputField directUserIdInput;
		[SerializeField] private InputField messageInput;
		[SerializeField] private InputField updateMessageIdInput;
		[SerializeField] private InputField leaveChannelIdInput;
		[SerializeField] private Text currentChannelIdText;

		[SerializeField] private Button joinRoomButton;
		[SerializeField] private Button joinGroupButton;
		[SerializeField] private Button joinDirectButton;
		[SerializeField] private Button leaveButton;
		[SerializeField] private Button sendButton;
		[SerializeField] private Button listButton;
		[SerializeField] private Button listNextButton;
		[SerializeField] private Button listPrevButton;
		[SerializeField] private Button updateMsgButton;
		[SerializeField] private Button removeMsgButton;
		[SerializeField] private Button listParticipantsButton;

		private IClientService _clientService;
		private IDeviceIdProvider _deviceIdProvider;
		private IAuthService _authService;
		private IChatPresenceService _chatPresenceService;
		private IUserCacheService _userCacheService;
		private IChatService _chatService;
		private ConnectionStateMachine _stateMachine;

		private string _currentChannelId;
		private string _prevCursor;
		private string _nextCursor;
		private bool _forward;

		private void Awake()
		{
			connectButton.onClick.AddListener(OnConnectClicked);
			disconnectButton.onClick.AddListener(OnDisconnectClicked);

			joinRoomButton.onClick.AddListener(OnJoinRoomClicked);
			joinGroupButton.onClick.AddListener(OnJoinGroupClicked);
			joinDirectButton.onClick.AddListener(OnJoinDirectClicked);
			leaveButton.onClick.AddListener(OnLeaveClicked);
			sendButton.onClick.AddListener(OnSendClicked);
			listButton.onClick.AddListener(OnListClicked);
			listNextButton.onClick.AddListener(OnListNextClicked);
			listPrevButton.onClick.AddListener(OnListPrevClicked);
			updateMsgButton.onClick.AddListener(OnUpdateMessageClicked);
			removeMsgButton.onClick.AddListener(OnRemoveMessageClicked);
		}

		private async void Start()
		{
			if (connectionConfig == null)
			{
				Log("ConnectionConfig not assigned", Color.red);
				return;
			}

			_clientService = new ClientService(connectionConfig);
			_deviceIdProvider = new DeviceIdProvider();

			var tokenPersistence = new PlayerPrefsTokenPersistence();
			_authService = new AuthService(_clientService, tokenPersistence);

			_userCacheService = new UserCacheService(_clientService, _authService);
			_chatPresenceService = new ChatPresenceService(_userCacheService);
			_chatService = new ChatService(_clientService, _authService, _userCacheService, _chatPresenceService);
			_chatService.OnMessageReceived += OnMessageReceived;

			_stateMachine = new ConnectionStateMachine(_authService, _clientService);
			_stateMachine.OnStateChanged += UpdateUiByState;

			UpdateUiByState(_stateMachine.CurrentState);

			var restored = await _authService.TryRestoreSessionAsync();
			if (!restored)
			{
				var deviceId = _deviceIdProvider.GetDeviceId();
				await _authService.LoginAsync(AuthType.Custom, deviceId);
			}

			UpdateUiByState(_stateMachine.CurrentState);
			_chatPresenceService.OnPresenceChanged += OnPresenceChanged;
			_chatPresenceService.OnChannelReady += OnChannelReady;
		}
		
		private async void OnConnectClicked()
		{
			var id = _deviceIdProvider.GetDeviceId();
			Log($"DeviceID: {id}", Color.cyan);
			await _authService.LoginAsync(AuthType.Custom, id);
		}

		private async void OnDisconnectClicked()
		{
			await _authService.LogoutAsync();
		}

		private async void OnJoinRoomClicked()
		{
			try
			{
				var roomName = roomNameInput != null ? roomNameInput.text : "lobby";
				var channel = await _chatService.JoinChannelAsync(ChannelType.Room, roomName);
				SetCurrentChannel(channel.Id);
				Log($"Joined room: {roomName} ({channel.Id})", Color.green);
			}
			catch (Exception ex)
			{
				Log($"Join room failed: {ex.Message}", Color.red);
			}
		}

		private async void OnJoinGroupClicked()
		{
			try
			{
				var gid = groupIdInput != null ? groupIdInput.text : string.Empty;
				var channel = await _chatService.JoinChannelAsync(ChannelType.Group, gid);
				SetCurrentChannel(channel.Id);
			}
			catch (Exception ex)
			{
				Log($"Join group failed: {ex.Message}", Color.red);
			}
		}

		private async void OnJoinDirectClicked()
		{
			try
			{
				var uid = directUserIdInput != null ? directUserIdInput.text : string.Empty;
				var channel = await _chatService.JoinChannelAsync(ChannelType.DirectMessage, uid);
				SetCurrentChannel(channel.Id);
				Log($"Joined direct with: {uid} ({channel.Id})", Color.green);
			}
			catch (Exception ex)
			{
				Log($"Join direct failed: {ex.Message}", Color.red);
			}
		}

		private async void OnLeaveClicked()
		{
			try
			{
				var channelId = leaveChannelIdInput != null && !string.IsNullOrWhiteSpace(leaveChannelIdInput.text)
					? leaveChannelIdInput.text
					: _currentChannelId;
				if (string.IsNullOrWhiteSpace(channelId)) return;
				await _chatService.LeaveChannelAsync(channelId);
				if (_currentChannelId == channelId) SetCurrentChannel(null);
				Log($"Left channel: {channelId}", Color.yellow);
			}
			catch (Exception ex)
			{
				Log($"Leave failed: {ex.Message}", Color.red);
			}
		}

		private async void OnSendClicked()
		{
			try
			{
				if (string.IsNullOrWhiteSpace(_currentChannelId))
				{
					Log("No active channel", Color.red);
					return;
				}
				var raw = messageInput != null ? messageInput.text : string.Empty;
				var content = MakeJsonContent(raw);
				await _chatService.SendMessageAsync(_currentChannelId, content);
				Log($"Sent: {raw}", Color.white);
			}
			catch (Exception ex)
			{
				Log($"Send failed: {ex.Message}", Color.red);
			}
		}

		private async void OnListClicked()
		{
			try
			{
				if (string.IsNullOrWhiteSpace(_currentChannelId))
				{
					Log("No active channel", Color.red);
					return;
				}
				_forward = false; // list newest->oldest from server, then reverse for UI oldest->newest
				logText.text = "";
				var page = await _chatService.ListMessagesAsync(_currentChannelId, 5, forward: _forward);
				PrintPage(page);
			}
			catch (Exception ex)
			{
				Log($"List failed: {ex.Message}", Color.red);
			}
		}

		private async void OnListNextClicked()
		{
			try
			{
				// Next → показать следующую страницу по времени UI (т.е. более старые при _forward=false)
				if (string.IsNullOrWhiteSpace(_currentChannelId) || string.IsNullOrWhiteSpace(_prevCursor)) return;
				logText.text = "";
				var page = await _chatService.ListMessagesAsync(_currentChannelId, 5, _prevCursor, _forward);
				PrintPage(page);
			}
			catch (Exception ex)
			{
				Log($"Next failed: {ex.Message}", Color.red);
			}
		}

		private async void OnListPrevClicked()
		{
			try
			{
				// Prev → показать предыдущую страницу по времени UI (т.е. более новые при _forward=false)
				if (string.IsNullOrWhiteSpace(_currentChannelId) || string.IsNullOrWhiteSpace(_nextCursor)) return;
				logText.text = "";
				var page = await _chatService.ListMessagesAsync(_currentChannelId, 5, _nextCursor, _forward);
				PrintPage(page);
			}
			catch (Exception ex)
			{
				Log($"Prev failed: {ex.Message}", Color.red);
			}
		}

		private async void OnUpdateMessageClicked()
		{
			try
			{
				if (string.IsNullOrWhiteSpace(_currentChannelId)) return;
				var msgId = updateMessageIdInput != null ? updateMessageIdInput.text : string.Empty;
				var raw = messageInput != null ? messageInput.text : string.Empty;
				var content = MakeJsonContent(raw);
				await _chatService.UpdateMessageAsync(_currentChannelId, msgId, content);
				Log($"Updated message: {msgId}", Color.yellow);
			}
			catch (Exception ex)
			{
				Log($"Update failed: {ex.Message}", Color.red);
			}
		}

		private async void OnRemoveMessageClicked()
		{
			try
			{
				if (string.IsNullOrWhiteSpace(_currentChannelId)) return;
				var msgId = updateMessageIdInput != null ? updateMessageIdInput.text : string.Empty;
				await _chatService.RemoveMessageAsync(_currentChannelId, msgId);
				Log($"Removed message: {msgId}", Color.yellow);
			}
			catch (Exception ex)
			{
				Log($"Remove failed: {ex.Message}", Color.red);
			}
		}

		private async void OnMessageReceived(IApiChannelMessage msg)
		{
			var user = await _userCacheService.GetUserAsync(msg.SenderId);
			var displayName = user?.DisplayName ?? msg.Username;
			Log($"[{msg.CreateTime}] {displayName}: {msg.Content} (id: {msg.MessageId})", Color.white);
		}
		
		private async void PrintPage(IApiChannelMessageList page)
		{
			_prevCursor = page.PrevCursor;
			_nextCursor = page.NextCursor;
			var sequence = _forward ? page.Messages : page.Messages.Reverse();
			foreach (var m in sequence)
			{
				var user = await _userCacheService.GetUserAsync(m.SenderId);
				var displayName = user?.DisplayName ?? m.Username;
				Log($"[{m.CreateTime}] {displayName}: {m.Content} (id: {m.MessageId})", Color.grey);
			}
		}

		private void SetCurrentChannel(string channelId)
		{
			_currentChannelId = channelId;
			_prevCursor = null;
			_nextCursor = null;
			if (currentChannelIdText != null)
			{
				currentChannelIdText.text = string.IsNullOrEmpty(channelId) ? "-" : channelId;
			}
		}

		private void UpdateUiByState(ConnectionState state)
		{
			connectButton.interactable = state is ConnectionState.Disconnected or ConnectionState.AuthFailed;
			disconnectButton.interactable = state == ConnectionState.Connected;
		}

		private void Log(string text, Color color)
		{
			var html = $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";
			if (logText != null)
			{
				logText.text += html + "\n";
				if (scrollRect != null)
				{
					Canvas.ForceUpdateCanvases();
					scrollRect.verticalNormalizedPosition = 0f;
				}
			}
		}

		private void OnDestroy()
		{
			if (_chatService != null)
			{
				_chatService.OnMessageReceived -= OnMessageReceived;
				_chatService.Dispose();
			}
			_stateMachine?.Dispose();
			if (_chatPresenceService != null)
			{
				_chatPresenceService.OnPresenceChanged -= OnPresenceChanged;
				_chatPresenceService.OnChannelReady -= OnChannelReady;
			}
		}

		private static string MakeJsonContent(string text)
		{
			if (string.IsNullOrEmpty(text)) return "{\"message\":\"\"}";
			var escaped = text
				.Replace("\\", "\\\\")
				.Replace("\"", "\\\"")
				.Replace("\n", "\\n")
				.Replace("\r", "\\r");
			return $"{{\"message\":\"{escaped}\"}}";
		}

		private async void OnPresenceChanged(IChannelPresenceEvent presenceEvent)
		{
			var channelId = presenceEvent.ChannelId;
			var presences = _chatPresenceService.GetPresences(channelId);
			var count = presences.Count;
			
			foreach (var join in presenceEvent.Joins)
			{
				var user = await _userCacheService.GetUserAsync(join.UserId);
				var displayName = user?.DisplayName ?? join.Username;
				Debug.Log($"User {displayName} joined channel {channelId}");
			}

			foreach (var leave in presenceEvent.Leaves)
			{
				var user = await _userCacheService.GetUserAsync(leave.UserId);
				var displayName = user?.DisplayName ?? leave.Username;
				Debug.Log($"User {displayName} left channel {channelId}");
			}

			Debug.Log($"Channel {channelId} presence changed. Current count: {count}");
		}

		private async void OnChannelReady(string channelId)
		{
			try
			{
				var presences = _chatPresenceService.GetPresences(channelId).Values;
				var names = new List<string>();
				
				foreach (var presence in presences)
				{
					var user = await _userCacheService.GetUserAsync(presence.UserId);
					var displayName = !string.IsNullOrWhiteSpace(user?.DisplayName) ? user.DisplayName : presence.Username;
					
					names.Add(displayName);
				}
				
				Debug.Log($"Channel {channelId} participants: {string.Join(", ", names)}. Current count: {names.Count}");
			}
			catch (Exception ex)
			{
				Debug.LogError($"Failed to list participants for channel {channelId}: {ex.Message}");
			}
		}
	}
}


