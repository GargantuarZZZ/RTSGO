using Godot;
using System.Collections.Generic;
using RTS.World;
using RTS.Data;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Units
{
	[GlobalClass]
	public partial class CreepSource : Node
	{
		[Export] public float MaxRadius = 6.0f;
		[Export] public float SpreadTime = 1.5f;
		[Export] public CreepType GeneratedCreep = CreepType.NanoCreep;

		private Structure _owner;
		private FP _currentRadius = FP.Zero;
		private FP _timer = FP.Zero;
		private FP _maxRadiusFP;
		private FP _spreadTimeFP;
		private readonly HashSet<Vector2I> _coveredCells = new();
		private bool _isInitialized = false;

		public void Initialize(Structure owner)
		{
			_owner = owner;
			_maxRadiusFP = (FP)MaxRadius;
			_spreadTimeFP = (FP)SpreadTime;
			// 严禁在 Initialize 里铺毯：此时建筑还没 SnapAndRegister，坐标未对齐
		}

		public void Tick(double delta)
		{
			if (_owner == null || _owner.IsDeadOrNull() || _currentRadius >= _maxRadiusFP)
				return;

			// 只有完成状态才铺毯（蓝图/建造中都不算功能建筑）
			if (_owner.CurrentState != Structure.StructureState.Completed)
				return;

			FP fixedDelta = RTS.Core.SimManager.Instance.World.FixedDelta;
			_timer += fixedDelta;

			if (!_isInitialized)
			{
				_isInitialized = true;
				ExpandCreep();
			}

			if (_timer >= _spreadTimeFP)
			{
				_timer -= _spreadTimeFP;
				_currentRadius += FP.One;
				ExpandCreep();
			}
		}

		private void ExpandCreep()
		{
			if (CreepManager.Instance == null || RTS.Core.SimManager.Instance == null)
				return;

			var grid = RTS.Core.SimManager.Instance.World.Grid;
			Vector2I topLeft = _owner.GridPosition;
			Vector2I bottomRight = topLeft + new Vector2I(_owner.GridSize - 1, _owner.GridSize - 1);

			int scanRange = (int)FP.Ceiling(_currentRadius);
			Vector2I scanTopLeft = topLeft - new Vector2I(scanRange, scanRange);
			Vector2I scanBottomRight = bottomRight + new Vector2I(scanRange, scanRange);

			for (int x = scanTopLeft.X; x <= scanBottomRight.X; x++)
			{
				for (int y = scanTopLeft.Y; y <= scanBottomRight.Y; y++)
				{
					var cell = new Vector2I(x, y);
					var simCell = new SimVector2I(x, y);

					// 只允许在合法地面且非墙的格子上铺毯（纯逻辑数据，不再依赖 Godot TileMap）
					if (!grid.TerrainCells.Contains(simCell))
						continue;
					if (grid.StaticObstacles.Contains(simCell))
						continue;

					int dx = Mathf.Max(0, Mathf.Max(topLeft.X - x, x - bottomRight.X));
					int dy = Mathf.Max(0, Mathf.Max(topLeft.Y - y, y - bottomRight.Y));
					FP dist = FP.Sqrt((FP)(dx * dx + dy * dy));

					if (dist <= _currentRadius && _coveredCells.Add(cell))
					{
						int ownerTeam = _owner.TeamID;
						// 中立纳米核心（TeamID<=0）铺的菌毯认领给纳米玩家：
						// 否则纳米 AI 的 TryBotNanoSpread 永远找不到“己方菌毯”，
						// 一张毯都铺不出去，采集器/塔也建不起来。
						if (ownerTeam <= 0 && GeneratedCreep == CreepType.NanoCreep)
						{
							foreach (var player in RTS.World.Game.GetAllPlayers())
							{
								if (player?.Race?.RaceName == "Nano")
								{
									ownerTeam = player.TeamId;
									break;
								}
							}
						}
						CreepManager.Instance.AddCreep(cell, GeneratedCreep, ownerTeam);
					}
				}
			}
		}

		public void ClearCreep()
		{
			if (CreepManager.Instance == null || RTS.Core.SimManager.Instance == null || _owner == null)
				return;

			// 清毯可能在任何线程触发（建筑死亡），扫 World.Structures 必须在 WorldLock 内
			lock (RTS.Core.SimManager.Instance.WorldLock)
			{
			// 按最大半径扫格清除本来源铺的菌毯（覆盖开局直铺的初始毯，
			// 以及任何不在 _coveredCells 记录里的同型毯格），拆掉源头即清毯
			var world = RTS.Core.SimManager.Instance.World;
			var grid = world.Grid;
			Vector2I topLeft = _owner.GridPosition;
			Vector2I bottomRight = topLeft + new Vector2I(_owner.GridSize - 1, _owner.GridSize - 1);
			int scan = (int)FP.Ceiling(_maxRadiusFP);

			for (int x = topLeft.X - scan; x <= bottomRight.X + scan; x++)
			{
				for (int y = topLeft.Y - scan; y <= bottomRight.Y + scan; y++)
				{
					var simCell = new SimVector2I(x, y);
					if (!grid.TerrainCells.Contains(simCell))
						continue;
					if (grid.StaticObstacles.Contains(simCell))
						continue;

					int dx = Mathf.Max(0, Mathf.Max(topLeft.X - x, x - bottomRight.X));
					int dy = Mathf.Max(0, Mathf.Max(topLeft.Y - y, y - bottomRight.Y));
					FP dist = FP.Sqrt((FP)(dx * dx + dy * dy));
					if (dist <= _maxRadiusFP && !IsCoveredByOtherSource(x, y))
						CreepManager.Instance.RemoveCreep(new Vector2I(x, y), GeneratedCreep);
				}
			}
			_coveredCells.Clear();
			}
		}

		// 该格是否已被其他我方同型菌毯源头覆盖（拆一棵树不误伤另一棵树的毯）
		private bool IsCoveredByOtherSource(int gx, int gy)
		{
			var world = RTS.Core.SimManager.Instance?.World;
			if (world == null || _owner?.SimStructureData == null)
				return false;

			foreach (var s in world.Structures.Values)
			{
				if (s == null || s.IsDead || s.ID == _owner.SimStructureData.ID ||
					s.TeamID != _owner.TeamID ||
					s.CurrentState != RTS.Simulation.SimStructure.StructureState.Active)
					continue;

				var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(s.StructureTypeId);
				if (cfg == null || cfg.CreepSpreadRadius <= 0f ||
					cfg.GeneratedCreepType != GeneratedCreep)
					continue;

				int dx = Mathf.Max(0, Mathf.Max(s.GridPosition.X - gx, gx - (s.GridPosition.X + s.GridWidth - 1)));
				int dy = Mathf.Max(0, Mathf.Max(s.GridPosition.Y - gy, gy - (s.GridPosition.Y + s.GridHeight - 1)));
				FP dist = FP.Sqrt((FP)(dx * dx + dy * dy));
				if (dist <= (FP)cfg.CreepSpreadRadius)
					return true;
			}
			return false;
		}

		// 科技升级（菌毯增殖）：半径 +N 格、蔓延时间 × 倍率
		public void ApplyUpgrade(int radiusBonus, float timeMultiplier)
		{
			if (radiusBonus > 0)
			{
				MaxRadius += radiusBonus;
				_maxRadiusFP = (FP)MaxRadius;
			}
			if (timeMultiplier > 0f && timeMultiplier < 1f)
			{
				SpreadTime *= timeMultiplier;
				_spreadTimeFP = (FP)SpreadTime;
			}
		}
	}
}
