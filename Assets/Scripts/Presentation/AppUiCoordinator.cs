using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using WebRtcV2.Application.Connection;
using WebRtcV2.Application.Room;
using WebRtcV2.Config;
using WebRtcV2.Shared;
using WebRtcV2.Transport;

namespace WebRtcV2.Presentation
{
    /// <summary>
    /// Scene-level UI router for the main game scene.
    /// Owns screen transitions, auto-lobby bootstrap and terminal-state handling.
    /// </summary>
    public sealed class AppUiCoordinator : IDisposable
    {
        private readonly ConnectionStatusView _statusView;
        private readonly IRoomFlow _roomFlow;
        private readonly IConnectionFlow _connectionFlow;
        private readonly AppConfig _config;
        private readonly CancellationToken _appToken;
        private readonly AndroidLocalNotificationService _notificationService;
        private readonly LobbyUiController _lobbyController;
        private readonly CallUiController _callController;

        private ConnectionSnapshot _previousSnapshot = ConnectionSnapshot.Idle;
        private bool _isHandlingTerminalSnapshot;
        private bool _disposed;

        public AppUiCoordinator(
            AppConfig config,
            LobbyScreenView lobbyView,
            CallScreenView callView,
            ConnectionStatusView statusView,
            AudioSource remoteAudioSource,
            MediaCaptureService mediaCapture,
            IRoomFlow roomFlow,
            IConnectionFlow connectionFlow,
            RoomHeartbeatService heartbeatService,
            AndroidLocalNotificationService notificationService,
            CancellationToken appToken)
        {
            _statusView = statusView;
            _roomFlow = roomFlow;
            _connectionFlow = connectionFlow;
            _config = config;
            _notificationService = notificationService;
            _appToken = appToken;

            _lobbyController = new LobbyUiController(
                lobbyView,
                statusView,
                roomFlow,
                connectionFlow,
                heartbeatService,
                notificationService,
                config,
                appToken,
                ShowCall);

            _callController = new CallUiController(
                callView,
                statusView,
                remoteAudioSource,
                mediaCapture,
                connectionFlow,
                appToken);

            _connectionFlow.OnSnapshotChanged += HandleSnapshotChanged;
        }

        public void Initialize()
        {
            ShowLobby();
            _lobbyController.EnterLobbyAsync(_appToken).Forget();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _connectionFlow.OnSnapshotChanged -= HandleSnapshotChanged;
            _callController.Dispose();
            _lobbyController.Dispose();
        }

        private void HandleSnapshotChanged(ConnectionSnapshot snapshot)
        {
            _callController.ApplySnapshot(snapshot);

            if (snapshot.LifecycleState == ConnectionLifecycleState.Connected &&
                _previousSnapshot.LifecycleState != ConnectionLifecycleState.Connected)
            {
                _notificationService.NotifyConnected(snapshot.SessionId, _roomFlow.LocalDisplayName);
            }

            if (snapshot.LifecycleState == ConnectionLifecycleState.Closed ||
                snapshot.LifecycleState == ConnectionLifecycleState.Failed)
            {
                HandleTerminalSnapshotAsync(snapshot).Forget();
            }

            _previousSnapshot = snapshot;
        }

        private async UniTaskVoid HandleTerminalSnapshotAsync(ConnectionSnapshot snapshot)
        {
            if (_isHandlingTerminalSnapshot) return;
            _isHandlingTerminalSnapshot = true;

            try
            {
                _notificationService.CancelSessionNotification(snapshot.SessionId);
                _callController.ClearTransientMedia();
                ShowLobby();

                if (snapshot.IsCreator && !string.IsNullOrEmpty(snapshot.SessionId))
                    DeleteOwnedRoomAfterHangupGraceAsync(snapshot.SessionId).Forget();

                if (!_appToken.IsCancellationRequested)
                    await _lobbyController.EnterLobbyAsync(_appToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _statusView.ShowError($"Return to lobby failed: {e.Message}");
            }
            finally
            {
                _isHandlingTerminalSnapshot = false;
            }
        }

        private void ShowLobby()
        {
            _callController.Hide();
            _lobbyController.Show();
        }

        private void ShowCall()
        {
            _lobbyController.Hide();
            _callController.Show();
        }

        private async UniTaskVoid DeleteOwnedRoomAfterHangupGraceAsync(string sessionId)
        {
            try
            {
                float graceSeconds = Math.Max(5f, _config.workerEndpoint.pollingIntervalSec * 3f);
                await UniTask.Delay(TimeSpan.FromSeconds(graceSeconds), cancellationToken: _appToken)
                    .SuppressCancellationThrow();

                if (_appToken.IsCancellationRequested || string.IsNullOrWhiteSpace(sessionId))
                    return;

                await _roomFlow.DeleteRoomAsync(sessionId, CancellationToken.None);
            }
            catch (Exception e)
            {
                WLog.Warn("AppUi", $"Deferred room delete failed: {e.Message}");
            }
        }
    }
}
