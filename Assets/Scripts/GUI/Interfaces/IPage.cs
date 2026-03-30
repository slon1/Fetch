using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface IPage {
	public void Show();
	public void Hide();
	void Execute<T>(PageActionId action, T param );
	void Execute(PageActionId action);
	public GameObject Root { get; }
	public PanelId PanelID { get; }	

	public bool IsStatic();
	public bool IsVisible();
}
