using DG.Tweening;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TMPro;
using UnityEngine;

public class GUIManager : MonoBehaviour, IGUIManager {


	private Dictionary<PanelId, IPage> panels;

	[SerializeField] private ScrAbs[] screens; // Назначается в инспекторе Unity

	private bool muteAudio=false;
	private bool muteVideo = false;
	// История открытых экранов для реализации кнопки Back



	private void Start() {
		Initialize();
	}

	public void Initialize() {
		// Проверяем, что экраны назначены в инспекторе
		if (screens == null || screens.Length == 0) {
			Debug.LogError("Screens array is not assigned in inspector!");
			return;
		}
		
		// Создаем словарь панелей из назначенных экранов
		panels = screens.Where(screen => screen != null)
		                .ToDictionary(panel => panel.PanelID, panel => (IPage)panel);
		
		EventBus.Instance.AddListener<ButtonId>(EventId.MenuEvent, OnMenuEvent);
		
		
		// Инициализируем историю с главным меню

		ShowPanel(PanelId.roomSelector);
	}
	
	
	private void OnMenuEvent(ButtonId id) {

		switch (id) {
			case ButtonId.CreateRoomEnter:
				ShowPanel(PanelId.create);
				break;
			case ButtonId.CreateRoomName:
				var roomName = (panels[PanelId.create] as CreateScr).RoomName;
				ShowPanel(PanelId.wait);
				CreateRoom(roomName);
				break;
			case ButtonId.Refresh:
				var roomselectorScr = panels[PanelId.roomSelector] as RoomSelectorScr;
				var roomListContainer = roomselectorScr.RoomListContainer;
				var roomItemPrefab= roomselectorScr.RoomItemPrefab;
				RefreshRate(roomListContainer, roomItemPrefab); ;
				break;
			case ButtonId.Mute:
				MuteAudio(muteAudio);
				break;
			case ButtonId.Video:
				MuteVideo(muteVideo);
				break;
			case ButtonId.Chat:
				ShowPanel(PanelId.roomChat);
				break;
			case ButtonId.Hangup:
				break;			
			case ButtonId.RoomSelector:
				ShowPanel( PanelId.roomSelector);
				break;
			case ButtonId.ChatSend:
				var chatScr = panels[PanelId.roomChat] as ChatScr;
				var text = chatScr.text;
				SendText(text);
				break;
			default:
				break;
		}

	}

	public void SetText (string text) {
		var chatScr = panels[PanelId.roomChat] as ChatScr;
		chatScr.SetText(text);
	}
	private void SendText(string text) {
		throw new NotImplementedException();
	}

	private void MuteVideo(bool muteVideo) {
		muteVideo = !muteVideo;
		throw new NotImplementedException();
	}

	private void MuteAudio(bool muteAudio) {
		muteAudio=!muteAudio;
		throw new NotImplementedException();
	}

	private void RefreshRate(Transform roomListContainer, GameObject roomItemPrefab) {
		throw new NotImplementedException();
	}

	private void CreateRoom(string roomName) {
		
		throw new NotImplementedException();
		
	}

	public void ShowPanelModal(PanelId panelId, bool show) {
		if (!panels.ContainsKey(panelId)) {
			Debug.LogError($"Panel {panelId} not found!");
			return;
		}
		
		if (show) {
			panels[panelId].Show();
		}
		else {
			panels[panelId].Hide();
		}
	}
	public void ShowPanel(PanelId panelId) {
		
		if (!panels.ContainsKey(panelId)) {
			Debug.LogError($"Panel {panelId} not found!");
			return;
		}
		
		// Сначала скрываем все нестатические панели
		foreach (var panel in panels.Values) {
			if (!panel.IsStatic()) {
				panel.Hide();
			}
		}	
		
		panels[panelId].Show();	
	
	}

	private void OnDestroy() {
		panels.Clear();
		panels = null;
		EventBus.Instance.RemoveListener<ButtonId>(EventId.MenuEvent, OnMenuEvent);
		
	
	}

	public void Execute<T>(PanelId panelId, PageActionId action, T param) {
		if (!panels.ContainsKey(panelId)) {
			Debug.LogError($"Panel {panelId} not found!");
			return;
		}
		panels[panelId].Execute(action, param);
	}

	public void Execute(PanelId panelId, PageActionId action) {
		if (!panels.ContainsKey(panelId)) {
			Debug.LogError($"Panel {panelId} not found!");
			return;
		}
		panels[panelId].Execute(action);
	}

	
}
