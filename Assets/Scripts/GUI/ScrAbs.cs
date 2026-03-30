using DG.Tweening;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using UnityEngine.UI;

public abstract class ScrAbs : MonoBehaviour, IPage {
	[SerializeField]
	protected List<EventButton> EventButtons;
	[SerializeField]
	protected GameObject root;
	[SerializeField]
	protected PanelId panelId;
	private CanvasGroup canvas;

	public GameObject Root => root;

	public PanelId PanelID => panelId;




	public bool Static;
	public bool IsStatic() {
		return Static;
	}
	private Dictionary<ButtonId, Button> dict;

	private void Awake() {
		canvas = root.GetComponent<CanvasGroup>();
		if (!canvas) {
			canvas = root.AddComponent<CanvasGroup>();
		}
	}

	public virtual void Start() {
		EventButtons.ForEach(button => button.InitEvent());
		dict = EventButtons.ToDictionary(x => x.ButtonId, x => x.Button);

	}
	public Button GetButton(ButtonId button) {
		return dict[button];
	}

	protected void RaiseMenuClick(ButtonId buttonId) {
		EventBus.Instance.Invoke(EventId.MenuEvent, buttonId);
	}

	protected void RaiseEventParam<T>(EventId eventId, T param) {
		EventBus.Instance.Invoke(eventId, param);
	}

	public virtual void Hide() {
		if (root != null) {
			canvas.blocksRaycasts = false; // Отключаем интерактивность сразу
			canvas.DOFade(0, 0.5f);
		}

	}

	public virtual void Show() {
		if (root != null) {
			canvas.DOFade(1, 0.5f).OnComplete(() => {
				canvas.blocksRaycasts = true;
			});
		}
	}

	public bool IsVisible() {
		return root.activeInHierarchy;
	}

	public virtual void Execute<T>(PageActionId action, T param) {

	}
	private void OnDestroy() {
		if (canvas != null) {
			DOTween.Kill(canvas);
		}		
		
	}

	public virtual void Execute(PageActionId action) {
		
	}
#if UNITY_EDITOR
	private void OnApplicationQuit() {
		DOTween.Kill(canvas);
		canvas = null;
	}	
#endif
}



