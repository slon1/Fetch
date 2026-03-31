using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using WebRtcV2.Shared;
using WebRtcV2.Transport;

namespace WebRtcV2.Application.Booth
{
    public sealed class BoothFlowCoordinator : IBoothFlow, IDisposable
    {
        private readonly WorkerClient _worker;
        private readonly BoothSocketService _socketService;
        private readonly ConnectionDiagnostics _diagnostics;
        private readonly LocalClientIdentity _identity;

        private BoothSnapshot _currentSnapshot = BoothSnapshot.Empty;
        private string _boothNumber;
        private bool _disposed;

        public string LocalClientId => _identity.ClientId;
        public string LocalDisplayName => _identity.DisplayName;
        public string BoothNumber => _boothNumber;
        public BoothSnapshot CurrentSnapshot => _currentSnapshot;

        public event Action<BoothSnapshot> OnSnapshotChanged;
        public event Action<CallSessionRef> OnIncomingCall;
        public event Action<CallSessionRef> OnCallAccepted;
        public event Action<string> OnCallEnded;

        public BoothFlowCoordinator(
            WorkerClient worker,
            BoothSocketService socketService,
            ConnectionDiagnostics diagnostics,
            LocalClientIdentity identity)
        {
            _worker = worker;
            _socketService = socketService;
            _diagnostics = diagnostics;
            _identity = identity;
            _socketService.OnEvent += HandleSocketEvent;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _socketService.OnEvent -= HandleSocketEvent;
        }

        public async UniTask InitializeAsync(CancellationToken ct = default)
        {
            if (_disposed)
                return;

            int startAttempt = Math.Max(0, _identity.BoothAttempt);
            string stored = _identity.BoothNumber;
            if (!string.IsNullOrWhiteSpace(stored))
            {
                var storedResult = await _worker.RegisterBoothAsync(stored, _identity.ClientId, ct);
                if (storedResult != null && storedResult.ok)
                {
                    _boothNumber = storedResult.boothNumber;
                    _socketService.Connect(_boothNumber, _identity.ClientId, ct);
                    SetSnapshot(new BoothSnapshot(_boothNumber, BoothLineState.Idle, null, null, true));
                    return;
                }
            }

            for (int attempt = startAttempt; attempt < startAttempt + 8; attempt++)
            {
                string candidate = _identity.GetBoothNumberCandidate(attempt);
                var result = await _worker.RegisterBoothAsync(candidate, _identity.ClientId, ct);
                if (result == null)
                    continue;
                if (result.ok)
                {
                    _boothNumber = result.boothNumber;
                    _identity.PersistBoothNumber(_boothNumber, attempt);
                    _socketService.Connect(_boothNumber, _identity.ClientId, ct);
                    SetSnapshot(new BoothSnapshot(_boothNumber, BoothLineState.Idle, null, null, true));
                    return;
                }

                if (!string.Equals(result.error, WorkerClient.RegisterBoothErrors.NumberConflict, StringComparison.Ordinal))
                    break;
            }

            throw new InvalidOperationException("booth-registration-failed");
        }

        public async UniTask<DialResult> DialAsync(string targetNumber, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_boothNumber))
                return DialResult.Failed("local booth not registered");

            var response = await _worker.DialAsync(_boothNumber, _identity.ClientId, targetNumber, ct);
            if (response == null)
                return DialResult.Failed("dial request failed");

            switch (response.outcome)
            {
                case WorkerClient.DialOutcomes.Ringing:
                    var outgoingCall = MapCall(response.callId, response.callerNumber, response.calleeNumber, response.callerClientId);
                    SetSnapshot(new BoothSnapshot(_boothNumber, BoothLineState.RingingOutgoing, targetNumber, outgoingCall, true));
                    return DialResult.Ringing(outgoingCall);
                case WorkerClient.DialOutcomes.NotRegistered:
                    return DialResult.NotRegistered();
                case WorkerClient.DialOutcomes.Offline:
                    return DialResult.Offline();
                case WorkerClient.DialOutcomes.Busy:
                    return DialResult.Busy();
                default:
                    return DialResult.Failed(response.error ?? response.outcome ?? "dial failed");
            }
        }

        public async UniTask<CallSessionRef> AcceptAsync(string callId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(_boothNumber))
                return null;

            var response = await _worker.AcceptCallAsync(callId, _boothNumber, _identity.ClientId, ct);
            if (response == null || !response.ok)
                return null;

            var call = MapCall(response.callId, response.callerNumber, response.calleeNumber, response.callerClientId);
            SetSnapshot(new BoothSnapshot(_boothNumber, BoothLineState.Connecting, call.CallerNumber, call, true));
            return call;
        }

        public async UniTask<bool> RejectAsync(string callId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(_boothNumber))
                return false;

            bool ok = await _worker.RejectCallAsync(callId, _boothNumber, _identity.ClientId, ct);
            if (ok)
                ResetToIdle();
            return ok;
        }

        public async UniTask<bool> HangupLineAsync(string callId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(_boothNumber))
                return false;

            bool ok = await _worker.HangupCallAsync(callId, _boothNumber, _identity.ClientId, ct);
            if (ok)
                ResetToIdle();
            return ok;
        }

        public async UniTask<bool> MarkInCallAsync(string callId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(_boothNumber))
                return false;

            bool ok = await _worker.MarkCallConnectedAsync(callId, _boothNumber, _identity.ClientId, ct);
            if (!ok)
                return false;

            if (_currentSnapshot.Call != null && string.Equals(_currentSnapshot.Call.CallId, callId, StringComparison.Ordinal))
            {
                string peer = _currentSnapshot.PeerNumber;
                if (string.IsNullOrWhiteSpace(peer))
                    peer = _currentSnapshot.Call.IsLocalCaller
                        ? _currentSnapshot.Call.CalleeNumber
                        : _currentSnapshot.Call.CallerNumber;

                SetSnapshot(new BoothSnapshot(_boothNumber, BoothLineState.InCall, peer, _currentSnapshot.Call, true));
            }

            return true;
        }

        private void HandleSocketEvent(BoothSocketEvent socketEvent)
        {
            if (_disposed || socketEvent == null || string.IsNullOrWhiteSpace(_boothNumber))
                return;
            if (!string.Equals(socketEvent.boothNumber, _boothNumber, StringComparison.Ordinal))
                return;

            switch (socketEvent.type)
            {
                case BoothSocketEvent.Types.LineSnapshot:
                    SetSnapshot(MapSnapshot(socketEvent));
                    break;
                case BoothSocketEvent.Types.IncomingCall:
                    var incomingCall = MapCall(socketEvent.callId, socketEvent.callerNumber, socketEvent.calleeNumber, socketEvent.callerClientId);
                    SetSnapshot(new BoothSnapshot(_boothNumber, BoothLineState.RingingIncoming, incomingCall.CallerNumber, incomingCall, true));
                    OnIncomingCall?.Invoke(incomingCall);
                    break;
                case BoothSocketEvent.Types.OutgoingRinging:
                    SetSnapshot(new BoothSnapshot(_boothNumber, BoothLineState.RingingOutgoing, socketEvent.peerNumber, MapCall(socketEvent.callId, socketEvent.callerNumber, socketEvent.calleeNumber, socketEvent.callerClientId), true));
                    break;
                case BoothSocketEvent.Types.CallAccepted:
                    var acceptedCall = MapCall(socketEvent.callId, socketEvent.callerNumber, socketEvent.calleeNumber, socketEvent.callerClientId);
                    SetSnapshot(new BoothSnapshot(_boothNumber, BoothLineState.Connecting, acceptedCall.IsLocalCaller ? acceptedCall.CalleeNumber : acceptedCall.CallerNumber, acceptedCall, true));
                    OnCallAccepted?.Invoke(acceptedCall);
                    break;
                case BoothSocketEvent.Types.CallRejected:
                case BoothSocketEvent.Types.RemoteHangup:
                case BoothSocketEvent.Types.LineReset:
                    string endedCallId = _currentSnapshot.Call?.CallId ?? socketEvent.callId;
                    ResetToIdle();
                    if (!string.IsNullOrWhiteSpace(endedCallId))
                        OnCallEnded?.Invoke(endedCallId);
                    break;
            }
        }

        private BoothSnapshot MapSnapshot(BoothSocketEvent socketEvent)
        {
            var lineState = ParseLineState(socketEvent.lineState);
            var call = string.IsNullOrWhiteSpace(socketEvent.callId)
                ? null
                : MapCall(socketEvent.callId, socketEvent.callerNumber, socketEvent.calleeNumber, socketEvent.callerClientId);
            return new BoothSnapshot(socketEvent.boothNumber, lineState, socketEvent.peerNumber, call, true);
        }

        private CallSessionRef MapCall(string callId, string callerNumber, string calleeNumber, string callerClientId)
        {
            if (string.IsNullOrWhiteSpace(callId))
                return null;

            bool isLocalCaller = string.Equals(callerNumber, _boothNumber, StringComparison.Ordinal);
            return new CallSessionRef(callId, callerNumber, calleeNumber, callerClientId, isLocalCaller);
        }

        private static BoothLineState ParseLineState(string lineState)
        {
            return lineState switch
            {
                "dialing" => BoothLineState.Dialing,
                "ringing_outgoing" => BoothLineState.RingingOutgoing,
                "ringing_incoming" => BoothLineState.RingingIncoming,
                "connecting" => BoothLineState.Connecting,
                "in_call" => BoothLineState.InCall,
                _ => BoothLineState.Idle,
            };
        }

        private void ResetToIdle()
        {
            SetSnapshot(new BoothSnapshot(_boothNumber, BoothLineState.Idle, null, null, !string.IsNullOrWhiteSpace(_boothNumber)));
        }

        private void SetSnapshot(BoothSnapshot snapshot)
        {
            _currentSnapshot = snapshot ?? BoothSnapshot.Empty;
            OnSnapshotChanged?.Invoke(_currentSnapshot);
        }
    }
}

