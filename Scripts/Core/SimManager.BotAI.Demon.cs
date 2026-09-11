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
	// Workers are bound, auto-generated souls; military production is primarily free and timed.
	private sealed class DemonBotStrategy : BotRaceStrategy
	{
		public override void Tick(SimManager sim, BotBrain b, int tick)
		{
			sim.TickBotCombat(b.Team, tick);
			sim.TickBotBuild(b.Team, b.RaceCfg, tick);
			sim.TickBotBlueprintWatchdog(b.Team);
			sim.TickBotEconomy(b.Team, b.RaceCfg);
			sim.TickBotDemonGreatRiftMode(b.Team);
			sim.TickBotTech(b.Team, b.RaceCfg);
			sim.TickBotDemonFieldExpansion(b.Team, b.RaceCfg, tick);
			sim.TickBotExpansion(b.Team, b.RaceCfg, tick);
		}
		public override int BuildTarget(SimManager sim, int team, StructureConfig cfg, int configured)
		{
			if (cfg == null || cfg.IsUnique || cfg.AutoProduceUnitIds.Count == 0 || cfg.IsMainBase)
				return configured;
			float metal = RTS.World.Game.GetPlayerByTeam(team)?.PlayerData?.GetResource(ResourceType.Metal) ?? 0;
			return configured + Math.Min(3, (int)(metal / 1000));
		}
		public override FPVector2 AdvanceTarget(SimManager sim, int team, FPVector2 target)
			=> sim.GetDemonSafeAdvance(team, target);
		public override bool HandleUnit(SimManager sim, int team, IEntity node, SimUnit unit, bool advancing)
		{
			var cell = sim.World.Grid.WorldToGrid(unit.Position);
			if (unit.LifespanTimer <= FP.Zero || sim.World.IsOnField(team, cell.X, cell.Y)) return false;
			EntityExtensions.CommandMoveTo(node, sim.GetDemonSafeAdvance(team, unit.Position));
			return true;
		}
		public override bool CanOrder(SimManager sim, int team, SimUnit unit, FPVector2 target)
		{
			var cell = sim.World.Grid.WorldToGrid(target);
			return unit.LifespanTimer <= FP.Zero || sim.World.IsOnField(team, cell.X, cell.Y);
		}
		public override bool CanRaid(SimManager sim, int team, SimStructure tower, int tick, bool continuing)
		{
			var cell = sim.World.Grid.WorldToGrid(tower.Position);
			return sim.World.IsOnField(team, cell.X, cell.Y) && base.CanRaid(sim, team, tower, tick, continuing);
		}
	}

	private FPVector2 GetDemonFieldCentroid(int team)
	{
		FP x = FP.Zero, y = FP.Zero;
		int n = 0;
		foreach (var sim in World.Structures.Values)
		{
			if (sim == null || sim.IsDead || sim.TeamID != team)
				continue;
			var cfg = ConfigDatabase.GetStructure(sim.StructureTypeId);
			if (cfg == null || cfg.FieldRadius <= 0)
				continue;
			x += sim.Position.X;
			y += sim.Position.Y;
			n++;
		}
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team || u.FieldRadius <= 0)
				continue;
			x += u.Position.X;
			y += u.Position.Y;
			n++;
		}
		if (n == 0)
			return FPVector2.Zero;
		return new FPVector2(x / (FP)n, y / (FP)n);
	}

	private FPVector2 GetDemonSafeAdvance(int team, FPVector2 target)
	{
		FPVector2 anchor = GetDemonFieldCentroid(team);
		if (anchor.X == FP.Zero && anchor.Y == FP.Zero)
			return target;
		FPVector2 dir = (target - anchor).Normalized();
		if (dir.X == FP.Zero && dir.Y == FP.Zero)
			return anchor;
		FPVector2 last = anchor;
		for (int step = 1; step <= 48; step++)
		{
			FPVector2 p = anchor + dir * (FP)(step * 64);
			var g = World.Grid.WorldToGrid(p);
			if (World.IsOnField(team, g.X, g.Y))
				last = p;
			else
				break;
		}
		return last;
	}

	private void TickBotDemonGreatRiftMode(int team)
	{
		var rifts = new List<SimStructure>();
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != team ||
				s.StructureTypeId != "GreatRift" ||
				s.CurrentState != SimStructure.StructureState.Active)
				continue;
			rifts.Add(s);
		}
		rifts.Sort((a, b) => a.ID.CompareTo(b.ID));
		for (int i = 0; i < rifts.Count; i++)
		{
			int want = i % 2; // 0=双狗 1=飞行射手，一半一半
			if (rifts[i].AutoProduceMode != want)
			{
				var node = FindEntityById(rifts[i].ID);
				node?.Brain?.StartAction("AutoProduceMode");
			}
		}
	}

	private void TickBotDemonFieldExpansion(int team, RaceConfig raceCfg, int currentTick)
	{
		FPVector2 target = GetNearestExpansionTarget(team);
		if (target.X == FP.Zero && target.Y == FP.Zero)
			target = GetNearestEnemyPos(team);
		if (target.X == FP.Zero && target.Y == FP.Zero)
			return;
		var tg = World.Grid.WorldToGrid(target);
		if (World.IsOnField(team, tg.X, tg.Y))
			return;

		var brain = GetBrain(team);
		if (currentTick - brain.LastRaidLogTick >= 300)
		{
			brain.LastRaidLogTick = currentTick;
					}
		TryBotDemonFieldPush(team, raceCfg, target, currentTick);
	}

	private int CountHellCitySouls(int team)
	{
		return CountUnits(team, u => u.UnitTypeId == "DemonWorker" && u.BoundStructureId > 0);
	}

	private FPVector2 GetNearestExpansionTarget(int team)
	{
		FPVector2 basePos = GetTeamMainBasePos(team);
		FPVector2 best = FPVector2.Zero;
		FP bestDist = FP.MaxValue;
		foreach (var sim in World.Structures.Values)
		{
			if (sim == null || sim.IsDead || sim.TeamID != -2)
				continue;
			if (sim.StructureTypeId != "Tower" && sim.StructureTypeId != "NanoCore")
				continue;
			FP d = FPVector2.DistanceSquared(sim.Position, basePos);
			if (d < bestDist)
			{
				bestDist = d;
				best = sim.Position;
			}
		}
		return best;
	}

	private bool TryBotDemonFieldPush(int team, RaceConfig raceCfg, FPVector2 towerPos, int currentTick)
	{
		string fieldStruct = "";
		int fieldRadius = 0;
		float bestCost = float.MaxValue;
		foreach (string sid in raceCfg.AiBuildOrderIds)
		{
			var sc = ConfigDatabase.GetStructure(sid);
			if (sc == null || sc.FieldRadius <= 0 || sc.IsUnique)
				continue;
			float cost = 0f;
			foreach (var kv in sc.Costs)
				cost += kv.Value;
			if (cost < bestCost)
			{
				bestCost = cost;
				fieldStruct = sid;
				fieldRadius = sc.FieldRadius;
			}
		}
		if (fieldStruct.Length == 0)
			return false;

		// 金属余额不足 100（一座 SoulStone 的成本）才停：
		// 立场是恶魔的扩张主线，之前 300 的门槛会让立场铺一两座就停住挂机
		var fieldPlayer = RTS.World.Game.GetPlayerByTeam(team);
		if (fieldPlayer?.PlayerData != null &&
			fieldPlayer.PlayerData.GetResource(ResourceType.Metal) < 100f)
			return false;

		// 节流：场上已有 ≥2 座未完工立场就等一等
		int pendingField = 0;
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != team ||
				s.StructureTypeId != fieldStruct ||
				s.CurrentState == SimStructure.StructureState.Active)
				continue;
			if (++pendingField >= 2)
				return false;
		}

		FPVector2 anchor = GetDemonFieldCentroid(team);
		if (anchor.X == FP.Zero && anchor.Y == FP.Zero)
			anchor = GetTeamMainBasePos(team);
		if (anchor.X == FP.Zero && anchor.Y == FP.Zero)
			anchor = GetTeamCentroid(team);
		FPVector2 dir = (towerPos - anchor).Normalized();
		if (dir.X == FP.Zero && dir.Y == FP.Zero)
			dir = new FPVector2(FP.One, FP.Zero);

		FPVector2 edge = GetDemonSafeAdvance(team, towerPos);
		if (edge.X == anchor.X && edge.Y == anchor.Y)
			edge = anchor + dir * (FP)((fieldRadius - 2) * 64);

		FPVector2 nextDir = dir;
		var brain = GetBrain(team);
		if (currentTick - brain.FieldPathTick >= 90)
		{
			brain.FieldPathTick = currentTick;
			var path = RTS.Simulation.SimPathfinder.FindPath(
				World.Grid, World.Structures, edge, towerPos);
			if (path != null && path.Count >= 2)
			{
				FPVector2 stepVec = path[1] - edge;
				if (stepVec.X != FP.Zero || stepVec.Y != FP.Zero)
					nextDir = stepVec.Normalized();
			}
		}
		for (int i = 0; i < 3; i++)
		{
			FPVector2 place = edge + nextDir * (FP)((4 + i * 2) * 64);
			if (TryPlaceStructureAt(team, fieldStruct, place, 2))
			{
								return true;
			}
		}
		for (int i = 0; i < 4; i++)
		{
			FPVector2 place = edge + dir * (FP)((2 + i * 3) * 64);
			if (TryPlaceStructureAt(team, fieldStruct, place, 2))
			{
								return true;
			}
		}
		return false;
	}

}
}
