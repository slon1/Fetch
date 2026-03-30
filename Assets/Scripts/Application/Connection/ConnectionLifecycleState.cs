namespace WebRtcV2.Application.Connection
{
    /// <summary>
    /// Lifecycle-only FSM states for a WebRTC session.
    ///
    /// Media mode (AudioOnly / DataOnly / Full) is tracked separately in
    /// <see cref="MediaMode"/> so the FSM does not need to encode codec/track state.
    ///
    /// Recovery strategy and route selection are also kept out of lifecycle:
    /// see <see cref="RouteMode"/> and the future policy engine (Stage 5).
    /// </summary>
    public enum ConnectionLifecycleState
    {
        Idle,
        Preparing,
        Signaling,
        Connecting,
        Connected,
        Recovering,
        Failed,
        Closed,
    }
}
