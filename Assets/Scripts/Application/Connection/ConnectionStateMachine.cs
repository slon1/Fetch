using System;
using System.Collections.Generic;
using WebRtcV2.Shared;

namespace WebRtcV2.Application.Connection
{
    /// <summary>
    /// Lifecycle-only FSM for a WebRTC session.
    ///
    /// Works exclusively with <see cref="ConnectionLifecycleState"/>.
    /// Media mode, route and signaling strategy are tracked in
    /// <see cref="ConnectionSession"/> — NOT encoded as FSM states.
    ///
    /// ConnectedAudio / ConnectedDataOnly from the old model are both represented
    /// as <see cref="ConnectionLifecycleState.Connected"/>; the coordinator updates
    /// <see cref="ConnectionSession.MediaMode"/> to distinguish them.
    ///
    /// Reconnecting from the old model is <see cref="ConnectionLifecycleState.Recovering"/>.
    /// </summary>
    public class ConnectionStateMachine
    {
        private ConnectionLifecycleState _state = ConnectionLifecycleState.Idle;
        private readonly ConnectionDiagnostics _diagnostics;

        public ConnectionLifecycleState Current => _state;

        /// <summary>Fired after every successful state transition.</summary>
        public event Action<ConnectionLifecycleState> OnStateChanged;

        // ── Allowed transitions ───────────────────────────────────────────

        private static readonly Dictionary<ConnectionLifecycleState, HashSet<ConnectionLifecycleState>>
            AllowedTransitions = new Dictionary<ConnectionLifecycleState, HashSet<ConnectionLifecycleState>>
            {
                [ConnectionLifecycleState.Idle] = new HashSet<ConnectionLifecycleState>
                {
                    ConnectionLifecycleState.Preparing,
                },
                [ConnectionLifecycleState.Preparing] = new HashSet<ConnectionLifecycleState>
                {
                    ConnectionLifecycleState.Signaling,
                    ConnectionLifecycleState.Failed,
                    ConnectionLifecycleState.Closed,
                },
                [ConnectionLifecycleState.Signaling] = new HashSet<ConnectionLifecycleState>
                {
                    ConnectionLifecycleState.Connecting,
                    ConnectionLifecycleState.Failed,
                    ConnectionLifecycleState.Closed,
                },
                [ConnectionLifecycleState.Connecting] = new HashSet<ConnectionLifecycleState>
                {
                    ConnectionLifecycleState.Connected,
                    ConnectionLifecycleState.Recovering,
                    ConnectionLifecycleState.Failed,
                    ConnectionLifecycleState.Closed,
                },
                [ConnectionLifecycleState.Connected] = new HashSet<ConnectionLifecycleState>
                {
                    ConnectionLifecycleState.Recovering,
                    ConnectionLifecycleState.Failed,
                    ConnectionLifecycleState.Closed,
                },
                [ConnectionLifecycleState.Recovering] = new HashSet<ConnectionLifecycleState>
                {
                    ConnectionLifecycleState.Connected,
                    ConnectionLifecycleState.Failed,
                    ConnectionLifecycleState.Closed,
                },
                // Terminal states — no outgoing transitions.
                [ConnectionLifecycleState.Failed] = new HashSet<ConnectionLifecycleState>(),
                [ConnectionLifecycleState.Closed] = new HashSet<ConnectionLifecycleState>(),
            };

        public ConnectionStateMachine(ConnectionDiagnostics diagnostics)
        {
            _diagnostics = diagnostics;
        }

        public void TransitionTo(ConnectionLifecycleState next, string reason = null)
        {
            if (_state == next) return;

            if (!AllowedTransitions.TryGetValue(_state, out var allowed) || !allowed.Contains(next))
            {
                _diagnostics.LogWarning("FSM",
                    $"Invalid transition {_state} -> {next} (reason={reason}) — ignored");
                return;
            }

            _diagnostics.LogTransition(_state.ToString(), next.ToString(), reason);
            _state = next;
            OnStateChanged?.Invoke(_state);
        }

        public bool IsTerminal =>
            _state == ConnectionLifecycleState.Failed ||
            _state == ConnectionLifecycleState.Closed;

        public bool IsConnected =>
            _state == ConnectionLifecycleState.Connected;

        /// <summary>
        /// Silently resets the FSM to Idle without firing OnStateChanged.
        /// Must be called before starting a new session when the previous one terminated.
        /// </summary>
        public void Reset() => _state = ConnectionLifecycleState.Idle;
    }
}
