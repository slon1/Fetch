using TMPro;
using UnityEngine;

public class CreateScr : ScrAbs
{
    [SerializeField] private TMP_InputField roomName;
	public string RoomName => roomName.text;
	
}
