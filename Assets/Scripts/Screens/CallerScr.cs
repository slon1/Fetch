using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WebRtcV2.Shared;

namespace WebRtcV2.Presentation
{
    public sealed class CallerScr : MonoBehaviour
    {
        [Header("Root")]
        [SerializeField] private RectTransform root;

        [Header("Dial Pad")]
        [SerializeField] private List<Button> digBtn;
        [SerializeField] private RectTransform digRoot;

        [Header("Actions")]
        [SerializeField] private Button callBtn;
        [SerializeField] private Button hangupBtn;
        [SerializeField] private Button delBtn;

        [Header("Display")]
        [SerializeField] private TMP_Text displayLbl;

        private readonly char[] _digitsByButtonIndex = { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
        private string _dialedNumber = string.Empty;
        private Mode _mode;

        public event Action<int> OnDigitRequested;
        public event Action OnDeleteRequested;
        public event Action<string> OnDialRequested;
        public event Action OnAcceptRequested;
        public event Action OnRejectRequested;
        public event Action OnHangupRequested;

        private enum Mode
        {
            Hidden,
            Idle,
            OutgoingRinging,
            IncomingRinging,
            Connecting,
        }

        private void Awake()
        {
            WireDigitButtons();

            if (callBtn != null)
                callBtn.onClick.AddListener(HandlePrimaryAction);

            if (hangupBtn != null)
                hangupBtn.onClick.AddListener(HandleSecondaryAction);

            if (delBtn != null)
                delBtn.onClick.AddListener(HandleDeleteClicked);
        }

        public void Show()
        {
            if (root != null)
                root.gameObject.SetActive(true);
        }

        public void Hide()
        {
            _mode = Mode.Hidden;
            if (root != null)
                root.gameObject.SetActive(false);
        }

        public void ShowIdle(bool clearNumber = false)
        {
            Show();
            _mode = Mode.Idle;

            if (clearNumber)
                _dialedNumber = string.Empty;

            SetDialPadVisible(true);
            SetButtonVisible(callBtn, true);
            SetButtonVisible(hangupBtn, false);
            SetButtonVisible(delBtn, true);
            RefreshIdleButtons();
            RefreshDisplay(_dialedNumber);
        }

        public void ShowOutgoingRinging(string targetNumber)
        {
            Show();
            _mode = Mode.OutgoingRinging;
            SetDialPadVisible(false);
            SetButtonVisible(callBtn, false);
            SetButtonVisible(delBtn, false);
            SetButtonVisible(hangupBtn, true);
            RefreshDisplay(targetNumber);
        }

        public void ShowIncomingRinging(string callerNumber)
        {
            Show();
            _mode = Mode.IncomingRinging;
            SetDialPadVisible(false);
            SetButtonVisible(callBtn, true);
            SetButtonVisible(hangupBtn, true);
            SetButtonVisible(delBtn, false);
            RefreshDisplay(callerNumber);
        }

        public void ShowConnecting(string peerNumber)
        {
            Show();
            _mode = Mode.Connecting;
            SetDialPadVisible(false);
            SetButtonVisible(callBtn, false);
            SetButtonVisible(delBtn, false);
            SetButtonVisible(hangupBtn, true);
            RefreshDisplay(peerNumber);
        }

        public string CurrentDialedNumber => _dialedNumber;

        public void ClearDialedNumber()
        {
            _dialedNumber = string.Empty;
            if (_mode == Mode.Idle)
            {
                RefreshDisplay(_dialedNumber);
                RefreshIdleButtons();
            }
        }

        private void WireDigitButtons()
        {
            if (digBtn == null)
                return;

            for (int i = 0; i < digBtn.Count; i++)
            {
                Button button = digBtn[i];
                if (button == null)
                    continue;

                int capturedIndex = i;
                button.onClick.AddListener(() => HandleDigitClicked(capturedIndex));
            }
        }

        private void HandleDigitClicked(int index)
        {
            if (_mode != Mode.Idle)
                return;
            if (index < 0 || index >= _digitsByButtonIndex.Length)
                return;
            if (_dialedNumber.Length >= LocalClientIdentity.BoothNumberLength)
                return;

            _dialedNumber += _digitsByButtonIndex[index];
            OnDigitRequested?.Invoke(index);
            RefreshDisplay(_dialedNumber);
            RefreshIdleButtons();
        }

        private void HandleDeleteClicked()
        {
            if (_mode != Mode.Idle || string.IsNullOrEmpty(_dialedNumber))
                return;

            _dialedNumber = _dialedNumber.Substring(0, _dialedNumber.Length - 1);
            OnDeleteRequested?.Invoke();
            RefreshDisplay(_dialedNumber);
            RefreshIdleButtons();
        }

        private void HandlePrimaryAction()
        {
            switch (_mode)
            {
                case Mode.Idle:
                    if (_dialedNumber.Length == LocalClientIdentity.BoothNumberLength)
                        OnDialRequested?.Invoke(_dialedNumber);
                    break;
                case Mode.IncomingRinging:
                    OnAcceptRequested?.Invoke();
                    break;
            }
        }

        private void HandleSecondaryAction()
        {
            switch (_mode)
            {
                case Mode.OutgoingRinging:
                case Mode.Connecting:
                    OnHangupRequested?.Invoke();
                    break;
                case Mode.IncomingRinging:
                    OnRejectRequested?.Invoke();
                    break;
            }
        }

        private void RefreshIdleButtons()
        {
            if (_mode != Mode.Idle)
                return;

            if (callBtn != null)
                callBtn.interactable = _dialedNumber.Length == LocalClientIdentity.BoothNumberLength;

            if (delBtn != null)
                delBtn.interactable = _dialedNumber.Length > 0;
        }

        private void RefreshDisplay(string value)
        {
            if (displayLbl == null)
                return;

            displayLbl.text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private void SetDialPadVisible(bool visible)
        {
            if (digRoot != null)
                digRoot.gameObject.SetActive(visible);
        }

        private static void SetButtonVisible(Button button, bool visible)
        {
            if (button == null)
                return;

            button.gameObject.SetActive(visible);
            button.interactable = visible;
        }
    }
}

