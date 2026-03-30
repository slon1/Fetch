namespace WebRtcV2.Application.Connection
{
    /// <summary>
    /// Immutable point-in-time snapshot of a <see cref="ConnectionSession"/>.
    ///
    /// Produced by <see cref="ConnectionSession.ToSnapshot"/> and published via
    /// <see cref="IConnectionFlow.OnSnapshotChanged"/>. UI and Bootstrap observe this
    /// object instead of reading mutable state directly.
    ///
    /// All fields reflect the session at the moment the snapshot was taken.
    /// If the session was reset before the snapshot was read, <see cref="SessionId"/>
    /// may be null and <see cref="LifecycleState"/> will be <see cref="ConnectionLifecycleState.Idle"/>.
    /// </summary>
    public sealed class ConnectionSnapshot
    {
        public ConnectionLifecycleState LifecycleState { get; }
        public MediaMode MediaMode { get; }
        public RouteMode RouteMode { get; }
        public SignalingMode SignalingMode { get; }

        public string SessionId { get; }
        public bool IsCreator { get; }
        public bool IsConnected { get; }
        public bool HasOpenDataChannel { get; }
        public int ReconnectAttempts { get; }
        public string LastError { get; }

        public ConnectionSnapshot(
            ConnectionLifecycleState lifecycleState,
            MediaMode mediaMode,
            RouteMode routeMode,
            SignalingMode signalingMode,
            string sessionId,
            bool isCreator,
            bool isConnected,
            bool hasOpenDataChannel,
            int reconnectAttempts,
            string lastError)
        {
            LifecycleState = lifecycleState;
            MediaMode = mediaMode;
            RouteMode = routeMode;
            SignalingMode = signalingMode;
            SessionId = sessionId;
            IsCreator = isCreator;
            IsConnected = isConnected;
            HasOpenDataChannel = hasOpenDataChannel;
            ReconnectAttempts = reconnectAttempts;
            LastError = lastError;
        }

        /// <summary>A neutral snapshot representing the idle/uninitialized state.</summary>
        public static readonly ConnectionSnapshot Idle = new ConnectionSnapshot(
            lifecycleState: ConnectionLifecycleState.Idle,
            mediaMode: MediaMode.AudioOnly,
            routeMode: RouteMode.Direct,
            signalingMode: SignalingMode.WorkerPolling,
            sessionId: null,
            isCreator: false,
            isConnected: false,
            hasOpenDataChannel: false,
            reconnectAttempts: 0,
            lastError: null);

        public override string ToString() =>
            $"[{LifecycleState}|{MediaMode}|{RouteMode}|{SignalingMode}]" +
            $" session={SessionId} creator={IsCreator} dc={HasOpenDataChannel}";
    }
}
