using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WebRtcV2.Application.Room;

namespace WebRtcV2.Presentation
{
    /// <summary>
    /// Displays a single lobby room entry and fires a join event when the user clicks Join.
    /// Attach to Assets/Prefabs/RoomBtn.prefab (or equivalent room list item prefab).
    /// </summary>
    public class RoomListItemView : MonoBehaviour
    {
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private Button joinButton;

        private RoomModel _room;

        /// <summary>Fired with the bound room when the user clicks Join.</summary>
        public event Action<RoomModel> OnJoinClicked;

        private void Awake()
        {
            joinButton.onClick.AddListener(() => OnJoinClicked?.Invoke(_room));
        }

        /// <summary>Binds this item to a room model. Call after Instantiate.</summary>
        public void Bind(RoomModel room)
        {
            _room = room;
            if (nameText != null)
                nameText.text = room?.DisplayName ?? string.Empty;
        }

        public void SetInteractable(bool interactable) =>
            joinButton.interactable = interactable;
    }
}
