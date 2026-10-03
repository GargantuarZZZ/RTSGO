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
	// 巫师：出生单位弱，靠法术与召唤物顶线 —— 所以 AI 必须真的会放面板技能，
	// 否则这套族在 AI 手里等于没有机制。
	//
	// 与人类玩家走**同一条结算路径**：这里只负责"决定放什么、打哪"，
	// 真正的能量扣除/科技门/冷却/效果全部走 HandleWizardSkill ——
	// 保证 AI 与玩家受完全相同的规则约束（科技没研究就是放不出来）。
	private sealed class WizardBotStrategy : BotRaceStrategy
	{
		public override void Tick(SimManager sim, BotBrain b, int tick)
		{
			sim.TickBotCombat(b.Team, tick);
			sim.TickBotBuild(b.Team, b.RaceCfg, tick);
			sim.TickBotBlueprintWatchdog(b.Team);
			sim.TickBotEconomy(b.Team, b.RaceCfg);
			sim.TickBotProduction(b.Team, b.RaceCfg, tick);
			sim.TickBotTech(b.Team, b.RaceCfg);
			sim.TickBotWizardSkills(b.Team, tick);
			sim.TickBotExpansion(b.Team, b.RaceCfg, tick);
		}
	}

	// 面板技能的施放优先级：先范围杀伤，再召唤，最后辅助。
	// 顺序固定（不按 tick 轮换）是为了 AI 行为可预期、可复现。
	private static readonly string[] WizardSkillPriority =
	{
		"FireRain", "SummonStoneGolem", "SummonEarthGolem",
		"TimeFreeze", "TeleportField", "InspireMelody", "SolemnMelody", "WaterWall",
	};

	private void TickBotWizardSkills(int team, int currentTick)
	{
		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (player?.PlayerData == null)
			return;
		// 面板技能是"贵"的爆发：按固定间隔评估，避免每 tick 反复找目标
		int phase = (team * 7) % BotTickInterval;
		if ((currentTick + phase) % (BotTickInterval * 2) != 0)
			return;

		foreach (var simStruct in World.Structures.Values)
		{
			if (simStruct == null || simStruct.IsDead || simStruct.TeamID != team ||
				simStruct.CurrentState != SimStructure.StructureState.Active)
				continue;
			var cfg = ConfigDatabase.GetStructure(simStruct.StructureTypeId);
			if (cfg == null || cfg.PanelSkillMode == 0 || cfg.SkillRadiusTiles <= 0f)
				continue;
			// 能量不够付最便宜的那个技能就不评估了
			if (player.PlayerData.GetResource(ResourceType.Energy) < cfg.SkillEnergyCost)
				continue;

			foreach (string skill in WizardSkillPriority)
			{
				long bit = WizardSkillBit(skill);
				if (bit == 0 || (cfg.PanelSkillMode & bit) == 0)
					continue;
				// 冷却中
				if (simStruct.SkillCooldown > FP.Zero && (simStruct.SkillCooldownMask & bit) != 0)
					continue;
				// 科技门（与模拟侧硬校验同源，避免"AI 想放但放不出来"空转）
				if (cfg.SkillRequiredTechIds != null &&
					cfg.SkillRequiredTechIds.TryGetValue(skill, out var needTech) &&
					!TeamHasTech(team, needTech))
					continue;
				// 充能型：没充能就别试（避免每轮都挑中同一个放不出的技能）
				if (cfg.SkillMaxCharges > 1 &&
					simStruct.SkillChargeMask == bit && simStruct.SkillCharges <= 0)
					continue;

				if (!TryFindBotSkillTarget(team, simStruct, cfg, out var aim))
					continue;

				// 直接走玩家同款结算入口：不做资源/冷却的重复实现
				HandleWizardSkill(new NetAction
				{
					PlayerID = team,
					ActionId = skill,
					EntityIDs = new[] { simStruct.ID },
					TargetX = (long)(aim.X * (FP)1000m),
					TargetY = (long)(aim.Y * (FP)1000m),
					TargetEntityID = -1,
				});
				break;   // 一座建筑一轮只放一个技能
			}
		}
	}

	/// <summary>
	/// 选施法点：半径内敌人最密集的位置（并列时取 ID 最小的敌人，保证确定性）。
	/// 找不到任何敌方目标时，召唤类技能退化为"往己方主基地前方丢"，
	/// 否则巫师 AI 在没接敌时永远不会攒召唤物。
	/// </summary>
	private bool TryFindBotSkillTarget(int team, SimStructure castFrom, StructureConfig cfg, out FPVector2 aim)
	{
		aim = FPVector2.Zero;
		FP castRange = (FP)(cfg.SkillRadiusTiles * 3f * World.Grid.TileSize);
		FP radiusSq = (FP)(cfg.SkillRadiusTiles * World.Grid.TileSize) *
			(FP)(cfg.SkillRadiusTiles * World.Grid.TileSize);

		SimUnit best = null;
		int bestScore = -1;
		FP bestDist = FP.MaxValue;
		foreach (var enemy in World.Units.Values)
		{
			if (enemy == null || enemy.IsDead || enemy.TeamID <= 0)
				continue;
			if (!AreTeamsHostile(team, enemy.TeamID))
				continue;
			if (!FPVector2.IsWithinRange(castFrom.Position, enemy.Position, castRange))
				continue;

			int score = 0;
			foreach (var other in World.Units.Values)
			{
				if (other == null || other.IsDead || other.TeamID != enemy.TeamID)
					continue;
				if (FPVector2.DistanceSquared(other.Position, enemy.Position) <= radiusSq)
					score++;
			}
			FP d = FPVector2.DistanceSquared(castFrom.Position, enemy.Position);
			if (score > bestScore || (score == bestScore && d < bestDist))
			{
				best = enemy;
				bestScore = score;
				bestDist = d;
			}
		}

		if (best != null)
		{
			aim = best.Position;
			return true;
		}

		// 没有敌方单位：只有召唤类技能值得空放（往"离老家最近的敌人"方向摆）
		bool summon = (cfg.PanelSkillMode & (256 | 512)) != 0;
		if (!summon)
			return false;
		FPVector2 basePos = GetTeamMainBasePos(team);
		if (basePos.X == FP.Zero && basePos.Y == FP.Zero)
			basePos = castFrom.Position;
		FPVector2 toward = GetNearestEnemyPos(team, basePos);
		if (toward.X == FP.Zero && toward.Y == FP.Zero)
			return false;
		FPVector2 dir = (toward - basePos).Normalized();
		if (dir.X == FP.Zero && dir.Y == FP.Zero)
			return false;
		aim = basePos + dir * (FP)(4 * World.Grid.TileSize);
		return true;
	}
}
}
