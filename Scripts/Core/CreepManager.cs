using Godot;
using RTS.Data;
using System.Collections.Generic;

namespace RTS.World
{
	[GlobalClass]
	public partial class CreepManager : Node2D
	{
		public static CreepManager Instance { get; private set; }

		// 保留此委托定义以防止 FogOfWar.cs 报错，但我们不再用它进行瞬间越权渲染了
		public System.Func<Vector2I, bool> IsCellVisibleToPlayer;

		[Export] public TileMapLayer CreepMapLayer { get; set; }
		[Export] public Godot.Collections.Dictionary<int, Vector2I> CreepAtlasMap { get; set; } = new();
		[Export] public int SourceId { get; set; } = 0;

		// 核心状态分离：底层真实菌毯与屏幕可见菌毯快照
		private Dictionary<Vector2I, CreepType> _logicCreepState = new Dictionary<Vector2I, CreepType>();
		private Dictionary<Vector2I, CreepType> _drawnCreepState = new Dictionary<Vector2I, CreepType>();
		// 菌毯变化事件可能在模拟线程触发（CreepSource 铺毯），与主线程 RevealCells 并发，
		// 必须加锁，否则 Dictionary 并发读写偶发异常会把模拟线程打死（整局时好时坏）
		private readonly object _creepStateLock = new();

		public override void _EnterTree()
		{
			Instance = this;
		}

		public override void _Ready()
		{
			// 伪 3D：菌毯 TileMap 只作逻辑缓存，不显示 2D 图层
			if (CreepMapLayer != null)
				CreepMapLayer.Visible = false;

			// SimManager 提前初始化，可在 _Ready 中安全绑定事件，
			// 从而捕获第 1 帧建筑铺设中心菌毯的事件
			if (RTS.Core.SimManager.Instance?.World?.CreepGrid != null)
			{
				RTS.Core.SimManager.Instance.World.CreepGrid.OnCreepChanged += UpdateCreepVisual;
			}
		}

		public override void _ExitTree()
		{
			if (RTS.Core.SimManager.Instance?.World?.CreepGrid != null)
			{
				RTS.Core.SimManager.Instance.World.CreepGrid.OnCreepChanged -= UpdateCreepVisual;
			}
		}

		private void UpdateCreepVisual(int x, int y, CreepType newType)
		{
			var cell = new Vector2I(x, y);

			// 仅记录底层状态，不直接贴图；绘画统一交给 RevealCells
			lock (_creepStateLock)
				_logicCreepState[cell] = newType;
		}

		// 由 FogOfWar 每帧调用：只有被迷雾扫描到的格子才刷新菌毯
		public void RevealCells(HashSet<Vector2I> visibleCells)
		{
			if (CreepMapLayer == null) return;
			// 全图没有菌毯时直接跳过逐格扫描（全图视野压测时是热点）
			lock (_creepStateLock)
			{
				if (_logicCreepState.Count == 0)
					return;
			}

			foreach (var cell in visibleCells)
			{
				CreepType realType;
				CreepType drawnType;
				lock (_creepStateLock)
				{
					_logicCreepState.TryGetValue(cell, out realType);
					_drawnCreepState.TryGetValue(cell, out drawnType);
				}

				// 只要这个格子被照亮了，且它的“真实状态”和“上一次画的状态”不一样，就立刻刷新！
				// 黑雾中的菌毯不显示，直到玩家视野扫过才显现
				if (realType != drawnType)
				{
					DrawCreepCell(cell, realType);
				}
			}
		}

		// 真正执行贴图的私有方法
		private void DrawCreepCell(Vector2I cell, CreepType type)
		{
			lock (_creepStateLock)
			lock (_creepStateLock)
			_drawnCreepState[cell] = type;

			if (CreepAtlasMap.TryGetValue((int)type, out Vector2I atlasCoords))
			{
				CreepMapLayer.SetCell(cell, SourceId, atlasCoords);
			}
			else
			{
				CreepMapLayer.SetCell(cell, -1);
			}
		}

		// =========================================================
		// 表现层与逻辑层的桥接代码
		// =========================================================

		public void AddCreep(Vector2I cell, CreepType type)
		{
			AddCreep(cell, type, 0);
		}

		public void AddCreep(Vector2I cell, CreepType type, int ownerTeam)
		{
			RTS.Core.SimManager.Instance.World.CreepGrid?.AddCreep(cell.X, cell.Y, type, ownerTeam);
		}

		public void RemoveCreep(Vector2I cell, CreepType type)
		{
			RTS.Core.SimManager.Instance.World.CreepGrid?.RemoveCreep(cell.X, cell.Y, type);
		}

		public CreepType GetActiveCreep(Vector2I cell)
		{
			return RTS.Core.SimManager.Instance.World.CreepGrid?.GetActiveCreep(cell.X, cell.Y) ?? CreepType.None;
		}
	}
}
