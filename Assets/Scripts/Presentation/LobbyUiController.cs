using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using WebRtcV2.Application.Connection;
using WebRtcV2.Application.Room;
using WebRtcV2.Config;
using WebRtcV2.Shared;
using WebRtcV2.Transport;

namespace WebRtcV2.Presentation
{
    /// <summary>
    /// Owns lobby-specific UI orchestration for auto-lobby, waiting room presence,
    /// join flow and re-bootstrap after terminal call states.
    /// </summary>
    public sealed class LobbyUiController : IDisposable
    {
        private readonly LobbyScreenView _lobbyView;
        private readonly ConnectionStatusView _statusView;
        private readonly IRoomFlow _roomFlow;
        private readonly IConnectionFlow _connectionFlow;
        private readonly RoomHeartbeatService _heartbeatService;
        private readonly RoomControlSocketService _roomControlSocket;
        private readonly AndroidLocalNotificationService _notificationService;
        private readonly AppConfig _config;
        private readonly CancellationToken _appToken;
        private readonly Action _showCall;
        private readonly object _bootstrapSync = new object();

        private CancellationTokenSource _lobbyCts;
        private UniTaskCompletionSource _bootstrapCompletion;
        private RoomModel _ownedWaitingRoom;
        private bool _isTransitioning;
        private bool _bootstrapInFlight;
        private bool _disposed;

        public LobbyUiController(
            LobbyScreenView lobbyView,
            ConnectionStatusView statusView,
            IRoomFlow roomFlow,
            IConnectionFlow connectionFlow,
            RoomHeartbeatService heartbeatService,
            RoomControlSocketService roomControlSocket,
            AndroidLocalNotificationService notificationService,
            AppConfig config,
            CancellationToken appToken,
            Action showCall)
        {
            _lobbyView = lobbyView;
            _statusView = statusView;
            _roomFlow = roomFlow;
            _connectionFlow = connectionFlow;
            _heartbeatService = heartbeatService;
            _roomControlSocket = roomControlSocket;
            _notificationService = notificationService;
            _config = config;
            _appToken = appToken;
            _showCall = showCall;

            _lobbyView.OnCreateRoom += HandleCreateRoom;
            _lobbyView.OnRefreshRooms += HandleRefreshRoomsClicked;
            _lobbyView.OnRoomSelected += HandleRoomSelected;
            _roomControlSocket.OnEvent += HandleRoomControlEvent;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopLobbyLoops(disconnectSocket: true);
            _lobbyView.OnCreateRoom -= HandleCreateRoom;
            _lobbyView.OnRefreshRooms -= HandleRefreshRoomsClicked;
            _lobbyView.OnRoomSelected -= HandleRoomSelected;
            _roomControlSocket.OnEvent -= HandleRoomControlEvent;
        }

        public void Show() => _lobbyView.Show();

        public void Hide()
        {
            StopLobbyLoops(disconnectSocket: false);
            _lobbyView.Hide();
        }

        public UniTask EnterLobbyAsync(CancellationToken ct) => RequestBootstrapAsync(ct);

        private async void HandleCreateRoom(string _)
        {
            if (_isTransitioning) return;
            await RequestBootstrapAsync(_appToken);
        }

        private void HandleRefreshRoomsClicked()
        {
            if (_isTransitioning) return;
            RequestBootstrapAsync(_appToken).Forget();
        }

        private UniTask RequestBootstrapAsync(CancellationToken ct)
        {
            if (_disposed)
                return UniTask.CompletedTask;

            UniTask waitTask;
            bool shouldStart;

            lock (_bootstrapSync)
            {
                if (_bootstrapInFlight)
                {
                    waitTask = _bootstrapCompletion?.Task ?? UniTask.CompletedTask;
                    shouldStart = false;
                }
                else
                {
                    _bootstrapInFlight = true;
                    _bootstrapCompletion = new UniTaskCompletionSource();
                    waitTask = _bootstrapCompletion.Task;
                    shouldStart = true;
                }
            }

            if (shouldStart)
                RunBootstrapAsync(ct).Forget();

            return waitTask;
        }

        private async UniTaskVoid RunBootstrapAsync(CancellationToken ct)
        {
            try
            {
                await BootstrapCoreAsync(ct);
            }
            finally
            {
                UniTaskCompletionSource completion;
                lock (_bootstrapSync)
                {
                    completion = _bootstrapCompletion;
                    _bootstrapCompletion = null;
                    _bootstrapInFlight = false;
                }

                completion?.TrySetResult();
            }
        }

        private async UniTask BootstrapCoreAsync(CancellationToken ct)
        {
            if (_disposed) return;

            StopLobbyLoops(disconnectSocket: true);
            _isTransitioning = true;
            _statusView.ClearError();
            _lobbyView.ShowLoadingState("Загрузка комнат...");

            _lobbyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _lobbyCts.Token;

            try
            {
                var bootstrap = await _roomFlow.BootstrapLobbyAsync(token);
                if (bootstrap.Mode == LobbyBootstrapMode.JoinExisting)
                {
                    _ownedWaitingRoom = null;
                    _lobbyView.ShowJoinableRooms(bootstrap.ForeignRooms);
                    return;
                }

                _ownedWaitingRoom = bootstrap.OwnedRoom;
                if (_ownedWaitingRoom == null)
                    throw new InvalidOperationException("owned waiting room was not created");

                _lobbyView.ShowWaitingRoom(_ownedWaitingRoom);
                _roomControlSocket.ConnectToRoom(_ownedWaitingRoom.SessionId, _roomFlow.LocalClientId, token);
                _heartbeatService.Start(_ownedWaitingRoom.SessionId, token);
                PollOwnedRoomStatusAsync(_ownedWaitingRoom.SessionId, token).Forget();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _statusView.ShowError($"Lobby bootstrap failed: {e.Message}");
                _lobbyView.ShowJoinableRooms(Array.Empty<RoomModel>());
            }
            finally
            {
                _isTransitioning = false;
            }
        }

        private async UniTaskVoid PollOwnedRoomStatusAsync(string sessionId, CancellationToken ct)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(10f, _config.workerEndpoint.roomHeartbeatIntervalSec));

            try
            {
                while (!ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(sessionId))
                {
                    var room = await _roomFlow.GetRoomAsync(sessionId, ct);
                    if (room == null || room.IsClosed)
                    {
                        _statusView.ShowError("Комната ожидания завершилась. Создаем заново...");
                        await RequestBootstrapAsync(_appToken);
                        return;
                    }

                    if (room.IsJoined)
                    {
                        BeginOwnedRoomConnectAsync(room).Forget();
                        return;
                    }

                    _ownedWaitingRoom = room;
                    _lobbyView.ShowWaitingRoom(room);
                    await UniTask.Delay(interval, cancellationToken: ct).SuppressCancellationThrow();
                    if (ct.IsCancellationRequested)
                        return;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _statusView.ShowError($"Waiting room failed: {e.Message}");
                await RequestBootstrapAsync(_appToken);
            }
        }

        private async void HandleRoomSelected(RoomModel room)
        {
            if (_isTransitioning || room == null) return;
            _isTransitioning = true;
            _statusView.ClearError();
            _lobbyView.SetLoading(true);
            StopLobbyLoops(disconnectSocket: true);

            try
            {
                var result = await _roomFlow.JoinRoomAsync(room, _appToken);
                if (!result.Success)
                {
                    _statusView.ShowError($"Join failed: {result.Error}");
                    await RequestBootstrapAsync(_appToken);
                    return;
                }

                _roomControlSocket.ConnectToRoom(result.SessionId, _roomFlow.LocalClientId, _appToken);
                _showCall?.Invoke();
                _connectionFlow.ConnectAsCalleeAsync(result.SessionId, result.CallerPeerId, _appToken).Forget();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _statusView.ShowError($"Join failed: {e.Message}");
                await RequestBootstrapAsync(_appToken);
            }
            finally
            {
                _isTransitioning = false;
                _lobbyView.SetLoading(false);
            }
        }

        private void HandleRoomControlEvent(RoomControlEvent controlEvent)
        {
            if (_disposed || controlEvent == null || _ownedWaitingRoom == null)
                return;
            if (!string.Equals(controlEvent.sessionId, _ownedWaitingRoom.SessionId, StringComparison.Ordinal))
                return;

            switch (controlEvent.type)
            {
                case RoomControlEvent.Types.PeerJoined:
                    BeginOwnedRoomConnectAsync(_ownedWaitingRoom).Forget();
                    break;
                case RoomControlEvent.Types.RoomState when string.Equals(controlEvent.roomStatus, "joined", StringComparison.OrdinalIgnoreCase):
                    BeginOwnedRoomConnectAsync(_ownedWaitingRoom).Forget();
                    break;
                case RoomControlEvent.Types.RoomClosed:
                    _statusView.ShowError("Комната ожидания закрылась. Создаем заново...");
                    RequestBootstrapAsync(_appToken).Forget();
                    break;
            }
        }

        private async UniTaskVoid BeginOwnedRoomConnectAsync(RoomModel room)
        {
            if (_disposed || _isTransitioning || room == null || _ownedWaitingRoom == null)
                return;
            if (!string.Equals(room.SessionId, _ownedWaitingRoom.SessionId, StringComparison.Ordinal))
                return;

            _isTransitioning = true;
            try
            {
                _notificationService.NotifyPeerJoined(room.SessionId, room.DisplayName);
                _heartbeatService.Stop();
                StopLobbyTokenOnly();
                _ownedWaitingRoom = room;
                _showCall?.Invoke();
                _connectionFlow.ConnectAsCallerAsync(room.SessionId, _appToken).Forget();
            }
            catch (Exception e)
            {
                _statusView.ShowError($"Waiting room connect failed: {e.Message}");
                await RequestBootstrapAsync(_appToken);
            }
            finally
            {
                _isTransitioning = false;
            }
        }

        private void StopLobbyLoops(bool disconnectSocket)
        {
            _ownedWaitingRoom = null;
            _heartbeatService.Stop();
            StopLobbyTokenOnly();
            if (disconnectSocket)
                _roomControlSocket.Disconnect();
        }

        private void StopLobbyTokenOnly()
        {
            _lobbyCts?.Cancel();
            _lobbyCts?.Dispose();
            _lobbyCts = null;
        }
    }
}
