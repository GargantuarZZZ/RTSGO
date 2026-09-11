using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Network;
using RTS.Simulation;
using RTS.World;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
// =========================================================
// 分族对战 AI —— 核心调度 + 每队状态（重构版）
//
// 结构：
//   BotBrain        每队一个状态对象（波次/拆塔/扩张/缓存全集中，不再散落字典）
//   TickBotAIAll    主调度：错峰 + 固定顺序（建造→经济→生产→扩张→科技→种族）
//   其余子系统按区域拆到 SimManager.BotAI.*.cs
//
// 设计原则：
//   - 一切决策输入 = 纯逻辑数据（SimWorld/ConfigDatabase/PlayerData），可进回放对拍
//   - 种族机制由独立策略实现；RaceConfig.Ai* 只提供可调目标与偏好
//   - 不在 AI 代码里碰 Godot 节点；视觉副作用走 EnqueueMain
// =========================================================
public partial class SimManager
{
	// =========================================================
	// BotBrain：每队 AI 的全部可变状态
	// =========================================================
	private sealed class BotBrain
	{
		public int Team;
		public RaceConfig RaceCfg;
		// Per-decision commands cannot be overwritten by subsequent waves/raids.
		public readonly HashSet<int> ReservedOrders = new();
		// Terran hysteresis: stay at supply until 80%, not just until above 30%.
		public readonly HashSet<int> Resupplying = new();

		// 波次状态机：0=无 1=集结 2=推进
		public int WaveMode;
		public FPVector2 WaveTarget;
		public int WaveTick;
		// 波次集结位置：true=前线（目标前 6 格），false=老家前 8 格。
		// 必须持久化：靠 prevMode 推断只有切换那一帧成立，
		// 下一帧就会翻回老家，把全军重新拉回家（“打着打着全军回老家”的根源）
		public bool WaveRallyFront;

		// 拆塔/扩张
		public int TowerRaidId = -1;
		public FPVector2 TowerRaidPos;
		public FPVector2 PendingExpansion;

		// 缓存（60 tick 刷新）
		public int ArmyPowerTick = -9999;
		public float ArmyPower;
		public int EnemyPowerTick = -9999;
		public float EnemyPower;

		// 上一轮训练的兵种（混编偏好用）
		public string LastTrain = "";

		// 恶魔立场寻路节流
		public int FieldPathTick = -9999;

		// 日志节流
		public int LastRaidLogTick = -9999;
		public int LastAttackLogTick = -9999;

		// 滞留蓝图看门狗：蓝图 ID -> 创建 tick
		public readonly Dictionary<int, int> BlueprintTicks = new();

		// 多足编队：采集编队 / 攻击编队各自的牧羊人 SimUnit ID
		// 最近一次分矿位置（分矿建成后采集编队继续留守采矿）
		public FPVector2 ExpansionPos;
		// 多足编队系统：占领队 / 攻击队 / 电浆炮队
		public readonly List<WandererSquad> Squads = new();
		// 蓝图结构 ID -> 目标编队（建造时指定从属，单位生成后按此入队）
		public readonly Dictionary<int, WandererSquad> BlueprintSquadMap = new();
		// 多足出生点锚点（开局第一只牧羊位置）：守家牧羊 / 出生点建造位以它为圆心，
		// 不随出征编队的牧羊人漂移（否则出生点补守家蓝图会放到野外）
		public FPVector2 HomeAnchor;
		// 扩张重试冷却（tick）：Shepherd 蓝图因资源/落点失败后记录，
		// 冷却期内不再重复下单（避免空队循环重建、Builder 每轮 +2 爆增）
		public int ExpandCooldownTick = -9999;

		// 纳米最近一次铺毯中心（避免连续在同一处重复铺）
		public int NanoLastSpreadGx = int.MinValue;
		public int NanoLastSpreadGy = int.MinValue;
	}

	private readonly Dictionary<int, BotBrain> _botBrains = new();

	private BotBrain GetBrain(int team)
	{
		if (!_botBrains.TryGetValue(team, out var brain))
		{
			brain = new BotBrain { Team = team };
			_botBrains[team] = brain;
		}
		return brain;
	}

	// =========================================================
	// 主调度：每 tick 进来，各队按相位错峰执行，具体决策顺序由种族策略负责
	// =========================================================
	private void TickBotAIAll(int currentTick)
	{
		foreach (int team in _botTeams.OrderBy(t => t))
		{
			int phase = (team * 7) % BotTickInterval;
			if ((currentTick + phase) % BotTickInterval != 0)
				continue;

			var brain = GetBrain(team);
			var player = RTS.World.Game.GetPlayerByTeam(team);
			brain.RaceCfg = player?.Race != null
				? ConfigDatabase.GetRace(player.Race.RaceName)
				: null;
			if (brain.RaceCfg == null)
				continue;

			brain.ReservedOrders.Clear();
			brain.Resupplying.RemoveWhere(id => World.FindSimEntity(id) is not SimUnit u || u.IsDead);
			// No generic fallback: a new race must define its own economic and tactical strategy.
			var strategy = GetBotStrategy(team);
			if (strategy == null)
				continue;
			strategy.Tick(this, brain, currentTick);

			if (BotLogTick(team, currentTick, 300))
				PrintBotDiagnostics(team, currentTick);
		}
	}

	// 按队伍相位对齐的周期日志判定（错峰后每个队伍仍能按时打印）
	private bool BotLogTick(int team, int currentTick, int interval)
	{
		int phase = (team * 7) % BotTickInterval;
		return (currentTick + phase) % interval == 0;
	}

	// 新对局：清空全部 AI 状态（跨局残留会污染第二局）
	private void ResetBotState()
	{
		_botBrains.Clear();
		_botDifficultyByTeam.Clear();
	}

	// =========================================================
	// 通用查询
	// =========================================================
	private int CountUnits(int team, Func<SimUnit, bool> extra = null)
	{
		int n = 0;
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team)
				continue;
			if (extra != null && !extra(u))
				continue;
			n++;
		}
		return n;
	}

	private int CountStructures(int team, Func<SimStructure, bool> extra = null)
	{
		int n = 0;
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != team)
				continue;
			if (extra != null && !extra(s))
				continue;
			n++;
		}
		return n;
	}

	private int CountOwnUnits(int team, string unitId)
		=> CountUnits(team, u => u.UnitTypeId == unitId);

	private int CountOwnStructures(int team, string structId)
		=> CountStructures(team, s => s.StructureTypeId == structId);

	private int CountOwnMilitary(int team)
		=> CountUnits(team, IsMilitaryUnit);

	private int CountAttackers(int team)
		=> CountUnits(team, u => IsMilitaryConfig(ConfigDatabase.GetUnit(u.UnitTypeId)));

	private int CountSupportUnits(int team)
		=> CountUnits(team, u =>
		{
			var cfg = ConfigDatabase.GetUnit(u.UnitTypeId);
			return cfg != null && !cfg.IsWorker && cfg.CanHeal;
		});

	// 军事单位判定（只看配置）：有武器、非工人、非采集、非控制锚点。
	// Shepherd（控制圈锚点）与 Harvester（经济单位）不能被当兵力派去冲锋。
	private static bool IsMilitaryConfig(UnitConfig cfg)
	{
		if (cfg == null || cfg.IsWorker || cfg.CanHarvest ||
			cfg.AutoHarvestRadiusTiles > 0 || cfg.AutoSubmitHarvest ||
			cfg.ControlRangeTiles > 0 || cfg.IsSegment)
			return false;
		return cfg.WeaponIds.Count > 0;
	}

	private bool IsMilitaryUnit(SimUnit u)
	{
		if (u == null || u.IsDead)
			return false;
		var node = FindEntityById(u.ID);
		if (node == null || node.CombatModule == null ||
			node.CombatModule.Weapons.Count == 0 || node.HarvestModule != null)
			return false;
		return IsMilitaryConfig(ConfigDatabase.GetUnit(u.UnitTypeId));
	}

	// 主基地锚点：有主基地建筑用主基地；多足用控制单位；纳米用第一座己方建筑
	private FPVector2 GetTeamMainBasePos(int team)
	{
		FPVector2 best = FPVector2.Zero;
		int bestId = int.MaxValue;
		foreach (var sim in World.Structures.Values)
		{
			if (sim == null || sim.IsDead || sim.TeamID != team)
				continue;
			var cfg = ConfigDatabase.GetStructure(sim.StructureTypeId);
			if (cfg == null || !cfg.IsMainBase)
				continue;
			if (sim.ID < bestId)
			{
				bestId = sim.ID;
				best = sim.Position;
			}
		}
		// 多足：固定基地锚点 = 出生点牧羊人（ID 最小），不随编队漂移
		if (best.X == FP.Zero && best.Y == FP.Zero)
		{
			FPVector2 homeShepherd = GetHomeShepherdPos(team);
			if (homeShepherd.X != FP.Zero || homeShepherd.Y != FP.Zero)
				return homeShepherd;
		}
		if (best.X == FP.Zero && best.Y == FP.Zero &&
			GetTeamControlAnchor(team, out var anchor, out var reach) && reach > FP.Zero)
			return anchor;
		if (best.X == FP.Zero && best.Y == FP.Zero)
		{
			// 纳米没有主基地/控制单位：用第一座已完工建筑当锚点，
			// 否则“最近中立塔”会算成地图中心塔，巨兽全图乱跑。
			foreach (var s in World.Structures.Values)
			{
				if (s != null && !s.IsDead && s.TeamID == team &&
					s.CurrentState == SimStructure.StructureState.Active)
				{
					best = s.Position;
					break;
				}
			}
		}
		if (best.X == FP.Zero && best.Y == FP.Zero)
			return GetTeamCentroid(team);
		return best;
	}

	// 多足固定基地锚点：ID 最小的存活牧羊人（出生点那只）。
	// 它不出征（见 DeployWandererSquad 守家规则），建造位以它为圆心，
	// 蓝图不会跟着出征编队的牧羊人漂到野外。
	private FPVector2 GetHomeShepherdPos(int team)
	{
		// 优先用出生点锚点（开局第一只牧羊的位置，固定不动）；
		// 未初始化时退回最小 ID 牧羊（旧逻辑兜底）
		var wBrain = GetBrain(team);
		if (wBrain.HomeAnchor.X != FP.Zero || wBrain.HomeAnchor.Y != FP.Zero)
			return wBrain.HomeAnchor;

		FPVector2 best = FPVector2.Zero;
		int minId = int.MaxValue;
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team || u.UnitTypeId != "Shepherd")
				continue;
			if (u.ID < minId)
			{
				minId = u.ID;
				best = u.Position;
			}
		}
		return best;
	}

	private bool GetTeamControlAnchor(int team, out FPVector2 anchor, out FP reach)
	{
		anchor = FPVector2.Zero;
		reach = FP.Zero;
		int n = 0;
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team)
				continue;
			var cfg = ConfigDatabase.GetUnit(u.UnitTypeId);
			if (cfg == null || cfg.ControlRangeTiles <= 0)
				continue;
			anchor += u.Position;
			n++;
			FP r = (FP)(cfg.ControlRangeTiles * World.Grid.TileSize);
			if (r > reach)
				reach = r;
		}
		if (n == 0)
		{
			anchor = FPVector2.Zero;
			reach = FP.Zero;
			return false;
		}
		anchor /= (FP)n;
		return true;
	}

	private FPVector2 GetTeamCentroid(int team)
	{
		FP x = FP.Zero, y = FP.Zero;
		int count = 0;
		foreach (var sim in World.Structures.Values)
		{
			if (sim == null || sim.IsDead || sim.TeamID != team)
				continue;
			x += sim.Position.X;
			y += sim.Position.Y;
			count++;
		}
		foreach (var simUnit in World.Units.Values)
		{
			if (simUnit == null || simUnit.IsDead || simUnit.TeamID != team)
				continue;
			x += simUnit.Position.X;
			y += simUnit.Position.Y;
			count++;
		}
		if (count == 0)
			return FPVector2.Zero;
		return new FPVector2(x / (FP)count, y / (FP)count);
	}

	// 建造位：以主基地为圆心按环排列（8 方向、逐环外扩、错位散开），对齐 64 网格。
	// 只 4 个轴向 + 3 格外环会把建筑全堆在主基地门口，堵住工人采矿/军队出门的路。
	private FPVector2 GetBotBuildPos(int team, int slot)
	{
		FPVector2 basePos = GetTeamMainBasePos(team);
		if (basePos.X == FP.Zero && basePos.Y == FP.Zero)
			basePos = GetTeamCentroid(team);
		return GetBuildPosAround(basePos, slot);
	}

	// 前置拍点：主基地向最近敌人方向外推 14 格（对齐 64 网格），
	// 生产建筑加建时往前线放，出兵更快到场
	private FPVector2 GetBotForwardBuildPos(int team, int slot)
	{
		FPVector2 basePos = GetTeamMainBasePos(team);
		if (basePos.X == FP.Zero && basePos.Y == FP.Zero)
			basePos = GetTeamCentroid(team);
		FPVector2 enemy = GetNearestEnemyPos(team);
		FPVector2 dir = (enemy - basePos).Normalized();
		if (dir.X == FP.Zero && dir.Y == FP.Zero)
			dir = new FPVector2(FP.One, FP.Zero);
		return GetBuildPosAround(basePos + dir * (FP)(14 * 64), slot);
	}

	// 8 方向单位向量（45° 步进），与 64 网格相乘后仍能对齐
	private static readonly FP[] _buildCos8 =
		{ FP.One, (FP)0.7071m, FP.Zero, -(FP)0.7071m, -FP.One, -(FP)0.7071m, FP.Zero, (FP)0.7071m };
	private static readonly FP[] _buildSin8 =
		{ FP.Zero, (FP)0.7071m, FP.One, (FP)0.7071m, FP.Zero, -(FP)0.7071m, -FP.One, -(FP)0.7071m };

	private static FPVector2 GetBuildPosAround(FPVector2 center, int slot)
	{
		int side = slot % 8;
		int ring = slot / 8;
		// 5~6 格外环起步（奇数位再外推 1 格错开），建筑之间天然拉开距离
		FP dist = (FP)((ring + 1) * 256 + (side % 2) * 64);
		FP x = _buildCos8[side] * dist;
		FP y = _buildSin8[side] * dist;
		FP gx = (FP)((long)((center.X + x) / (FP)64) * 64);
		FP gy = (FP)((long)((center.Y + y) / (FP)64) * 64);
		return new FPVector2(gx, gy);
	}

	private static SimVector2I BotGridTopLeft(FPVector2 center, int size)
	{
		int tile = 64;
		int gx = (int)FP.Floor(center.X / (FP)tile);
		int gy = (int)FP.Floor(center.Y / (FP)tile);
		return new SimVector2I(gx - size / 2, gy - size / 2);
	}

	// =========================================================
	// 经济：空闲工人去最近资源点（非金属资源保底 1~2 人）
	// =========================================================
	private void TickBotEconomy(int team, RaceConfig raceCfg)
	{
		var resourceNodes = new List<RTS.Units.ResourceStructure>();
		var gasNodes = new List<RTS.Units.ResourceStructure>();
		foreach (var simStruct in World.Structures.Values)
		{
			if (simStruct == null || simStruct.IsDead)
				continue;
			if (FindEntityById(simStruct.ID) is RTS.Units.ResourceStructure res)
			{
				if (!IsResourceAccessibleToTeam(team, res))
					continue;
				resourceNodes.Add(res);
				var cfg = ConfigDatabase.GetStructure(res.StructureName);
				if (cfg != null && cfg.ResourceType != ResourceType.Metal)
					gasNodes.Add(res);
			}
		}

		// 气/野果保底：工人多时每点 2 人（不要因为金属矿近就全员挤金属，气/果断供）。
		// 多足牧羊蓝图要 400 气，工人 ≥6 就优先双人采气
		int workerCount = CountOwnUnits(team, raceCfg.AiWorkerUnitId);
		int specialTarget = raceCfg.RaceId == "Wanderer"
			? (workerCount >= 6 ? 2 : 1)
			: (workerCount >= 8 ? 2 : 1);
		// 采气总人数上限：最多工人数的一半，剩下的人必须采金属。
		// 多足 3 个气井 × 2 人会把全队工人吸去采气，金属归零 → 蓝图全买不起。
		// 多足采气上限放宽：Shepherd 蓝图要 400 气，而外面气泉往往很多，
		// 只派一半工人采气（6 人 → 3 人）根本攒不起气（实测气余 7000+ 却
		// 卡在气 375 造不起第 8 只牧羊）。改为至少 2/3 工人采气，
		// 剩余 1/3 采金属保收入；12 工人 → 8 采气 4 采金。
		int gasLimit = raceCfg.RaceId == "Wanderer"
			? System.Math.Max(2, workerCount * 2 / 3)
			: System.Math.Max(1, workerCount / 2);
		int gasAssigned = 0;
		foreach (var special in gasNodes)
		{
			if (special?.LogicEntity == null)
				continue;
			int onGas = 0;
			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead || u.TeamID != team)
					continue;
				var n = FindEntityById(u.ID);
				if (n?.Brain?.GetActiveHarvestSource() == special)
					onGas++;
			}
			for (int need = specialTarget - onGas; need > 0; need--)
			{
				if (gasAssigned + onGas >= gasLimit)
					break;
				var worker = PickIdleHarvester(team, special.LogicEntity.Position);
				if (worker == null)
					break;
				RTS.Core.EntityExtensions.CommandHarvest(worker, special);
				gasAssigned++;
			}
		}

		// 金属保底回收：气储量已足够（≥ 一座牧羊蓝图 400 气 + 余量）时，
		// 把超过 gasLimit 的采气工人强制改派去最近的金属矿，
		// 否则全队采气、金属 0 → Shepherd 蓝图（100 金）永远买不起（实测金 50 卡死）
		float gasStock = RTS.World.Game.GetPlayerByTeam(team)?.PlayerData?.GetResource(ResourceType.Gas) ?? 0f;
		if (raceCfg.RaceId == "Wanderer" && gasStock >= 600f)
		{
			int onGasTotal = 0;
			foreach (var special in gasNodes)
				onGasTotal += CountHarvestersOn(team, special);
			if (onGasTotal > gasLimit)
			{
				int toReassign = onGasTotal - gasLimit;
				foreach (var u in World.Units.Values)
				{
					if (toReassign <= 0)
						break;
					if (u == null || u.IsDead || u.TeamID != team || u.CombatTargetId >= 0)
						continue;
					var n = FindEntityById(u.ID);
					if (n?.Brain?.GetActiveHarvestSource() is not RTS.Units.ResourceStructure gasRes)
						continue;
					// 正在建造蓝图的工人不能拉：接了 Shepherd/Builder 施工单
					// 却被改派去采矿会打断建造 → 蓝图滞留 0% → 看门狗取消 →
					// 队长永远建不出来（实测 Shepherd 蓝图反复滞留）
					if (n.Brain.GetActiveBuildTarget() != null)
						continue;
					var gcfg = ConfigDatabase.GetStructure(gasRes.StructureName);
					if (gcfg == null || gcfg.ResourceType == ResourceType.Metal)
						continue;
					// 找最近的金属矿改派
					RTS.Units.ResourceStructure bestMetal = null;
					FP bestD = FP.MaxValue;
					foreach (var res in resourceNodes)
					{
						if (res?.LogicEntity == null)
							continue;
						var rcfg = ConfigDatabase.GetStructure(res.StructureName);
						if (rcfg == null || rcfg.ResourceType != ResourceType.Metal)
							continue;
						FP d = FPVector2.DistanceSquared(u.Position, res.LogicEntity.Position);
						if (d < bestD)
						{
							bestD = d;
							bestMetal = res;
						}
					}
					if (bestMetal != null)
					{
						RTS.Core.EntityExtensions.CommandHarvest(n, bestMetal);
						toReassign--;
					}
				}
			}
		}

		// 其余空闲工人去金属矿：按“占用最少”优先，其次距离最近，
		// 避免一窝蜂挤一个矿导致其它工人站桩“看着像闲置”。
		foreach (var simUnit in World.Units.Values)
		{
			if (simUnit == null || simUnit.IsDead || simUnit.TeamID != team ||
				simUnit.CombatTargetId >= 0 || simUnit.HasTarget)
				continue;
			var node = FindEntityById(simUnit.ID);
			if (IsBotWorkerBusy(node, simUnit) || node.HarvestModule == null ||
				node.Brain.GetActiveHarvestSource() != null ||
				node.Brain.GetActiveBuildTarget() != null)
				continue;

			RTS.Units.ResourceStructure best = null;
			FP bestScore = FP.MaxValue;
			int bestOcc = int.MaxValue;
			foreach (var res in resourceNodes)
			{
				if (res?.LogicEntity == null)
					continue;
				var resCfg = ConfigDatabase.GetStructure(res.StructureName);
				if (resCfg != null && resCfg.ResourceType != ResourceType.Metal)
					continue;
				int occ = CountHarvestersOn(team, res);
				FP dx = res.LogicEntity.Position.X - simUnit.Position.X;
				FP dy = res.LogicEntity.Position.Y - simUnit.Position.Y;
				FP distSq = dx * dx + dy * dy;
				// 占用优先：少 1 人 ≈ 近 8 格（4096 距离²）
				FP score = distSq + (FP)(occ * 4096);
				if (occ < bestOcc || (occ == bestOcc && score < bestScore))
				{
					bestScore = score;
					bestOcc = occ;
					best = res;
				}
			}
			if (best != null)
				RTS.Core.EntityExtensions.CommandHarvest(node, best);
		}
	}

	// 统计某资源点当前已分配的工人数
	private int CountHarvestersOn(int team, RTS.Units.ResourceStructure res)
	{
		int n = 0;
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team)
				continue;
			var node = FindEntityById(u.ID);
			if (node?.Brain?.GetActiveHarvestSource() == res)
				n++;
		}
		return n;
	}

	// 资源点是否被己方主基地/分矿圈住（多足看控制圈、纳米看采集器）
	private bool IsResourceAccessibleToTeam(int team, RTS.Units.ResourceStructure res)
	{
		if (res?.LogicEntity == null)
			return false;
		FP radius = (FP)(12 * World.Grid.TileSize);
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != team ||
				s.CurrentState != SimStructure.StructureState.Active)
				continue;
			var cfg = ConfigDatabase.GetStructure(s.StructureTypeId);
			if (cfg == null || !cfg.IsMainBase)
				continue;
			FP centerX = s.Position.X + (FP)(s.GridWidth * 64 / 2);
			FP centerY = s.Position.Y + (FP)(s.GridHeight * 64 / 2);
			if (FPVector2.IsWithinRange(new FPVector2(centerX, centerY), res.LogicEntity.Position, radius))
				return true;
		}
		// 任一生单位 12 格内的资源都算可达（多足等无主基地种族）。
		// 牧羊人作为可移动控制锚点，一旦被 AI 移动/出征，只认控制圈会让
		// 出生点矿全部“不可达”，金属断供 → 蓝图买不起 → 整局卡死。
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team)
				continue;
			if (FPVector2.IsWithinRange(u.Position, res.LogicEntity.Position, radius))
				return true;
		}
		// 牧羊人控制圈（覆盖范围更远，作为第二重判定）
		if (GetTeamControlAnchor(team, out var anchor, out var reach) && reach > FP.Zero)
			return FPVector2.IsWithinRange(anchor, res.LogicEntity.Position, reach);
		return false;
	}

	private IEntity PickIdleHarvester(int team, FPVector2 targetPos)
	{
		IEntity best = null;
		FP bestDistSq = FP.MaxValue;
		foreach (var simUnit in World.Units.Values)
		{
			if (simUnit == null || simUnit.IsDead || simUnit.TeamID != team ||
				simUnit.CombatTargetId >= 0 || simUnit.HasTarget)
				continue;
			var node = FindEntityById(simUnit.ID);
			if (IsBotWorkerBusy(node, simUnit) || node.HarvestModule == null ||
				node.Brain.GetActiveHarvestSource() != null ||
				node.Brain.GetActiveBuildTarget() != null)
				continue;
			FP dx = simUnit.Position.X - targetPos.X;
			FP dy = simUnit.Position.Y - targetPos.Y;
			FP d = dx * dx + dy * dy;
			if (d < bestDistSq)
			{
				bestDistSq = d;
				best = node;
			}
		}
		return best;
	}

	// =========================================================
	// 建造：按 AiBuildOrderIds 补缺（每 tick 每队最多下一单）
	// =========================================================
	private void TickBotBuild(int team, RaceConfig raceCfg, int currentTick)
	{
		if (raceCfg.AiBuildOrderIds.Count == 0)
			return;

		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (player?.PlayerData == null) return;
		bool panelBuild = GetBotStrategy(team)?.PanelConstruction == true;
		int supplyFree = player?.PlayerData != null
			? player.PlayerData.GetMaxSupply() - player.PlayerData.GetUsedSupply()
			: 0;
		if (supplyFree < 4 && !IsSupplyBuildingPending(team, raceCfg) &&
			(raceCfg.MaxSupplyCap <= 0 || player.PlayerData.GetMaxSupply() < raceCfg.MaxSupplyCap))
		{
			string supplyId = PickSupplyStructure(team, raceCfg);
			if (supplyId.Length > 0)
			{
				// 补给站按已建数量错开槽位，避免全部堆在 slot0 一个落点上
				var supplyPos = GetBotBuildPos(team, CountOwnStructures(team, supplyId));
				bool placed = panelBuild
					? TryBotPanelBuild(team, supplyId, supplyPos)
					: TryPlaceStructureAt(team, supplyId, supplyPos, 8);
				if (placed)
					return;
			}
		}

		int slot = 0;
		foreach (string structId in raceCfg.AiBuildOrderIds)
		{
			int target = raceCfg.AiBuildTargetCounts.GetValueOrDefault(structId, 1);
			var buildCfg = ConfigDatabase.GetStructure(structId);
			target = GetBotStrategy(team)?.BuildTarget(this, team, buildCfg, target) ?? target;
			int owned = CountOwnStructures(team, structId);
			if (owned >= target)
			{
				slot++;
				continue;
			}

			// 奢侈品建筑门：科技/功能/收入建筑等军队成型再拍（菌毯面板种族豁免）
			int armyReady = Mathf.Max(2, raceCfg.AiArmyTarget / 3);
			if (!panelBuild && IsLuxuryStructure(structId) && CountOwnMilitary(team) < armyReady)
				return;

			bool placed = false;
			if (panelBuild)
			{
				for (int attempt = 0; attempt < 3; attempt++)
				{
					placed = TryBotPanelBuild(team, structId, GetBotBuildPos(team, slot + owned + attempt * 4));
					if (placed)
						break;
				}
			}
			else
			{
				// 搜索环加大：出生点常被矿点/单位占满，4 环(256)内找不到
				// 2x2 空地 → 建筑永远建不出（泰伦实测 AlgaeFactory 全地形占用）
				// 槽位加 owned：同类型第 2、3 座换到不同方向/外环，不再堆同一落点
				// 生产类建筑第 2 座起可以前置拍（靠近前线），出兵更快到场
				bool production = buildCfg != null &&
					(buildCfg.TrainableUnitIds.Count > 0 || buildCfg.AutoProduceUnitIds.Count > 0);
				FPVector2 buildCenter = (production && owned >= 1)
					? GetBotForwardBuildPos(team, slot + owned)
					: GetBotBuildPos(team, slot + owned);
				placed = TryPlaceStructureAt(team, structId, buildCenter, 8);
			}
			if (placed)
				return;
				if (target > 1 && owned == 0)
					return;
			slot++;
		}
	}

	// 奢侈品 = 非产能/非人口/非主基地，且是科技/功能/收入建筑
	private bool IsLuxuryStructure(string structId)
	{
		var cfg = ConfigDatabase.GetStructure(structId);
		if (cfg == null || cfg.IsMainBase || cfg.IsProductionBuilding || cfg.SupplyProvided > 0 ||
			cfg.TrainableUnitIds.Count > 0 || cfg.ResearchableTechIds.Count > 0 ||
			cfg.GarrisonCapacity > 0 || cfg.ProvidesResourceIncome)
			return false;
		if (cfg.IsResourceBuilding)
			return false;
		return cfg.IsTechBuilding || cfg.ResearchableTechIds.Count > 0 ||
			cfg.IsInteractiveBuilding || cfg.ProvidesResourceIncome;
	}

	private string PickSupplyStructure(int team, RaceConfig raceCfg)
	{
		string best = "";
		int bestProvided = 0;
		foreach (string sid in raceCfg.AvailableStructureIds)
		{
			var cfg = ConfigDatabase.GetStructure(sid);
			if (cfg == null || cfg.SupplyProvided <= 0 || cfg.IsMainBase ||
				(cfg.IsUnique && CountOwnStructures(team, sid) > 0))
				continue;
						if (cfg.SupplyProvided > bestProvided)
			{
				bestProvided = cfg.SupplyProvided;
				best = sid;
			}
		}
		return best;
	}

	// 工人建造（镜像 Build_ 派发：确定性校验 + 主线程生成蓝图）
	private bool TryBotPlaceStructure(int team, string structName, FPVector2 centerFP, WandererSquad assignSquad = null, IEntity builderOverride = null)
	{
		var buildCfg = ConfigDatabase.GetStructure(structName);
		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (buildCfg == null || player?.PlayerData == null)
			return false;

		if (buildCfg.RequiresSacrifice && CountOwnUnits(team, "DemonWorker") <= 2 &&
			CountOwnStructures(team, "HellCity") > 0) return false;
		var worker = builderOverride ?? PickIdleBotBuilder(team, structName, centerFP);
		if (worker == null)
		{
			return false;
		}
		var buildAction = worker.Brain?.GetAction<RTS.Actions.Implementation.BuildAction>("Build_" + structName);
		if (buildAction == null)
		{
			return false;
		}

		foreach (string req in buildCfg.RequiredTechIds)
			if (!player.PlayerData.HasTech(req))
				return false;
		if (buildCfg.IsUnique && PlayerOwnsStructure(team, structName))
			return false;
		// 蓝图建筑豁免人口检查（多足牧羊人蓝图：满人口先建后扩，否则 25/25 死锁）
		if (buildCfg.SupplyUsed > 0 && !structName.StartsWith("Blueprint_") &&
			player.PlayerData.GetUsedSupply() + buildCfg.SupplyUsed > player.PlayerData.GetMaxSupply())
			return false;

		int size = System.Math.Max(buildCfg.GridWidth, buildCfg.GridHeight);
		var topLeft = BotGridTopLeft(centerFP, size);
		if (!World.Grid.IsAreaPlaceable(topLeft, size) ||
			World.IsAreaOccupiedByStructure(topLeft, size) ||
			BlocksMiningPath(team, centerFP) ||
			TooCloseToOwnStructure(team, topLeft, size) ||
			World.IsAreaNearResourceNode(topLeft, size, 2))
		{
			return false;
		}
		// 多足：蓝图必须落在牧羊人控制圈内，否则工人施工/产出单位出圈失控
		if (RTS.World.Game.GetPlayerByTeam(team)?.Race?.RaceName == "Wanderer" &&
			!structName.StartsWith("Blueprint_Shepherd") &&
			!IsBlueprintCoveredByControl(team, centerFP, size))
		{
			return false;
		}
		if (!player.PlayerData.TryConsumeResources(buildAction.Costs))
		{
			return false;
		}

		string spawnName = structName;
		int spawnTeam = team;
		float spawnX = (float)centerFP.X;
		float spawnY = (float)centerFP.Y;
		var costRef = buildAction.Costs;
		IEntity workerRef = worker;
		int workerId = worker.LogicEntity?.ID ?? -1;
		var brain = GetBrain(team);
		brain.ReservedOrders.Add(workerId);
		RTS.Core.SimEventQueue.EnqueueMain(() =>
		{
			try
			{
				var targetObj = EntitySpawner.Instance?.SpawnEntity(
					spawnName, spawnTeam, new FPVector2((FP)spawnX, (FP)spawnY));
				if (targetObj is RTS.Units.Structure s)
				{
					s.InitAsBlueprint(costRef);
					if (s.LogicEntity != null)
					{
						brain.BlueprintTicks[s.LogicEntity.ID] = LockstepManager.Instance?.CurrentTick ?? 0;
						// 多足：蓝图创建时指定从属编队，单位生成后按此入队
						if (assignSquad != null && s.TeamID > 0 &&
							RTS.World.Game.GetPlayerByTeam(s.TeamID)?.Race?.RaceName == "Wanderer")
							GetBrain(s.TeamID).BlueprintSquadMap[s.LogicEntity.ID] = assignSquad;
					}
				}
				if (workerRef is { } w && w.Brain != null && targetObj != null)
				{
					w.Brain.StartAction(
						"Build_" + spawnName,
						new FPVector2((FP)spawnX, (FP)spawnY),
						targetObj, false, false);
					bool accepted = w.Brain.GetActiveBuildTarget() != null;
										if (spawnName.StartsWith("Blueprint_Shepherd"))
					{
						var ws = w.LogicEntity;
						long wx = ws != null ? (long)ws.Position.X : 0;
						long wy = ws != null ? (long)ws.Position.Y : 0;
						long dist = -1;
						if (ws != null)
						{
							var dx = ws.Position.X - (FP)spawnX;
							var dy = ws.Position.Y - (FP)spawnY;
							dist = (long)FixMath.NET.Fix64.Sqrt(dx * dx + dy * dy);
						}
											}
				}
				else
				{
					if (targetObj is RTS.Units.Structure bad)
						bad.CancelConstruction();
				}
			}
			catch (System.Exception ex)
			{
				GD.PrintErr($"[BotAI] 建造派工异常 Team{spawnTeam} {spawnName}: {ex}");
			}
		});
		return true;
	}

	// 建筑间距约束：任何己方建筑（含蓝图）必须与新建筑保持至少 2 格净空，
	// 否则基地堆成一坨，工人/军队进出全被卡死
	private bool TooCloseToOwnStructure(int team, SimVector2I topLeft, int size)
	{
		int x0 = topLeft.X - 2, x1 = topLeft.X + size + 2;
		int y0 = topLeft.Y - 2, y1 = topLeft.Y + size + 2;
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != team)
				continue;
			if (s.GridPosition.X < x1 && s.GridPosition.X + s.GridWidth > x0 &&
				s.GridPosition.Y < y1 && s.GridPosition.Y + s.GridHeight > y0)
				return true;
		}
		return false;
	}

	// 建筑不能堵路：候选点落在“主基地 → 任一中立资源点”的 2.5 格宽走廊内、
	// 且离主基地 12 格以内（家门口）就算挡路，换位置重试。
	// 否则 AI 把补给站/兵营拍在矿路上，工人采矿和军队出门全被堵死。
	private bool BlocksMiningPath(int team, FPVector2 centerFP)
	{
		FPVector2 basePos = GetTeamMainBasePos(team);
		if (basePos.X == FP.Zero && basePos.Y == FP.Zero)
			return false;

		FP dx0 = centerFP.X - basePos.X;
		FP dy0 = centerFP.Y - basePos.Y;
		FP homeSq = dx0 * dx0 + dy0 * dy0;
		if (homeSq > (FP)(12 * 64) * (FP)(12 * 64))
			return false; // 离家太远（分矿等），不查家门口走廊

		FP corridorSq = (FP)(160 * 160);
		foreach (var sim in World.Structures.Values)
		{
			if (sim == null || sim.IsDead || sim.TeamID != -1)
				continue;
			if (!SimWorld.IsResourceNodeType(sim.StructureTypeId) || sim.ResourceAmount <= FP.Zero)
				continue;

			FP abx = sim.Position.X - basePos.X;
			FP aby = sim.Position.Y - basePos.Y;
			FP lenSq = abx * abx + aby * aby;
			if (lenSq < (FP)(64 * 64))
				continue;

			// 点到线段（基地→资源）的最短距离
			FP t = (dx0 * abx + dy0 * aby) / lenSq;
			if (t < FP.Zero) t = FP.Zero;
			else if (t > FP.One) t = FP.One;
			FP px = basePos.X + t * abx;
			FP py = basePos.Y + t * aby;
			FP ddx = centerFP.X - px;
			FP ddy = centerFP.Y - py;
			if (ddx * ddx + ddy * ddy <= corridorSq)
				return true;
		}
		return false;
	}

	// 蓝图中心是否被任一牧羊人的控制圈覆盖（留 1 格余量）
	private bool IsBlueprintCoveredByControl(int team, FPVector2 center, int size)
	{
		FP margin = (FP)(World.Grid.TileSize);
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team || u.UnitTypeId != "Shepherd")
				continue;
			var cfg = ConfigDatabase.GetUnit(u.UnitTypeId);
			FP reach = (FP)((cfg?.ControlRangeTiles ?? 15) * World.Grid.TileSize) - margin;
			if (FPVector2.IsWithinRange(u.Position, center, reach))
				return true;
		}
		return false;
	}

	// 滞留蓝图看门狗：90 秒仍是 Blueprint 就取消退款重试
	private void TickBotBlueprintWatchdog(int team)
	{
		var brain = GetBrain(team);
		if (brain.BlueprintTicks.Count == 0)
			return;
		int now = LockstepManager.Instance?.CurrentTick ?? 0;

		foreach (int id in brain.BlueprintTicks.Keys.ToList())
		{
			if (!brain.BlueprintTicks.TryGetValue(id, out int sinceTick))
				continue;
			var sim = World.FindSimEntity(id) as SimStructure;
			if (sim == null || sim.IsDead || sim.TeamID != team ||
				sim.CurrentState != SimStructure.StructureState.Blueprint)
			{
				brain.BlueprintTicks.Remove(id);
				continue;
			}
			if (now - sinceTick <= 1800)
				continue;
			brain.BlueprintTicks.Remove(id);
			var node = FindEntityById(id);
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (node is RTS.Units.Structure st && GodotObject.IsInstanceValid(st))
					st.CancelConstruction();
			});
		}
	}

	// 面板凭空建造（纳米/植物共用内核，两族各自校验自己的菌毯/资源类型）：
	// 需己方菌毯、扣资源、主线程生成后直接施工
	private bool TryBotPanelBuild(int team, string structName, FPVector2 centerFP)
	{
		var cfg = ConfigDatabase.GetStructure(structName);
		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (cfg == null || player?.PlayerData == null)
			return false;
		if (cfg.IsUnique && PlayerOwnsStructure(team, structName))
			return false;
		foreach (string req in cfg.RequiredTechIds)
			if (!player.PlayerData.HasTech(req))
				return false;

		int size = System.Math.Max(cfg.GridWidth, cfg.GridHeight);
		var topLeft = BotGridTopLeft(centerFP, size);
		if (!World.Grid.IsAreaPlaceable(topLeft, size) ||
			World.IsAreaOccupiedByStructure(topLeft, size) ||
			World.IsAreaNearResourceNode(topLeft, size, 2))
			return false;
		if (cfg.RequiredCreepType != CreepType.Any)
		{
			for (int dx = 0; dx < size; dx++)
				for (int dy = 0; dy < size; dy++)
					if (World.CreepGrid.GetActiveCreep(topLeft.X + dx, topLeft.Y + dy) != cfg.RequiredCreepType ||
						World.CreepGrid.GetOwner(topLeft.X + dx, topLeft.Y + dy) != team)
						return false;
		}
		if (!player.PlayerData.TryConsumeResources(cfg.Costs))
			return false;

		string spawnName = structName;
		int spawnTeam = team;
		float spawnX = (float)centerFP.X;
		float spawnY = (float)centerFP.Y;
		var costRef = cfg.Costs;
		RTS.Core.SimEventQueue.EnqueueMain(() =>
		{
			var ent = EntitySpawner.Instance?.SpawnEntity(
				spawnName, spawnTeam, new FPVector2((FP)spawnX, (FP)spawnY));
			if (ent is RTS.Units.Structure structure)
			{
				structure.InitAsBlueprint(costRef);
				structure.PromoteFromBlueprint();
			}
		});
		return true;
	}

	private IEntity PickIdleBotBuilder(int team, string structName, FPVector2 targetPos)
	{
		// 目标距离区间 3~10 格：太近会让寻路终点压蓝图，太远不优先
		FP minDSq = (FP)(3 * 64) * (FP)(3 * 64);
		// Shepherd 蓝图放宽到 40 格：蓝图常放出生点外，而工人可能在采矿点，
		// 10 格内没工人 → 只能选超远工人 → 途中被打断 → 蓝图 0% 滞留。
		// 40 格内选最近工人，保证 Sheperd 蓝图施工有人接。
		FP maxDSq = structName.StartsWith("Blueprint_Shepherd")
			? (FP)(40 * 64) * (FP)(40 * 64)
			: (FP)(10 * 64) * (FP)(10 * 64);
		IEntity bestIdle = null;
		FP bestIdleDistSq = FP.MaxValue;
		IEntity bestHarvester = null;
		FP bestHarvesterDistSq = FP.MaxValue;

		foreach (var simUnit in World.Units.Values)
		{
			if (simUnit == null || simUnit.IsDead || simUnit.TeamID != team ||
				simUnit.CombatTargetId >= 0)
				continue;
			var node = FindEntityById(simUnit.ID);
			if (IsBotWorkerBusy(node, simUnit) ||
				node.Brain.GetAction<RTS.Actions.Implementation.BuildAction>("Build_" + structName) == null ||
				node.Brain.GetActiveBuildTarget() != null)
				continue;

			FP dx = simUnit.Position.X - targetPos.X;
			FP dy = simUnit.Position.Y - targetPos.Y;
			FP distSq = dx * dx + dy * dy;
			if (distSq < minDSq)
				continue;
			bool inRange = distSq <= maxDSq;

			if (node.Brain.GetActiveHarvestSource() == null)
			{
				if ((inRange && distSq < bestIdleDistSq) ||
					(distSq == bestIdleDistSq && (bestIdle == null || simUnit.ID < bestIdle.LogicEntity.ID)))
				{
					bestIdleDistSq = distSq;
					bestIdle = node;
				}
			}
			else if ((inRange && distSq < bestHarvesterDistSq) ||
				(distSq == bestHarvesterDistSq && (bestHarvester == null || simUnit.ID < bestHarvester.LogicEntity.ID)))
			{
				bestHarvesterDistSq = distSq;
				bestHarvester = node;
			}
		}
		return bestIdle ?? bestHarvester ?? PickFarBotBuilder(team, structName, targetPos);
	}

	private IEntity PickFarBotBuilder(int team, string structName, FPVector2 targetPos)
	{
		IEntity best = null;
		FP bestDistSq = FP.MaxValue;
		foreach (var simUnit in World.Units.Values)
		{
			if (simUnit == null || simUnit.IsDead || simUnit.TeamID != team ||
				simUnit.CombatTargetId >= 0)
				continue;
			var node = FindEntityById(simUnit.ID);
			if (IsBotWorkerBusy(node, simUnit) ||
				node.Brain.GetAction<RTS.Actions.Implementation.BuildAction>("Build_" + structName) == null ||
				node.Brain.GetActiveBuildTarget() != null)
				continue;
			FP dx = simUnit.Position.X - targetPos.X;
			FP dy = simUnit.Position.Y - targetPos.Y;
			FP distSq = dx * dx + dy * dy;
			if (distSq < bestDistSq ||
				(distSq == bestDistSq && (best == null || simUnit.ID < best.LogicEntity.ID)))
			{
				bestDistSq = distSq;
				best = node;
			}
		}
		return best;
	}

	// =========================================================
	// 生产：先补经济单位（随主基地数量增长），再按兵种混编训练
	// =========================================================
	private void TickBotProduction(int team, RaceConfig raceCfg, int currentTick)
	{
		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (player?.PlayerData == null)
			return;

		// 补给站施工且人口余量不足：暂停出兵，避免“钱全被兵吃掉、补给站建不成”死循环
		int supplyFree = player.PlayerData.GetMaxSupply() - player.PlayerData.GetUsedSupply();
		if (supplyFree < 4 && IsSupplyBuildingPending(team, raceCfg))
			return;

		// 早期兵力引导：第一个生产建筑落地后先攒 2 个攻击兵
		int attackerCount = CountAttackers(team);
		bool needEarlyArmy = attackerCount < 2 && HasActiveMilitaryProduction(team, raceCfg);
		int supportCount = CountSupportUnits(team);

		string workerId = raceCfg.AiWorkerUnitId;
		int workerCount = !string.IsNullOrEmpty(workerId) ? CountOwnUnits(team, workerId) : 0;
		int queuedWorkers = 0;
		if (!string.IsNullOrEmpty(workerId))
		{
			foreach (var simStruct in World.Structures.Values)
			{
				if (simStruct == null || simStruct.IsDead || simStruct.TeamID != team ||
					simStruct.CurrentState != SimStructure.StructureState.Active)
					continue;
				var structureNode = FindEntityById(simStruct.ID);
				if (structureNode?.Brain == null)
					continue;
				if (structureNode.Brain.GetActiveProductionAction() is RTS.Actions.Implementation.TrainUnitAction ta &&
					ta.UnitName == workerId)
					queuedWorkers++;
				foreach (var queued in structureNode.Brain.GetProductionQueueSnapshot())
					if (queued.ActionName == workerId)
						queuedWorkers++;
			}
		}

		// 分矿也要农民：工人目标随主基地数量增长（第 2 个基地起每个 +4）
		int mainBaseCount = CountStructures(team, s =>
		{
			var cfg = ConfigDatabase.GetStructure(s.StructureTypeId);
			return cfg != null && cfg.IsMainBase;
		});
		int workerTarget = raceCfg.AiWorkerTarget + System.Math.Max(0, mainBaseCount - 1) * 4;
		bool workerOk = !string.IsNullOrEmpty(workerId) &&
			workerCount + queuedWorkers < workerTarget;

		foreach (var simStruct in World.Structures.Values)
		{
			if (simStruct == null || simStruct.IsDead || simStruct.TeamID != team ||
				simStruct.CurrentState != SimStructure.StructureState.Active)
				continue;
			var structureNode = FindEntityById(simStruct.ID);
			if (structureNode == null || structureNode.Brain == null)
				continue;
			var cfg = ConfigDatabase.GetStructure(simStruct.StructureTypeId);
			if (cfg == null || cfg.TrainableUnitIds.Count == 0)
				continue;

			int queued = (structureNode.Brain.GetActiveProductionAction() != null ? 1 : 0)
				+ structureNode.Brain.GetProductionQueueCount();
			if (queued >= Mathf.Max(1, cfg.ProductionQueueSize))
				continue;

			string unitId = "";
			if (workerOk && cfg.CanTrainUnit(workerId) && !needEarlyArmy)
				unitId = workerId;
			if (unitId.Length == 0 && raceCfg.AiUnitMixIds.Count > 0)
			{
				int mixIdx = ((currentTick / BotTickInterval) + simStruct.ID) % raceCfg.AiUnitMixIds.Count;
				string fallback = "";
				for (int offset = 0; offset < raceCfg.AiUnitMixIds.Count; offset++)
				{
					string candidate = raceCfg.AiUnitMixIds[(mixIdx + offset) % raceCfg.AiUnitMixIds.Count];
					if (!cfg.CanTrainUnit(candidate))
						continue;
					var trainAction = structureNode.Brain.GetAction<RTS.Actions.Implementation.TrainUnitAction>(candidate);
					if (trainAction == null || !trainAction.CanExecute() || !player.PlayerData.HasResources(trainAction.Costs))
						continue;
					var ucfg = ConfigDatabase.GetUnit(candidate);
					if (ucfg == null)
						continue;
					if (GetBotStrategy(team)?.CanTrain(this, team, ucfg) != true)
						continue;
					if (needEarlyArmy && ucfg.WeaponIds.Count == 0)
						continue;
					if (ucfg.CanHeal && supportCount >= System.Math.Max(1, attackerCount / 2))
						continue;
					if (fallback.Length == 0)
						fallback = candidate;
					var brain = GetBrain(team);
					if (brain.LastTrain == candidate)
						continue;
					unitId = candidate;
					break;
				}
				if (unitId.Length == 0)
					unitId = fallback;
			}
			if (unitId.Length == 0 && workerOk && cfg.CanTrainUnit(workerId))
				unitId = workerId;

			if (unitId.Length > 0)
			{
				var action = structureNode.Brain.GetAction<RTS.Actions.Implementation.TrainUnitAction>(unitId);
				if (action == null || !action.CanExecute()) continue;
				structureNode.Brain.StartAction(unitId);
				int after = (structureNode.Brain.GetActiveProductionAction() != null ? 1 : 0) +
					structureNode.Brain.GetProductionQueueCount();
				if (after <= queued) continue;
				GetBrain(team).LastTrain = unitId;
				if (unitId == workerId)
				{
					queuedWorkers++;
					workerOk = workerCount + queuedWorkers < workerTarget;
				}
			}
		}
	}

	private bool HasActiveMilitaryProduction(int team, RaceConfig raceCfg)
	{
		foreach (var simStruct in World.Structures.Values)
		{
			if (simStruct == null || simStruct.IsDead || simStruct.TeamID != team ||
				simStruct.CurrentState != SimStructure.StructureState.Active)
				continue;
			var cfg = ConfigDatabase.GetStructure(simStruct.StructureTypeId);
			if (cfg == null || !cfg.IsProductionBuilding || cfg.TrainableUnitIds.Count == 0)
				continue;
			foreach (string unitId in cfg.TrainableUnitIds)
			{
				var ucfg = ConfigDatabase.GetUnit(unitId);
				if (ucfg != null && !ucfg.IsWorker)
					return true;
			}
		}
		return false;
	}

	private bool IsSupplyBuildingPending(int team, RaceConfig raceCfg)
	{
		foreach (string sid in raceCfg.AvailableStructureIds)
		{
			var cfg = ConfigDatabase.GetStructure(sid);
			if (cfg == null || cfg.SupplyProvided <= 0 || cfg.IsMainBase)
				continue;
			foreach (var s in World.Structures.Values)
			{
				if (s != null && !s.IsDead && s.TeamID == team &&
					s.StructureTypeId == sid &&
					s.CurrentState != SimStructure.StructureState.Active)
					return true;
			}
		}
		return false;
	}

	// =========================================================
	// 科技：按 AiTechOrderIds 顺序研究第一项可研究的
	// =========================================================
	private void TickBotTech(int team, RaceConfig raceCfg)
	{
		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (player?.PlayerData == null || player.PlayerData.IsResearching ||
			raceCfg.AiTechOrderIds.Count == 0)
			return;

		foreach (string techId in raceCfg.AiTechOrderIds)
		{
			if (player.PlayerData.HasTech(techId))
				continue;
			foreach (var executor in BotResearchExecutors(team))
			{
				var action = executor?.Brain?.GetAction<RTS.Actions.Implementation.ResearchAction>("Research_" + techId);
				if (action == null || !action.CanExecute()) continue;
				// ResearchAction describes availability; research itself is player-owned state.
				var tech = ConfigDatabase.GetTech(techId);
				var costs = RTS.Actions.Implementation.ResearchAction.GetScaledCost(tech, player.PlayerData);
				if (!player.PlayerData.TryConsumeResources(costs)) continue;
				if (CheatInstantResearch)
				{
					player.PlayerData.GrantTech(techId);
					TechEffects.ApplyToPlayer(player);
				}
				else player.PlayerData.StartResearch(techId, tech.ResearchTimeSeconds);
				return;
			}
		}
	}
}
}
