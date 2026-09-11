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
	// Forest area is the economy. Grow from the current frontier before spending on units.
	private sealed class PlantBotStrategy : BotRaceStrategy
	{
		public override bool PanelConstruction => true;
		public override void Tick(SimManager sim, BotBrain b, int tick)
		{
			sim.TickBotCombat(b.Team, tick);
			var pd = RTS.World.Game.GetPlayerByTeam(b.Team)?.PlayerData;
			if (pd == null) return;
			// Save for population before optional spending; a workerless race cannot improvise a depot.
			if (pd.GetMaxSupply() - pd.GetUsedSupply() < 4 &&
				(b.RaceCfg.MaxSupplyCap <= 0 || pd.GetMaxSupply() < b.RaceCfg.MaxSupplyCap) &&
				!sim.IsSupplyBuildingPending(b.Team, b.RaceCfg))
			{
				string supply = sim.PickSupplyStructure(b.Team, b.RaceCfg);
				var cfg = ConfigDatabase.GetStructure(supply);
				if (cfg != null && !pd.HasResources(cfg.Costs)) return;
				sim.TickBotBuild(b.Team, b.RaceCfg, tick);
				return;
			}
			sim.TickBotPlantSpread(b.Team, tick);
			sim.TickBotBuild(b.Team, b.RaceCfg, tick);
			sim.TickBotProduction(b.Team, b.RaceCfg, tick);
			sim.TickBotTech(b.Team, b.RaceCfg);
			sim.TickBotPlantDefense(b.Team, tick);
			sim.TickBotExpansion(b.Team, b.RaceCfg, tick);
		}
		public override bool CanTrain(SimManager sim, int team, UnitConfig cfg)
		{
			if (sim.IsBaseUnderAttack(team)) return true;
			// Leave one forest spread's cost in the bank, including across multiple producers.
			float wood = RTS.World.Game.GetPlayerByTeam(team)?.PlayerData?.GetResource(ResourceType.Wood) ?? 0;
			return wood - (cfg.Costs.TryGetValue(ResourceType.Wood, out float cost) ? cost : 0) >= 50;
		}
		public override int BuildTarget(SimManager sim, int team, StructureConfig cfg, int configured)
		{
			if (cfg == null || cfg.TrainableUnitIds.Count == 0) return configured;
			float wood = RTS.World.Game.GetPlayerByTeam(team)?.PlayerData?.GetResource(ResourceType.Wood) ?? 0;
			return configured + (wood >= 1000 ? 1 : 0) + (wood >= 2000 ? 1 : 0);
		}
	}

	private void TickBotPlantDefense(int team, int currentTick)
	{
		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (player?.PlayerData == null || player.PlayerData.GetResource(ResourceType.Wood) < 400f)
			return;
		if (!BotLogTick(team, currentTick, 300))
			return;
		if (CountOwnStructures(team, "PlantSunlightTurret") >= 2)
			return;

		FPVector2 basePos = GetTeamMainBasePos(team);
		if (basePos.X == FP.Zero && basePos.Y == FP.Zero)
			basePos = GetTeamCentroid(team);

		for (int i = 0; i < 6; i++)
		{
			FP angle = FP.Pi * (FP)i / (FP)3;
			FPVector2 pos = basePos +
				new FPVector2(FP.Cos(angle), FP.Sin(angle)) * (FP)(6 * 64);
			if (TryBotPanelBuild(team, "PlantSunlightTurret", pos))
				return;
		}
	}

	private void TickBotPlantSpread(int team, int currentTick)
	{
		if (!BotLogTick(team, currentTick, 150)) return;
		var pd = RTS.World.Game.GetPlayerByTeam(team)?.PlayerData;
		// Keep room for military spending as well: do not spend the last 50 on another node.
		if (pd == null || pd.GetResource(ResourceType.Wood) < 100f) return;
		var aim = GetNearestEnemyPos(team);
		// Front-most ready node wins, stable ID breaks ties. No search tree or prediction.
		foreach (var source in World.Structures.Values
			.Where(s => !s.IsDead && s.TeamID == team && s.CurrentState == SimStructure.StructureState.Active &&
				(s.StructureTypeId == "PlantLifeTree" || s.StructureTypeId == "PlantBranchTree" ||
				 (s.StructureTypeId == "PlantForestNode" && !s.ForestSpreadUsed)) && s.ForestSpreadCooldown <= FP.Zero)
			.OrderBy(s => FPVector2.DistanceSquared(s.Position, aim)).ThenBy(s => s.ID))
		{
			int range = ConfigDatabase.GetStructure(source.StructureTypeId)?.ForestSpreadRangeTiles ?? 0;
			if (range <= 0) continue;
			var dir = (aim - source.Position).Normalized();
			if (dir.MagnitudeSquared() == FP.Zero) dir = new FPVector2(FP.One, FP.Zero);
			var side = new FPVector2(-dir.Y, dir.X);
			for (int attempt = 0; attempt < 3; attempt++)
			{
				var heading = attempt == 0 ? dir : (dir + side * (FP)(attempt == 1 ? 1 : -1)).Normalized();
				var target = source.Position + heading * (FP)((range - 1) * World.Grid.TileSize);
				var cell = World.Grid.WorldToGrid(target);
				// Don't pay for repeatedly filling already-owned forest.
				int radius = (int)(ConfigDatabase.GetStructure("PlantForestNode")?.CreepSpreadRadius ?? 8);
				bool addsForest = false;
				foreach (var offset in new[] { new SimVector2I(radius, 0), new SimVector2I(-radius, 0),
					new SimVector2I(0, radius), new SimVector2I(0, -radius) })
				{
					var edge = new SimVector2I(cell.X + offset.X, cell.Y + offset.Y);
					if (World.Grid.TerrainCells.Contains(edge) && !World.Grid.StaticObstacles.Contains(edge) &&
						World.CreepGrid.GetActiveCreep(edge.X, edge.Y) == CreepType.None) addsForest = true;
				}
				if (!addsForest) continue;
				if (TrySpawnForestNode(team, target)) return;
			}
		}
	}
}
}
