using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using Unity.WebRTC;
using UnityEngine;
using WebRtcV2.Config;
using WebRtcV2.Shared;
using WebRtcV2.Transport;

namespace WebRtcV2.Application.Connection {
    /// <summary>
    /// Main use-case orchestrator for the WebRTC session lifecycle.
    ///
    /// Architecture note (post-migration):
    ///   - <see cref="ConnectionStateMachine"/> tracks lifecycle only (IdleРІвЂ вЂ™PreparingРІвЂ вЂ™РІР‚В¦РІвЂ вЂ™Closed).
    ///   - <see cref="ConnectionSession"/> holds the full runtime model: lifecycle, mode, route,
    ///     signaling strategy, reconnect counter, last error, and DataChannel state.
    ///   - <see cref="ConnectionSnapshot"/> is the immutable read model published to UI/Bootstrap.
    ///   - <see cref="OnSnapshotChanged"/> is the primary event. <see cref="OnStateChanged"/>
    ///     is a compatibility shim that maps snapshot РІвЂ вЂ™ old <see cref="ConnectionState"/> enum.
    ///
    /// Invariant (event ordering):
    ///   SetLifecycle() fires OnSnapshotChanged BEFORE CleanupSession() clears SessionId.
    ///   This ensures that when AppBootstrap receives Closed/Failed, snapshot.SessionId and
    ///   snapshot.IsCreator are still populated and can be used directly.
    ///
    /// Non-trickle ICE (MVP):
    ///   All ICE candidates are gathered before SDP is posted to the Worker.
    ///   LocalSdp is only read after WaitForIceGatheringAsync returns.
    ///
    /// Recovery (MVP):
    ///   On ICE failure the coordinator transitions to Recovering for a brief delay,
    ///   then falls back to Failed. ICE restart is Stage 5.
    /// </summary>
    public class ConnectionFlowCoordinator : IConnectionFlow, IDisposable {
        private readonly WorkerClient _worker;
        private readonly AppConfig _config;
        private readonly ISecretsProvider _secrets;
        private readonly ConnectionDiagnostics _diagnostics;
        private readonly MediaCaptureService _mediaCapture;
        private readonly ConnectionStateMachine _fsm;
        private readonly QualityMonitor _qualityMonitor;
        private readonly string _localPeerId;

        // РІвЂќР‚РІвЂќР‚ Session runtime state РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        private readonly ConnectionSession _session = new ConnectionSession();

        private WebRtcPeerAdapter _peer;
        private RTCDataChannel _dataChannel;
        private string _activeSessionId;
        private bool _hangupPollingStarted;

        private CancellationTokenSource _sessionCts;

        // РІвЂќР‚РІвЂќР‚ Quality sampling & policy РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        private WebRtcStatsSampler _statsSampler;
        private readonly ConnectionPolicy _policy;
        private readonly RecoveryCoordinator _recovery;
        private bool _iceRestartInProgress;

        // РІвЂќР‚РІвЂќР‚ IConnectionFlow РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        public ConnectionSnapshot CurrentSnapshot => _session.ToSnapshot();

        /// <summary>Primary event РІР‚вЂќ fired on every state or mode change.</summary>
        public event Action<ConnectionSnapshot> OnSnapshotChanged;

        /// <summary>
        /// Compatibility shim. Maps snapshot РІвЂ вЂ™ old ConnectionState for code that has
        /// not yet migrated to <see cref="OnSnapshotChanged"/>.
        /// Will be removed once AppBootstrap and all consumers use snapshot API.
        /// </summary>
        public event Action<ConnectionState> OnStateChanged;

        public event Action<AudioStreamTrack> OnRemoteAudioTrackAvailable;
        public event Action<Texture> OnRemoteVideoTextureAvailable;
        public event Action<string, string> OnChatMessageReceived;
        public event Action<bool> OnRemoteSpeakingChanged;

        // Kept for IConnectionFlow.CurrentState compatibility; derived from snapshot.
        public ConnectionState CurrentState => CompatState(_session.ToSnapshot());

        // РІвЂќР‚РІвЂќР‚ Constructor РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        public ConnectionFlowCoordinator(
            WorkerClient worker,
            AppConfig config,
            ISecretsProvider secrets,
            ConnectionDiagnostics diagnostics,
            MediaCaptureService mediaCapture,
            string localPeerId) {
            _worker = worker;
            _config = config;
            _secrets = secrets;
            _diagnostics = diagnostics;
            _mediaCapture = mediaCapture;
            _localPeerId = localPeerId;

            _fsm = new ConnectionStateMachine(diagnostics);
            _fsm.OnStateChanged += HandleFsmStateChanged;

            _qualityMonitor = new QualityMonitor();
            _qualityMonitor.OnSignalChanged += HandleQualitySignal;

            _policy = new ConnectionPolicy(config, diagnostics);
            _recovery = new RecoveryCoordinator(config, diagnostics);
        }

        // РІвЂќР‚РІвЂќР‚ Connect flows РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        public async UniTask ConnectAsCallerAsync(string sessionId, CancellationToken ct = default) {
            _fsm.Reset();
            _session.Initialize(sessionId, isCreator: true,
                mediaMode: MediaMode.AudioOnly,
                routeMode: RouteMode.Direct,
                signalingMode: SignalingMode.WorkerPolling);
            _activeSessionId = sessionId;
            _hangupPollingStarted = false;
            StartSessionCts(ct);

            var flowTimer = Stopwatch.StartNew();

            try {
                SetLifecycle(ConnectionLifecycleState.Preparing, "connect-as-caller");
                await SetupPeerAsync(_sessionCts.Token);

                _dataChannel = _peer.CreateDataChannel("data");
                SetupDataChannel(_dataChannel);

                // Step 1: create offer + set local description (starts ICE gathering).
                await _peer.CreateOfferAsync(_sessionCts.Token);

                // Step 2: wait for all ICE candidates РІР‚вЂќ non-trickle ICE (MVP).
                await _peer.WaitForIceGatheringAsync(
                    _config.connection.iceGatheringTimeoutMs, _sessionCts.Token, _config.ice.relayOnly);

                // Step 3: LocalSdp is valid only after gathering completes.
                string localSdp = _peer.LocalSdp;
                if (string.IsNullOrEmpty(localSdp))
                    throw new InvalidOperationException("empty-local-sdp");

                var envelope = BuildSdpEnvelope(sessionId, SignalingEnvelope.Types.Offer,
                    localSdp, "offer");

                SetLifecycle(ConnectionLifecycleState.Signaling, "offer-ready");
                _diagnostics.LogSignaling("OUT", SignalingEnvelope.Types.Offer, $"t={flowTimer.ElapsedMilliseconds}ms");

                if (!await _worker.PostSignalAsync(envelope, _sessionCts.Token))
                    throw new InvalidOperationException("offer-post-failed");

                _diagnostics.LogSignaling("OUT", SignalingEnvelope.Types.Offer, $"posted t={flowTimer.ElapsedMilliseconds}ms");

                var answerEnvelope = await PollForSignalAsync(
                    sessionId, SignalingEnvelope.Types.Answer, _sessionCts.Token);
                if (answerEnvelope == null)
                    throw new InvalidOperationException("answer-timeout");

                _diagnostics.LogSignaling("IN", SignalingEnvelope.Types.Answer, $"received t={flowTimer.ElapsedMilliseconds}ms");

                var sdp = JsonUtility.FromJson<SdpPayload>(answerEnvelope.payloadJson);
                await _peer.SetRemoteDescriptionAsync(sdp.sdp, RTCSdpType.Answer, _sessionCts.Token);

                // Clean up offer/answer slots now that both sides have exchanged SDPs.
                CleanupSignalingSlotsBestEffort(_activeSessionId,
                    SignalingEnvelope.Types.Offer, SignalingEnvelope.Types.Answer);

                SetLifecycle(ConnectionLifecycleState.Connecting, "remote-desc-set");
                await WaitForConnectionAsync(_sessionCts.Token);
            }
            catch (OperationCanceledException) {
                // Fire event with SessionId/IsCreator still set, then clean up.
                SetLifecycle(ConnectionLifecycleState.Closed, "cancelled");
                CleanupSession();
            }
            catch (Exception e) {
                _session.SetLastError(e.Message);
                _diagnostics.LogError("ConnectionFlow", e.Message);
                SetLifecycle(ConnectionLifecycleState.Failed, e.Message);
                CleanupSession();
            }
        }

        /// <param name="callerPeerId">
        /// PeerId of the room creator. Used to validate the received offer actually
        /// comes from the expected peer and not a stale or misrouted message.
        /// </param>
        public async UniTask ConnectAsCalleeAsync(
            string sessionId, string callerPeerId, CancellationToken ct = default) {
            _fsm.Reset();
            _session.Initialize(sessionId, isCreator: false,
                mediaMode: MediaMode.AudioOnly,
                routeMode: RouteMode.Direct,
                signalingMode: SignalingMode.WorkerPolling);
            _activeSessionId = sessionId;
            _hangupPollingStarted = false;
            StartSessionCts(ct);

            var flowTimer = Stopwatch.StartNew();

            try {
                SetLifecycle(ConnectionLifecycleState.Preparing, "connect-as-callee");
                await SetupPeerAsync(_sessionCts.Token);
                _peer.OnDataChannel += ch => {
                    _dataChannel = ch;
                    SetupDataChannel(ch);
                };

                _diagnostics.LogSignaling("IN", SignalingEnvelope.Types.Offer, "fetching");

                var offerEnvelope = await PollForSignalAsync(
                    sessionId, SignalingEnvelope.Types.Offer, _sessionCts.Token);
                if (offerEnvelope == null)
                    throw new InvalidOperationException("offer-not-found");

                // Validate that the offer came from the expected caller.
                if (!string.IsNullOrEmpty(callerPeerId) &&
                    !string.IsNullOrEmpty(offerEnvelope.fromPeerId) &&
                    offerEnvelope.fromPeerId != callerPeerId) {
                    _diagnostics.LogError("ConnectionFlow",
                        $"Offer peer mismatch: expected={callerPeerId} got={offerEnvelope.fromPeerId}");
                    throw new InvalidOperationException("offer-peer-mismatch");
                }

                _diagnostics.LogSignaling("IN", SignalingEnvelope.Types.Offer, $"received t={flowTimer.ElapsedMilliseconds}ms");

                var offerSdp = JsonUtility.FromJson<SdpPayload>(offerEnvelope.payloadJson);
                await _peer.SetRemoteDescriptionAsync(
                    offerSdp.sdp, RTCSdpType.Offer, _sessionCts.Token);

                // Step 1: create answer + set local description (starts ICE gathering).
                await _peer.CreateAnswerAsync(_sessionCts.Token);

                // Step 2: wait for all ICE candidates РІР‚вЂќ non-trickle ICE (MVP).
                await _peer.WaitForIceGatheringAsync(
                    _config.connection.iceGatheringTimeoutMs, _sessionCts.Token, _config.ice.relayOnly);

                // Step 3: LocalSdp is valid only after gathering completes.
                string localSdp = _peer.LocalSdp;
                if (string.IsNullOrEmpty(localSdp))
                    throw new InvalidOperationException("empty-local-sdp");

                var answerEnvelope = BuildSdpEnvelope(sessionId, SignalingEnvelope.Types.Answer,
                    localSdp, "answer");

                SetLifecycle(ConnectionLifecycleState.Signaling, "answer-ready");
                _diagnostics.LogSignaling("OUT", SignalingEnvelope.Types.Answer, $"t={flowTimer.ElapsedMilliseconds}ms");

                if (!await _worker.PostSignalAsync(answerEnvelope, _sessionCts.Token))
                    throw new InvalidOperationException("answer-post-failed");

                _diagnostics.LogSignaling("OUT", SignalingEnvelope.Types.Answer, $"posted t={flowTimer.ElapsedMilliseconds}ms");

                SetLifecycle(ConnectionLifecycleState.Connecting, "answer-sent");
                await WaitForConnectionAsync(_sessionCts.Token);
            }
            catch (OperationCanceledException) {
                SetLifecycle(ConnectionLifecycleState.Closed, "cancelled");
                CleanupSession();
            }
            catch (Exception e) {
                _session.SetLastError(e.Message);
                _diagnostics.LogError("ConnectionFlow", e.Message);
                SetLifecycle(ConnectionLifecycleState.Failed, e.Message);
                CleanupSession();
            }
        }

        public async UniTask HangupAsync(CancellationToken ct = default) {
            if (_fsm.IsTerminal) return;

            _diagnostics.LogSignaling("OUT", SignalingEnvelope.Types.Hangup);

            if (!string.IsNullOrEmpty(_activeSessionId)) {
                var envelope = new SignalingEnvelope {
                    sessionId = _activeSessionId,
                    fromPeerId = _localPeerId,
                    messageId = Guid.NewGuid().ToString("N"),
                    type = SignalingEnvelope.Types.Hangup,
                    ttlMs = _config.workerEndpoint.signalingMessageTtlSec * 1000,
                    sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    payloadJson = "{}"
                };
                await _worker.PostSignalAsync(envelope, ct).SuppressCancellationThrow();
            }

            // Fire event BEFORE CleanupSession so the snapshot carries
            // SessionId and IsCreator РІР‚вЂќ Bootstrap uses them for room deletion.
            SetLifecycle(ConnectionLifecycleState.Closed, "local-hangup");
            CleanupSession();
        }

        public bool SendChatMessage(string text) {
            if (_dataChannel == null || _dataChannel.ReadyState != RTCDataChannelState.Open) return false;
            if (string.IsNullOrEmpty(text)) return false;

            var env = DataChannelEnvelope.Chat(text);
            _dataChannel.Send(Encoding.UTF8.GetBytes(env.Serialize()));
            return true;
        }

        public void SendSpeakingState(bool speaking) {
            if (_dataChannel == null || _dataChannel.ReadyState != RTCDataChannelState.Open) return;

            var env = DataChannelEnvelope.VoiceState(speaking);
            _dataChannel.Send(Encoding.UTF8.GetBytes(env.Serialize()));
        }

        public void SetMicMuted(bool muted) => _mediaCapture.SetMicMuted(muted);

        public void SetVideoEnabled(bool enabled) => _mediaCapture.SetVideoEnabled(enabled);

        // РІвЂќР‚РІвЂќР‚ Private: FSM / session sync РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        /// <summary>
        /// Transitions the FSM and synchronises session.LifecycleState.
        /// Records error in session when transitioning to Failed.
        /// </summary>
        private void SetLifecycle(ConnectionLifecycleState state, string reason = null) {
            if (state == ConnectionLifecycleState.Failed && !string.IsNullOrEmpty(reason))
                _session.SetLastError(reason);

            _fsm.TransitionTo(state, reason);
            // HandleFsmStateChanged is invoked synchronously by the FSM's OnStateChanged.
        }

        /// <summary>
        /// Transitions to Failed (firing snapshot with SessionId/IsCreator still set)
        /// and immediately performs full session cleanup.
        ///
        /// Invariant preserved: SetLifecycle publishes OnSnapshotChanged synchronously
        /// BEFORE CleanupSession resets the session, so AppBootstrap can use
        /// snapshot.SessionId and snapshot.IsCreator for room deletion.
        ///
        /// Safe if the session is already terminal РІР‚вЂќ SetLifecycle is a no-op for
        /// invalid FSM transitions and CleanupSession is idempotent (null-checks throughout).
        ///
        /// Use this for any Failed path that is NOT already inside a catch block
        /// that calls CleanupSession (i.e. outside ConnectAsCallerAsync /
        /// ConnectAsCalleeAsync try/catch).
        /// </summary>
        private void FailSession(string reason) {
            SetLifecycle(ConnectionLifecycleState.Failed, reason);
            CleanupSession();
        }

        private void HandleFsmStateChanged(ConnectionLifecycleState state) {
            _session.SetLifecycleState(state);
            RaiseSnapshotChanged();
        }

        private void RaiseSnapshotChanged() {
            var snapshot = _session.ToSnapshot();
            OnSnapshotChanged?.Invoke(snapshot);
            OnStateChanged?.Invoke(CompatState(snapshot)); // compatibility shim
        }

        /// <summary>Maps the new snapshot model to the old ConnectionState enum.</summary>
        private static ConnectionState CompatState(ConnectionSnapshot s) => s.LifecycleState switch {
            ConnectionLifecycleState.Idle => ConnectionState.Idle,
            ConnectionLifecycleState.Preparing => ConnectionState.Preparing,
            ConnectionLifecycleState.Signaling => ConnectionState.Signaling,
            ConnectionLifecycleState.Connecting => ConnectionState.Connecting,
            ConnectionLifecycleState.Connected => s.MediaMode == MediaMode.DataOnly
                                                    ? ConnectionState.ConnectedDataOnly
                                                    : ConnectionState.ConnectedAudio,
            ConnectionLifecycleState.Recovering => ConnectionState.Reconnecting,
            ConnectionLifecycleState.Failed => ConnectionState.Failed,
            ConnectionLifecycleState.Closed => ConnectionState.Closed,
            _ => ConnectionState.Idle,
        };

        // РІвЂќР‚РІвЂќР‚ Private: setup РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        private async UniTask SetupPeerAsync(CancellationToken ct) {
            var turnCredentials = await _secrets.GetTurnCredentialsAsync(ct);
            var rtcConfig = BuildRtcConfiguration(turnCredentials);

            _peer?.Dispose();
            _peer = new WebRtcPeerAdapter(_diagnostics);
            _peer.Initialize(rtcConfig);
            _peer.OnTrack += HandleRemoteTrack;
            _peer.OnIceConnectionChange += state => {
                _qualityMonitor.OnIceStateChanged(state);
                HandleIceStateChange(state);
            };

            var audioTrack = await _mediaCapture.GetAudioTrackAsync(ct);
            _peer.AddTrack(audioTrack);

            var videoTrack = await _mediaCapture.GetVideoTrackAsync(ct);
            _peer.AddTrack(videoTrack);
        }

        private RTCConfiguration BuildRtcConfiguration(TurnCredentials turn) {
            var servers = new System.Collections.Generic.List<RTCIceServer>();

            foreach (var url in _config.ice.stunUrls)
                servers.Add(new RTCIceServer { urls = new[] { url } });

            if (!turn.IsEmpty && turn.TurnUrls != null && turn.TurnUrls.Length > 0) {
                servers.Add(new RTCIceServer {
                    urls = turn.TurnUrls,
                    username = turn.Username,
                    credential = turn.Credential
                });
            }

            return new RTCConfiguration
            {
                iceServers = servers.ToArray(),
                iceTransportPolicy = _config.ice.relayOnly
                    ? RTCIceTransportPolicy.Relay
                    : RTCIceTransportPolicy.All
            };
        }

        // РІвЂќР‚РІвЂќР‚ Private: ICE / track events РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        private void HandleRemoteTrack(RTCTrackEvent e) {
            if (e.Track is AudioStreamTrack audioTrack) {
                _diagnostics.LogIce("RemoteTrack", "audio");
                SetRemoteAudioOnMainThread(audioTrack).Forget();
            }
            else if (e.Track is VideoStreamTrack videoTrack) {
                _diagnostics.LogIce("RemoteTrack", "video");
                videoTrack.OnVideoReceived += texture => SetRemoteVideoOnMainThread(texture).Forget();
            }
        }

        private async UniTaskVoid SetRemoteAudioOnMainThread(AudioStreamTrack track) {
            await UniTask.SwitchToMainThread();
            OnRemoteAudioTrackAvailable?.Invoke(track);
        }

        private async UniTaskVoid SetRemoteVideoOnMainThread(Texture texture) {
            await UniTask.SwitchToMainThread();
            if (texture != null)
                OnRemoteVideoTextureAvailable?.Invoke(texture);
        }

        private void HandleIceStateChange(RTCIceConnectionState state) {
            switch (state) {
                case RTCIceConnectionState.Connected:
                case RTCIceConnectionState.Completed:
                    _recovery.CancelPending();
                    _iceRestartInProgress = false;
                    if (_fsm.Current == ConnectionLifecycleState.Recovering || !_fsm.IsConnected) {
                        string reason = _fsm.Current == ConnectionLifecycleState.Recovering
                            ? "ice-recovered"
                            : "ice-connected";

                        SetLifecycle(ConnectionLifecycleState.Connected, reason);
                        if (!_hangupPollingStarted && _sessionCts != null) {
                            _hangupPollingStarted = true;
                            StartQualitySampling(_sessionCts.Token);
                            PollForRemoteHangupAsync(_sessionCts.Token).Forget();
                        }
                    }
                    break;

                case RTCIceConnectionState.Disconnected:
                    HandleIceDisconnectedAsync().Forget();
                    break;

                case RTCIceConnectionState.Failed:
                    HandleIceFailedAsync().Forget();
                    break;
            }
        }

        private async UniTaskVoid HandleIceDisconnectedAsync() {
            if (_fsm.IsTerminal || !_fsm.IsConnected || _iceRestartInProgress) return;

            var sessionToken = _sessionCts?.Token ?? CancellationToken.None;
            var outcome = await _recovery.BeginDisconnectedGracePeriodAsync(
                () => _peer?.IceConnectionState ?? RTCIceConnectionState.Closed,
                sessionToken);

            if (_fsm.IsTerminal || outcome == RecoveryOutcome.Cancelled || outcome == RecoveryOutcome.Recovered)
                return;

            if (outcome == RecoveryOutcome.DisconnectedTimeout) {
                SetLifecycle(ConnectionLifecycleState.Recovering, "ice-disconnected-timeout");
                HandleIceFailedAsync().Forget();
            }
        }

        private async UniTaskVoid HandleIceFailedAsync() {
            if (_fsm.IsTerminal || _iceRestartInProgress) return;

            if (_fsm.Current != ConnectionLifecycleState.Recovering)
                SetLifecycle(ConnectionLifecycleState.Recovering, "ice-failed");

            var sessionToken = _sessionCts?.Token ?? CancellationToken.None;
            var outcome = await _recovery.BeginFailedRecoveryAsync(_session, sessionToken);
            if (_fsm.IsTerminal || outcome == RecoveryOutcome.Cancelled || outcome == RecoveryOutcome.Recovered)
                return;

            switch (outcome) {
                case RecoveryOutcome.RetryBudgetExceeded:
                    FailSession("max-reconnect-reached");
                    break;
                case RecoveryOutcome.StartIceRestart:
                    if (_session.IsCreator)
                        TryIceRestartAsCallerAsync().Forget();
                    else
                        WaitForRemoteIceRestartOfferAsync().Forget();
                    break;
            }
        }

        private void HandleQualitySignal(QualityMonitor.Signal signal) {
            _diagnostics.LogInfo("Quality", signal.ToString());
            // ICE-state-based signals are logged only; real quality decisions
            // are driven by WebRtcStatsSampler РІвЂ вЂ™ ConnectionPolicy (see below).
        }

        private void StartQualitySampling(CancellationToken ct) {
            _statsSampler?.Dispose();
            _statsSampler = new WebRtcStatsSampler(_peer, _config, _diagnostics);
            _statsSampler.OnSnapshot += HandleQualitySnapshot;
            _statsSampler.Start(ct);
        }

        private void HandleQualitySnapshot(QualitySnapshot quality) {
            var decision = _policy.Evaluate(_session.ToSnapshot(), quality);

            if (decision == ConnectionPolicyDecision.DowngradeToDataOnly) {
                _diagnostics.LogWarning("Policy", $"Decision=DowngradeToDataOnly РІР‚вЂќ switching to logical DataOnly mode");
                ApplyDataOnlyMode();
            }
        }

        /// <summary>
        /// Pragmatic MVP downgrade: AudioOnly РІвЂ вЂ™ DataOnly.
        ///
        /// IMPLEMENTATION NOTE РІР‚вЂќ this is NOT a true data-only transport:
        ///   True data-only would require: _peer.RemoveTrack(sender) + renegotiation (offer/answer).
        ///   That is complex in Unity WebRTC and risks breaking the session mid-call.
        ///
        /// What we do instead ("logical DataOnly mode"):
        ///   1. AudioStreamTrack.Enabled = false  РІР‚вЂќ stops the WebRTC stack from encoding
        ///      and transmitting audio frames. The track remains in the peer connection.
        ///   2. AudioSource.mute = true            РІР‚вЂќ belt-and-suspenders: silences local mic.
        ///   3. SessionModel.MediaMode = DataOnly  РІР‚вЂќ truthful session state.
        ///   4. RaiseSnapshotChanged()             РІР‚вЂќ UI receives updated snapshot.
        ///
        /// The remote peer still has an audio track but receives no audio data.
        /// DataChannel continues working normally.
        ///
        /// Upgrade back to AudioOnly is not implemented in this release.
        /// </summary>
        private void ApplyDataOnlyMode() {
            _mediaCapture.DisableAudioTrack();
            _session.SetMediaMode(MediaMode.DataOnly);
            RaiseSnapshotChanged();
            _diagnostics.LogWarning("ConnectionFlow",
                "MediaMode=DataOnly (pragmatic MVP: track disabled, not renegotiated)");
        }

        // РІвЂќР‚РІвЂќР‚ Private: post-connect hangup polling РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        /// <summary>
        /// Lightweight background poll for remote hangup after the session is established.
        /// Deletes the hangup slot after processing so it does not linger in Worker KV.
        /// Fires Closed event BEFORE CleanupSession so Bootstrap receives SessionId/IsCreator.
        /// </summary>
        private async UniTaskVoid PollForRemoteHangupAsync(CancellationToken ct) {
            var interval = TimeSpan.FromSeconds(_config.workerEndpoint.pollingIntervalSec * 2);

            while (!ct.IsCancellationRequested && !_fsm.IsTerminal && !string.IsNullOrEmpty(_activeSessionId)) {
                await UniTask.Delay(interval, cancellationToken: ct).SuppressCancellationThrow();
                if (ct.IsCancellationRequested || _fsm.IsTerminal || string.IsNullOrEmpty(_activeSessionId)) break;

                var hangup = await _worker.GetSignalAsync(
                    _activeSessionId, SignalingEnvelope.Types.Hangup, ct);

                if (hangup != null) {
                    _diagnostics.LogSignaling("IN", SignalingEnvelope.Types.Hangup, "remote");

                    await _worker.DeleteSignalAsync(
                        _activeSessionId, SignalingEnvelope.Types.Hangup, ct)
                        .SuppressCancellationThrow();

                    SetLifecycle(ConnectionLifecycleState.Closed, "remote-hangup");
                    CleanupSession();
                    return;
                }

                if (!_session.IsCreator && !_iceRestartInProgress) {
                    var restartOffer = await _worker.GetSignalAsync(
                        _activeSessionId, SignalingEnvelope.Types.Offer, ct);
                    if (restartOffer != null) {
                        CleanupSignalingSlotsBestEffort(_activeSessionId, SignalingEnvelope.Types.Offer);
                        HandleIncomingIceRestartOfferAsync(restartOffer, ct).Forget();
                        continue;
                    }
                }
            }
        }

        // РІвЂќР‚РІвЂќР‚ Private: data channel РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        private void SetupDataChannel(RTCDataChannel channel) {
            channel.OnOpen = () => {
                _diagnostics.LogIce("DataChannel", "opened");
                _session.SetDataChannelOpen(true);
                RaiseSnapshotChanged();
            };
            channel.OnClose = () => {
                _diagnostics.LogIce("DataChannel", "closed");
                _session.SetDataChannelOpen(false);
                RaiseSnapshotChanged();
            };
            channel.OnMessage = bytes => {
                string raw = Encoding.UTF8.GetString(bytes);
                var env = DataChannelEnvelope.Deserialize(raw);
                if (env == null) return;

                if (env.type == DataChannelEnvelope.Types.Chat)
                    HandleChatOnMainThread(env.payload).Forget();
                else if (env.type == DataChannelEnvelope.Types.Ping)
                    SendPong();
                else if (env.type == DataChannelEnvelope.Types.VoiceState)
                    HandleVoiceStateOnMainThread(env.payload).Forget();
            };
        }

        private async UniTaskVoid HandleChatOnMainThread(string text) {
            await UniTask.SwitchToMainThread();
            OnChatMessageReceived?.Invoke("remote", text);
        }

        private async UniTaskVoid HandleVoiceStateOnMainThread(string payload) {
            await UniTask.SwitchToMainThread();
            bool speaking = string.Equals(payload, DataChannelEnvelope.VoiceStates.Speaking,
                StringComparison.OrdinalIgnoreCase);
            OnRemoteSpeakingChanged?.Invoke(speaking);
        }

        private void SendPong() {
            if (_dataChannel?.ReadyState == RTCDataChannelState.Open)
                _dataChannel.Send(Encoding.UTF8.GetBytes(DataChannelEnvelope.Pong().Serialize()));
        }

        // РІвЂќР‚РІвЂќР‚ Private: signaling helpers РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        private async UniTaskVoid TryIceRestartAsCallerAsync() {
            if (_fsm.IsTerminal || _iceRestartInProgress || !_session.IsCreator ||
                _peer == null || string.IsNullOrEmpty(_activeSessionId)) return;

            var sessionToken = _sessionCts?.Token ?? CancellationToken.None;
            _iceRestartInProgress = true;
            try {
                _diagnostics.LogInfo("Recovery", "Starting ICE restart as caller");
                CleanupSignalingSlotsBestEffort(_activeSessionId,
                    SignalingEnvelope.Types.Offer,
                    SignalingEnvelope.Types.Answer);

                await _peer.CreateOfferAsync(true, sessionToken);
                await _peer.WaitForIceGatheringAsync(
                    _config.connection.iceGatheringTimeoutMs, sessionToken, _config.ice.relayOnly);

                string localSdp = _peer.LocalSdp;
                if (string.IsNullOrEmpty(localSdp))
                    throw new InvalidOperationException("ice-restart-empty-local-sdp");

                var envelope = BuildSdpEnvelope(_activeSessionId, SignalingEnvelope.Types.Offer,
                    localSdp, "offer");
                _diagnostics.LogSignaling("OUT", SignalingEnvelope.Types.Offer, "ice-restart");

                if (!await _worker.PostSignalAsync(envelope, sessionToken))
                    throw new InvalidOperationException("ice-restart-offer-post-failed");

                var answerEnvelope = await PollForSignalAsync(
                    _activeSessionId, SignalingEnvelope.Types.Answer, sessionToken);
                if (answerEnvelope == null)
                    throw new InvalidOperationException("ice-restart-answer-timeout");

                _diagnostics.LogSignaling("IN", SignalingEnvelope.Types.Answer, "ice-restart");

                var answerSdp = JsonUtility.FromJson<SdpPayload>(answerEnvelope.payloadJson);
                await _peer.SetRemoteDescriptionAsync(answerSdp.sdp, RTCSdpType.Answer, sessionToken);

                CleanupSignalingSlotsBestEffort(_activeSessionId,
                    SignalingEnvelope.Types.Offer,
                    SignalingEnvelope.Types.Answer);

                bool connected = await _peer.WaitForIceConnectedAsync(
                    _config.connection.iceRestartTimeoutMs, sessionToken);
                if (!connected)
                    throw new InvalidOperationException("ice-restart-timeout");
            }
            catch (OperationCanceledException) {
            }
            catch (Exception e) {
                _iceRestartInProgress = false;
                _diagnostics.LogError("Recovery", $"ICE restart (caller) failed: {e.Message}");
                if (!_fsm.IsTerminal)
                    HandleIceFailedAsync().Forget();
                return;
            }

            _iceRestartInProgress = false;
        }

        private async UniTaskVoid WaitForRemoteIceRestartOfferAsync() {
            if (_fsm.IsTerminal || _iceRestartInProgress || _session.IsCreator ||
                _peer == null || string.IsNullOrEmpty(_activeSessionId)) return;

            var sessionToken = _sessionCts?.Token ?? CancellationToken.None;
            _iceRestartInProgress = true;
            try {
                _diagnostics.LogInfo("Recovery", "Waiting for remote ICE restart offer");
                var offerEnvelope = await PollForSignalAsync(
                    _activeSessionId, SignalingEnvelope.Types.Offer, sessionToken);
                if (offerEnvelope == null)
                    throw new InvalidOperationException("ice-restart-offer-timeout");

                await ApplyRemoteIceRestartOfferAsync(offerEnvelope, sessionToken);
            }
            catch (OperationCanceledException) {
            }
            catch (Exception e) {
                _iceRestartInProgress = false;
                _diagnostics.LogError("Recovery", $"ICE restart (callee) failed: {e.Message}");
                if (!_fsm.IsTerminal)
                    HandleIceFailedAsync().Forget();
                return;
            }

            _iceRestartInProgress = false;
        }

        private async UniTaskVoid HandleIncomingIceRestartOfferAsync(
            SignalingEnvelope offerEnvelope, CancellationToken ct) {
            if (_fsm.IsTerminal || _iceRestartInProgress || _session.IsCreator || _peer == null)
                return;

            _iceRestartInProgress = true;
            try {
                await ApplyRemoteIceRestartOfferAsync(offerEnvelope, ct);
            }
            catch (OperationCanceledException) {
            }
            catch (Exception e) {
                _iceRestartInProgress = false;
                _diagnostics.LogError("Recovery", $"Incoming ICE restart failed: {e.Message}");
                if (!_fsm.IsTerminal)
                    HandleIceFailedAsync().Forget();
                return;
            }

            _iceRestartInProgress = false;
        }

        private async UniTask ApplyRemoteIceRestartOfferAsync(
            SignalingEnvelope offerEnvelope, CancellationToken ct) {
            _diagnostics.LogSignaling("IN", SignalingEnvelope.Types.Offer, "ice-restart");
            SetLifecycle(ConnectionLifecycleState.Recovering, "ice-restart-offer");

            var offerSdp = JsonUtility.FromJson<SdpPayload>(offerEnvelope.payloadJson);
            await _peer.SetRemoteDescriptionAsync(offerSdp.sdp, RTCSdpType.Offer, ct);
            await _peer.CreateAnswerAsync(ct);
            await _peer.WaitForIceGatheringAsync(_config.connection.iceGatheringTimeoutMs, ct, _config.ice.relayOnly);

            string localSdp = _peer.LocalSdp;
            if (string.IsNullOrEmpty(localSdp))
                throw new InvalidOperationException("ice-restart-empty-local-sdp");

            var answerEnvelope = BuildSdpEnvelope(_activeSessionId, SignalingEnvelope.Types.Answer,
                localSdp, "answer");
            _diagnostics.LogSignaling("OUT", SignalingEnvelope.Types.Answer, "ice-restart");

            if (!await _worker.PostSignalAsync(answerEnvelope, ct))
                throw new InvalidOperationException("ice-restart-answer-post-failed");

            bool connected = await _peer.WaitForIceConnectedAsync(
                _config.connection.iceRestartTimeoutMs, ct);
            if (!connected)
                throw new InvalidOperationException("ice-restart-timeout");

            CleanupSignalingSlotsBestEffort(_activeSessionId,
                SignalingEnvelope.Types.Offer,
                SignalingEnvelope.Types.Answer);
        }

        private async UniTask<SignalingEnvelope> PollForSignalAsync(
            string sessionId, string type, CancellationToken ct) {
            float elapsed = 0f;
            float timeout = _config.connection.signalingPollTimeoutMs / 1000f;
            float interval = _config.workerEndpoint.pollingIntervalSec;
            int attempts = 0;
            var timer = Stopwatch.StartNew();

            _diagnostics.LogSignaling("POLL", type, $"start timeout={_config.connection.signalingPollTimeoutMs}ms interval={(int)(interval * 1000f)}ms");

            while (elapsed < timeout) {
                ct.ThrowIfCancellationRequested();

                attempts++;
                var envelope = await _worker.GetSignalAsync(sessionId, type, ct);
                if (envelope != null)
                {
                    _diagnostics.LogSignaling("POLL", type, $"hit elapsed={timer.ElapsedMilliseconds}ms attempts={attempts}");
                    return envelope;
                }

                await UniTask.Delay(TimeSpan.FromSeconds(interval), cancellationToken: ct)
                    .SuppressCancellationThrow();

                if (ct.IsCancellationRequested) return null;
                elapsed += interval;
            }

            _diagnostics.LogWarning("ConnectionFlow", $"Poll timeout for type={type} elapsed={timer.ElapsedMilliseconds}ms attempts={attempts}");
            return null;
        }

        private async UniTask WaitForConnectionAsync(CancellationToken ct) {
            bool connected = await _peer.WaitForIceConnectedAsync(
                _config.connection.initialIceConnectTimeoutMs, ct);

            if (!connected && !_fsm.IsConnected)
                FailSession("ice-connect-timeout");
        }

        private void CleanupSignalingSlotsBestEffort(string sessionId, params string[] types) {
            if (string.IsNullOrEmpty(sessionId)) return;
            DeleteSignalingSlotsAsync(sessionId, types).Forget();
        }

        private async UniTaskVoid DeleteSignalingSlotsAsync(string sessionId, string[] types) {
            foreach (var type in types) {
                await _worker.DeleteSignalAsync(sessionId, type, CancellationToken.None)
                    .SuppressCancellationThrow();
            }
        }

        private SignalingEnvelope BuildSdpEnvelope(
            string sessionId, string type, string sdp, string sdpType) {
            var payload = JsonUtility.ToJson(new SdpPayload { sdp = sdp, sdpType = sdpType });
            return new SignalingEnvelope {
                sessionId = sessionId,
                fromPeerId = _localPeerId,
                messageId = Guid.NewGuid().ToString("N"),
                type = type,
                ttlMs = _config.workerEndpoint.signalingMessageTtlSec * 1000,
                sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payloadJson = payload
            };
        }

        // РІвЂќР‚РІвЂќР‚ Private: session lifecycle РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

        private void StartSessionCts(CancellationToken externalCt) {
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            _iceRestartInProgress = false;
        }

        private void CleanupSession() {
            _sessionCts?.Cancel();

            // Stop sampler before disposing peer РІР‚вЂќ sampler uses _sessionCts token,
            // so cancellation signals it to exit the loop. Dispose then drops the reference.
            _statsSampler?.Dispose();
            _statsSampler = null;
            _policy.Reset();
            _recovery.CancelPending();
            _iceRestartInProgress = false;

            _dataChannel?.Close();
            _dataChannel = null;
            _peer?.Dispose();
            _peer = null;
            _qualityMonitor.Reset();
            _hangupPollingStarted = false;

            // Best-effort cleanup of SDP slots. Hangup is intentionally excluded:
            // - local hangup: the slot must remain readable for the remote peer (TTL=15s).
            // - remote hangup: the slot is already deleted in PollForRemoteHangupAsync.
            CleanupSignalingSlotsBestEffort(_activeSessionId,
                SignalingEnvelope.Types.Offer,
                SignalingEnvelope.Types.Answer);

            _activeSessionId = null;
            _session.Reset();
        }

        public void Dispose() {
            CleanupSession();
            _recovery.Dispose();
            _sessionCts?.Dispose();
            _sessionCts = null;
        }
    }
}

















