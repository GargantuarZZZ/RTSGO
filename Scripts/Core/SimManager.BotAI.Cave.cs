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
	// Rebuild durable underground remains before buying replacement structures.
	// Keep harvesters working; only spare workers man defenses when the base is attacked.
	private sealed class CaveBotStrategy : BotRaceStrategy
	{
		public override void Tick(SimManager sim, BotBrain b, int tick)
		{
			sim.TickBotCombat(b.Team, tick);
			sim.TickBotCaveRebuild(b.Team, b.RaceCfg, tick);
			sim.TickBotBuild(b.Team, b.RaceCfg, tick);
			sim.TickBotBlueprintWatchdog(b.Team);
			sim.TickBotEconomy(b.Team, b.RaceCfg);
			sim.TickBotProduction(b.Team, b.RaceCfg, tick);
			sim.TickBotTech(b.Team, b.RaceCfg);
			sim.TickBotCaveDefense(b.Team);
			sim.TickBotExpansion(b.Team, b.RaceCfg, tick);
		}
	}

	private void TickBotCaveDefense(int team)
	{
		if (!IsBaseUnderAttack(team) || !TeamHasTech(team, "CaveTech_Militia") || CountOwnUnits(team, "CaveWorker") <= 4)
			return;
		foreach (var s in World.Structures.Values)
		{
			if (s.IsDead || s.TeamID != team || s.CurrentState != SimStructure.StructureState.Active ||
				(s.StructureTypeId != "CaveOutpost" && s.StructureTypeId != "CaveMainNest")) continue;
			var cfg = ConfigDatabase.GetStructure(s.StructureTypeId);
			if (cfg == null || s.GarrisonedCount >= cfg.GarrisonCapacity) continue;
			foreach (var u in World.Units.Values)
			{
				if (u.IsDead || u.TeamID != team || u.UnitTypeId != "CaveWorker") continue;
				var node = FindEntityById(u.ID);
				if (IsBotWorkerBusy(node, u) || node.Brain.GetActiveHarvestSource() != null) continue;
				if (!FPVector2.IsWithinRange(u.Position, s.Position, (FP)(8 * World.Grid.TileSize))) continue;
				node.Brain.StartAction("Garrison", s.Position, FindEntityById(s.ID));
				GetBrain(team).ReservedOrders.Add(u.ID);
				return;
			}
		}
	}

	private void TickBotCaveRebuild(int team, RaceConfig raceCfg, int currentTick)
	{
		if (!BotLogTick(team, currentTick, 60))
			return;
		// One reconstruction at a time prevents several workers claiming the same remains.
		foreach (var u in World.Units.Values)
			if (!u.IsDead && u.TeamID == team &&
				FindEntityById(u.ID)?.Brain?.GetAction<RTS.Actions.Implementation.RebuildWreckageAction>("RebuildWreckage")?.IsActive == true)
				return;
		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (player?.PlayerData == null)
			return;

		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != team || s.StructureTypeId != "CaveWreckage")
				continue;

			var worker = PickIdleBotBuilder(team, s.RebuildTargetId, s.Position);
			if (worker?.LogicEntity == null)
				continue;

			var wreckNode = FindEntityById(s.ID);
			worker.Brain.StartAction("RebuildWreckage", s.Position, wreckNode, false, false);
			GetBrain(team).ReservedOrders.Add(worker.LogicEntity.ID);
			return;
		}
	}

}
}
