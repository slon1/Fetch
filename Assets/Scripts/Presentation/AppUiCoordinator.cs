using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using WebRtcV2.Application.Booth;
using WebRtcV2.Application.Connection;
using WebRtcV2.Shared;
using WebRtcV2.Transport;

namespace WebRtcV2.Presentation
{
    public sealed class AppUiCoordinator : IDisposable
    {
        private readonly CallerScr _callerScreen;
        private readonly VideoScr _videoScreen;
        private readonly ChatScr _chatScreen;
        private readonly InfoScr _infoScreen;
        private readonly AudioSource _remoteAudioSource;
        private readonly MediaCaptureService _mediaCapture;
        private readonly IBoothFlow _boothFlow;
        private readonly IConnectionFlow _connectionFlow;
        private readonly AndroidLocalNotificationService _notificationService;
        private readonly AppVisibilityTracker _visibilityTracker;
        private readonly CancellationToken _appToken;
        private readonly HashSet<string> _dismissedCallIds = new HashSet<string>(StringComparer.Ordinal);

        private BoothSnapshot _currentBoothSnapshot = BoothSnapshot.Empty;
        private ConnectionSnapshot _currentConnectionSnapshot = ConnectionSnapshot.Idle;
        private CallSessionRef _currentPendingCall;
        private string _startedCallId;
        private string _externalError;
        private string _connectionMessage;
        private string _trackedSessionId;
        private bool _hasConnectedInCurrentSession;
        private bool _isChatVisible;
        private bool _manualVideoEnabled = true;
        private bool _isHandlingTerminalSnapshot;
        private bool _initialized;
        private bool _disposed;
        private ConnectionLifecycleState _previousLifecycleState = ConnectionLifecycleState.Idle;

        public AppUiCoordinator(
            CallerScr callerScreen,
            VideoScr videoScreen,
            ChatScr chatScreen,
            InfoScr infoScreen,
            AudioSource remoteAudioSource,
            MediaCaptureService mediaCapture,
            IBoothFlow boothFlow,
            IConnectionFlow connectionFlow,
            AndroidLocalNotificationService notificationService,
            AppVisibilityTracker visibilityTracker,
            CancellationToken appToken)
        {
            _callerScreen = callerScreen;
            _videoScreen = videoScreen;
            _chatScreen = chatScreen;
            _infoScreen = infoScreen;
            _remoteAudioSource = remoteAudioSource;
            _mediaCapture = mediaCapture;
            _boothFlow = boothFlow;
            _connectionFlow = connectionFlow;
            _notificationService = notificationService;
            _visibilityTracker = visibilityTracker;
            _appToken = appToken;

            WireScreenEvents();
            WireFlowEvents();
        }

        public void Initialize()
        {
            _infoScreen?.Show();
            _videoScreen?.Hide();
            _chatScreen?.Hide();
            _callerScreen?.ShowIdle(clearNumber: true);
            _infoScreen?.ResetFlags();
            _currentBoothSnapshot = _boothFlow.CurrentSnapshot ?? BoothSnapshot.Empty;
            _currentConnectionSnapshot = _connectionFlow.CurrentSnapshot ?? ConnectionSnapshot.Idle;
            UpdateOverlay();
            InitializeBoothAsync().Forget();
        }

        public void SetAppVisibility()
        {
            if (_disposed)
                return;

            ApplyEffectiveVideoState();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            UnwireFlowEvents();
            UnwireScreenEvents();
            StopRemoteAudio();
            _videoScreen?.ClearRemoteVideo();
        }

        private void WireScreenEvents()
        {
            if (_callerScreen != null)
            {
                _callerScreen.OnDigitRequested += HandleDigitRequested;
                _callerScreen.OnDeleteRequested += HandleDeleteRequested;
                _callerScreen.OnDialRequested += HandleDialRequested;
                _callerScreen.OnAcceptRequested += HandleAcceptRequested;
                _callerScreen.OnRejectRequested += HandleRejectRequested;
                _callerScreen.OnHangupRequested += HandleCallerHangupRequested;
            }

            if (_videoScreen != null)
            {
                _videoScreen.OnHangupRequested += HandleVideoHangupRequested;
                _videoScreen.OnPushToTalkChanged += HandlePushToTalkChanged;
                _videoScreen.OnToggleVideoRequested += HandleToggleVideoRequested;
                _videoScreen.OnOpenChatRequested += HandleOpenChatRequested;
            }

            if (_chatScreen != null)
            {
                _chatScreen.OnBackToVideoRequested += HandleBackToVideoRequested;
                _chatScreen.OnSendMessageRequested += HandleSendMessageRequested;
            }
        }

        private void UnwireScreenEvents()
        {
            if (_callerScreen != null)
            {
                _callerScreen.OnDigitRequested -= HandleDigitRequested;
                _callerScreen.OnDeleteRequested -= HandleDeleteRequested;
                _callerScreen.OnDialRequested -= HandleDialRequested;
                _callerScreen.OnAcceptRequested -= HandleAcceptRequested;
                _callerScreen.OnRejectRequested -= HandleRejectRequested;
                _callerScreen.OnHangupRequested -= HandleCallerHangupRequested;
            }

            if (_videoScreen != null)
            {
                _videoScreen.OnHangupRequested -= HandleVideoHangupRequested;
                _videoScreen.OnPushToTalkChanged -= HandlePushToTalkChanged;
                _videoScreen.OnToggleVideoRequested -= HandleToggleVideoRequested;
                _videoScreen.OnOpenChatRequested -= HandleOpenChatRequested;
            }

            if (_chatScreen != null)
            {
                _chatScreen.OnBackToVideoRequested -= HandleBackToVideoRequested;
                _chatScreen.OnSendMessageRequested -= HandleSendMessageRequested;
            }
        }

        private void WireFlowEvents()
        {
            _boothFlow.OnSnapshotChanged += HandleBoothSnapshotChanged;
            _boothFlow.OnIncomingCall += HandleIncomingCall;
            _boothFlow.OnCallAccepted += HandleCallAccepted;
            _boothFlow.OnCallEnded += HandleCallEnded;

            _connectionFlow.OnSnapshotChanged += HandleConnectionSnapshotChanged;
            _connectionFlow.OnRemoteAudioTrackAvailable += HandleRemoteAudioTrackAvailable;
            _connectionFlow.OnRemoteVideoTextureAvailable += HandleRemoteVideoTextureAvailable;
            _connectionFlow.OnChatMessageReceived += HandleChatMessageReceived;
            _connectionFlow.OnRemoteSpeakingChanged += HandleRemoteSpeakingChanged;
            _mediaCapture.OnLocalVideoPreviewChanged += HandleLocalVideoPreviewChanged;
        }

        private void UnwireFlowEvents()
        {
            _boothFlow.OnSnapshotChanged -= HandleBoothSnapshotChanged;
            _boothFlow.OnIncomingCall -= HandleIncomingCall;
            _boothFlow.OnCallAccepted -= HandleCallAccepted;
            _boothFlow.OnCallEnded -= HandleCallEnded;

            _connectionFlow.OnSnapshotChanged -= HandleConnectionSnapshotChanged;
            _connectionFlow.OnRemoteAudioTrackAvailable -= HandleRemoteAudioTrackAvailable;
            _connectionFlow.OnRemoteVideoTextureAvailable -= HandleRemoteVideoTextureAvailable;
            _connectionFlow.OnChatMessageReceived -= HandleChatMessageReceived;
            _connectionFlow.OnRemoteSpeakingChanged -= HandleRemoteSpeakingChanged;
            _mediaCapture.OnLocalVideoPreviewChanged -= HandleLocalVideoPreviewChanged;
        }

        private async UniTaskVoid InitializeBoothAsync()
        {
            try
            {
                _externalError = null;
                _currentBoothSnapshot = BoothSnapshot.Empty;
                UpdateOverlay();
                await _boothFlow.InitializeAsync(_appToken);
                _initialized = true;
                RenderBoothSnapshot(_boothFlow.CurrentSnapshot, clearDialNumber: true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                ShowUiError($"Booth init failed: {e.Message}");
            }
        }

        private void HandleDigitRequested(int _)
        {
            ClearUiError();
        }

        private void HandleDeleteRequested()
        {
            ClearUiError();
        }

        private async void HandleDialRequested(string targetNumber)
        {
            if (_disposed || !_initialized)
                return;

            string normalizedNumber = NormalizeDialNumber(targetNumber);
            if (normalizedNumber == null)
            {
                ShowUiError("Enter a valid booth number to call");
                return;
            }

            try
            {
                ClearUiError();
                DialResult result = await _boothFlow.DialAsync(normalizedNumber, _appToken);
                switch (result.Outcome)
                {
                    case BoothDialOutcome.Ringing:
                        ForgetDismissedCall(result.Call?.CallId);
                        _currentPendingCall = result.Call;
                        RenderBoothSnapshot(_boothFlow.CurrentSnapshot, clearDialNumber: false);
                        break;
                    case BoothDialOutcome.NotRegistered:
                        RenderBoothSnapshot(_boothFlow.CurrentSnapshot, clearDialNumber: false);
                        ShowUiError("Number is not registered");
                        break;
                    case BoothDialOutcome.Offline:
                        RenderBoothSnapshot(_boothFlow.CurrentSnapshot, clearDialNumber: false);
                        ShowUiError("User is offline");
                        break;
                    case BoothDialOutcome.Busy:
                        RenderBoothSnapshot(_boothFlow.CurrentSnapshot, clearDialNumber: false);
                        ShowUiError("Line is busy");
                        break;
                    default:
                        RenderBoothSnapshot(_boothFlow.CurrentSnapshot, clearDialNumber: false);
                        ShowUiError($"Dial failed: {result.Error ?? "unknown error"}");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                ShowUiError($"Dial failed: {e.Message}");
            }
        }

        private async void HandleAcceptRequested()
        {
            if (_disposed || _currentPendingCall == null)
                return;

            try
            {
                ClearUiError();
                CallSessionRef acceptedCall = await _boothFlow.AcceptAsync(_currentPendingCall.CallId, _appToken);
                if (acceptedCall == null)
                {
                    ShowUiError("Could not accept the call");
                    return;
                }

                StartCall(acceptedCall);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                ShowUiError($"Accept failed: {e.Message}");
            }
        }

        private async void HandleRejectRequested()
        {
            if (_disposed || _currentPendingCall == null)
                return;

            string dismissedCallId = _currentPendingCall.CallId;
            try
            {
                ClearUiError();
                RememberDismissedCall(dismissedCallId);
                bool ok = await _boothFlow.RejectAsync(dismissedCallId, _appToken);
                if (!ok)
                {
                    ShowUiError("Could not reject the call");
                    return;
                }

                _notificationService.CancelSessionNotification(dismissedCallId);
                _currentPendingCall = null;
                _startedCallId = null;
                ResetUiToIdle(clearDialNumber: true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                ShowUiError($"Reject failed: {e.Message}");
            }
        }

        private async void HandleCallerHangupRequested()
        {
            if (_disposed)
                return;

            string callId = ResolveCurrentCallId();
            if (string.IsNullOrWhiteSpace(callId))
                return;

            try
            {
                ClearUiError();
                RememberDismissedCall(callId);

                if (ShouldKeepCallScreens())
                    await _connectionFlow.HangupAsync(_appToken);

                bool ok = await _boothFlow.HangupLineAsync(callId, _appToken);
                if (!ok)
                {
                    ShowUiError("Hangup failed");
                    return;
                }

                if (!ShouldKeepCallScreens())
                {
                    _currentPendingCall = null;
                    _startedCallId = null;
                    ResetUiToIdle(clearDialNumber: true);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                ShowUiError($"Hangup failed: {e.Message}");
            }
        }

        private async void HandleVideoHangupRequested()
        {
            string callId = ResolveCurrentCallId();
            try
            {
                ClearUiError();
                RememberDismissedCall(callId);
                await _connectionFlow.HangupAsync(_appToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                ShowUiError($"Hangup failed: {e.Message}");
            }
        }

        private void HandlePushToTalkChanged(bool isPressed)
        {
            _connectionFlow.SetMicMuted(!isPressed);
            _connectionFlow.SendSpeakingState(isPressed);
        }

        private void HandleToggleVideoRequested(bool enabled)
        {
            _manualVideoEnabled = enabled;
            ApplyEffectiveVideoState();
        }

        private void HandleOpenChatRequested()
        {
            _isChatVisible = true;
            _videoScreen?.Hide();
            _chatScreen?.Show();
            ApplyEffectiveVideoState();
        }

        private void HandleBackToVideoRequested()
        {
            _isChatVisible = false;
            _chatScreen?.Hide();
            _videoScreen?.Show();
            ApplyEffectiveVideoState();
        }

        private void HandleSendMessageRequested(string text)
        {
            if (_connectionFlow.SendChatMessage(text))
                _chatScreen?.AppendLocalMessage(text);
        }

        private void HandleBoothSnapshotChanged(BoothSnapshot snapshot)
        {
            if (_disposed) return;
            if (ShouldIgnoreCall(snapshot?.Call?.CallId))
            {
                if (!ShouldKeepCallScreens())
                    ResetUiToIdle(clearDialNumber: true);
                return;
            }
            RenderBoothSnapshot(snapshot, clearDialNumber: false);
            TryStartCallFromSnapshot(snapshot);
        }

        private void HandleIncomingCall(CallSessionRef call)
        {
            if (_disposed || call == null || ShouldIgnoreCall(call.CallId))
                return;

            _currentPendingCall = call;
            _notificationService.NotifyIncomingCall(call.CallId, call.CallerNumber);
            RenderBoothSnapshot(_boothFlow.CurrentSnapshot, clearDialNumber: false);
        }

        private void HandleCallAccepted(CallSessionRef call)
        {
            if (_disposed || call == null || !call.IsLocalCaller || ShouldIgnoreCall(call.CallId))
                return;

            StartCall(call);
        }

        private void HandleCallEnded(string callId)
        {
            if (string.IsNullOrWhiteSpace(callId))
                return;

            _notificationService.CancelSessionNotification(callId);

            if (_currentPendingCall != null && string.Equals(_currentPendingCall.CallId, callId, StringComparison.Ordinal))
                _currentPendingCall = null;

            if (!ShouldKeepCallScreens())
            {
                if (string.Equals(_startedCallId, callId, StringComparison.Ordinal))
                    _startedCallId = null;
                ResetUiToIdle(clearDialNumber: true);
            }
            else
            {
                UpdateOverlay();
            }
        }

        private void HandleConnectionSnapshotChanged(ConnectionSnapshot snapshot)
        {
            if (_disposed)
                return;

            _currentConnectionSnapshot = snapshot ?? ConnectionSnapshot.Idle;
            UpdateConnectionStateTracking(_currentConnectionSnapshot);
            _videoScreen?.SetMuteAvailable(_currentConnectionSnapshot.MediaMode != MediaMode.DataOnly);
            _infoScreen?.SetConnected(_currentConnectionSnapshot.LifecycleState == ConnectionLifecycleState.Connected);
            _infoScreen?.SetDirectRoute(IsRouteDirect(_currentConnectionSnapshot));
            RenderOverlayStatus();

            if (_currentConnectionSnapshot.LifecycleState == ConnectionLifecycleState.Connected &&
                _previousLifecycleState != ConnectionLifecycleState.Connected)
            {
                SyncBoothLineInCallAsync(_currentConnectionSnapshot.SessionId).Forget();
                string peerNumber = ResolvePeerNumber(_currentBoothSnapshot);
                _notificationService.NotifyConnected(_currentConnectionSnapshot.SessionId, peerNumber);
                if (!_isChatVisible)
                    _videoScreen?.Show();
            }

            if (_currentConnectionSnapshot.LifecycleState == ConnectionLifecycleState.Closed ||
                _currentConnectionSnapshot.LifecycleState == ConnectionLifecycleState.Failed)
            {
                HandleTerminalSnapshotAsync(_currentConnectionSnapshot).Forget();
            }

            _previousLifecycleState = _currentConnectionSnapshot.LifecycleState;
        }

        private async UniTaskVoid SyncBoothLineInCallAsync(string callId)
        {
            if (string.IsNullOrWhiteSpace(callId))
                return;

            try
            {
                bool ok = await _boothFlow.MarkInCallAsync(callId, CancellationToken.None);
                if (ok || _disposed || _connectionFlow.CurrentSnapshot?.LifecycleState == ConnectionLifecycleState.Closed)
                    return;

                RememberDismissedCall(callId);
                await _connectionFlow.HangupAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppUiCoordinator] Failed to mark line in-call: {e.Message}");
            }
        }

        private async UniTaskVoid HandleTerminalSnapshotAsync(ConnectionSnapshot snapshot)
        {
            if (_isHandlingTerminalSnapshot)
                return;

            _isHandlingTerminalSnapshot = true;
            try
            {
                _notificationService.CancelSessionNotification(snapshot.SessionId);
                StopRemoteAudio();
                RememberDismissedCall(snapshot.SessionId);
                _videoScreen?.ClearRemoteVideo();
                _infoScreen?.SetPeerSpeaking(false);

                if (!string.IsNullOrWhiteSpace(snapshot.SessionId))
                    await _boothFlow.HangupLineAsync(snapshot.SessionId, CancellationToken.None);

                _startedCallId = null;
                _currentPendingCall = null;
                _isChatVisible = false;
                _manualVideoEnabled = true;
                ResetUiToIdle(clearDialNumber: true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                ShowUiError($"Return to idle failed: {e.Message}");
            }
            finally
            {
                _isHandlingTerminalSnapshot = false;
            }
        }

        private void StartCall(CallSessionRef call)
        {
            if (call == null || ShouldIgnoreCall(call.CallId))
                return;
            if (string.Equals(_startedCallId, call.CallId, StringComparison.Ordinal))
                return;

            ForgetDismissedCall(call.CallId);
            _startedCallId = call.CallId;
            _currentPendingCall = call;
            _manualVideoEnabled = true;
            _isChatVisible = false;
            _chatScreen?.Hide();
            _callerScreen?.Hide();
            _videoScreen?.Show();
            _chatScreen?.ClearLog();
            _chatScreen?.ClearInput();
            ApplyEffectiveVideoState();
            UpdateOverlay();

            if (call.IsLocalCaller)
                _connectionFlow.ConnectAsCallerAsync(call.CallId, _appToken).Forget();
            else
                _connectionFlow.ConnectAsCalleeAsync(call.CallId, call.CallerClientId, _appToken).Forget();
        }

        private void TryStartCallFromSnapshot(BoothSnapshot snapshot)
        {
            if (_disposed || snapshot?.Call == null || ShouldIgnoreCall(snapshot.Call.CallId))
                return;
            if (snapshot.LineState != BoothLineState.Connecting)
                return;

            StartCall(snapshot.Call);
        }

        private void HandleRemoteAudioTrackAvailable(AudioStreamTrack track)
        {
            if (_remoteAudioSource == null)
            {
                Debug.LogWarning("[AppUiCoordinator] remoteAudioSource not wired - remote audio will not play.");
                return;
            }

            _remoteAudioSource.Stop();
            _remoteAudioSource.SetTrack(track);
            _remoteAudioSource.loop = true;
            _remoteAudioSource.Play();
        }

        private void HandleRemoteVideoTextureAvailable(Texture texture)
        {
            _videoScreen?.SetRemoteVideoTexture(texture);
        }

        private void HandleChatMessageReceived(string _, string text)
        {
            _chatScreen?.AppendRemoteMessage(text);
        }

        private void HandleRemoteSpeakingChanged(bool speaking)
        {
            _infoScreen?.SetPeerSpeaking(speaking);
        }

        private void HandleLocalVideoPreviewChanged(Texture texture)
        {
            _videoScreen?.SetLocalVideoTexture(texture);
        }

        private void ResetUiToIdle(bool clearDialNumber)
        {
            _videoScreen?.Hide();
            _chatScreen?.Hide();
            _callerScreen?.ShowIdle(clearDialNumber);
            ApplyEffectiveVideoState();
            RenderBoothSnapshot(_boothFlow.CurrentSnapshot ?? BoothSnapshot.Empty, clearDialNumber);
        }

        private void RenderBoothSnapshot(BoothSnapshot snapshot, bool clearDialNumber)
        {
            _currentBoothSnapshot = snapshot ?? BoothSnapshot.Empty;
            _currentPendingCall = _currentBoothSnapshot.Call;

            if (ShouldKeepCallScreens())
            {
                UpdateOverlay();
                return;
            }

            switch (_currentBoothSnapshot.LineState)
            {
                case BoothLineState.RingingIncoming:
                    _callerScreen?.ShowIncomingRinging(ResolvePeerNumber(_currentBoothSnapshot));
                    break;
                case BoothLineState.RingingOutgoing:
                    _callerScreen?.ShowOutgoingRinging(ResolvePeerNumber(_currentBoothSnapshot));
                    break;
                case BoothLineState.Connecting:
                    _callerScreen?.ShowConnecting(ResolvePeerNumber(_currentBoothSnapshot));
                    break;
                case BoothLineState.InCall:
                    _callerScreen?.Hide();
                    if (_isChatVisible)
                        _chatScreen?.Show();
                    else
                        _videoScreen?.Show();
                    break;
                case BoothLineState.Dialing:
                    _callerScreen?.ShowConnecting(ResolvePeerNumber(_currentBoothSnapshot));
                    break;
                case BoothLineState.Idle:
                default:
                    _callerScreen?.ShowIdle(clearDialNumber);
                    break;
            }

            UpdateOverlay();
        }

        private void UpdateOverlay()
        {
            _infoScreen?.SetInfo(BuildLineHeadline(_currentBoothSnapshot));
            RenderOverlayStatus();
        }

        private void RenderOverlayStatus()
        {
            _infoScreen?.SetWarning(BuildStatusText(_currentConnectionSnapshot));
            RenderErrorText();
        }

        private void UpdateConnectionStateTracking(ConnectionSnapshot snapshot)
        {
            if (_trackedSessionId != snapshot.SessionId)
            {
                _trackedSessionId = snapshot.SessionId;
                _hasConnectedInCurrentSession = false;
            }

            if (snapshot.LifecycleState == ConnectionLifecycleState.Connected)
                _hasConnectedInCurrentSession = true;

            _connectionMessage = snapshot.LifecycleState switch
            {
                ConnectionLifecycleState.Recovering when _hasConnectedInCurrentSession => "Connection lost. Trying to restore...",
                ConnectionLifecycleState.Failed when _hasConnectedInCurrentSession => "Connection lost.",
                ConnectionLifecycleState.Closed when _previousLifecycleState == ConnectionLifecycleState.Recovering => "Connection lost.",
                ConnectionLifecycleState.Connected when _previousLifecycleState == ConnectionLifecycleState.Recovering => "Reconnected.",
                _ => null,
            };
        }

        private string BuildLineHeadline(BoothSnapshot snapshot)
        {
            snapshot ??= BoothSnapshot.Empty;

            if (string.IsNullOrWhiteSpace(snapshot.BoothNumber))
                return "Registering booth...";

            string peer = ResolvePeerNumber(snapshot);
            return snapshot.LineState switch
            {
                BoothLineState.RingingOutgoing => $"Calling {peer}",
                BoothLineState.RingingIncoming => $"Incoming call from {peer}",
                BoothLineState.Connecting => $"Connecting to {peer}",
                BoothLineState.InCall => $"In call with {peer}",
                BoothLineState.Dialing => $"Dialing {peer}",
                _ => $"Your number: {snapshot.BoothNumber}",
            };
        }

        private string BuildStatusText(ConnectionSnapshot snapshot)
        {
            if (snapshot == null)
                return string.Empty;

            return snapshot.LifecycleState switch
            {
                ConnectionLifecycleState.Connected => $"{(_previousLifecycleState == ConnectionLifecycleState.Recovering ? "Reconnected" : "Connected")} | {FormatMediaMode(snapshot.MediaMode)} | {snapshot.RouteMode} | {snapshot.SignalingMode}{BuildVideoPauseSuffix(snapshot)}",
                ConnectionLifecycleState.Recovering => _hasConnectedInCurrentSession
                    ? $"Recovering | {FormatMediaMode(snapshot.MediaMode)} | {snapshot.RouteMode} | {snapshot.SignalingMode}{BuildVideoPauseSuffix(snapshot)}"
                    : "Connecting...",
                ConnectionLifecycleState.Preparing => "Preparing...",
                ConnectionLifecycleState.Signaling => "Signaling...",
                ConnectionLifecycleState.Connecting => "Connecting...",
                ConnectionLifecycleState.Failed => "Connection failed",
                _ => string.Empty,
            };
        }

        private string BuildVideoPauseSuffix(ConnectionSnapshot snapshot)
        {
            if (!_manualVideoEnabled || !ShouldSuspendVideoForVisibility())
                return string.Empty;

            return snapshot != null && snapshot.MediaMode != MediaMode.DataOnly
                ? " | Video paused in background"
                : string.Empty;
        }

        private static string FormatMediaMode(MediaMode mode) => mode switch
        {
            MediaMode.Full => "Video",
            MediaMode.DataOnly => "DataOnly",
            _ => "AudioOnly",
        };

        private void ClearUiError()
        {
            _externalError = null;
            RenderErrorText();
        }

        private void ShowUiError(string message)
        {
            _externalError = message;
            RenderErrorText();
        }

        private void RenderErrorText()
        {
            string value = !string.IsNullOrWhiteSpace(_externalError)
                ? _externalError
                : _connectionMessage;
            _infoScreen?.SetError(value);
        }

        private void ApplyEffectiveVideoState()
        {
            bool enabled = _manualVideoEnabled && !ShouldSuspendVideoForVisibility();
            _connectionFlow.SetVideoEnabled(enabled);
            _videoScreen?.SetLocalVideoEnabled(enabled);
        }

        private bool ShouldSuspendVideoForVisibility()
        {
            return _visibilityTracker != null && _visibilityTracker.ShouldShowBackgroundNotification;
        }

        private void StopRemoteAudio()
        {
            if (_remoteAudioSource == null)
                return;

            if (_remoteAudioSource.isPlaying)
                _remoteAudioSource.Stop();
        }

        private bool ShouldKeepCallScreens()
        {
            return _currentConnectionSnapshot.LifecycleState == ConnectionLifecycleState.Preparing ||
                   _currentConnectionSnapshot.LifecycleState == ConnectionLifecycleState.Signaling ||
                   _currentConnectionSnapshot.LifecycleState == ConnectionLifecycleState.Connecting ||
                   _currentConnectionSnapshot.LifecycleState == ConnectionLifecycleState.Connected ||
                   _currentConnectionSnapshot.LifecycleState == ConnectionLifecycleState.Recovering;
        }

        private string ResolveCurrentCallId()
        {
            return _currentPendingCall?.CallId ??
                   _currentBoothSnapshot?.Call?.CallId ??
                   _startedCallId ??
                   _currentConnectionSnapshot?.SessionId;
        }

        private void RememberDismissedCall(string callId)
        {
            if (string.IsNullOrWhiteSpace(callId))
                return;

            _dismissedCallIds.Add(callId);
        }

        private void ForgetDismissedCall(string callId)
        {
            if (string.IsNullOrWhiteSpace(callId))
                return;

            _dismissedCallIds.Remove(callId);
        }

        private bool ShouldIgnoreCall(string callId)
        {
            return !string.IsNullOrWhiteSpace(callId) && _dismissedCallIds.Contains(callId);
        }

        private static string NormalizeDialNumber(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            char[] buffer = new char[LocalClientIdentity.BoothNumberLength];
            int count = 0;
            foreach (char c in raw)
            {
                if (!char.IsDigit(c))
                    continue;
                if (count >= buffer.Length)
                    return null;
                buffer[count++] = c;
            }

            return count == LocalClientIdentity.BoothNumberLength ? new string(buffer, 0, count) : null;
        }

        private static string ResolvePeerNumber(BoothSnapshot snapshot)
        {
            if (snapshot == null)
                return "Unknown";
            if (!string.IsNullOrWhiteSpace(snapshot.PeerNumber))
                return snapshot.PeerNumber;
            if (snapshot.Call == null)
                return "Unknown";
            return snapshot.Call.IsLocalCaller ? snapshot.Call.CalleeNumber : snapshot.Call.CallerNumber;
        }

        private static bool IsRouteDirect(ConnectionSnapshot snapshot)
        {
            return snapshot != null &&
                   snapshot.LifecycleState != ConnectionLifecycleState.Idle &&
                   snapshot.LifecycleState != ConnectionLifecycleState.Closed &&
                   snapshot.LifecycleState != ConnectionLifecycleState.Failed &&
                   snapshot.RouteMode == RouteMode.Direct;
        }
    }
}
