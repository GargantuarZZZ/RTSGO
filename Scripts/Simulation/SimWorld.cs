using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FixMath.NET;
using RTS.Data;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	public class CarpetSpreadState
	{
		public int CenterX;
		public int CenterY;
		public int OwnerTeam;
		public FP MaxRadius;
		public int TicksElapsed;
		public int DurationTicks;
	}

	public partial class SimWorld
	{
		public object SyncRoot { get; }
		public ISimulationRules Rules { get; }

		public SimWorld(object syncRoot = null, ISimulationRules rules = null)
		{
			SyncRoot = syncRoot ?? new object();
			Rules = rules ?? DefaultSimulationRules.Instance;
		}

		// Optional diagnostics only; never read by gameplay or included in state hashes.
		// Configure before starting ticks. The sink must not modify simulation state.
		public Action<string> DiagnosticSink { get; set; }

		public SortedDictionary<int, SimUnit> Units = new();
		public SortedDictionary<int, SimStructure> Structures = new();
		public List<SimProjectile> Projectiles = new();


		private readonly SwarmManager _swarmManager = new SwarmManager();
		private readonly List<SimUnit> _neighborScratch = new();
		private readonly List<SimUnit> _visionScratch = new();
		private readonly List<SimUnit> _nearestScratch = new();
		private readonly List<SimEntity> _fieldSources = new();

		// 单位空间索引：加速分离力与碰撞的邻近查询
		public SimSpatialGrid SpatialGrid { get; } = new SimSpatialGrid();

		// 寻路预算：每个 Tick 最多算多少条新路径，爆发式指令分摊到后续 Tick（确定性：按单位 ID 顺序）
		// 直线可达不占预算后，预算只给真实 A*：16 -> 32，单位接令后更快全体动起来
		public const int PathBudgetPerTick = 32;
		public int PathBudgetRemaining = PathBudgetPerTick;

		public FP FixedDelta = (FP)0.05m;

		public SimGrid Grid = new SimGrid();
		public SimRandom RNG = new SimRandom(12345);
		public SimCreepGrid CreepGrid { get; private set; } = new SimCreepGrid();

		// =========================================================
		// 纳米虫地毯面板技能状态（确定性数据，纳入世界哈希）
		// =========================================================
		public FP CarpetSpreadCooldown = FP.Zero;
		public int CarpetSpreadCharges = 3;
		public int CarpetSpreadMaxCharges = 3;
		public FP CarpetSpreadCost = (FP)20m;
		public FP CarpetSpreadCooldownTime = (FP)10m;
		public FP CarpetSpreadRadiusTiles = (FP)10m;
		public FP CarpetSpreadDuration = (FP)1m;
		public int CarpetIncomeCellsPerResource = 20;

		// 进行中的地毯扩散（圆心向四周蔓延，1 秒扩散到最大半径）
		public List<CarpetSpreadState> CarpetSpreads = new();

		// 植物：大地核心已增幅过的资源点 / 已生效的大地核心（去重，确定性）
		public readonly System.Collections.Generic.HashSet<int> EarthCoreBoostedNodes = new();
		public readonly System.Collections.Generic.HashSet<int> AppliedEarthCores = new();

		// 由配置表注入纳米地毯技能数值（必须在逻辑 Tick 开始前调用，跨端一致）
		public void ApplyNanoConfig(
			FP cost,
			FP cooldownSeconds,
			FP radiusTiles,
			int maxCharges,
			FP spreadDurationSeconds,
			int incomeCellsPerResource)
		{
			CarpetSpreadCost = cost;
			CarpetSpreadCooldownTime = cooldownSeconds;
			CarpetSpreadRadiusTiles = radiusTiles;
			CarpetSpreadMaxCharges = maxCharges;
			CarpetSpreadDuration = spreadDurationSeconds;
			CarpetIncomeCellsPerResource = incomeCellsPerResource;

			CarpetSpreadCharges = maxCharges;
			CarpetSpreadCooldown = FP.Zero;
			CarpetSpreads.Clear();
		}

		// 全局唯一且必须同步的逻辑实体 ID 计数器。
		private int _nextEntityId = 1000;

		public int GetNextEntityId()
		{
			return _nextEntityId++;
		}

		public void AddUnit(SimUnit unit)
		{
			if (unit == null)
				return;

			Units[unit.ID] = unit;
			unit.World = this;
			SpatialGrid.Rebuild(Units.Values);
		}

		public void AddStructure(SimStructure structure)
		{
			if (structure == null)
				return;

			Structures[structure.ID] = structure;
			structure.World = this;
			Grid.SyncStructureBlocking(Structures);
		}

		// 建筑落点是否与现存建筑（含蓝图）重叠（确定性判定）
		public bool IsAreaOccupiedByStructure(SimVector2I topLeft, int size)
		{
			foreach (var s in Structures.Values)
			{
				if (s == null || s.IsDead)
					continue;

				bool overlap =
					topLeft.X < s.GridPosition.X + s.GridWidth &&
					topLeft.X + size > s.GridPosition.X &&
					topLeft.Y < s.GridPosition.Y + s.GridHeight &&
					topLeft.Y + size > s.GridPosition.Y;

				if (overlap)
					return true;
			}
			return false;
		}

		/// <summary>
		/// 作者在地图里画的建造禁区掩码查询（SimGrid 坐标）。
		/// 由宿主（MapGrid）在载入地图时注入：Simulation 层不能直接引用 World/Godot，
		/// 但建造合法性判定在模拟层，需要这一份"作者意图"。
		/// null = 没有掩码，全部允许。
		/// </summary>
		public static System.Func<SimVector2I, bool> BuildBlockedQuery;

		/// <summary>该格是否被作者的建造禁区覆盖。</summary>
		public static bool IsBuildBlockedByMap(SimVector2I cell) =>
			BuildBlockedQuery != null && BuildBlockedQuery(cell);

		// 资源点外 marginTiles 格内禁止建造：建筑占地不得进入资源点（含其 2 格外扩）的禁区
		public bool IsAreaNearResourceNode(SimVector2I topLeft, int size, int marginTiles)
		{
			// 作者手绘的禁区优先（编辑器里能给任意区域上禁建）
			for (int x = 0; x < size; x++)
			{
				for (int y = 0; y < size; y++)
				{
					if (IsBuildBlockedByMap(new SimVector2I(topLeft.X + x, topLeft.Y + y)))
						return true;
				}
			}

			foreach (var s in Structures.Values)
			{
				if (s == null || s.IsDead)
					continue;
				if (!IsResourceNodeType(s.StructureTypeId))
					continue;
				if (s.GridPosition.X - marginTiles < topLeft.X + size &&
					s.GridPosition.X + s.GridWidth + marginTiles > topLeft.X &&
					s.GridPosition.Y - marginTiles < topLeft.Y + size &&
					s.GridPosition.Y + s.GridHeight + marginTiles > topLeft.Y)
					return true;
			}
			return false;
		}

		public static bool IsResourceNodeType(string type)
		{
			return type == "IronOre" || type == "GasSpring" ||
				type == "MetalMine" || type == "WildFruit";
		}

		public void AddProjectile(SimProjectile projectile)
		{
			if (projectile == null)
				return;

			Projectiles.Add(projectile);
		}

		public SimEntity FindEntityById(int id)
		{
			if (Units.TryGetValue(id, out var unit))
				return unit;

			if (Structures.TryGetValue(id, out var structure))
				return structure;

			return null;
		}

		// =========================================================
		// 蓝图占地与"是谁站进去了"的确定性判定
		//
		// 规则（帝国时代 4 式）：蓝图只对"自己人"（同队/盟友）生效——
		// 自己人不看蓝图会站进去，因此必须被强制移出；敌方既看不见也不被阻挡。
		// 施工只有在蓝图范围内没有任何单位时才允许开始。
		// =========================================================

		/// <summary>某个世界坐标是否落在该蓝图占地范围内。</summary>
		public static bool IsInsideStructureFootprint(SimStructure structure, FPVector2 pos)
		{
			if (structure == null)
				return false;

			FP tile = (FP)64m;
			FP minX = (FP)structure.GridPosition.X * tile;
			FP minY = (FP)structure.GridPosition.Y * tile;
			FP maxX = minX + (FP)structure.GridWidth * tile;
			FP maxY = minY + (FP)structure.GridHeight * tile;

			return pos.X >= minX && pos.X < maxX && pos.Y >= minY && pos.Y < maxY;
		}

		/// <summary>
		/// 蓝图占地内是否有任何单位（不分敌我，因为施工期间敌方也可能走进来）。
		/// 施工闸门用它：不清空就不许开工。
		/// </summary>
		public bool IsFootprintClearOfUnits(SimStructure structure)
		{
			if (structure == null)
				return true;

			foreach (var unit in Units.Values)
			{
				if (unit == null || unit.IsDead)
					continue;
				if (IsInsideStructureFootprint(structure, unit.Position))
					return false;
			}
			return true;
		}

		/// <summary>
		/// 找出该蓝图占地内"属于自己人"的单位。敌方单位不在此列：他们看不见蓝图，
		/// 也不该被蓝图推开。返回结果按单位 ID 升序，保证两端一致。
		/// </summary>
		public void CollectFriendlyUnitsOnBlueprint(SimStructure structure, List<SimUnit> results)
		{
			results.Clear();
			if (structure == null)
				return;

			foreach (var unit in Units.Values)
			{
				if (unit == null || unit.IsDead)
					continue;
				if (unit.IsAir || unit.IsGhost || unit.IsSegmentBody)
					continue;
				if (!IsInsideStructureFootprint(structure, unit.Position))
					continue;
				if (!Rules.AreTeamsFriendly(unit.TeamID, structure.TeamID))
					continue;

				results.Add(unit);
			}
		}

		/// <summary>把单位强制移出蓝图：帝国时代 4 式——不论它当前在执行什么指令，
		/// 都插队一个"移出寻路"（EvictStructureId），并中断当前路径。
		/// 返回是否真的下达了移出指令。</summary>
		public bool ForceEvictUnitFromBlueprint(SimUnit unit, SimStructure blueprint)
		{
			if (unit == null || blueprint == null || unit.IsDead || blueprint.IsDead)
				return false;

			// 已经在为同一张蓝图让路：不重复打断，等它走完
			if (unit.EvictStructureId == blueprint.ID)
				return false;

			unit.EvictStructureId = blueprint.ID;
			unit.PathPending = false;
			unit.Path?.Clear();
			unit.Path = null;
			unit.CurrentWaypointIndex = 0;

			// 目标必须落在"真正站得住"的格子上：空取矩形边中点会把单位钉在
			// 墙角/墙内/别的建筑上——那里根本走不过去也不可站立。
			if (!TryFindBlueprintExitPoint(unit, blueprint.ID, out var exit))
			{
				// 四周全被堵死（基地塞满）：保持让路标记，下一 tick 重试
				unit.CancelMoveIntent();
				return false;
			}

			// 忽略该蓝图本身寻路：它正是要让开的东西，不该被当成障碍把自己堵死
			unit.CommandMove(this, exit, blueprint.ID);
			return true;
		}

		/// <summary>
		/// 为一个正站在蓝图里的单位找一个"站得住"的落点。
		///
		/// 步骤：先把"该单位队伍会被蓝图覆盖"的格子全部收集起来（同一张蓝图可能由
		/// 多片 Blueprint_Building_* 拼成，单片的 GridWidth/Height 未必是整张图），
		/// 取外接矩形并向四方向各外扩一圈作为候选环；然后在候选格里挑**真正可通行**
		/// 的（用该单位和该蓝图之外的全部阻挡判定），按"离单位最近"排序，
		/// 再退化为一次邻域可达性搜索（GetNearestWalkableNode，最多 15 格）兜底。
		///
		/// 判定与排序全部按格子坐标做整数比较，保证两端结果一致。
		/// </summary>
		private bool TryFindBlueprintExitPoint(SimUnit unit, int blueprintLeaderId, out FPVector2 exit)
		{
			exit = unit.Position;

			int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
			foreach (var s in Structures.Values)
			{
				if (s == null || s.IsDead ||
					s.CurrentState != SimStructure.StructureState.Blueprint)
					continue;
				// 只算"对该单位生效"的蓝图（自己人）；敌方蓝图与该单位无关
				if (!SimGrid.AreTeamsFriendly(unit.TeamID, s.TeamID))
					continue;
				if (!IsInsideStructureFootprint(s, unit.Position))
					continue;

				if (s.GridPosition.X < minX) minX = s.GridPosition.X;
				if (s.GridPosition.Y < minY) minY = s.GridPosition.Y;
				if (s.GridPosition.X + s.GridWidth > maxX) maxX = s.GridPosition.X + s.GridWidth;
				if (s.GridPosition.Y + s.GridHeight > maxY) maxY = s.GridPosition.Y + s.GridHeight;
			}

			if (minX > maxX)
				return false;

			SimVector2I unitCell = Grid.WorldToGrid(unit.Position);

			// 候选环：外接矩形四边各外扩 1 格（固定顺序，保证确定性）
			List<SimVector2I> candidates = new();
			for (int x = minX - 1; x <= maxX; x++)
			{
				candidates.Add(new SimVector2I(x, minY - 1));
				candidates.Add(new SimVector2I(x, maxY));
			}
			for (int y = minY; y <= maxY - 1; y++)
			{
				candidates.Add(new SimVector2I(minX - 1, y));
				candidates.Add(new SimVector2I(maxX, y));
			}

			// 只挑真正站得住的：忽略该蓝图本身，其余地形/建筑/敌方蓝图照常判定
			List<SimVector2I> walkable = new();
			foreach (var c in candidates)
			{
				if (Grid.IsWalkableForRadius(c, Structures, blueprintLeaderId, 0, unit.TeamID))
					walkable.Add(c);
			}

			if (walkable.Count > 0)
			{
				// 按"离单位最近的格子"排序；同距离时 X、Y 小者优先（确定性）
				walkable.Sort((a, b) =>
				{
					int da = (a.X - unitCell.X) * (a.X - unitCell.X) + (a.Y - unitCell.Y) * (a.Y - unitCell.Y);
					int db = (b.X - unitCell.X) * (b.X - unitCell.X) + (b.Y - unitCell.Y) * (b.Y - unitCell.Y);
					if (da != db) return da.CompareTo(db);
					if (a.X != b.X) return a.X.CompareTo(b.X);
					return a.Y.CompareTo(b.Y);
				});

				exit = Grid.GridToWorldCentered(walkable[0]);
				return true;
			}

			// 候选环全被堵：从单位所在格做一次邻域可达性搜索兜底。
			// ignoreId 传该蓝图，保证"蓝图自己"不会把搜索堵死，但地形与其它建筑仍然算数。
			SimVector2I fallback = Grid.GetNearestWalkableNode(
				unitCell, Structures, 15, blueprintLeaderId, 0, unit.TeamID);

			if (fallback == unitCell)
				return false;

			exit = Grid.GridToWorldCentered(fallback);
			return true;
		}

		/// <summary>
		/// 每个 tick 对账"谁站在蓝图里"。
		///
		/// 帝国时代 4 的处理方式：自己人站在蓝图占地内时，无论当前在执行什么指令，
		/// 都强制插队一个移出寻路；被打断的原指令由各自动作在移出结束后自然恢复。
		/// 敌方单位不在处理范围内（看不见蓝图，也不被它阻挡）。
		///
		/// 阈值用"是否深入蓝图"而不是"碰到边缘"：贴边单位由碰撞推出处理（更自然），
		/// 只有真正站进蓝图内部才触发强制移出，避免每帧无谓打断。
		/// </summary>
		public void TickBlueprintEvictions()
		{
			foreach (var unit in Units.Values)
			{
				if (unit == null || unit.IsDead)
					continue;
				if (unit.IsAir || unit.IsGhost || unit.IsSegmentBody)
					continue;

				// 已经离开蓝图（或蓝图已拆/已完工）：撤销让自己人让路的状态
				if (unit.EvictStructureId != 0)
				{
					if (!Structures.TryGetValue(unit.EvictStructureId, out var prev) ||
						prev == null || prev.IsDead ||
						prev.CurrentState != SimStructure.StructureState.Blueprint)
					{
						// 蓝图没了：清掉让路标记。若"移出"目标还没走到，
						// 顺手撤销它，避免单位继续跑向一个已经没意义的落点
						// （除非它本来就有玩家/AI 的其他目标）。
						if (unit.HasTarget)
							unit.CancelMoveIntent();
						unit.EvictStructureId = 0;
						continue;
					}

					if (!IsDeepInsideStructureFootprint(prev, unit.Position))
					{
						// 已经走出去：撤销让路标记；残留的"移出"目标同时清掉，
						// 动作层会在下一次 OnUpdate 重新下达原本的指令。
						if (unit.HasTarget)
							unit.CancelMoveIntent();
						unit.EvictStructureId = 0;
						continue;
					}

					// 仍在蓝图里：若上一次没能算出落点（四周被堵），这里重试
					if (!unit.HasTarget || unit.Path == null || unit.Path.Count == 0)
					{
						if (TryFindBlueprintExitPoint(unit, unit.EvictStructureId, out var retryExit))
							unit.CommandMove(this, retryExit, unit.EvictStructureId);
					}
					continue;
				}

				foreach (var structure in Structures.Values)
				{
					if (structure == null || structure.IsDead ||
						structure.CurrentState != SimStructure.StructureState.Blueprint)
						continue;
					if (!Rules.AreTeamsFriendly(unit.TeamID, structure.TeamID))
						continue;
					if (!IsDeepInsideStructureFootprint(structure, unit.Position))
						continue;

					ForceEvictUnitFromBlueprint(unit, structure);
					break;
				}
			}
		}

		// 单位是否"深入"建筑占地：四角采样全部落在矩形内才算，避免贴边误触发。
		private static bool IsDeepInsideStructureFootprint(SimStructure structure, FPVector2 pos)
		{
			FP inset = (FP)8m;
			FP tile = (FP)64m;
			FP minX = (FP)structure.GridPosition.X * tile + inset;
			FP minY = (FP)structure.GridPosition.Y * tile + inset;
			FP maxX = (FP)(structure.GridPosition.X + structure.GridWidth) * tile - inset;
			FP maxY = (FP)(structure.GridPosition.Y + structure.GridHeight) * tile - inset;

			return pos.X >= minX && pos.X <= maxX && pos.Y >= minY && pos.Y <= maxY;
		}

	}
}
