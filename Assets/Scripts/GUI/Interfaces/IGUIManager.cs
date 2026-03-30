using NUnit.Framework;
using System.Collections.Generic;

public interface IGUIManager {	
	public void Initialize();
	public void ShowPanel(PanelId panelId);
	public void ShowPanelModal(PanelId panelId, bool show);	
	public void Execute<T>(PanelId  panelId, PageActionId action, T param );
	public void Execute(PanelId panelId, PageActionId action);
	
	
};
