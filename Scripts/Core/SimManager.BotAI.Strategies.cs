using System;
using System.Collections.Generic;
using System.Linq;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
public partial class SimManager
{
	// Strategies are stateless. All match state belongs to the team's BotBrain.
	// A race owns its decision order and tactical constraints; shared helpers only execute tasks.
	private abstract class BotRaceStrategy
	{
		public abstract void Tick(SimManager sim, BotBrain brain, int tick);
		public virtual bool PanelConstruction => false;
		public virtual bool GlobalWaves => true;
		public virtual bool CanTrain(SimManager sim, int team, UnitConfig cfg) => true;
		public virtual bool ShouldAdvance(SimManager sim, int team, bool proposed) => proposed;
		public virtual FPVector2 AdvanceTarget(SimManager sim, int team, FPVector2 target) => target;
		public virtual bool HandleUnit(SimManager sim, int team, IEntity node, SimUnit unit, bool advancing) => false;
		public virtual bool CanOrder(SimManager sim, int team, SimUnit unit, FPVector2 target) => true;
		public virtual bool CanRaid(SimManager sim, int team, SimStructure tower, int tick, bool continuing)
		{
			var cfg = sim.GetBrain(team).RaceCfg;
			int minimum = continuing ? 4 : Math.Max(6, cfg.AiArmyTarget / 2);
			float power = CombatRating.ComputeStructureRating(ConfigDatabase.GetStructure(tower.StructureTypeId));
			return sim.CountOwnMilitary(team) >= minimum &&
				sim.GetBotArmyPower(team, tick) >= power * (continuing ? 0.8f : 1.2f);
		}
		public virtual int BuildTarget(SimManager sim, int team, StructureConfig cfg, int configured)
		{
			if (cfg == null || cfg.TrainableUnitIds.Count == 0) return configured;
			float metal = RTS.World.Game.GetPlayerByTeam(team)?.PlayerData?.GetResource(ResourceType.Metal) ?? 0;
			return configured + (metal >= 3000 ? 1 : 0) + (metal >= 6000 ? 1 : 0) + (metal >= 10000 ? 2 : 0);
		}
	}

	private static readonly Dictionary<string, BotRaceStrategy> BotStrategies = new(StringComparer.Ordinal)
	{
		["Union"] = new UnionBotStrategy(),
		["Terran"] = new TerranBotStrategy(),
		["Demon"] = new DemonBotStrategy(),
		["Nano"] = new NanoBotStrategy(),
		["Plant"] = new PlantBotStrategy(),
		["Cave"] = new CaveBotStrategy(),
		["Wanderer"] = new WandererBotStrategy()
	};

	private BotRaceStrategy GetBotStrategy(int team)
	{
		string race = GetBrain(team).RaceCfg?.RaceId ?? RTS.World.Game.GetPlayerByTeam(team)?.Race?.RaceName;
		return race != null && BotStrategies.TryGetValue(race, out var strategy) ? strategy : null;
	}

	// Every producer of army movement (waves AND tower raids) must honor local tasks.
	private bool CanIssueBotArmyOrder(int team, SimUnit unit, FPVector2 target)
		=> !GetBrain(team).ReservedOrders.Contains(unit.ID) &&
			(GetBotStrategy(team)?.CanOrder(this, team, unit, target) ?? false);

	private bool IsBotWorkerBusy(IEntity node, SimUnit unit)
		=> node?.Brain == null || unit.HasTarget || unit.CombatTargetId >= 0 ||
			GetBrain(unit.TeamID).ReservedOrders.Contains(unit.ID) ||
			node.Brain.GetActiveBuildTarget() != null ||
			node.Brain.GetAction<RTS.Actions.Implementation.GarrisonAction>("Garrison")?.IsActive == true ||
			node.Brain.GetAction<RTS.Actions.Implementation.RebuildWreckageAction>("RebuildWreckage")?.IsActive == true;

	private IEnumerable<IEntity> BotResearchExecutors(int team)
	{
		foreach (var s in World.Structures.Values)
			if (!s.IsDead && s.TeamID == team && s.CurrentState == SimStructure.StructureState.Active)
				yield return FindEntityById(s.ID);
		// Mobile researchers (notably Shepherd) are real research providers too.
		foreach (var u in World.Units.Values)
			if (!u.IsDead && u.TeamID == team && ConfigDatabase.GetUnit(u.UnitTypeId)?.IsTechBuilding == true)
				yield return FindEntityById(u.ID);
	}

	private long GetBotStateHash()
	{
		long hash = 1469598103934665603L;
		foreach (var pair in _botBrains.OrderBy(p => p.Key))
		{
			var b = pair.Value;
			hash = Mix(hash, pair.Key);
			hash = Mix(hash, b.WaveMode);
			hash = Mix(hash, b.WaveTick);
			hash = Mix(hash, b.WaveRallyFront ? 1 : 0);
			foreach (var p in new[] { b.WaveTarget, b.TowerRaidPos, b.PendingExpansion, b.ExpansionPos, b.HomeAnchor })
			{
				hash = Mix(hash, (long)(p.X * (FP)1000m));
				hash = Mix(hash, (long)(p.Y * (FP)1000m));
			}
			hash = Mix(hash, b.TowerRaidId);
			hash = Mix(hash, b.FieldPathTick);
			hash = Mix(hash, b.ExpandCooldownTick);
			hash = Mix(hash, b.NanoLastSpreadGx);
			hash = Mix(hash, b.NanoLastSpreadGy);
			hash = Mix(hash, b.ArmyPowerTick);
			hash = Mix(hash, BitConverter.SingleToInt32Bits(b.ArmyPower));
			hash = Mix(hash, b.EnemyPowerTick);
			hash = Mix(hash, BitConverter.SingleToInt32Bits(b.EnemyPower));
			foreach (char c in b.LastTrain) hash = Mix(hash, c);
			hash = Mix(hash, -1);
			foreach (int id in b.Resupplying.OrderBy(id => id)) hash = Mix(hash, id);
			hash = Mix(hash, -2);
			foreach (var pending in b.BlueprintTicks.OrderBy(p => p.Key))
			{
				hash = Mix(hash, pending.Key);
				hash = Mix(hash, pending.Value);
			}
			foreach (var squad in b.Squads.OrderBy(s => s.Index))
			{
				hash = Mix(hash, squad.Index);
				hash = Mix(hash, (int)squad.Kind);
				hash = Mix(hash, squad.ShepherdId);
				hash = Mix(hash, (long)(squad.Home.X * (FP)1000m));
				hash = Mix(hash, (long)(squad.Home.Y * (FP)1000m));
				hash = Mix(hash, (long)(squad.Mission.X * (FP)1000m));
				hash = Mix(hash, (long)(squad.Mission.Y * (FP)1000m));
				hash = Mix(hash, squad.HasMission ? 1 : 0);
				hash = Mix(hash, squad.WaitingRecruits ? 1 : 0);
				foreach (char c in squad.PendingUnit) hash = Mix(hash, c);
				hash = Mix(hash, squad.PendingSince);
				hash = Mix(hash, squad.PendingBuilderId);
				hash = Mix(hash, squad.TargetId);
				hash = Mix(hash, squad.TargetTick);
				hash = Mix(hash, squad.Advancing ? 1 : 0);
				foreach (int id in squad.MemberIds.OrderBy(id => id)) hash = Mix(hash, id);
				hash = Mix(hash, -3);
			}
			foreach (var entry in b.BlueprintSquadMap.OrderBy(p => p.Key))
			{
				hash = Mix(hash, entry.Key);
				hash = Mix(hash, entry.Value.Index);
			}
		}
		return hash;
	}
}
}
