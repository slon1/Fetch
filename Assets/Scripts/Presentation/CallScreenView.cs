using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WebRtcV2.Presentation
{
    /// <summary>
    /// View for the active call screen: hangup, push-to-talk, camera controls, and data-channel chat.
    /// Fires events and updates local UI only.
    /// </summary>
    public class CallScreenView : MonoBehaviour
    {
        [Header("Root")]
        [SerializeField] private GameObject root;
        [SerializeField] private GameObject videoRoot;
        [SerializeField] private GameObject chatRoot;

        [Header("Controls")]
        [SerializeField] private Button hangupButton;
        [SerializeField] private Button muteButton;
        [SerializeField] private TMP_Text muteButtonLabel;
        [SerializeField] private Button videoToggleButton;
        [SerializeField] private TMP_Text videoToggleLabel;
        [SerializeField] private Button rotateLeftButton;
        [SerializeField] private Button rotateRightButton;
        [SerializeField] private Button showVideoTabButton;
        [SerializeField] private Button showChatTabButton;

        [Header("Remote Speaking")]
        [SerializeField] private GameObject remoteSpeakingIndicator;
        [SerializeField] private TMP_Text remoteSpeakingLabel;

        [Header("Video")]
        [SerializeField] private RawImage localVideo;
        [SerializeField] private RawImage remoteVideo;

        [Header("Chat")]
        [SerializeField] private TMP_InputField chatInput;
        [SerializeField] private Button sendButton;
        [SerializeField] private TMP_Text chatLog;

        public event Action OnHangup;
        public event Action<bool> OnPushToTalkChanged;
        public event Action<bool> OnToggleVideoChanged;
        public event Action<string> OnSendMessage;
        public event Action<bool> OnChatVisibilityChanged;

        private bool _isPushToTalkActive;
        private bool _isLocalVideoEnabled = true;
        private bool _isChatVisible;
        private int _manualLocalRotationSteps;

        private void Awake()
        {
            if (hangupButton != null)
                hangupButton.onClick.AddListener(() => OnHangup?.Invoke());

            if (sendButton != null)
                sendButton.onClick.AddListener(HandleSendClicked);

            if (chatInput != null)
                chatInput.onSubmit.AddListener(_ => HandleSendClicked());

            if (muteButton != null)
                InstallPushToTalkHandlers(muteButton.gameObject);

            if (videoToggleButton != null)
                videoToggleButton.onClick.AddListener(HandleVideoToggleClicked);

            if (rotateLeftButton != null)
                rotateLeftButton.onClick.AddListener(() => RotateLocalPreview(-1));

            if (rotateRightButton != null)
                rotateRightButton.onClick.AddListener(() => RotateLocalPreview(1));

            if (showVideoTabButton != null)
                showVideoTabButton.onClick.AddListener(() => SetChatVisible(false, notify: true));

            if (showChatTabButton != null)
                showChatTabButton.onClick.AddListener(() => SetChatVisible(true, notify: true));
        }

        private void OnDisable()
        {
            ReleasePushToTalk();
        }

        private void LateUpdate()
        {
            UpdateVideoPresentation(localVideo, isLocal: true);
            UpdateVideoPresentation(remoteVideo, isLocal: false);
        }

        public void Show()
        {
            root.SetActive(true);
            ResetState();
        }

        public void Hide()
        {
            ReleasePushToTalk();
            root.SetActive(false);
        }

        public void AppendLocalMessage(string text) =>
            AppendLine($"<color=#aaffaa>You:</color> {EscapeTmp(text)}");

        public void AppendRemoteMessage(string text) =>
            AppendLine($"<color=#88ccff>Remote:</color> {EscapeTmp(text)}");

        /// <summary>
        /// Enables or disables push-to-talk based on whether audio can be controlled.
        /// </summary>
        public void SetMuteAvailable(bool available)
        {
            if (!available)
                ReleasePushToTalk();

            if (muteButton != null)
                muteButton.interactable = available;

            UpdatePushToTalkLabel(available);
        }

        public void SetRemoteSpeaking(bool speaking)
        {
            if (remoteSpeakingIndicator != null)
                remoteSpeakingIndicator.SetActive(speaking);

            if (remoteSpeakingLabel != null)
                remoteSpeakingLabel.text = speaking ? "Peer is talking..." : string.Empty;
        }

        public void SetLocalVideoTexture(Texture texture)
        {
            if (localVideo == null) return;
            localVideo.texture = texture;
            localVideo.enabled = _isLocalVideoEnabled && texture != null;
            UpdateVideoPresentation(localVideo, isLocal: true);
        }

        public void SetRemoteVideoTexture(Texture texture)
        {
            if (remoteVideo == null) return;
            remoteVideo.texture = texture;
            remoteVideo.enabled = texture != null;
            UpdateVideoPresentation(remoteVideo, isLocal: false);
        }

        public void ClearRemoteVideo()
        {
            if (remoteVideo == null) return;
            remoteVideo.texture = null;
            remoteVideo.enabled = false;
        }

        public void SetLocalVideoEnabled(bool enabled)
        {
            _isLocalVideoEnabled = enabled;
            if (localVideo != null)
                localVideo.enabled = enabled && localVideo.texture != null;
            UpdateVideoToggleLabel();
        }

        private void ResetState()
        {
            _isPushToTalkActive = false;
            _isLocalVideoEnabled = true;
            _isChatVisible = false;
            _manualLocalRotationSteps = 0;
            SetMuteAvailable(true);
            SetLocalVideoEnabled(true);
            SetChatVisible(false, notify: false);
            SetRemoteSpeaking(false);
            ClearRemoteVideo();
            if (chatLog != null) chatLog.text = string.Empty;
            if (chatInput != null) chatInput.text = string.Empty;
        }

        public void SetChatVisible(bool visible) => SetChatVisible(visible, notify: false);

        private void InstallPushToTalkHandlers(GameObject target)
        {
            var trigger = target.GetComponent<EventTrigger>();
            if (trigger == null)
                trigger = target.AddComponent<EventTrigger>();

            if (trigger.triggers == null)
                trigger.triggers = new List<EventTrigger.Entry>();

            AddTrigger(trigger, EventTriggerType.PointerDown, _ => PressPushToTalk());
            AddTrigger(trigger, EventTriggerType.PointerUp, _ => ReleasePushToTalk());
            AddTrigger(trigger, EventTriggerType.PointerExit, _ => ReleasePushToTalk());
        }

        private static void AddTrigger(
            EventTrigger trigger,
            EventTriggerType eventType,
            Action<BaseEventData> action)
        {
            var entry = new EventTrigger.Entry { eventID = eventType };
            entry.callback.AddListener(data => action(data));
            trigger.triggers.Add(entry);
        }

        private void PressPushToTalk()
        {
            if (muteButton == null || !muteButton.interactable) return;
            if (_isPushToTalkActive) return;

            _isPushToTalkActive = true;
            UpdatePushToTalkLabel(true);
            OnPushToTalkChanged?.Invoke(true);
        }

        private void ReleasePushToTalk()
        {
            if (!_isPushToTalkActive) return;

            _isPushToTalkActive = false;
            UpdatePushToTalkLabel(muteButton == null || muteButton.interactable);
            OnPushToTalkChanged?.Invoke(false);
        }

        private void HandleVideoToggleClicked()
        {
            SetLocalVideoEnabled(!_isLocalVideoEnabled);
            OnToggleVideoChanged?.Invoke(_isLocalVideoEnabled);
        }

        private void RotateLocalPreview(int deltaSteps)
        {
            _manualLocalRotationSteps = NormalizeQuarterTurns(_manualLocalRotationSteps + deltaSteps);
            UpdateVideoPresentation(localVideo, isLocal: true);
        }

        private void HandleSendClicked()
        {
            if (chatInput == null) return;

            string text = chatInput.text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            chatInput.text = string.Empty;
            chatInput.ActivateInputField();
            OnSendMessage?.Invoke(text);
        }

        private void UpdatePushToTalkLabel(bool available)
        {
            if (muteButtonLabel == null) return;

            muteButtonLabel.text = !available
                ? "Audio Off"
                : _isPushToTalkActive ? "Talking..." : "Hold to Talk";
        }

        private void UpdateVideoToggleLabel()
        {
            if (videoToggleLabel == null) return;
            videoToggleLabel.text = _isLocalVideoEnabled ? "Camera On" : "Camera Off";
        }

        private void SetChatVisible(bool visible, bool notify)
        {
            _isChatVisible = visible;

            if (videoRoot != null)
                videoRoot.SetActive(!visible);

            if (chatRoot != null)
                chatRoot.SetActive(visible);

            if (showVideoTabButton != null)
                showVideoTabButton.interactable = visible;

            if (showChatTabButton != null)
                showChatTabButton.interactable = !visible;

            if (notify)
                OnChatVisibilityChanged?.Invoke(visible);
        }

        private void AppendLine(string line)
        {
            if (chatLog == null) return;
            chatLog.text = string.IsNullOrEmpty(chatLog.text)
                ? line
                : chatLog.text + "\n" + line;
        }

        private void UpdateVideoPresentation(RawImage image, bool isLocal)
        {
            if (image == null) return;

            if (image.texture == null)
            {
                image.uvRect = new Rect(0f, 0f, 1f, 1f);
                image.rectTransform.localRotation = Quaternion.identity;
                return;
            }

            var fitter = image.GetComponent<AspectRatioFitter>();
            if (fitter != null)
                fitter.enabled = false;

            int rotation = 0;
            bool flipX = false;
            bool flipY = false;

            if (isLocal)
            {
                rotation = _manualLocalRotationSteps * 90;
                flipX = true;

                if (image.texture is WebCamTexture webCamTexture)
                {
                    rotation += webCamTexture.videoRotationAngle;
                    flipY = webCamTexture.videoVerticallyMirrored;
                }
            }

            ApplyCoverUv(image, rotation, flipX, flipY);
            image.rectTransform.localRotation = Quaternion.Euler(0f, 0f, rotation);
        }

        private static void ApplyCoverUv(RawImage image, int rotationDegrees, bool flipX, bool flipY)
        {
            float sourceWidth = image.texture.width;
            float sourceHeight = image.texture.height;
            if (sourceWidth <= 0f || sourceHeight <= 0f) return;

            if (Mathf.Abs(rotationDegrees % 180) == 90)
            {
                (sourceWidth, sourceHeight) = (sourceHeight, sourceWidth);
            }

            var rect = image.rectTransform.rect;
            if (rect.width <= 0f || rect.height <= 0f) return;

            float sourceAspect = sourceWidth / sourceHeight;
            float targetAspect = rect.width / rect.height;

            float uvX = 0f;
            float uvY = 0f;
            float uvWidth = 1f;
            float uvHeight = 1f;

            if (sourceAspect > targetAspect)
            {
                uvWidth = targetAspect / sourceAspect;
                uvX = (1f - uvWidth) * 0.5f;
            }
            else if (sourceAspect < targetAspect)
            {
                uvHeight = sourceAspect / targetAspect;
                uvY = (1f - uvHeight) * 0.5f;
            }

            if (flipX)
            {
                uvX += uvWidth;
                uvWidth = -uvWidth;
            }

            if (flipY)
            {
                uvY += uvHeight;
                uvHeight = -uvHeight;
            }

            image.uvRect = new Rect(uvX, uvY, uvWidth, uvHeight);
        }

        private static int NormalizeQuarterTurns(int steps)
        {
            steps %= 4;
            if (steps < 0) steps += 4;
            return steps;
        }

        private static string EscapeTmp(string text) =>
            text?.Replace("<", "\u003c").Replace(">", "\u003e") ?? string.Empty;
    }
}
