using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.WebRTC;
using WebRtcV2.Shared;

namespace WebRtcV2.Transport
{
    /// <summary>
    /// Thin adapter over <see cref="RTCPeerConnection"/>.
    /// Converts callback-based WebRTC API into awaitable UniTask calls.
    ///
    /// Non-trickle ICE contract:
    ///   1. Call CreateOfferAsync / CreateAnswerAsync  -> sets local description, starts ICE gathering.
    ///   2. Call WaitForIceGatheringAsync              -> waits for all candidates to be gathered.
    ///   3. Read LocalSdp                              -> this is the complete SDP with candidates.
    ///   Never send LocalSdp before WaitForIceGatheringAsync returns.
    /// </summary>
    public class WebRtcPeerAdapter : IDisposable
    {
        private RTCPeerConnection _pc;
        private readonly ConnectionDiagnostics _diagnostics;
        private bool _hasAnyCandidate;
        private bool _hasHostCandidate;
        private bool _hasSrflxCandidate;
        private bool _hasRelayCandidate;
        private bool _disposed;

        public event Action<RTCTrackEvent> OnTrack;
        public event Action<RTCIceConnectionState> OnIceConnectionChange;
        public event Action<RTCDataChannel> OnDataChannel;

        /// <summary>
        /// The complete local SDP. Valid only AFTER <see cref="WaitForIceGatheringAsync"/> returns.
        /// </summary>
        public string LocalSdp => _pc?.LocalDescription.sdp;

        public RTCIceConnectionState IceConnectionState =>
            _pc?.IceConnectionState ?? RTCIceConnectionState.Closed;

        public WebRtcPeerAdapter(ConnectionDiagnostics diagnostics)
        {
            _diagnostics = diagnostics;
        }

        public void Initialize(RTCConfiguration config)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WebRtcPeerAdapter));

            if (_pc != null)
            {
                ClearPcCallbacks();
                _pc.Dispose();
            }

            _hasAnyCandidate = false;
            _hasHostCandidate = false;
            _hasSrflxCandidate = false;
            _hasRelayCandidate = false;
            _pc = new RTCPeerConnection(ref config);
            _pc.OnTrack = e => OnTrack?.Invoke(e);
            _pc.OnIceConnectionChange = state =>
            {
                _diagnostics.LogIce("StateChange", state.ToString());
                OnIceConnectionChange?.Invoke(state);
            };
            _pc.OnDataChannel = ch => OnDataChannel?.Invoke(ch);
            _pc.OnIceCandidate = candidate =>
            {
                if (candidate == null) return;

                string line = candidate.Candidate;
                _diagnostics.LogIce("Candidate", line);
                _hasAnyCandidate = true;
                if (!_hasHostCandidate && IsHostCandidate(line))
                    _hasHostCandidate = true;
                if (!_hasSrflxCandidate && IsSrflxCandidate(line))
                    _hasSrflxCandidate = true;
                if (!_hasRelayCandidate && IsRelayCandidate(line))
                    _hasRelayCandidate = true;
            };
        }

        public void AddTrack(MediaStreamTrack track)
        {
            if (track != null) _pc.AddTrack(track);
        }

        public RTCDataChannel CreateDataChannel(string label)
        {
            var init = new RTCDataChannelInit();
            return _pc.CreateDataChannel(label, init);
        }

        /// <summary>
        /// Creates offer and sets local description, which triggers ICE gathering.
        /// After this call you MUST await <see cref="WaitForIceGatheringAsync"/> before reading
        /// <see cref="LocalSdp"/> and sending the SDP to the remote peer.
        /// </summary>
        public UniTask CreateOfferAsync(CancellationToken ct) => CreateOfferAsync(false, ct);

        public async UniTask CreateOfferAsync(bool iceRestart, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var options = RTCOfferAnswerOptions.Default;
            options.iceRestart = iceRestart;

            var op = _pc.CreateOffer(ref options);
            await op;
            if (op.IsError)
                throw new InvalidOperationException($"CreateOffer: {op.Error.message}");

            var desc = op.Desc;
            var setOp = _pc.SetLocalDescription(ref desc);
            await setOp;
            if (setOp.IsError)
                throw new InvalidOperationException($"SetLocalDescription(offer): {setOp.Error.message}");
        }

        /// <summary>
        /// Creates answer and sets local description, which triggers ICE gathering.
        /// After this call you MUST await <see cref="WaitForIceGatheringAsync"/> before reading
        /// <see cref="LocalSdp"/> and sending the SDP to the remote peer.
        /// </summary>
        public async UniTask CreateAnswerAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var op = _pc.CreateAnswer();
            await op;
            if (op.IsError)
                throw new InvalidOperationException($"CreateAnswer: {op.Error.message}");

            var desc = op.Desc;
            var setOp = _pc.SetLocalDescription(ref desc);
            await setOp;
            if (setOp.IsError)
                throw new InvalidOperationException($"SetLocalDescription(answer): {setOp.Error.message}");
        }

        public async UniTask SetRemoteDescriptionAsync(string sdp, RTCSdpType sdpType, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var desc = new RTCSessionDescription { sdp = sdp, type = sdpType };
            var op = _pc.SetRemoteDescription(ref desc);
            await op;
            if (op.IsError)
                throw new InvalidOperationException($"SetRemoteDescription: {op.Error.message}");
        }

        /// <summary>
        /// Waits until ICE gathering completes or <paramref name="timeoutMs"/> elapses.
        /// On timeout, proceeds with whatever candidates were gathered so far.
        /// Uses pragmatic early-exit heuristics for non-trickle ICE:
        /// - in relayOnly mode, returns as soon as a relay candidate exists
        /// - otherwise, returns early when srflx/relay candidates exist
        /// - for host-only cases, waits a short settle window before proceeding
        /// </summary>
        public async UniTask WaitForIceGatheringAsync(int timeoutMs, CancellationToken ct, bool allowEarlyRelayExit = false)
        {
            if (_pc == null) return;
            if (_pc.GatheringState == RTCIceGatheringState.Complete) return;
            string immediateReason = TryGetEarlyExitReason(allowEarlyRelayExit, 0);
            if (immediateReason != null)
            {
                _diagnostics.LogIce("Gathering", immediateReason);
                return;
            }

            bool completed = false;
            var previousHandler = _pc.OnIceGatheringStateChange;
            _pc.OnIceGatheringStateChange = state =>
            {
                previousHandler?.Invoke(state);
                if (state == RTCIceGatheringState.Complete)
                    completed = true;
            };

            const int stepMs = 100;
            int elapsedMs = 0;

            try
            {
                while (elapsedMs < timeoutMs)
                {
                    ct.ThrowIfCancellationRequested();

                    if (completed || _pc.GatheringState == RTCIceGatheringState.Complete)
                        return;

                    string earlyExitReason = TryGetEarlyExitReason(allowEarlyRelayExit, elapsedMs);
                    if (earlyExitReason != null)
                    {
                        _diagnostics.LogIce("Gathering", earlyExitReason);
                        return;
                    }

                    await UniTask.Delay(stepMs, cancellationToken: ct, cancelImmediately: true);
                    elapsedMs += stepMs;
                }

                _diagnostics.LogWarning("WebRTC",
                    "ICE gathering timeout - proceeding with available candidates");
            }
            finally
            {
                _pc.OnIceGatheringStateChange = previousHandler;
            }
        }

        /// <summary>
        /// Waits until ICE reaches Connected/Completed, fails, or <paramref name="timeoutMs"/> elapses.
        /// Returns false on timeout or failure.
        /// </summary>
        public async UniTask<bool> WaitForIceConnectedAsync(int timeoutMs, CancellationToken ct)
        {
            if (_pc == null) return false;

            var current = _pc.IceConnectionState;
            if (current == RTCIceConnectionState.Connected || current == RTCIceConnectionState.Completed)
                return true;
            if (current == RTCIceConnectionState.Failed || current == RTCIceConnectionState.Closed)
                return false;

            var tcs = new UniTaskCompletionSource<bool>();

            void Handler(RTCIceConnectionState state)
            {
                if (state == RTCIceConnectionState.Connected || state == RTCIceConnectionState.Completed)
                    tcs.TrySetResult(true);
                else if (state == RTCIceConnectionState.Failed || state == RTCIceConnectionState.Closed)
                    tcs.TrySetResult(false);
            }

            OnIceConnectionChange += Handler;
            try
            {
                var (hasResult, result) = await UniTask.WhenAny(
                    tcs.Task,
                    UniTask.Delay(timeoutMs, cancellationToken: ct, cancelImmediately: true));
                return hasResult && result;
            }
            finally
            {
                OnIceConnectionChange -= Handler;
            }
        }

        /// <summary>
        /// Collects a stats report from the peer connection.
        /// Returns null if the peer is not initialized, disposed, or if GetStats() fails.
        /// </summary>
        public async UniTask<RTCStatsReport> GetStatsAsync(CancellationToken ct)
        {
            if (_pc == null || _disposed) return null;
            ct.ThrowIfCancellationRequested();

            var op = _pc.GetStats();
            await op;

            return op.IsError ? null : op.Value;
        }

        private static bool IsRelayCandidate(string line) =>
            !string.IsNullOrEmpty(line) &&
            line.IndexOf(" typ relay", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsSrflxCandidate(string line) =>
            !string.IsNullOrEmpty(line) &&
            line.IndexOf(" typ srflx", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsHostCandidate(string line) =>
            !string.IsNullOrEmpty(line) &&
            line.IndexOf(" typ host", StringComparison.OrdinalIgnoreCase) >= 0;

        private string TryGetEarlyExitReason(bool allowEarlyRelayExit, int elapsedMs)
        {
            const int hostOnlySettleMs = 1200;

            if (allowEarlyRelayExit && _hasRelayCandidate)
                return "Relay candidate gathered - proceeding early";

            if (_hasSrflxCandidate)
                return "Server-reflexive candidate gathered - proceeding early";

            if (_hasRelayCandidate)
                return "Relay candidate gathered - proceeding early";

            if (_hasHostCandidate && elapsedMs >= hostOnlySettleMs)
                return $"Host candidates only after {hostOnlySettleMs}ms - proceeding early";

            if (_hasAnyCandidate && elapsedMs >= hostOnlySettleMs)
                return $"Candidate(s) available after {hostOnlySettleMs}ms - proceeding early";

            return null;
        }

        private void ClearPcCallbacks()
        {
            if (_pc == null) return;
            _pc.OnTrack = null;
            _pc.OnIceConnectionChange = null;
            _pc.OnDataChannel = null;
            _pc.OnIceCandidate = null;
            _pc.OnIceGatheringStateChange = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClearPcCallbacks();
            _pc?.Dispose();
            _pc = null;
        }
    }
}
