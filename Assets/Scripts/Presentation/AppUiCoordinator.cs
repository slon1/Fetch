using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using WebRtcV2.Application.Booth;
using WebRtcV2.Application.Connection;
using WebRtcV2.Shared;
using WebRtcV2.Transport;

namespace WebRtcV2.Presentation
{
    public sealed class AppUiCoordinator : IDisposable
    {
        private readonly ConnectionStatusView _statusView;
        private readonly IBoothFlow _boothFlow;
        private readonly IConnectionFlow _connectionFlow;
        private readonly CancellationToken _appToken;
        private readonly AndroidLocalNotificationService _notificationService;
        private readonly LobbyUiController _lobbyController;
        private readonly CallUiController _callController;

        private ConnectionSnapshot _previousSnapshot = ConnectionSnapshot.Idle;
        private bool _isHandlingTerminalSnapshot;
        private bool _disposed;

        public AppUiCoordinator(
            LobbyScreenView lobbyView,
            CallScreenView callView,
            ConnectionStatusView statusView,
            AudioSource remoteAudioSource,
            MediaCaptureService mediaCapture,
            IBoothFlow boothFlow,
            IConnectionFlow connectionFlow,
            AndroidLocalNotificationService notificationService,
            CancellationToken appToken)
        {
            _statusView = statusView;
            _boothFlow = boothFlow;
            _connectionFlow = connectionFlow;
            _notificationService = notificationService;
            _appToken = appToken;

            _lobbyController = new LobbyUiController(
                lobbyView,
                statusView,
                boothFlow,
                connectionFlow,
                notificationService,
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
                SyncBoothLineInCallAsync(snapshot.SessionId).Forget();
                string peer = _boothFlow.CurrentSnapshot?.PeerNumber;
                _notificationService.NotifyConnected(snapshot.SessionId, peer);
            }

            if (snapshot.LifecycleState == ConnectionLifecycleState.Closed ||
                snapshot.LifecycleState == ConnectionLifecycleState.Failed)
            {
                HandleTerminalSnapshotAsync(snapshot).Forget();
            }

            _previousSnapshot = snapshot;
        }

        private async UniTaskVoid SyncBoothLineInCallAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return;

            try
            {
                await _boothFlow.MarkInCallAsync(sessionId, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppUiCoordinator] Failed to mark line in-call: {e.Message}");
            }
        }

        private async UniTaskVoid HandleTerminalSnapshotAsync(ConnectionSnapshot snapshot)
        {
            if (_isHandlingTerminalSnapshot) return;
            _isHandlingTerminalSnapshot = true;

            try
            {
                _notificationService.CancelSessionNotification(snapshot.SessionId);
                _callController.ClearTransientMedia();

                if (!string.IsNullOrWhiteSpace(snapshot.SessionId))
                    await _boothFlow.HangupLineAsync(snapshot.SessionId, CancellationToken.None).SuppressCancellationThrow();

                ShowLobby();
                if (!_appToken.IsCancellationRequested)
                    await _lobbyController.EnterLobbyAsync(_appToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _statusView.ShowError($"Return to booth failed: {e.Message}");
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
    }
}
