using Unity.WebRTC;
using UnityEngine;

namespace WebRtcV2.Transport
{
    /// <summary>
    /// Pushes microphone samples from Unity's audio thread into a WebRTC AudioStreamTrack.
    ///
    /// This avoids coupling network transmission to AudioSource.mute.
    /// The AudioSource can remain muted locally to prevent sidetone, while samples are still
    /// forwarded via SetData on the audio thread.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class MicrophoneAudioSender : MonoBehaviour
    {
        private AudioStreamTrack _track;
        private int _sampleRate;

        public void Initialize(AudioStreamTrack track, int sampleRate)
        {
            _track = track;
            _sampleRate = sampleRate;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (_track == null || data == null || data.Length == 0) return;
            if (channels <= 0 || _sampleRate <= 0) return;

            _track.SetData(data, channels, _sampleRate);
        }

        private void OnDestroy()
        {
            _track = null;
        }
    }
}
