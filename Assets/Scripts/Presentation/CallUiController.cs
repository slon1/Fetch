using System;
using System.Threading;
using Unity.WebRTC;
using UnityEngine;
using WebRtcV2.Application.Connection;
using WebRtcV2.Transport;

namespace WebRtcV2.Presentation
{
    /// <summary>
    /// Owns call-specific UI orchestration: call controls, media previews, chat, and remote media attachment.
    /// </summary>
    public sealed class CallUiController : IDisposable
    {
        private readonly CallScreenView _callView;
        private readonly ConnectionStatusView _statusView;
        private readonly AudioSource _remoteAudioSource;
        private readonly MediaCaptureService _mediaCapture;
        private readonly IConnectionFlow _connectionFlow;
        private readonly CancellationToken _appToken;

        private bool _manualVideoEnabled = true;
        private bool _isChatVisible;
        private bool _disposed;

        public CallUiController(
            CallScreenView callView,
            ConnectionStatusView statusView,
            AudioSource remoteAudioSource,
            MediaCaptureService mediaCapture,
            IConnectionFlow connectionFlow,
            CancellationToken appToken)
        {
            _callView = callView;
            _statusView = statusView;
            _remoteAudioSource = remoteAudioSource;
            _mediaCapture = mediaCapture;
            _connectionFlow = connectionFlow;
            _appToken = appToken;

            WireApplicationEvents();
            WireViewEvents();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _connectionFlow.OnRemoteAudioTrackAvailable -= HandleRemoteAudioTrackAvailable;
            _connectionFlow.OnRemoteVideoTextureAvailable -= HandleRemoteVideoTextureAvailable;
            _connectionFlow.OnChatMessageReceived -= HandleChatMessageReceived;
            _connectionFlow.OnRemoteSpeakingChanged -= HandleRemoteSpeakingChanged;
            _mediaCapture.OnLocalVideoPreviewChanged -= HandleLocalVideoPreviewChanged;

            _callView.OnHangup -= HandleHangup;
            _callView.OnPushToTalkChanged -= HandlePushToTalkChanged;
            _callView.OnToggleVideoChanged -= HandleToggleVideoChanged;
            _callView.OnSendMessage -= HandleSendMessage;
            _callView.OnChatVisibilityChanged -= HandleChatVisibilityChanged;

            StopRemoteAudio();
            _callView.ClearRemoteVideo();
        }

        public void Show()
        {
            _manualVideoEnabled = true;
            _isChatVisible = false;
            ApplyEffectiveVideoState();
            _callView.SetChatVisible(false);
            _callView.Show();
        }

        public void Hide()
        {
            _callView.Hide();
        }

        public void ApplySnapshot(ConnectionSnapshot snapshot)
        {
            _statusView.SetSnapshot(snapshot);
            _callView.SetMuteAvailable(snapshot.MediaMode == MediaMode.AudioOnly);
        }

        public void ClearTransientMedia()
        {
            StopRemoteAudio();
            _callView.ClearRemoteVideo();
        }

        private void WireApplicationEvents()
        {
            _connectionFlow.OnRemoteAudioTrackAvailable += HandleRemoteAudioTrackAvailable;
            _connectionFlow.OnRemoteVideoTextureAvailable += HandleRemoteVideoTextureAvailable;
            _connectionFlow.OnChatMessageReceived += HandleChatMessageReceived;
            _connectionFlow.OnRemoteSpeakingChanged += HandleRemoteSpeakingChanged;
            _mediaCapture.OnLocalVideoPreviewChanged += HandleLocalVideoPreviewChanged;
        }

        private void WireViewEvents()
        {
            _callView.OnHangup += HandleHangup;
            _callView.OnPushToTalkChanged += HandlePushToTalkChanged;
            _callView.OnToggleVideoChanged += HandleToggleVideoChanged;
            _callView.OnSendMessage += HandleSendMessage;
            _callView.OnChatVisibilityChanged += HandleChatVisibilityChanged;
        }

        private async void HandleHangup()
        {
            try
            {
                await _connectionFlow.HangupAsync(_appToken);
            }
            catch (OperationCanceledException) { }
        }

        private void HandlePushToTalkChanged(bool isPressed)
        {
            _connectionFlow.SetMicMuted(!isPressed);
            _connectionFlow.SendSpeakingState(isPressed);
        }

        private void HandleToggleVideoChanged(bool enabled)
        {
            _manualVideoEnabled = enabled;
            ApplyEffectiveVideoState();
        }

        private void HandleSendMessage(string text)
        {
            if (_connectionFlow.SendChatMessage(text))
                _callView.AppendLocalMessage(text);
        }

        private void HandleChatVisibilityChanged(bool isChatVisible)
        {
            _isChatVisible = isChatVisible;
            ApplyEffectiveVideoState();
        }

        private void HandleRemoteAudioTrackAvailable(AudioStreamTrack track)
        {
            if (_remoteAudioSource == null)
            {
                Debug.LogWarning("[CallUiController] remoteAudioSource not wired - remote audio will not play.");
                return;
            }

            _remoteAudioSource.Stop();
            _remoteAudioSource.SetTrack(track);
            _remoteAudioSource.loop = true;
            _remoteAudioSource.Play();
        }

        private void HandleChatMessageReceived(string senderId, string text) =>
            _callView.AppendRemoteMessage(text);

        private void HandleLocalVideoPreviewChanged(Texture texture) =>
            _callView.SetLocalVideoTexture(texture);

        private void HandleRemoteVideoTextureAvailable(Texture texture) =>
            _callView.SetRemoteVideoTexture(texture);

        private void HandleRemoteSpeakingChanged(bool speaking) =>
            _callView.SetRemoteSpeaking(speaking);

        private void StopRemoteAudio()
        {
            if (_remoteAudioSource == null || !_remoteAudioSource.isPlaying) return;
            _remoteAudioSource.Stop();
        }

        private void ApplyEffectiveVideoState()
        {
            bool enabled = _manualVideoEnabled && !_isChatVisible;
            _connectionFlow.SetVideoEnabled(enabled);
            _callView.SetLocalVideoEnabled(enabled);
        }
    }
}
