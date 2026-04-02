using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WebRtcV2.CrashHandling
{
    public sealed class FatalErrorView
    {
        private Canvas _canvas;
        private Text _titleText;
        private Text _summaryText;
        private Text _detailsText;
        private Text _errorCodeText;

        public void Show(CrashReport report)
        {
            EnsureView();

            _titleText.text = "Что-то пошло не так";
            _summaryText.text = string.IsNullOrWhiteSpace(report?.message)
                ? "Приложение остановлено из-за ошибки."
                : report.message;

            _detailsText.text =
                $"Устройство: {report?.deviceModel}\n" +
                $"Платформа: {report?.platform}\n" +
                $"Android API: {report?.apiLevel}\n" +
                $"Версия: {report?.appVersion}\n" +
                $"Стадия: {report?.startupStage}";

            _errorCodeText.text = $"Код ошибки: {report?.errorCode}";
            _canvas.gameObject.SetActive(true);
        }

        private void EnsureView()
        {
            if (_canvas != null)
                return;

            EnsureEventSystem();

            Font font = LoadFont();
            var root = new GameObject("FatalErrorCanvas");
            UnityEngine.Object.DontDestroyOnLoad(root);

            _canvas = root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = short.MaxValue;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();

            RectTransform panel = CreatePanel(root.transform, new Color32(10, 14, 22, 245));
            RectTransform card = CreateCenteredCard(panel, new Color32(28, 33, 44, 255));

            var layout = card.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(48, 48, 48, 48);
            layout.spacing = 18;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;

            _titleText = CreateText(card, font, string.Empty, 42, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white, 90f);
            CreateText(card, font, "Приложение остановлено, чтобы не остаться в поврежденном состоянии.", 24, FontStyle.Normal, TextAnchor.MiddleCenter, new Color32(220, 225, 232, 255), 80f);
            _summaryText = CreateText(card, font, string.Empty, 28, FontStyle.Bold, TextAnchor.MiddleCenter, new Color32(255, 214, 179, 255), 100f);
            _errorCodeText = CreateText(card, font, string.Empty, 26, FontStyle.Bold, TextAnchor.MiddleCenter, new Color32(255, 242, 170, 255), 60f);
            _detailsText = CreateText(card, font, string.Empty, 22, FontStyle.Normal, TextAnchor.UpperLeft, new Color32(210, 216, 224, 255), 160f);

            var spacer = new GameObject("Spacer");
            spacer.transform.SetParent(card, false);
            var spacerLayout = spacer.AddComponent<LayoutElement>();
            spacerLayout.flexibleHeight = 1f;
            spacerLayout.minHeight = 16f;

            RectTransform buttonRect = CreateButton(card, font, "Выход", ExitApplication);
            var buttonLayout = buttonRect.gameObject.AddComponent<LayoutElement>();
            buttonLayout.minHeight = 110f;
            buttonLayout.minWidth = 360f;
            buttonLayout.preferredHeight = 110f;
            buttonLayout.preferredWidth = 360f;

            _canvas.gameObject.SetActive(false);
        }

        private static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null)
                return;

            var eventSystem = new GameObject("FatalErrorEventSystem");
            UnityEngine.Object.DontDestroyOnLoad(eventSystem);
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();
        }

        private static RectTransform CreatePanel(Transform parent, Color color)
        {
            var go = new GameObject("FatalErrorPanel", typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = color;
            return rect;
        }

        private static RectTransform CreateCenteredCard(Transform parent, Color color)
        {
            var go = new GameObject("FatalErrorCard", typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.08f, 0.1f);
            rect.anchorMax = new Vector2(0.92f, 0.9f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = color;
            return rect;
        }

        private static Text CreateText(
            Transform parent,
            Font font,
            string content,
            int size,
            FontStyle style,
            TextAnchor alignment,
            Color color,
            float minHeight)
        {
            var go = new GameObject($"Text-{size}", typeof(Text));
            go.transform.SetParent(parent, false);

            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = minHeight;
            layout.preferredHeight = minHeight;

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var text = go.GetComponent<Text>();
            text.font = font;
            text.text = content;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return text;
        }

        private static RectTransform CreateButton(Transform parent, Font font, string label, UnityEngine.Events.UnityAction action)
        {
            var go = new GameObject("FatalExitButton", typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(420f, 110f);

            var image = go.GetComponent<Image>();
            image.color = new Color32(210, 80, 80, 255);

            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);

            CreateText(go.transform, font, label, 30, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white, 64f);
            return rect;
        }

        private static Font LoadFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return font != null ? font : Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private static void ExitApplication()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            UnityEngine.Application.Quit();
#endif
        }
    }
}
