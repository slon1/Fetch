using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ChatScr : ScrAbs
{
	[SerializeField] private TMP_Text chatContext;
    
    [SerializeField] private TMP_InputField inputField;
    public string text => inputField.text;

	public override void Start() {
		base.Start();
	
		
	}

	public void SetText (string text) {
        chatContext.text = text;
    }
}
