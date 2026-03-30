using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;

namespace WebRtcV2.Application.Connection
{
    /// <summary>
    /// Public API of the connection subsystem.
    /// UIMapper calls commands; the presentation layer observes events and state snapshots.
    /// </summary>
    public interface IConnectionFlow
    {
        // ── Primary snapshot API ──────────────────────────────────────────

        /// <summary>Current immutable snapshot of the session runtime state.</summary>
        ConnectionSnapshot CurrentSnapshot { get; }

        /// <summary>
        /// Fired on every lifecycle transition, mode change, or DataChannel state change.
        /// Preferred API for all new UI and Bootstrap code.
        /// </summary>
        event Action<ConnectionSnapshot> OnSnapshotChanged;

        // ── Compatibility layer (kept until all consumers migrate) ─────────

        /// <summary>Derived from the current snapshot via CompatState mapping.</summary>
        ConnectionState CurrentState { get; }

        /// <summary>
        /// Maps snapshot → old ConnectionState. Will be removed once all consumers
        /// use <see cref="OnSnapshotChanged"/>.
        /// </summary>
        event Action<ConnectionState> OnStateChanged;

        // ── Track / chat events ───────────────────────────────────────────

        /// <summary>Raised when a remote audio track is ready to be attached to an AudioSource.</summary>
        event Action<AudioStreamTrack> OnRemoteAudioTrackAvailable;

        /// <summary>Raised when a remote video frame source becomes available for preview.</summary>
        event Action<Texture> OnRemoteVideoTextureAvailable;

        /// <summary>Raised on incoming DataChannel chat message: (senderId, text).</summary>
        event Action<string, string> OnChatMessageReceived;

        /// <summary>Raised when the remote peer changes speaking state over the DataChannel.</summary>
        event Action<bool> OnRemoteSpeakingChanged;

        /// <summary>Start signaling as the room creator (caller/offerer).</summary>
        UniTask ConnectAsCallerAsync(string sessionId, CancellationToken ct = default);

        /// <summary>Start signaling as the room joiner (callee/answerer).</summary>
        UniTask ConnectAsCalleeAsync(string sessionId, string callerPeerId, CancellationToken ct = default);

        UniTask HangupAsync(CancellationToken ct = default);

        /// <returns>True if the message was actually sent; false if DataChannel is not open.</returns>
        bool SendChatMessage(string text);

        /// <summary>Best-effort UX signaling for remote speaking indicator.</summary>
        void SendSpeakingState(bool speaking);

        void SetMicMuted(bool muted);

        void SetVideoEnabled(bool enabled);
    }
}
