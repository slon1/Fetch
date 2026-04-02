using System;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace WebRtcV2.Presentation
{
    public class LobbyScreenView : MonoBehaviour
    {
        [Header("Root")]
        [FormerlySerializedAs("root")]
        [SerializeField] private GameObject screenRoot;

        [Header("Booth Input")]
        [FormerlySerializedAs("displayNameInput")]
        [SerializeField] private TMP_InputField dialNumberInput;

        [Header("Actions")]
        [FormerlySerializedAs("createButton")]
        [SerializeField] private Button primaryActionButton;

        [FormerlySerializedAs("refreshButton")]
        [SerializeField] private Button secondaryActionButton;

        [Header("Status")]
        [FormerlySerializedAs("loadingLabel")]
        [SerializeField] private TMP_Text statusText;

        private TMP_Text _primaryActionLabel;
        private TMP_Text _secondaryActionLabel;
        private Mode _mode;

        public event Action<string> OnDialRequested;
        public event Action OnAcceptRequested;
        public event Action OnRejectRequested;

        private enum Mode
        {
            Initializing,
            Idle,
            OutgoingRinging,
            IncomingRinging,
            Connecting,
        }

        private void Awake()
        {
            _primaryActionLabel = primaryActionButton != null
                ? primaryActionButton.GetComponentInChildren<TMP_Text>(true)
                : null;
            _secondaryActionLabel = secondaryActionButton != null
                ? secondaryActionButton.GetComponentInChildren<TMP_Text>(true)
                : null;

            if (primaryActionButton != null)
                primaryActionButton.onClick.AddListener(HandlePrimaryActionClicked);
            if (secondaryActionButton != null)
                secondaryActionButton.onClick.AddListener(HandleSecondaryActionClicked);
        }

        public void Show()
        {
            if (screenRoot != null)
                screenRoot.SetActive(true);
        }

        public void Hide()
        {
            if (screenRoot != null)
                screenRoot.SetActive(false);
        }

        public void ShowInitializing(string message)
        {
            _mode = Mode.Initializing;
            SetDialInputVisible(false);
            SetPrimaryAction(false, string.Empty);
            SetSecondaryAction(false, string.Empty);
            SetStatus(message);
        }

        public void ShowIdle(string boothNumber, string message = null)
        {
            _mode = Mode.Idle;
            SetDialInputVisible(true);
            SetPrimaryAction(true, "Call");
            SetSecondaryAction(false, string.Empty);
            SetStatus(BuildIdleStatus(boothNumber, message));
        }

        public void ShowOutgoingRinging(string boothNumber, string targetNumber)
        {
            _mode = Mode.OutgoingRinging;
            SetDialInputVisible(false);
            SetPrimaryAction(false, string.Empty);
            SetSecondaryAction(true, "Cancel");
            SetStatus($"Your number: {SafeNumber(boothNumber)}\nOutgoing call\nTo: {SafeNumber(targetNumber)}");
        }

        public void ShowIncomingCall(string boothNumber, string callerNumber)
        {
            _mode = Mode.IncomingRinging;
            SetDialInputVisible(false);
            SetPrimaryAction(true, "Accept");
            SetSecondaryAction(true, "Reject");
            SetStatus($"Your number: {SafeNumber(boothNumber)}\nIncoming call\nFrom: {SafeNumber(callerNumber)}");
        }

        public void ShowConnecting(string boothNumber, string peerNumber)
        {
            _mode = Mode.Connecting;
            SetDialInputVisible(false);
            SetPrimaryAction(false, string.Empty);
            SetSecondaryAction(false, string.Empty);
            SetStatus($"Your number: {SafeNumber(boothNumber)}\nConnecting...\nPeer: {SafeNumber(peerNumber)}");
        }

        public void SetBusy(bool isBusy)
        {
            if (_mode == Mode.Idle && primaryActionButton != null)
                primaryActionButton.interactable = !isBusy;
        }

        private void HandlePrimaryActionClicked()
        {
            switch (_mode)
            {
                case Mode.Idle:
                    string number = dialNumberInput != null ? dialNumberInput.text?.Trim() : string.Empty;
                    if (!string.IsNullOrWhiteSpace(number))
                        OnDialRequested?.Invoke(number);
                    break;
                case Mode.IncomingRinging:
                    OnAcceptRequested?.Invoke();
                    break;
            }
        }

        private void HandleSecondaryActionClicked()
        {
            switch (_mode)
            {
                case Mode.OutgoingRinging:
                case Mode.IncomingRinging:
                    OnRejectRequested?.Invoke();
                    break;
            }
        }

        private void SetDialInputVisible(bool visible)
        {
            if (dialNumberInput == null)
                return;

            dialNumberInput.gameObject.SetActive(visible);
            if (!visible)
                return;

            dialNumberInput.text = string.Empty;
            dialNumberInput.ActivateInputField();
        }

        private void SetPrimaryAction(bool visible, string label)
        {
            if (primaryActionButton != null)
            {
                primaryActionButton.gameObject.SetActive(visible);
                primaryActionButton.interactable = visible;
            }

            if (_primaryActionLabel != null)
                _primaryActionLabel.text = label;
        }

        private void SetSecondaryAction(bool visible, string label)
        {
            if (secondaryActionButton != null)
            {
                secondaryActionButton.gameObject.SetActive(visible);
                secondaryActionButton.interactable = visible;
            }

            if (_secondaryActionLabel != null)
                _secondaryActionLabel.text = label;
        }

        private void SetStatus(string message)
        {
            if (statusText == null)
                return;

            statusText.text = message ?? string.Empty;
            statusText.gameObject.SetActive(true);
        }

        private static string BuildIdleStatus(string boothNumber, string message)
        {
            string baseText = $"Your number: {SafeNumber(boothNumber)}\nEnter a booth number to call";
            return string.IsNullOrWhiteSpace(message)
                ? baseText
                : baseText + "\n" + message.Trim();
        }

        private static string SafeNumber(string number) => string.IsNullOrWhiteSpace(number) ? "-" : number.Trim();
    }
}
