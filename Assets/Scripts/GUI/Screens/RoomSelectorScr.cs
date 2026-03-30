using UnityEngine;
using UnityEngine.UI;

public class RoomSelectorScr: ScrAbs
{
	[SerializeField] private Transform roomListContainer;
	[SerializeField] private GameObject roomItemPrefab;
	public Transform RoomListContainer=>roomListContainer;
	public GameObject RoomItemPrefab=>roomItemPrefab;

}
