using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace WebRtcV2.Presentation
{
    public sealed class VideoScr : MonoBehaviour
    {
        [SerializeField] private RectTransform root;
        [FormerlySerializedAs("RemoteScr")]
        [SerializeField] private RawImage remoteScr;
        [FormerlySerializedAs("LocalScr")]
        [SerializeField] private RawImage localScr;
        [SerializeField] private TMP_Text audioLbl;
        [SerializeField] private Button videoBtn;
        [SerializeField] private Button audioBtn;
        [SerializeField] private Button chatBtn;
        [SerializeField] private Button hangUpBtn;

        private bool _isPushToTalkActive;
        private bool _isLocalVideoEnabled = true;

        public event Action OnHangupRequested;
        public event Action<bool> OnPushToTalkChanged;
        public event Action<bool> OnToggleVideoRequested;
        public event Action OnOpenChatRequested;

        private void Awake()
        {
            if (hangUpBtn != null)
                hangUpBtn.onClick.AddListener(() => OnHangupRequested?.Invoke());

            if (videoBtn != null)
                videoBtn.onClick.AddListener(HandleVideoToggleClicked);

            if (chatBtn != null)
                chatBtn.onClick.AddListener(() => OnOpenChatRequested?.Invoke());

            if (audioBtn != null)
                InstallPushToTalkHandlers(audioBtn.gameObject);
        }

        private void OnDisable()
        {
            ReleasePushToTalk();
        }

        private void LateUpdate()
        {
            UpdateVideoPresentation(localScr, isLocal: true);
            UpdateVideoPresentation(remoteScr, isLocal: false);
        }

        public void Show()
        {
            if (root != null)
                root.gameObject.SetActive(true);
        }

        public void Hide()
        {
            ReleasePushToTalk();
            if (root != null)
                root.gameObject.SetActive(false);
        }

        public void SetMuteAvailable(bool available)
        {
            if (!available)
                ReleasePushToTalk();

            if (audioBtn != null)
                audioBtn.interactable = available;

            UpdatePushToTalkLabel(available);
        }

        public void SetLocalVideoEnabled(bool enabled)
        {
            _isLocalVideoEnabled = enabled;

            if (localScr != null)
                localScr.enabled = enabled && localScr.texture != null;
        }

        public void SetLocalVideoTexture(Texture texture)
        {
            if (localScr == null)
                return;

            localScr.texture = texture;
            localScr.enabled = _isLocalVideoEnabled && texture != null;
            UpdateVideoPresentation(localScr, isLocal: true);
        }

        public void SetRemoteVideoTexture(Texture texture)
        {
            if (remoteScr == null)
                return;

            remoteScr.texture = texture;
            remoteScr.enabled = texture != null;
            UpdateVideoPresentation(remoteScr, isLocal: false);
        }

        public void ClearRemoteVideo()
        {
            if (remoteScr == null)
                return;

            remoteScr.texture = null;
            remoteScr.enabled = false;
        }

        private void InstallPushToTalkHandlers(GameObject target)
        {
            var trigger = target.GetComponent<EventTrigger>();
            if (trigger == null)
                trigger = target.AddComponent<EventTrigger>();

            trigger.triggers ??= new List<EventTrigger.Entry>();
            AddTrigger(trigger, EventTriggerType.PointerDown, _ => PressPushToTalk());
            AddTrigger(trigger, EventTriggerType.PointerUp, _ => ReleasePushToTalk());
            AddTrigger(trigger, EventTriggerType.PointerExit, _ => ReleasePushToTalk());
        }

        private static void AddTrigger(EventTrigger trigger, EventTriggerType eventType, Action<BaseEventData> action)
        {
            var entry = new EventTrigger.Entry { eventID = eventType };
            entry.callback.AddListener(data => action(data));
            trigger.triggers.Add(entry);
        }

        private void PressPushToTalk()
        {
            if (audioBtn == null || !audioBtn.interactable || _isPushToTalkActive)
                return;

            _isPushToTalkActive = true;
            UpdatePushToTalkLabel(true);
            OnPushToTalkChanged?.Invoke(true);
        }

        private void ReleasePushToTalk()
        {
            if (!_isPushToTalkActive)
                return;

            _isPushToTalkActive = false;
            UpdatePushToTalkLabel(audioBtn == null || audioBtn.interactable);
            OnPushToTalkChanged?.Invoke(false);
        }

        private void HandleVideoToggleClicked()
        {
            _isLocalVideoEnabled = !_isLocalVideoEnabled;
            SetLocalVideoEnabled(_isLocalVideoEnabled);
            OnToggleVideoRequested?.Invoke(_isLocalVideoEnabled);
        }

        private void UpdatePushToTalkLabel(bool available)
        {
            if (audioLbl == null)
                return;

            audioLbl.text = !available
                ? "Audio Off"
                : _isPushToTalkActive ? "Talking..." : "Hold to Talk";
        }

        private static void UpdateVideoPresentation(RawImage image, bool isLocal)
        {
            if (image == null)
                return;

            if (image.texture == null)
            {
                image.uvRect = new Rect(0f, 0f, 1f, 1f);
                image.rectTransform.localRotation = Quaternion.identity;
                ResetAspectRatio(image);
                return;
            }

            int rotation = 0;
            bool flipX = false;
            bool flipY = false;

            if (isLocal && image.texture is WebCamTexture webCamTexture)
            {
                rotation = NormalizeRotation(webCamTexture.videoRotationAngle);
                flipX = true;
                flipY = webCamTexture.videoVerticallyMirrored;
            }

            ApplyAspectRatio(image, rotation, isLocal);
            image.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -rotation);
            image.uvRect = new Rect(
                flipX ? 1f : 0f,
                flipY ? 1f : 0f,
                flipX ? -1f : 1f,
                flipY ? -1f : 1f);
        }

        private static void ApplyAspectRatio(RawImage image, int rotation, bool isLocal)
        {
            if (image == null || image.texture == null)
                return;

            float width = image.texture.width;
            float height = image.texture.height;
            if (width <= 0f || height <= 0f)
                return;

            if (Mathf.Abs(rotation % 180) == 90)
            {
                float temp = width;
                width = height;
                height = temp;
            }

            var fitter = image.GetComponent<AspectRatioFitter>();
            if (fitter == null)
                fitter = image.gameObject.AddComponent<AspectRatioFitter>();

            fitter.enabled = true;
            fitter.aspectMode = isLocal
                ? AspectRatioFitter.AspectMode.EnvelopeParent
                : AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = width / height;
        }

        private static void ResetAspectRatio(RawImage image)
        {
            if (image == null)
                return;

            var fitter = image.GetComponent<AspectRatioFitter>();
            if (fitter != null)
                fitter.enabled = false;
        }

        private static int NormalizeRotation(int degrees)
        {
            int normalized = degrees % 360;
            if (normalized < 0)
                normalized += 360;
            return normalized;
        }
    }
}




