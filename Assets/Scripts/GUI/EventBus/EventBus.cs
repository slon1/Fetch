using System;
using System.Collections.Generic;
using UnityEditor;


public class EventBus : IDisposable {
	
	private static readonly Lazy<EventBus> lazy = new Lazy<EventBus>(() => new EventBus());
	public static EventBus Instance => lazy.Value;

	
	private readonly Dictionary<EventId, Delegate> eventHandlers = new();

	
	public void AddListener(EventId eventId, Action handler) {
		if (!eventHandlers.ContainsKey(eventId)) {
			eventHandlers[eventId] = null;
		}
		eventHandlers[eventId] = (Action)eventHandlers[eventId] + handler;
	}

	public void AddListener<T>(EventId eventId, Action<T> handler) {
		if (!eventHandlers.ContainsKey(eventId)) {
			eventHandlers[eventId] = null;
		}
		eventHandlers[eventId] = (Action<T>)eventHandlers[eventId] + handler;
	}

	
	public void RemoveListener(EventId eventId, Action handler) {
		if (eventHandlers.TryGetValue(eventId, out var existingHandler) && 
		    existingHandler is Action action) {
			var newHandler = action - handler;
			if (newHandler == null) {
				eventHandlers.Remove(eventId);
			} else {
				eventHandlers[eventId] = newHandler;
			}
		}
	}

	
	public void RemoveListener<T>(EventId eventId, Action<T> handler) {
		if (eventHandlers.TryGetValue(eventId, out var existingHandler) && 
		    existingHandler is Action<T> action) {
			var newHandler = action - handler;
			if (newHandler == null) {
				eventHandlers.Remove(eventId);
			} else {
				eventHandlers[eventId] = newHandler;
			}
		}
	}

	
	public void Invoke(EventId eventId) {
		if (eventHandlers.TryGetValue(eventId, out var handler) && handler is Action action) {
			action.Invoke();
		}
	}

	
	public void Invoke<T>(EventId eventId, T param) {
		if (eventHandlers.TryGetValue(eventId, out var handler) && handler is Action<T> action) {
			action.Invoke(param);
		}
	}

	
	public void RemoveAllListeners(EventId eventId) {
		if (eventHandlers.ContainsKey(eventId)) {
			eventHandlers[eventId] = null;
		}
	}

	
	public void Dispose() {
		var keys = new List<EventId>(eventHandlers.Keys);
		foreach (var key in keys) {
			eventHandlers[key] = null;
		}
		eventHandlers.Clear();
	}

	
#if UNITY_EDITOR
	[InitializeOnLoadMethod]
	private static void InitializeEditorSupport() {
		EditorApplication.playModeStateChanged += state => {
			if (state == PlayModeStateChange.ExitingPlayMode) {
				Instance.Dispose();
			}
		};
	}
#endif
}
