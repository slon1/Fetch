using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WebRtcV2.Application.Room;

namespace WebRtcV2.Presentation
{
    /// <summary>
    /// View for the lobby screen. In iteration 3 it is driven by auto-lobby state,
    /// while keeping the same inspector wiring and room item prefab setup.
    /// </summary>
    public class LobbyScreenView : MonoBehaviour
    {
        [Header("Root")]
        [SerializeField] private GameObject root;

        [Header("Inputs")]
        [SerializeField] private TMP_InputField displayNameInput;

        [Header("Buttons")]
        [SerializeField] private Button createButton;
        [SerializeField] private Button refreshButton;

        [Header("Room List")]
        [SerializeField] private Transform roomListContainer;
        [SerializeField] private RoomListItemView roomItemPrefab;

        [Header("State")]
        [SerializeField] private TMP_Text loadingLabel;

        public event Action<string> OnCreateRoom;
        public event Action OnRefreshRooms;
        public event Action<RoomModel> OnRoomSelected;

        private readonly List<RoomListItemView> _items = new List<RoomListItemView>();

        private void Awake()
        {
            if (createButton != null)
                createButton.onClick.AddListener(HandleCreateClicked);
            if (refreshButton != null)
                refreshButton.onClick.AddListener(HandleRefreshClicked);
        }

        public void Show() => root.SetActive(true);

        public void Hide() => root.SetActive(false);

        public void ShowLoadingState(string message)
        {
            SetAutoLobbyChrome(showRefresh: false);
            RenderRooms(Array.Empty<RoomModel>());
            SetLoading(true, message);
        }

        public void ShowJoinableRooms(RoomModel[] rooms)
        {
            SetAutoLobbyChrome(showRefresh: true);
            SetLoading(false, rooms != null && rooms.Length > 0
                ? "Выберите комнату"
                : "Свободных комнат нет");
            RenderRooms(rooms);
        }

        public void ShowWaitingRoom(RoomModel room)
        {
            SetAutoLobbyChrome(showRefresh: false);
            RenderRooms(Array.Empty<RoomModel>());
            string roomName = room?.DisplayName ?? "Комната";
            SetLoading(false, $"Ожидание собеседника\n{roomName}");
        }

        public void SetLoading(bool loading) => SetLoading(loading, loading ? "Загрузка..." : null);

        public void RenderRooms(RoomModel[] rooms)
        {
            ClearRoomItems();
            if (rooms == null) return;

            foreach (var room in rooms)
            {
                var item = Instantiate(roomItemPrefab, roomListContainer);
                item.Bind(room);
                item.OnJoinClicked += r => OnRoomSelected?.Invoke(r);
                _items.Add(item);
            }
        }

        private void SetAutoLobbyChrome(bool showRefresh)
        {
            if (displayNameInput != null)
                displayNameInput.gameObject.SetActive(false);
            if (createButton != null)
                createButton.gameObject.SetActive(false);
            if (refreshButton != null)
                refreshButton.gameObject.SetActive(showRefresh);
        }

        private void SetLoading(bool loading, string message)
        {
            if (refreshButton != null)
                refreshButton.interactable = !loading;

            foreach (var item in _items)
                item.SetInteractable(!loading);

            if (loadingLabel != null)
            {
                loadingLabel.text = message ?? string.Empty;
                loadingLabel.gameObject.SetActive(!string.IsNullOrWhiteSpace(message));
            }
        }

        private void HandleCreateClicked()
        {
            string name = displayNameInput != null ? displayNameInput.text?.Trim() : string.Empty;
            if (string.IsNullOrEmpty(name)) return;
            OnCreateRoom?.Invoke(name);
        }

        private void HandleRefreshClicked() => OnRefreshRooms?.Invoke();

        private void ClearRoomItems()
        {
            foreach (var item in _items)
            {
                if (item != null) Destroy(item.gameObject);
            }
            _items.Clear();
        }
    }
}
