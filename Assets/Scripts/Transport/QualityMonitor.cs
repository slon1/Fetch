using System;
using Unity.WebRTC;

namespace WebRtcV2.Transport
{
    /// <summary>
    /// Monitor-lite for MVP: watches ICE connection state and raises neutral quality signals.
    /// Does NOT make policy decisions. The application layer (ConnectionFlowCoordinator) acts on these signals.
    /// Full getStats() analysis is deferred to a later release.
    /// </summary>
    public class QualityMonitor
    {
        public enum Signal
        {
            Normal,
            Degraded,
            Critical,
            DisconnectedLikely
        }

        private Signal _current = Signal.Normal;

        public Signal CurrentSignal => _current;

        public event Action<Signal> OnSignalChanged;

        public void OnIceStateChanged(RTCIceConnectionState state)
        {
            var next = state switch
            {
                RTCIceConnectionState.Connected  => Signal.Normal,
                RTCIceConnectionState.Completed  => Signal.Normal,
                RTCIceConnectionState.Checking   => Signal.Normal,
                RTCIceConnectionState.New        => Signal.Normal,
                RTCIceConnectionState.Disconnected => Signal.DisconnectedLikely,
                RTCIceConnectionState.Failed     => Signal.Critical,
                RTCIceConnectionState.Closed     => Signal.DisconnectedLikely,
                _                               => Signal.Normal
            };

            if (next == _current) return;
            _current = next;
            OnSignalChanged?.Invoke(_current);
        }

        public void Reset()
        {
            _current = Signal.Normal;
        }
    }
}
