using System;
using System.Collections.Generic;
using RTS.Data;

namespace RTS.Core.Events
{
	/// <summary>
	/// 游戏内事件总线：解耦输入层（UserController）与表现层（面板/特效/队列）。
	/// 主线程专用；模拟线程 → 主线程仍走 SimEventQueue（确定性锁步边界不变）。
	/// 面板各自订阅自己关心的事件，自行节流刷新，不再被控制器直接调用。
	/// </summary>
	public static class GameEventBus
	{
		// 当前选中集合（总线缓存：面板 10fps 刷新、路径线绘制直接读这里）
		public static List<IEntity> CurrentSelection { get; private set; } = new();

		/// <summary>选中变化：selection 为快照，localTeamId 用于路径线/颜色归属。</summary>
		public static event Action<List<IEntity>, int> SelectionChanged;

		/// <summary>动作按钮被点击（ActionPanel → UserController 路由）。</summary>
		public static event Action<string> ActionTriggered;

		public static void PublishSelectionChanged(List<IEntity> selection, int localTeamId)
		{
			CurrentSelection = selection != null
				? new List<IEntity>(selection)
				: new List<IEntity>();
			SelectionChanged?.Invoke(CurrentSelection, localTeamId);
		}

		public static void PublishActionTriggered(string actionId)
		{
			ActionTriggered?.Invoke(actionId);
		}

	}
}
