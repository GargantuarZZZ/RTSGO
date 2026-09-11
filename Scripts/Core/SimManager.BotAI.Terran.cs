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
	// Supply logistics precede waves. Difficulty changes income, never these rules.
	private sealed class TerranBotStrategy : BotRaceStrategy
	{
		public override void Tick(SimManager sim, BotBrain b, int tick)
		{
			sim.TickBotCombat(b.Team, tick);
			sim.TickBotBuild(b.Team, b.RaceCfg, tick);
			sim.TickBotBlueprintWatchdog(b.Team);
			sim.TickBotTerranGarrison(b.Team);
			sim.TickBotEconomy(b.Team, b.RaceCfg);
			sim.TickBotProduction(b.Team, b.RaceCfg, tick);
			sim.TickBotTech(b.Team, b.RaceCfg);
			sim.TickBotExpansion(b.Team, b.RaceCfg, tick);
		}

		public override bool HandleUnit(SimManager sim, int team, IEntity node, SimUnit unit, bool advancing)
		{
			var resupplying = sim.GetBrain(team).Resupplying;
			if (unit.MaxAmmo > FP.Zero && unit.Ammo < unit.MaxAmmo * (FP)0.3m)
				resupplying.Add(unit.ID);
			if (resupplying.Contains(unit.ID))
			{
				if (unit.Ammo >= unit.MaxAmmo * (FP)0.8m)
					resupplying.Remove(unit.ID);
				else
				{
					if (!sim.IsTerranUnitInAmmoRange(unit))
					{
						var depot = sim.GetNearestAmmoDepotPos(team, unit);
						if ((depot.X != FP.Zero || depot.Y != FP.Zero)) EntityExtensions.CommandMoveTo(node, depot);
					}
					else EntityExtensions.TryAutoAttack(node, node.CombatModule.GetAttackRange());
					return true;
				}
			}
			return sim.HandleTerranDeploy(node, unit, advancing);
		}

		public override bool CanOrder(SimManager sim, int team, SimUnit unit, FPVector2 target)
			=> !unit.IsDeployed && !unit.IsDeployBusy && !sim.GetBrain(team).Resupplying.Contains(unit.ID);
	}

	private bool HandleTerranDeploy(IEntity node, SimUnit simUnit, bool shouldAdvance)
	{
		if (node?.Brain == null)
			return false;
		var deploy = node.Brain.GetAction<RTS.Actions.Implementation.DeployAction>("Deploy");
		if (deploy == null)
			return false;
		var cfg = ConfigDatabase.GetUnit(simUnit.UnitTypeId);
		if (cfg == null || !cfg.CanDeploy)
			return false;

		// 移动中的单位不架设（否则架设打断移动 → CommandMove 清 HasTarget → 走一步停住）
		if (simUnit.HasTarget || simUnit.PathPending)
			return false;

		// 接敌距离 = 当前射程 + 架设射程加成 + 1.5 格余量（架设要 1 秒，提前停脚）
		float range = node.CombatModule?.GetAttackRange() ?? 320f;
		float engageRange = Mathf.Max(320f, range + (float)simUnit.DeployRangeBonus + 96f);
		// 纯逻辑索敌（模拟线程安全）：只读 sim 数据，不碰 Godot 节点 API。
		// 解放者等仅架设武器单位只找地面敌人（避免对飞机架设），其余按武器类型过滤
		var nearEnemy = FindTerranDeployTarget(simUnit, engageRange, simUnit.DeployOnlyWeapon);

		// 已架设：需要推进且附近已无敌人则收起准备走，否则保持
		if (simUnit.IsDeployed)
		{
			// 只要当前可攻击范围内仍有可选中敌人就不取消架设
			float currentRange = node.CombatModule?.GetAttackRange() ?? 320f;
			var inRangeEnemy = FindTerranDeployTarget(simUnit, Mathf.Max(320f, currentRange), simUnit.DeployOnlyWeapon);
			if (shouldAdvance && inRangeEnemy == null)
			{
				node.Brain.StartAction("Deploy");
				return true;
			}
			return false;
		}
		if (simUnit.DeployState != 0)
			return true;

		if (nearEnemy != null)
		{
			FPVector2 aim = nearEnemy.LogicEntity != null
				? nearEnemy.LogicEntity.Position
				: simUnit.Position;
			node.Brain.StartAction("Deploy", aim, null);
			return true;
		}
		return false;
	}

	private IEntity FindTerranDeployTarget(SimUnit self, float range, bool groundOnly)
	{
		if (self == null)
			return null;

		FP rangeSq = (FP)(range * range);
		var nearest = World.FindNearestUnit(self.Position, rangeSq, u =>
		{
			if (u.ID == self.ID || u.TeamID <= 0)
				return false;
			if (!AreTeamsHostile(self.TeamID, u.TeamID))
				return false;
			if (groundOnly && u.IsAir)
				return false;
			return true;
		}, self.ID);

		return nearest != null ? FindEntityById(nearest.ID) : null;
	}

	private bool IsTerranUnitInAmmoRange(SimUnit u)
	{
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != u.TeamID ||
				s.CurrentState != SimStructure.StructureState.Active)
				continue;
			if (s.AmmoRangeTiles <= 0)
				continue;
			if (u.IsAir ? !s.AmmoRangeAffectsAir : s.AmmoRangeAffectsAir)
				continue;
			FP r = (FP)(s.AmmoRangeTiles * World.Grid.TileSize);
			if (FPVector2.IsWithinRange(s.Position, u.Position, r))
				return true;
		}
		return false;
	}

	private FPVector2 GetNearestAmmoDepotPos(int team, SimUnit u)
	{
		FP bestDist = FP.MaxValue;
		FPVector2 best = FPVector2.Zero;
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != team ||
				s.CurrentState != SimStructure.StructureState.Active)
				continue;
			if (s.AmmoRangeTiles <= 0)
				continue;
			if (u.IsAir ? !s.AmmoRangeAffectsAir : s.AmmoRangeAffectsAir)
				continue;
			FP d = FPVector2.DistanceSquared(s.Position, u.Position);
			if (d < bestDist)
			{
				bestDist = d;
				best = s.Position;
			}
		}
		return best;
	}

	private void TickBotTerranGarrison(int team)
	{
		// 先保采矿：场上采矿的工程师不足 3 个时不送进驻，
		// 避免 AI 把工程师全塞进藻类田导致金属断供（工人看着像闲置的另一面）
		int activeHarvesters = 0;
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team)
				continue;
			var node = FindEntityById(u.ID);
			if (node?.Brain?.GetActiveHarvestSource() != null)
				activeHarvesters++;
		}
		if (activeHarvesters < 3)
			return;

		foreach (var simStruct in World.Structures.Values)
		{
			if (simStruct == null || simStruct.IsDead || simStruct.TeamID != team ||
				simStruct.CurrentState != SimStructure.StructureState.Active)
				continue;
			var cfg = ConfigDatabase.GetStructure(simStruct.StructureTypeId);
			if (cfg == null || cfg.GarrisonIncomePerSecondPerUnit <= 0 || cfg.GarrisonCapacity <= 0 ||
				simStruct.GarrisonedCount >= cfg.GarrisonCapacity)
				continue;
			var factoryNode = FindEntityById(simStruct.ID);
			if (factoryNode == null)
				continue;
			foreach (var simUnit in World.Units.Values)
			{
				if (simUnit == null || simUnit.IsDead || simUnit.TeamID != team ||
					simUnit.HasTarget || simUnit.CombatTargetId >= 0)
					continue;
				var node = FindEntityById(simUnit.ID);
				if (IsBotWorkerBusy(node, simUnit) || simUnit.UnitTypeId != "Engineer" ||
					!node.Brain.HasCachedAction("Garrison"))
					continue;
				node.Brain.StartAction("Garrison", simStruct.Position, factoryNode, false, false);
				GetBrain(team).ReservedOrders.Add(simUnit.ID);
				return;
			}
		}
	}

}
}
