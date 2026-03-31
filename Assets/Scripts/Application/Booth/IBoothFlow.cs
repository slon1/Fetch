using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace WebRtcV2.Application.Booth
{
    public interface IBoothFlow
    {
        string LocalClientId { get; }
        string LocalDisplayName { get; }
        string BoothNumber { get; }
        BoothSnapshot CurrentSnapshot { get; }

        event Action<BoothSnapshot> OnSnapshotChanged;
        event Action<CallSessionRef> OnIncomingCall;
        event Action<CallSessionRef> OnCallAccepted;
        event Action<string> OnCallEnded;

        UniTask InitializeAsync(CancellationToken ct = default);
        UniTask<DialResult> DialAsync(string targetNumber, CancellationToken ct = default);
        UniTask<CallSessionRef> AcceptAsync(string callId, CancellationToken ct = default);
        UniTask<bool> RejectAsync(string callId, CancellationToken ct = default);
        UniTask<bool> HangupLineAsync(string callId, CancellationToken ct = default);
        UniTask<bool> MarkInCallAsync(string callId, CancellationToken ct = default);
    }

    public enum BoothLineState
    {
        Idle,
        Dialing,
        RingingOutgoing,
        RingingIncoming,
        Connecting,
        InCall,
    }

    public enum BoothDialOutcome
    {
        Ringing,
        NotRegistered,
        Offline,
        Busy,
        Failed,
    }

    public sealed class DialResult
    {
        public BoothDialOutcome Outcome { get; }
        public CallSessionRef Call { get; }
        public string Error { get; }

        public bool IsSuccess => Outcome == BoothDialOutcome.Ringing && Call != null;

        private DialResult(BoothDialOutcome outcome, CallSessionRef call, string error)
        {
            Outcome = outcome;
            Call = call;
            Error = error;
        }

        public static DialResult Ringing(CallSessionRef call) => new DialResult(BoothDialOutcome.Ringing, call, null);
        public static DialResult NotRegistered() => new DialResult(BoothDialOutcome.NotRegistered, null, null);
        public static DialResult Offline() => new DialResult(BoothDialOutcome.Offline, null, null);
        public static DialResult Busy() => new DialResult(BoothDialOutcome.Busy, null, null);
        public static DialResult Failed(string error) => new DialResult(BoothDialOutcome.Failed, null, error);
    }

    public sealed class CallSessionRef
    {
        public string CallId { get; }
        public string CallerNumber { get; }
        public string CalleeNumber { get; }
        public string CallerClientId { get; }
        public bool IsLocalCaller { get; }

        public CallSessionRef(
            string callId,
            string callerNumber,
            string calleeNumber,
            string callerClientId,
            bool isLocalCaller)
        {
            CallId = callId;
            CallerNumber = callerNumber;
            CalleeNumber = calleeNumber;
            CallerClientId = callerClientId;
            IsLocalCaller = isLocalCaller;
        }
    }

    public sealed class BoothSnapshot
    {
        public string BoothNumber { get; }
        public BoothLineState LineState { get; }
        public string PeerNumber { get; }
        public CallSessionRef Call { get; }
        public bool IsRegistered { get; }

        public BoothSnapshot(
            string boothNumber,
            BoothLineState lineState,
            string peerNumber,
            CallSessionRef call,
            bool isRegistered)
        {
            BoothNumber = boothNumber;
            LineState = lineState;
            PeerNumber = peerNumber;
            Call = call;
            IsRegistered = isRegistered;
        }

        public static BoothSnapshot Empty => new BoothSnapshot(null, BoothLineState.Idle, null, null, false);
    }
}
