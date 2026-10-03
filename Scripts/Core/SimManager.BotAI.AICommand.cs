using System;
using System.Collections.Generic;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Network;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
public partial class SimManager
{
	// AI 指挥系统：基地车坐地成指挥核心，之后靠工厂产无人机/飞机。
	// 两个族特有的动作 AI 必须自己做，否则"有机制但永远不用"：
	//   · AIDeploy —— 基地车 ⇄ 指挥核心 的形态转换；
	//   · ClusterRelease —— 武库鸟把库存按秒投出去。
	//
	// 其余（建造/生产/科技/波次）走通用管线，配置来自 AICommand.tres。
	private sealed class AICommandBotStrategy : BotRaceStrategy
	{
		public override void Tick(SimManager sim, BotBrain b, int tick)
		{
			sim.TickBotAIDeploy(b.Team);
			sim.TickBotArsenalRelease(b.Team, tick);
			sim.TickBotCombat(b.Team, tick);
			sim.TickBotBuild(b.Team, b.RaceCfg, tick);
			sim.TickBotBlueprintWatchdog(b.Team);
			sim.TickBotEconomy(b.Team, b.RaceCfg);
			sim.TickBotProduction(b.Team, b.RaceCfg, tick);
			sim.TickBotTech(b.Team, b.RaceCfg);
			sim.TickBotExpansion(b.Team, b.RaceCfg, tick);
		}

		// 无人机是消耗品：不为了等血/等弹药把它们留在后方。
		public override bool ShouldAdvance(SimManager sim, int team, bool proposed)
			=> proposed || sim.IsBaseUnderAttack(team);
	}

	/// <summary>
	/// 基地车坐地 / 指挥核心收起。
	///
	/// 为什么要 AI 显式做：AIDeploy 是**面板动作**，通用建造/生产管线
	/// 完全不碰它 —— 不写这段，AI 的基地车会永远是一辆不能建造的车。
	/// 收起只在"核心受到威胁且车能跑"时才做，避免来回变形。
	/// </summary>
	private void TickBotAIDeploy(int team)
	{
		// ---- 基地车 → 指挥核心（开局第一件事）----
		foreach (var unit in World.Units.Values)
		{
			if (unit == null || unit.IsDead || unit.TeamID != team)
				continue;
			var cfg = ConfigDatabase.GetUnit(unit.UnitTypeId);
			if (cfg == null || !cfg.CanDeploy || string.IsNullOrEmpty(cfg.DeployStructureId))
				continue;
			if (unit.IsDeployed || unit.IsDeployBusy || unit.DeployState == 2)
				continue;
			// 正在移动就先让它走完（CommandMove 会被 StartAction 打断）
			if (unit.HasTarget || unit.PathPending)
				continue;
			var node = FindEntityById(unit.ID);
			if (node?.Brain == null || !node.Brain.HasCachedAction("AIDeploy"))
				continue;
			node.Brain.StartAction("AIDeploy");
			GetBrain(team).ReservedOrders.Add(unit.ID);
			return;
		}

		// ---- 指挥核心 → 基地车（基地被拆到没血了才跑）----
		if (!IsBaseUnderAttack(team))
			return;
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != team ||
				s.CurrentState != SimStructure.StructureState.Active)
				continue;
			var cfg = ConfigDatabase.GetStructure(s.StructureTypeId);
			if (cfg == null || (cfg.PanelSkillMode & 65536) == 0)
				continue;
			if (s.Hp > s.MaxHp / (FP)3)
				continue;   // 还有血就守着，别把生产建筑变成车
			var node = FindEntityById(s.ID);
			if (node?.Brain == null || !node.Brain.HasCachedAction("AIDeploy"))
				continue;
			HandleAIDeploy(new NetAction
			{
				PlayerID = team,
				ActionId = "AIDeploy",
				EntityIDs = new[] { s.ID },
				TargetEntityID = -1,
			});
			return;
		}
	}

	/// <summary>
	/// 武库鸟投放：库存够多、且要么正在交战、要么正在推进时才放。
	/// 造价减半的库存攒得慢，见敌就倒空会让它一路裸奔。
	/// </summary>
	private void TickBotArsenalRelease(int team, int currentTick)
	{
		int phase = (team * 7) % BotTickInterval;
		if ((currentTick + phase) % BotTickInterval != 0)
			return;

		foreach (var bird in World.Units.Values)
		{
			if (bird == null || bird.IsDead || bird.TeamID != team)
				continue;
			var cfg = ConfigDatabase.GetUnit(bird.UnitTypeId);
			if (cfg == null || cfg.ProducesToInventoryIds.Count == 0)
				continue;
			if (bird.InventoryTotal <= 0 || bird.ClusterReleaseActive)
				continue;
			if (bird.ClusterReleaseCooldown > FP.Zero)
				continue;
			// 库存到一半以上才放：留出持续作战能力
			int capacity = cfg.InventoryCapacity > 0 ? cfg.InventoryCapacity : 10;
			if (bird.InventoryTotal * 2 < capacity)
				continue;

			bool engaged = bird.CombatTargetId >= 0 || IsBaseUnderAttack(team);
			if (!engaged)
				continue;

			// 投放点：朝最近敌人的方向外推 2 格（无人机自己会继续压上）
			FPVector2 drop = bird.Position;
			var enemy = World.FindNearestUnit(bird.Position, FP.MaxValue,
				u => u.ID != bird.ID && u.TeamID > 0 && AreTeamsHostile(bird.TeamID, u.TeamID), bird.ID);
			if (enemy != null)
			{
				FPVector2 dir = (enemy.Position - bird.Position).Normalized();
				if (dir.X != FP.Zero || dir.Y != FP.Zero)
					drop = bird.Position + dir * (FP)(2 * World.Grid.TileSize);
			}
			HandleClusterRelease(new NetAction
			{
				PlayerID = team,
				ActionId = "ClusterRelease",
				EntityIDs = new[] { bird.ID },
				TargetX = (long)(drop.X * (FP)1000m),
				TargetY = (long)(drop.Y * (FP)1000m),
				TargetEntityID = -1,
			});
		}
	}
}
}
