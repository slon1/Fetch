namespace WebRtcV2.Application.Connection
{
    public enum ConnectionState
    {
        Idle,
        Preparing,
        Signaling,
        Connecting,
        ConnectedAudio,
        ConnectedDataOnly,
        Reconnecting,
        Failed,
        Closed
    }
}
