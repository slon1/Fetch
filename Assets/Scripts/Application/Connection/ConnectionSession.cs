namespace WebRtcV2.Application.Connection
{
    /// <summary>
    /// Mutable runtime state for a single WebRTC session.
    ///
    /// Owned and updated exclusively by <see cref="ConnectionFlowCoordinator"/>.
    /// The outside world reads state through <see cref="ToSnapshot"/>.
    ///
    /// Lifecycle:
    ///   <see cref="Initialize"/> at the start of each new caller or callee flow.
    ///   Individual Set* methods keep fields in sync as the session progresses.
    ///   <see cref="Reset"/> on session teardown (called from CleanupSession).
    /// </summary>
    public sealed class ConnectionSession
    {
        public ConnectionLifecycleState LifecycleState { get; private set; } = ConnectionLifecycleState.Idle;
        public MediaMode MediaMode { get; private set; } = MediaMode.AudioOnly;
        public RouteMode RouteMode { get; private set; } = RouteMode.Direct;
        public SignalingMode SignalingMode { get; private set; } = SignalingMode.WorkerPolling;

        public string SessionId { get; private set; }
        public bool IsCreator { get; private set; }
        public bool HasOpenDataChannel { get; private set; }
        public int ReconnectAttempts { get; private set; }
        public string LastError { get; private set; }

        public bool IsConnected => LifecycleState == ConnectionLifecycleState.Connected;

        // ── Session lifecycle ─────────────────────────────────────────────

        /// <summary>
        /// Resets all session fields and prepares for a new caller or callee flow.
        /// Must be called before the first FSM transition of the new session.
        /// </summary>
        public void Initialize(
            string sessionId,
            bool isCreator,
            MediaMode mediaMode = MediaMode.AudioOnly,
            RouteMode routeMode = RouteMode.Direct,
            SignalingMode signalingMode = SignalingMode.WorkerPolling)
        {
            SessionId = sessionId;
            IsCreator = isCreator;
            MediaMode = mediaMode;
            RouteMode = routeMode;
            SignalingMode = signalingMode;
            LifecycleState = ConnectionLifecycleState.Idle;
            HasOpenDataChannel = false;
            ReconnectAttempts = 0;
            LastError = null;
        }

        /// <summary>
        /// Clears runtime-only fields. Called at the end of a session.
        /// Mode fields (MediaMode, RouteMode, SignalingMode) are left at their last values
        /// so they serve as defaults for the next <see cref="Initialize"/> call.
        /// </summary>
        public void Reset()
        {
            LifecycleState = ConnectionLifecycleState.Idle;
            HasOpenDataChannel = false;
            SessionId = null;
            IsCreator = false;
            LastError = null;
            ReconnectAttempts = 0;
        }

        // ── Per-event updates ─────────────────────────────────────────────

        public void SetLifecycleState(ConnectionLifecycleState state) =>
            LifecycleState = state;

        public void SetLastError(string error) =>
            LastError = error;

        public void SetDataChannelOpen(bool open) =>
            HasOpenDataChannel = open;

        public void SetMediaMode(MediaMode mode) =>
            MediaMode = mode;

        public void SetRouteMode(RouteMode mode) =>
            RouteMode = mode;

        public void SetSignalingMode(SignalingMode mode) =>
            SignalingMode = mode;

        public void IncrementReconnectAttempts() =>
            ReconnectAttempts++;

        // ── Snapshot ──────────────────────────────────────────────────────

        public ConnectionSnapshot ToSnapshot() => new ConnectionSnapshot(
            lifecycleState: LifecycleState,
            mediaMode: MediaMode,
            routeMode: RouteMode,
            signalingMode: SignalingMode,
            sessionId: SessionId,
            isCreator: IsCreator,
            isConnected: IsConnected,
            hasOpenDataChannel: HasOpenDataChannel,
            reconnectAttempts: ReconnectAttempts,
            lastError: LastError);
    }
}
