using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using WebRtcV2.Application.Booth;
using WebRtcV2.Application.Connection;
using WebRtcV2.Shared;

namespace WebRtcV2.Presentation
{
    public sealed class LobbyUiController : IDisposable
    {
        private readonly LobbyScreenView _lobbyView;
        private readonly ConnectionStatusView _statusView;
        private readonly IBoothFlow _boothFlow;
        private readonly IConnectionFlow _connectionFlow;
        private readonly AndroidLocalNotificationService _notificationService;
        private readonly CancellationToken _appToken;
        private readonly Action _showCall;

        private CallSessionRef _currentPendingCall;
        private string _startedCallId;
        private bool _initialized;
        private bool _isTransitioning;
        private bool _disposed;

        public LobbyUiController(
            LobbyScreenView lobbyView,
            ConnectionStatusView statusView,
            IBoothFlow boothFlow,
            IConnectionFlow connectionFlow,
            AndroidLocalNotificationService notificationService,
            CancellationToken appToken,
            Action showCall)
        {
            _lobbyView = lobbyView;
            _statusView = statusView;
            _boothFlow = boothFlow;
            _connectionFlow = connectionFlow;
            _notificationService = notificationService;
            _appToken = appToken;
            _showCall = showCall;

            _lobbyView.OnDialRequested += HandleDialRequested;
            _lobbyView.OnAcceptRequested += HandleAcceptRequested;
            _lobbyView.OnRejectRequested += HandleRejectRequested;

            _boothFlow.OnSnapshotChanged += HandleBoothSnapshotChanged;
            _boothFlow.OnIncomingCall += HandleIncomingCall;
            _boothFlow.OnCallAccepted += HandleCallAccepted;
            _boothFlow.OnCallEnded += HandleCallEnded;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _lobbyView.OnDialRequested -= HandleDialRequested;
            _lobbyView.OnAcceptRequested -= HandleAcceptRequested;
            _lobbyView.OnRejectRequested -= HandleRejectRequested;

            _boothFlow.OnSnapshotChanged -= HandleBoothSnapshotChanged;
            _boothFlow.OnIncomingCall -= HandleIncomingCall;
            _boothFlow.OnCallAccepted -= HandleCallAccepted;
            _boothFlow.OnCallEnded -= HandleCallEnded;
        }

        public void Show()
        {
            _lobbyView.Show();
            RenderSnapshot(_boothFlow.CurrentSnapshot, null);
        }

        public void Hide() => _lobbyView.Hide();

        public async UniTask EnterLobbyAsync(CancellationToken ct)
        {
            if (_disposed) return;

            if (!_initialized)
            {
                _lobbyView.ShowInitializing("Registering booth...");
                await _boothFlow.InitializeAsync(ct);
                _initialized = true;
            }

            var snapshot = _boothFlow.CurrentSnapshot;
            RenderSnapshot(snapshot, null);
            TryStartCallFromSnapshot(snapshot);
        }

        private async void HandleDialRequested(string targetNumber)
        {
            if (_disposed || _isTransitioning) return;
            _isTransitioning = true;
            _statusView.ClearError();
            _lobbyView.SetBusy(true);

            try
            {
                string normalizedNumber = NormalizeDialNumber(targetNumber);
                if (normalizedNumber == null)
                {
                    _lobbyView.ShowIdle(_boothFlow.BoothNumber, "Enter a valid booth number");
                    return;
                }

                var result = await _boothFlow.DialAsync(normalizedNumber, _appToken);
                switch (result.Outcome)
                {
                    case BoothDialOutcome.Ringing:
                        _currentPendingCall = result.Call;
                        _lobbyView.ShowOutgoingRinging(_boothFlow.BoothNumber, normalizedNumber);
                        break;
                    case BoothDialOutcome.NotRegistered:
                        _lobbyView.ShowIdle(_boothFlow.BoothNumber, "Number is not registered");
                        break;
                    case BoothDialOutcome.Offline:
                        _lobbyView.ShowIdle(_boothFlow.BoothNumber, "User is offline");
                        break;
                    case BoothDialOutcome.Busy:
                        _lobbyView.ShowIdle(_boothFlow.BoothNumber, "Line is busy");
                        break;
                    default:
                        _statusView.ShowError($"Dial failed: {result.Error}");
                        _lobbyView.ShowIdle(_boothFlow.BoothNumber);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _statusView.ShowError($"Dial failed: {e.Message}");
                _lobbyView.ShowIdle(_boothFlow.BoothNumber);
            }
            finally
            {
                _isTransitioning = false;
                _lobbyView.SetBusy(false);
            }
        }

        private async void HandleAcceptRequested()
        {
            if (_disposed || _isTransitioning || _currentPendingCall == null) return;
            _isTransitioning = true;
            _statusView.ClearError();

            try
            {
                var acceptedCall = await _boothFlow.AcceptAsync(_currentPendingCall.CallId, _appToken);
                if (acceptedCall == null)
                {
                    _lobbyView.ShowIdle(_boothFlow.BoothNumber, "Could not accept the call");
                    return;
                }

                StartCall(acceptedCall);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _statusView.ShowError($"Accept failed: {e.Message}");
                _lobbyView.ShowIdle(_boothFlow.BoothNumber);
            }
            finally
            {
                _isTransitioning = false;
            }
        }

        private async void HandleRejectRequested()
        {
            if (_disposed || _isTransitioning || _currentPendingCall == null) return;
            _isTransitioning = true;
            _statusView.ClearError();

            try
            {
                await _boothFlow.RejectAsync(_currentPendingCall.CallId, _appToken);
                _currentPendingCall = null;
                _lobbyView.ShowIdle(_boothFlow.BoothNumber);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _statusView.ShowError($"Reject failed: {e.Message}");
                _lobbyView.ShowIdle(_boothFlow.BoothNumber);
            }
            finally
            {
                _isTransitioning = false;
            }
        }

        private void HandleBoothSnapshotChanged(BoothSnapshot snapshot)
        {
            if (_disposed) return;
            RenderSnapshot(snapshot, null);
            TryStartCallFromSnapshot(snapshot);
        }

        private void HandleIncomingCall(CallSessionRef call)
        {
            if (_disposed || call == null) return;
            _currentPendingCall = call;
            _notificationService.NotifyIncomingCall(call.CallId, call.CallerNumber);
            RenderSnapshot(_boothFlow.CurrentSnapshot, null);
        }

        private void HandleCallAccepted(CallSessionRef call)
        {
            if (_disposed || call == null || !call.IsLocalCaller) return;
            StartCall(call);
        }

        private void HandleCallEnded(string callId)
        {
            if (string.IsNullOrWhiteSpace(callId)) return;
            if (string.Equals(_startedCallId, callId, StringComparison.Ordinal))
                _startedCallId = null;
            if (_currentPendingCall != null && string.Equals(_currentPendingCall.CallId, callId, StringComparison.Ordinal))
                _currentPendingCall = null;

            RenderSnapshot(_boothFlow.CurrentSnapshot, null);
        }

        private void StartCall(CallSessionRef call)
        {
            if (call == null)
                return;
            if (string.Equals(_startedCallId, call.CallId, StringComparison.Ordinal))
                return;

            _startedCallId = call.CallId;
            _currentPendingCall = call;
            string peerNumber = call.IsLocalCaller ? call.CalleeNumber : call.CallerNumber;
            _lobbyView.ShowConnecting(_boothFlow.BoothNumber, peerNumber);
            _showCall?.Invoke();

            if (call.IsLocalCaller)
                _connectionFlow.ConnectAsCallerAsync(call.CallId, _appToken).Forget();
            else
                _connectionFlow.ConnectAsCalleeAsync(call.CallId, call.CallerClientId, _appToken).Forget();
        }

        private void TryStartCallFromSnapshot(BoothSnapshot snapshot)
        {
            if (_disposed || snapshot?.Call == null)
                return;
            if (snapshot.LineState != BoothLineState.Connecting)
                return;

            StartCall(snapshot.Call);
        }

        private static string NormalizeDialNumber(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            char[] buffer = new char[LocalClientIdentity.BoothNumberLength];
            int count = 0;
            foreach (char c in raw)
            {
                if (!char.IsDigit(c))
                    continue;
                if (count >= buffer.Length)
                    return null;
                buffer[count++] = c;
            }

            return count == LocalClientIdentity.BoothNumberLength ? new string(buffer, 0, count) : null;
        }

        private void RenderSnapshot(BoothSnapshot snapshot, string message)
        {
            snapshot ??= BoothSnapshot.Empty;
            _currentPendingCall = snapshot.Call;

            switch (snapshot.LineState)
            {
                case BoothLineState.RingingIncoming:
                    _lobbyView.ShowIncomingCall(snapshot.BoothNumber, snapshot.Call?.CallerNumber ?? snapshot.PeerNumber);
                    break;
                case BoothLineState.RingingOutgoing:
                    _lobbyView.ShowOutgoingRinging(snapshot.BoothNumber, snapshot.Call?.CalleeNumber ?? snapshot.PeerNumber);
                    break;
                case BoothLineState.Connecting:
                case BoothLineState.InCall:
                    _lobbyView.ShowConnecting(snapshot.BoothNumber, snapshot.PeerNumber ?? snapshot.Call?.CallerNumber ?? snapshot.Call?.CalleeNumber);
                    break;
                case BoothLineState.Idle:
                default:
                    _lobbyView.ShowIdle(snapshot.BoothNumber, message);
                    break;
            }
        }
    }
}
