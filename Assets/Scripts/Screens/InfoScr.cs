using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace WebRtcV2.Presentation
{
    public sealed class InfoScr : MonoBehaviour
    {
        [SerializeField] private RectTransform root;
        [SerializeField] private TMP_Text infoText;
        [SerializeField] private TMP_Text warningText;
        [FormerlySerializedAs("ErrorText")]
        [SerializeField] private TMP_Text errorText;
        [SerializeField] private Image flag1;
        [SerializeField] private Image flag2;
        [SerializeField] private Image flag3;

        public void Show()
        {
            if (root != null)
                root.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (root != null)
                root.gameObject.SetActive(false);
        }

        public void SetInfo(string value) => SetText(infoText, value);
        public void SetWarning(string value) => SetText(warningText, value);
        public void SetError(string value) => SetText(errorText, value);

        public void SetPeerSpeaking(bool active) => SetFlag(flag1, active);
        public void SetConnected(bool active) => SetFlag(flag2, active);
        public void SetDirectRoute(bool active) => SetFlag(flag3, active);

        public void ResetFlags()
        {
            SetPeerSpeaking(false);
            SetConnected(false);
            SetDirectRoute(false);
        }

        private static void SetText(TMP_Text target, string value)
        {
            if (target == null)
                return;

            bool hasText = !string.IsNullOrWhiteSpace(value);
            target.text = hasText ? value.Trim() : string.Empty;
            target.gameObject.SetActive(hasText);
        }

        private static void SetFlag(Image target, bool active)
        {
            if (target == null)
                return;

            target.enabled = active;
        }
    }
}
