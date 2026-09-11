using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Network;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
public partial class SimManager
{
	// One irreplaceable behemoth, carpet income and turret expansion. No worker/army production.
	private sealed class NanoBotStrategy : BotRaceStrategy
	{
		public override bool PanelConstruction => true;
		public override void Tick(SimManager sim, BotBrain b, int tick)
		{
			sim.TickBotCombat(b.Team, tick);
			sim.TickBotNanoSpread(b.Team, tick);
			sim.TickBotBuild(b.Team, b.RaceCfg, tick);
			sim.TickBotTech(b.Team, b.RaceCfg);
			sim.TickBotNanoDefenseTowers(b.Team, tick);
			sim.TickBotExpansion(b.Team, b.RaceCfg, tick);
		}
		public override bool ShouldAdvance(SimManager sim, int team, bool proposed) => sim.IsBaseUnderAttack(team);
		public override bool CanOrder(SimManager sim, int team, SimUnit unit, FPVector2 target)
		{
			if (unit.Hp < unit.MaxHp * (FP)0.6m) return false;
			if (TeamHasTech(team, "NanoTech_MobilityA")) return true;
			// Check the corridor, not just the target: outside carpet the behemoth cannot move.
			var delta = target - unit.Position;
			int steps = Math.Max(1, (int)(delta.Magnitude() / (FP)sim.World.Grid.TileSize) + 1);
			for (int i = 0; i <= steps; i++)
			{
				var cell = sim.World.Grid.WorldToGrid(unit.Position + delta * (FP)i / (FP)steps);
				if (!sim.IsOwnNanoCreep(team, cell.X, cell.Y)) return false;
			}
			return true;
		}
		public override bool HandleUnit(SimManager sim, int team, IEntity node, SimUnit unit, bool advancing)
		{
			if (unit.Hp >= unit.MaxHp * (FP)0.6m) return false;
			EntityExtensions.CommandMoveTo(node, sim.GetTeamMainBasePos(team));
			return true;
		}
		public override bool CanRaid(SimManager sim, int team, SimStructure tower, int tick, bool continuing)
		{
			var behemoth = sim.World.Units.Values.FirstOrDefault(u => !u.IsDead && u.TeamID == team && u.UnitTypeId == "NanoBehemoth");
			if (behemoth == null || behemoth.Hp < behemoth.MaxHp * (continuing ? (FP)0.6m : (FP)0.85m)) return false;
			float towerPower = CombatRating.ComputeStructureRating(ConfigDatabase.GetStructure(tower.StructureTypeId));
			return CanOrder(sim, team, behemoth, tower.Position) && sim.GetBotArmyPower(team, tick) >= towerPower * 1.5f;
		}
	}

	private void TickBotNanoDefenseTowers(int team, int currentTick)
	{
		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (player?.PlayerData == null)
			return;
		// 有钱 500+ 就飞快铺塔：每个决策 tick（1.5 秒）都试一次，靠资源自然限速
		if (player.PlayerData.GetResource(ResourceType.NanoBots) < 500f)
			return;

		var candidates = new List<SimVector2I>();
		FPVector2 basePos = GetTeamMainBasePos(team);
		if (basePos.X == FP.Zero && basePos.Y == FP.Zero)
			basePos = GetTeamCentroid(team);
		int baseGx = (int)FP.Floor(basePos.X / (FP)World.Grid.TileSize);
		int baseGy = (int)FP.Floor(basePos.Y / (FP)World.Grid.TileSize);

		// 菌毯长多大就扫多大（与铺毯同一取窗口方式）
		int far = 0;
		foreach (var kvp in World.CreepGrid.GetAllCellData())
		{
			if (kvp.Value.Type != CreepType.NanoCreep || kvp.Value.OwnerTeam != team)
				continue;
			int d = System.Math.Max(System.Math.Abs(kvp.Key.X - baseGx), System.Math.Abs(kvp.Key.Y - baseGy));
			if (d > far) far = d;
		}
		int scan = far + 2;
		for (int y = baseGy - scan; y <= baseGy + scan; y++)
		{
			for (int x = baseGx - scan; x <= baseGx + scan; x++)
			{
				if (!IsOwnNanoCreep(team, x, y) || !HasOpenNeighbor(x, y))
					continue;
				// 四塔一簇：2×2 炮台块占 4×4，整块都必须是自己菌毯
				bool clusterOk = true;
				for (int cx = 0; cx < 4 && clusterOk; cx++)
					for (int cy = 0; cy < 4; cy++)
						if (!IsOwnNanoCreep(team, x + cx, y + cy))
						{
							clusterOk = false;
							break;
						}
				if (!clusterOk)
					continue;
				// 附近 10 格内己方炮台已满 4 座（完整簇）→ 不是薄弱点，跳过；
				// 1~3 座说明是没铺完的簇，允许补完
				if (CountOwnTowersNear(team, x + 1, y + 1, 10) >= 4)
					continue;
				candidates.Add(new SimVector2I(x, y));
			}
		}
		if (candidates.Count == 0)
			return;

		// 一次 4 座塔挨成一簇（2×2 排列），簇位从薄弱点里确定性伪随机挑一个
		int pick = (int)((uint)(currentTick * 7919 + team * 104729) % (uint)candidates.Count);
		var c = candidates[pick];
		// 类型：前 3 座从机炮/防空/狙击里随机，第 4 座是烟雾或活性化塔（已研究才随机到）
		string[] combatPool = { "NanoTurret", "NanoAA", "NanoSniper" };
		var types = new string[4];
		for (int i = 0; i < 3; i++)
			types[i] = combatPool[(int)((uint)(currentTick * 7919 + team * 104729 + i * 31337) % 3u)];
		var specialPool = new List<string>();
		if (player.PlayerData.HasTech("NanoTech_GeneralA"))
			specialPool.Add("NanoActiveTower");
		if (player.PlayerData.HasTech("NanoTech_GeneralB"))
			specialPool.Add("NanoSmokeTower");
		if (specialPool.Count > 0)
			types[3] = specialPool[(int)((uint)(currentTick * 7919 + team * 104729 + 97) % (uint)specialPool.Count)];
		else
			types[3] = combatPool[(int)((uint)(currentTick * 7919 + team * 104729 + 97) % 3u)];
		for (int i = 0; i < 4; i++)
		{
			if (player.PlayerData.GetResource(ResourceType.NanoBots) < 150f)
				break;
			int ox = (i % 2) * 2;
			int oy = (i / 2) * 2;
			TryBotPanelBuild(team, types[i],
				new FPVector2((FP)((c.X + ox) * World.Grid.TileSize + 32), (FP)((c.Y + oy) * World.Grid.TileSize + 32)));
		}
	}

	private int CountOwnTowersNear(int team, int gx, int gy, int tiles)
	{
		int n = 0;
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != team)
				continue;
			var cfg = ConfigDatabase.GetStructure(s.StructureTypeId);
			if (cfg == null || (!cfg.IsDefenseBuilding && !cfg.IsInteractiveBuilding))
				continue;
			int dx = System.Math.Abs(s.GridPosition.X + s.GridWidth / 2 - gx);
			int dy = System.Math.Abs(s.GridPosition.Y + s.GridHeight / 2 - gy);
			if (dx <= tiles && dy <= tiles)
				n++;
		}
		return n;
	}

	private void TickBotNanoSpread(int team, int currentTick)
	{
		int spreadInterval = BotTickInterval * 3;
		// 铺摊必须与队伍错峰相位对齐：决策 tick 本身带 (team*7)%30 相位，
		// 直接判 currentTick%90 会永远不成立（90 是 30 的整数倍），纳米一张毯都铺不出去。
		int phase = (team * 7) % BotTickInterval;
		if ((currentTick + phase) % spreadInterval != 0)
			return;
		if (World.CarpetSpreadCharges <= 0)
			return;

		// 在菌毯外缘找“10 格铺毯圆内新格最多”的己方菌毯格：
		// 天然选在朝开阔方向的最外沿，不重复铺旧毯，也不会在地图边缘/墙边浪费充能
		if (!TryFindBestSpreadCell(team, out int gx, out int gy))
			return;
		TryBotNanoSpread(team, new FPVector2((FP)(gx * World.Grid.TileSize + 32), (FP)(gy * World.Grid.TileSize + 32)));
	}

	private bool TryFindBestSpreadCell(int team, out int bestGx, out int bestGy)
	{
		bestGx = 0;
		bestGy = 0;
		FPVector2 basePos = GetTeamMainBasePos(team);
		if (basePos.X == FP.Zero && basePos.Y == FP.Zero)
			basePos = GetTeamCentroid(team);
		int baseGx = (int)FP.Floor(basePos.X / (FP)World.Grid.TileSize);
		int baseGy = (int)FP.Floor(basePos.Y / (FP)World.Grid.TileSize);

		// 菌毯长多大就扫多大：最远菌毯格 + 2 格为扫描半径
		int far = 0;
		foreach (var kvp in World.CreepGrid.GetAllCellData())
		{
			if (kvp.Value.Type != CreepType.NanoCreep || kvp.Value.OwnerTeam != team)
				continue;
			int d = System.Math.Max(System.Math.Abs(kvp.Key.X - baseGx), System.Math.Abs(kvp.Key.Y - baseGy));
			if (d > far) far = d;
		}
		int scan = far + 2;
		int best = 0;
		bool found = false;
		for (int y = baseGy - scan; y <= baseGy + scan; y++)
		{
			for (int x = baseGx - scan; x <= baseGx + scan; x++)
			{
				if (!IsOwnNanoCreep(team, x, y))
					continue;
				// 内圈格四邻都是毯子，铺毯圆大半重叠旧毯，跳过加速
				if (!HasOpenNeighbor(x, y))
					continue;
				// 总分 = 铺毯新格数 - 离家距离的一半（防御性文明：老家周围优先，
				// 但远处潜力足够大时仍会外扩）
				int distTiles = System.Math.Max(System.Math.Abs(x - baseGx), System.Math.Abs(y - baseGy));
				int p = CountSpreadPotential(team, x, y) - distTiles / 2;
				if (p > best)
				{
					best = p;
					bestGx = x;
					bestGy = y;
					found = true;
				}
			}
		}
		return found;
	}

	private bool HasOpenNeighbor(int gx, int gy)
	{
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dy = -1; dy <= 1; dy++)
			{
				if (dx == 0 && dy == 0)
					continue;
				var c = new SimVector2I(gx + dx, gy + dy);
				if (!World.Grid.TerrainCells.Contains(c))
					continue;
				if (World.Grid.StaticObstacles.Contains(c))
					continue;
				if (World.CreepGrid.GetActiveCreep(gx + dx, gy + dy) == CreepType.None)
					return true;
			}
		}
		return false;
	}

	private static void GetNanoSpreadOffset(int dir, FP dist, out FP x, out FP y)
	{
		x = FP.Zero;
		y = FP.Zero;
		switch (dir % 8)
		{
			case 0: x = dist; break;
			case 1: x = dist; y = dist; break;
			case 2: y = dist; break;
			case 3: x = -dist; y = dist; break;
			case 4: x = -dist; break;
			case 5: x = -dist; y = -dist; break;
			case 6: y = -dist; break;
			default: x = dist; y = -dist; break;
		}
	}

	private bool TryBotNanoSpread(int team, FPVector2 centerFP)
	{
		int gx = (int)FP.Floor(centerFP.X / (FP)World.Grid.TileSize);
		int gy = (int)FP.Floor(centerFP.Y / (FP)World.Grid.TileSize);
		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (player?.PlayerData == null)
			return false;

		// 目标点必须是己方菌毯；不是就在周围找“朝向目标的最外侧”菌毯格施放。
		// 选外缘（离基地最远）而不是最近格，扩展的 10 格大部分朝外，尽量不重叠旧毯。
		if (!IsOwnNanoCreep(team, gx, gy))
		{
			if (!TryFindOuterCreepCell(team, gx, gy, 12, GetTeamMainBasePos(team), out gx, out gy) &&
				!TryFindOwnCreepNear(team, gx, gy, 12, out gx, out gy))
				return false;
		}

		// 撞墙/重叠检测：扩展圆内“无任何菌毯且地形可铺”的新格不足 15 个就不铺。
		// 否则充能会全浪费在地图边缘/墙边，菌毯总数不再增长（实测卡 1315 格）。
		// 阈值不能太高：出生点区域被墙切成窄路时，10 格圆内多数是墙，潜力天然低。
		if (CountSpreadPotential(team, gx, gy) < 15)
			return false;

		// 防连续在同一处重复铺：上次铺毯点 5 格内不再铺，等菌毯外缘推进
		var brain = GetBrain(team);
		if (brain.NanoLastSpreadGx != int.MinValue)
		{
			int dx = gx - brain.NanoLastSpreadGx;
			int dy = gy - brain.NanoLastSpreadGy;
			if (dx * dx + dy * dy < 5 * 5)
				return false;
		}

		// 模拟线程：不能 new Godot.Collections.Dictionary（Handle is not initialized 崩溃来源）
		float nanoCost = (float)(World.CarpetSpreadCost * (FP)TechEffects.GetSpreadCostMultiplier(player.PlayerData));
		if (!player.PlayerData.TryConsumeResources(ResourceType.NanoBots, nanoCost))
			return false;
		if (!World.StartCarpetSpread(gx, gy, team))
			return false;
		brain.NanoLastSpreadGx = gx;
		brain.NanoLastSpreadGy = gy;
		World.CarpetSpreadCharges--;
		World.CarpetSpreadCooldown = World.CarpetSpreadCooldownTime *
			(FP)TechEffects.GetSpreadCooldownMultiplier(player.PlayerData);
				return true;
	}

	private int CountSpreadPotential(int team, int gx, int gy)
	{
		int scan = (int)FP.Round(World.CarpetSpreadRadiusTiles);
		if (scan <= 0)
			return 0;
		int n = 0;
		for (int x = gx - scan; x <= gx + scan; x++)
		{
			for (int y = gy - scan; y <= gy + scan; y++)
			{
				int dx = x - gx;
				int dy = y - gy;
				if (dx * dx + dy * dy > scan * scan)
					continue;
				var cell = new SimVector2I(x, y);
				if (!World.Grid.TerrainCells.Contains(cell))
					continue;
				if (World.Grid.StaticObstacles.Contains(cell))
					continue;
				if (World.CreepGrid.GetActiveCreep(x, y) != CreepType.None)
					continue;
				n++;
			}
		}
		return n;
	}

	private bool IsOwnNanoCreep(int team, int gx, int gy)
	{
		return World.CreepGrid.GetActiveCreep(gx, gy) == CreepType.NanoCreep &&
			World.CreepGrid.GetOwner(gx, gy) == team;
	}

	private bool TryFindOuterCreepCell(
		int team, int cx, int cy, int maxTiles, FPVector2 basePos, out int ox, out int oy)
	{
		ox = cx;
		oy = cy;
		bool found = false;
		FP bestDistSq = FP.MinValue;
		for (int d = 0; d <= maxTiles; d++)
		{
			for (int dx = -d; dx <= d; dx++)
			{
				for (int dy = -d; dy <= d; dy++)
				{
					if (d > 0 && System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != d)
						continue;
					if (!IsOwnNanoCreep(team, cx + dx, cy + dy))
						continue;
					FP cellDist = FPVector2.DistanceSquared(
						new FPVector2((FP)((cx + dx) * 64 + 32), (FP)((cy + dy) * 64 + 32)),
						basePos);
					if (cellDist > bestDistSq)
					{
						bestDistSq = cellDist;
						ox = cx + dx;
						oy = cy + dy;
						found = true;
					}
				}
			}
		}
		return found;
	}

	private bool TryFindOwnCreepNear(int team, int cx, int cy, int maxTiles, out int ox, out int oy)
	{
		for (int d = 0; d <= maxTiles; d++)
		{
			for (int dx = -d; dx <= d; dx++)
			{
				for (int dy = -d; dy <= d; dy++)
				{
					if (d > 0 && System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != d)
						continue;
					if (IsOwnNanoCreep(team, cx + dx, cy + dy))
					{
						ox = cx + dx;
						oy = cy + dy;
						return true;
					}
				}
			}
		}
		ox = cx;
		oy = cy;
		return false;
	}

}
}
