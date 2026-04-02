using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using WebRtcV2.Shared;

namespace WebRtcV2.Transport
{
    /// <summary>
    /// Prepares local media tracks and local preview sources.
    /// Audio and video are cached and reused across sessions until the service is disposed.
    /// Video is normalized once at capture time so the same oriented feed is used both
    /// for the local preview and for the outgoing WebRTC track.
    /// </summary>
    public class MediaCaptureService : IDisposable
    {
        private readonly Transform _parent;
        private readonly ConnectionDiagnostics _diagnostics;

        private AudioStreamTrack _audioTrack;
        private VideoStreamTrack _videoTrack;
        private AudioSource _localMicSource;
        private AudioClip _micClip;
        private string _micDeviceName;
        private WebCamTexture _webCamTexture;
        private Texture2D _normalizedVideoTexture;
        private CancellationTokenSource _videoCopyCts;
        private Color32[] _sourcePixels;
        private Color32[] _normalizedPixels;
        private int _sourceRotationDegrees;
        private bool _sourceHorizontalMirror;
        private bool _sourceVerticalMirror;
        private bool _selectedCameraIsFrontFacing;
        private bool _disposed;
        private bool _micMuted = true;
        private bool _videoEnabled = true;

        public event Action<Texture> OnLocalVideoPreviewChanged;

        public bool IsVideoEnabled => _videoEnabled;

        public MediaCaptureService(Transform parent, ConnectionDiagnostics diagnostics)
        {
            _parent = parent;
            _diagnostics = diagnostics;
        }

        /// <summary>
        /// Starts the microphone and returns a ready <see cref="AudioStreamTrack"/>.
        /// Calling this multiple times returns the cached track.
        /// </summary>
        public async UniTask<AudioStreamTrack> GetAudioTrackAsync(CancellationToken ct = default)
        {
            if (_audioTrack != null)
            {
                _audioTrack.Enabled = !_micMuted;
                if (_localMicSource != null)
                    _localMicSource.mute = _micMuted;
                return _audioTrack;
            }

            string[] mics = Microphone.devices;
            if (mics.Length == 0)
            {
                _diagnostics.LogWarning("MediaCapture", "No microphone found - using silent audio track");
                _audioTrack = new AudioStreamTrack();
                _audioTrack.Enabled = !_micMuted;
                return _audioTrack;
            }

            _micDeviceName = mics[0];
            const int sampleRate = 48000;
            const int bufferSec = 1;

            _micClip = Microphone.Start(_micDeviceName, true, bufferSec, sampleRate);

            try
            {
                await UniTask.WaitUntil(
                    () => Microphone.GetPosition(_micDeviceName) > 0,
                    cancellationToken: ct);
            }
            catch (OperationCanceledException)
            {
                Microphone.End(_micDeviceName);
                throw;
            }

            var go = new GameObject("LocalMicSource");
            if (_parent != null)
                go.transform.SetParent(_parent);

            _localMicSource = go.AddComponent<AudioSource>();
            _localMicSource.loop = true;
            _localMicSource.clip = _micClip;
            _localMicSource.mute = _micMuted;
            _localMicSource.Play();

            _audioTrack = new AudioStreamTrack(_localMicSource)
            {
                Enabled = !_micMuted
            };

            _diagnostics.LogIce("MediaCapture", $"Microphone started: {_micDeviceName}");
            return _audioTrack;
        }

        /// <summary>
        /// Starts the local camera and returns a ready <see cref="VideoStreamTrack"/>.
        /// Calling this multiple times returns the cached track.
        /// </summary>
        public async UniTask<VideoStreamTrack> GetVideoTrackAsync(CancellationToken ct = default)
        {
            if (_videoTrack != null)
            {
                _videoTrack.Enabled = _videoEnabled;
                if (_normalizedVideoTexture != null)
                    OnLocalVideoPreviewChanged?.Invoke(_normalizedVideoTexture);
                return _videoTrack;
            }

            CheckGraphicsApiForVideoReceive();

            var devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
            {
                _diagnostics.LogWarning("MediaCapture", "No camera found - video disabled");
                return null;
            }

            WebCamDevice selectedDevice = SelectCameraDevice(devices);
            _selectedCameraIsFrontFacing = selectedDevice.isFrontFacing;
            const int requestedWidth = 640;
            const int requestedHeight = 480;

            _webCamTexture = new WebCamTexture(selectedDevice.name, requestedWidth, requestedHeight);
            _webCamTexture.Play();

            try
            {
                await UniTask.WaitUntil(
                    () => _webCamTexture != null &&
                          _webCamTexture.didUpdateThisFrame &&
                          _webCamTexture.width > 16 &&
                          _webCamTexture.height > 16,
                    cancellationToken: ct);
            }
            catch (OperationCanceledException)
            {
                _webCamTexture?.Stop();
                _webCamTexture = null;
                throw;
            }

            InitializeNormalizedVideoFeed();

            _videoTrack = new VideoStreamTrack(_normalizedVideoTexture)
            {
                Enabled = _videoEnabled
            };

            _videoCopyCts = new CancellationTokenSource();
            CopyVideoFramesAsync(_videoCopyCts.Token).Forget();

            OnLocalVideoPreviewChanged?.Invoke(_normalizedVideoTexture);
            _diagnostics.LogIce(
                "MediaCapture",
                $"Camera started: {selectedDevice.name} | source={_webCamTexture.width}x{_webCamTexture.height} rotation={_sourceRotationDegrees} mirroredX={_sourceHorizontalMirror} mirroredY={_sourceVerticalMirror} frontFacing={_selectedCameraIsFrontFacing}");
            return _videoTrack;
        }

        public void SetMicMuted(bool muted)
        {
            _micMuted = muted;

            if (_audioTrack != null)
                _audioTrack.Enabled = !muted;

            if (_localMicSource != null)
                _localMicSource.mute = _micMuted;
        }

        public void SetVideoEnabled(bool enabled)
        {
            _videoEnabled = enabled;

            if (_videoTrack != null)
                _videoTrack.Enabled = enabled;
        }

        /// <summary>
        /// Disables the audio track at the WebRTC layer without renegotiation.
        /// </summary>
        public void DisableAudioTrack()
        {
            _micMuted = true;
            if (_audioTrack != null)
                _audioTrack.Enabled = false;
            if (_localMicSource != null)
                _localMicSource.mute = _micMuted;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _videoCopyCts?.Cancel();
            _videoCopyCts?.Dispose();
            _videoCopyCts = null;

            _webCamTexture?.Stop();
            _webCamTexture = null;
            _sourcePixels = null;
            _normalizedPixels = null;

            if (_normalizedVideoTexture != null)
            {
                UnityEngine.Object.Destroy(_normalizedVideoTexture);
                _normalizedVideoTexture = null;
            }

            _videoTrack?.Dispose();
            _videoTrack = null;

            _audioTrack?.Dispose();
            _audioTrack = null;

            if (!string.IsNullOrEmpty(_micDeviceName))
            {
                Microphone.End(_micDeviceName);
                _micDeviceName = null;
            }

            _micClip = null;

            if (_localMicSource != null)
            {
                _localMicSource.Stop();
                UnityEngine.Object.Destroy(_localMicSource.gameObject);
                _localMicSource = null;
            }
        }

        private void InitializeNormalizedVideoFeed()
        {
            if (_webCamTexture == null)
                throw new InvalidOperationException("camera-not-started");

            RefreshSourceTransform();

            int sourceWidth = _webCamTexture.width;
            int sourceHeight = _webCamTexture.height;
            bool swapAxes = _sourceRotationDegrees == 90 || _sourceRotationDegrees == 270;
            int outputWidth = swapAxes ? sourceHeight : sourceWidth;
            int outputHeight = swapAxes ? sourceWidth : sourceHeight;

            GraphicsFormat format = WebRTC.GetSupportedGraphicsFormat(SystemInfo.graphicsDeviceType);
            _normalizedVideoTexture = new Texture2D(outputWidth, outputHeight, format, TextureCreationFlags.None);
            _normalizedPixels = new Color32[outputWidth * outputHeight];
            _sourcePixels = new Color32[sourceWidth * sourceHeight];

            NormalizeFrameCpu();
        }

        private async UniTaskVoid CopyVideoFramesAsync(CancellationToken ct)
        {
            for (int i = 0; i < 10 && !ct.IsCancellationRequested; i++)
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);

            float warmupSeconds = 0.25f;
            while (warmupSeconds > 0f && !ct.IsCancellationRequested && _webCamTexture != null)
            {
                warmupSeconds -= Time.deltaTime;
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }

            while (!ct.IsCancellationRequested && _webCamTexture != null && _normalizedVideoTexture != null)
            {
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);

                if (!_videoEnabled || !_webCamTexture.didUpdateThisFrame)
                    continue;

                if (RefreshSourceTransform())
                    RebuildNormalizedTextureIfNeeded();

                NormalizeFrameCpu();
            }
        }

        private bool RefreshSourceTransform()
        {
            if (_webCamTexture == null)
                return false;

            int rotation = NormalizeRotation(_webCamTexture.videoRotationAngle);
            bool mirroredX = UnityEngine.Application.isMobilePlatform && _selectedCameraIsFrontFacing;
            bool mirroredY = _webCamTexture.videoVerticallyMirrored;
            bool changed = rotation != _sourceRotationDegrees ||
                           mirroredX != _sourceHorizontalMirror ||
                           mirroredY != _sourceVerticalMirror;

            _sourceRotationDegrees = rotation;
            _sourceHorizontalMirror = mirroredX;
            _sourceVerticalMirror = mirroredY;
            return changed;
        }

        private void RebuildNormalizedTextureIfNeeded()
        {
            if (_webCamTexture == null || _normalizedVideoTexture == null)
                return;

            int sourceWidth = _webCamTexture.width;
            int sourceHeight = _webCamTexture.height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return;

            bool swapAxes = _sourceRotationDegrees == 90 || _sourceRotationDegrees == 270;
            int outputWidth = swapAxes ? sourceHeight : sourceWidth;
            int outputHeight = swapAxes ? sourceWidth : sourceHeight;

            if (_normalizedVideoTexture.width == outputWidth && _normalizedVideoTexture.height == outputHeight)
            {
                _diagnostics.LogIce(
                    "MediaCapture",
                    $"Camera transform changed: rotation={_sourceRotationDegrees} mirroredX={_sourceHorizontalMirror} mirroredY={_sourceVerticalMirror}");
                return;
            }

            _diagnostics.LogWarning(
                "MediaCapture",
                $"Camera aspect changed after track start: current={_normalizedVideoTexture.width}x{_normalizedVideoTexture.height} new={outputWidth}x{outputHeight}. Keeping current track texture until next session.");
        }

        private void NormalizeFrameCpu()
        {
            if (_webCamTexture == null || _normalizedVideoTexture == null)
                return;

            int sourceWidth = _webCamTexture.width;
            int sourceHeight = _webCamTexture.height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return;

            if (_sourcePixels == null || _sourcePixels.Length != sourceWidth * sourceHeight)
                _sourcePixels = new Color32[sourceWidth * sourceHeight];

            int expectedOutputLength = _normalizedVideoTexture.width * _normalizedVideoTexture.height;
            if (_normalizedPixels == null || _normalizedPixels.Length != expectedOutputLength)
                _normalizedPixels = new Color32[expectedOutputLength];

            _webCamTexture.GetPixels32(_sourcePixels);

            for (int srcY = 0; srcY < sourceHeight; srcY++)
            {
                for (int srcX = 0; srcX < sourceWidth; srcX++)
                {
                    int transformedX = _sourceHorizontalMirror ? (sourceWidth - 1 - srcX) : srcX;
                    int transformedY = _sourceVerticalMirror ? (sourceHeight - 1 - srcY) : srcY;
                    int sourceIndex = srcY * sourceWidth + srcX;
                    int destinationIndex = GetDestinationIndex(transformedX, transformedY, sourceWidth, sourceHeight, _sourceRotationDegrees);
                    _normalizedPixels[destinationIndex] = _sourcePixels[sourceIndex];
                }
            }

            _normalizedVideoTexture.SetPixels32(_normalizedPixels);
            _normalizedVideoTexture.Apply(false, false);
        }

        private int GetDestinationIndex(int srcX, int srcY, int sourceWidth, int sourceHeight, int rotationDegrees)
        {
            int outputWidth = _normalizedVideoTexture.width;
            int outputHeight = _normalizedVideoTexture.height;
            int dstX;
            int dstY;

            switch (rotationDegrees)
            {
                case 90:
                    dstX = sourceHeight - 1 - srcY;
                    dstY = srcX;
                    break;
                case 180:
                    dstX = sourceWidth - 1 - srcX;
                    dstY = sourceHeight - 1 - srcY;
                    break;
                case 270:
                    dstX = srcY;
                    dstY = sourceWidth - 1 - srcX;
                    break;
                default:
                    dstX = srcX;
                    dstY = srcY;
                    break;
            }

            dstX = Mathf.Clamp(dstX, 0, outputWidth - 1);
            dstY = Mathf.Clamp(dstY, 0, outputHeight - 1);
            return dstY * outputWidth + dstX;
        }

        private static int NormalizeRotation(int rotationDegrees)
        {
            rotationDegrees %= 360;
            if (rotationDegrees < 0)
                rotationDegrees += 360;

            if (rotationDegrees < 45) return 0;
            if (rotationDegrees < 135) return 90;
            if (rotationDegrees < 225) return 180;
            if (rotationDegrees < 315) return 270;
            return 0;
        }

        private static WebCamDevice SelectCameraDevice(WebCamDevice[] devices)
        {
            if (UnityEngine.Application.isMobilePlatform)
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    if (devices[i].isFrontFacing)
                        return devices[i];
                }
            }

            return devices[0];
        }

        private void CheckGraphicsApiForVideoReceive()
        {
            var api = SystemInfo.graphicsDeviceType;
            var platform = UnityEngine.Application.platform;
            bool isWinOrMac = platform == RuntimePlatform.WindowsEditor ||
                              platform == RuntimePlatform.WindowsPlayer ||
                              platform == RuntimePlatform.OSXEditor ||
                              platform == RuntimePlatform.OSXPlayer;
            bool isOpenGl = api == UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore ||
                            api == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3;
            if (isWinOrMac && isOpenGl)
            {
                _diagnostics.LogWarning(
                    "WebRTC",
                    "Video receive may not work with OpenGL on Windows/macOS. Use Direct3D11, Vulkan, or Metal.");
            }
        }
    }
}
