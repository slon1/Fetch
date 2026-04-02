using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WebRtcV2.Presentation
{
    public sealed class ChatScr : MonoBehaviour
    {
        [SerializeField] private RectTransform root;
        [SerializeField] private TMP_Text chatOut;
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private Button send;
        [SerializeField] private Button back2Video;

        public event Action<string> OnSendMessageRequested;
        public event Action OnBackToVideoRequested;

        private void Awake()
        {
            if (send != null)
                send.onClick.AddListener(HandleSendClicked);

            if (back2Video != null)
                back2Video.onClick.AddListener(() => OnBackToVideoRequested?.Invoke());

            if (inputField != null)
                inputField.onSubmit.AddListener(_ => HandleSendClicked());
        }

        public void Show()
        {
            if (root != null)
                root.gameObject.SetActive(true);
            inputField?.ActivateInputField();
        }

        public void Hide()
        {
            if (root != null)
                root.gameObject.SetActive(false);
        }

        public void ClearLog()
        {
            if (chatOut != null)
                chatOut.text = string.Empty;
        }

        public void ClearInput()
        {
            if (inputField != null)
                inputField.text = string.Empty;
        }

        public void AppendLocalMessage(string text) =>
            AppendLine($"<color=#aaffaa>You:</color> {EscapeTmp(text)}");

        public void AppendRemoteMessage(string text) =>
            AppendLine($"<color=#88ccff>Remote:</color> {EscapeTmp(text)}");

        private void HandleSendClicked()
        {
            if (inputField == null)
                return;

            string text = inputField.text?.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            inputField.text = string.Empty;
            inputField.ActivateInputField();
            OnSendMessageRequested?.Invoke(text);
        }

        private void AppendLine(string line)
        {
            if (chatOut == null)
                return;

            chatOut.text = string.IsNullOrEmpty(chatOut.text)
                ? line
                : chatOut.text + "\n" + line;
        }

        private static string EscapeTmp(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text.Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
