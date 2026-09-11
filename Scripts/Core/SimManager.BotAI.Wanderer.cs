using Godot;
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
    // Fixed priorities, identical at every difficulty. Squads exclusively own all orders.
    private sealed class WandererBotStrategy : BotRaceStrategy
    {
        public override bool GlobalWaves => false;
        public override bool CanOrder(SimManager sim, int team, SimUnit unit, FPVector2 target) => false;
        public override bool CanRaid(SimManager sim, int team, SimStructure tower, int tick, bool continuing) => false;
        public override void Tick(SimManager sim, BotBrain b, int tick)
        {
            sim.TickWandererSquads(b.Team, b.RaceCfg, tick);
        }
    }

    private enum WandererSquadKind { Occupation, Attack, Plasma }
    private sealed class WandererSquad
    {
        public WandererSquadKind Kind;
        public int ShepherdId = -1;
        public FPVector2 Home, Mission;
        public bool HasMission, WaitingRecruits;
        public int Index;
        public readonly HashSet<int> MemberIds = new();
        // Reserve immediately, before the main-thread spawn callback registers the blueprint.
        public string PendingUnit = "";
        public int PendingSince;
        public int PendingBuilderId = -1;
        public int TargetId = -1;
        public int TargetTick;
        public bool Advancing;
    }

    private SimUnit WandererUnit(int team, int id)
        => World.FindSimEntity(id) is SimUnit u && !u.IsDead && u.TeamID == team ? u : null;
    private IEnumerable<SimUnit> WandererMembers(int team, WandererSquad s)
        => s.MemberIds.OrderBy(id => id).Select(id => WandererUnit(team, id)).Where(u => u != null);
    private int CountSquadType(int team, WandererSquad s, string type)
        => WandererMembers(team, s).Count(u => u.UnitTypeId == type);
    private int CountSquadFighters(int team, WandererSquad s)
        => WandererMembers(team, s).Count(u => u.UnitTypeId is "Hunter" or "Harvester" or "Tank" or "PlasmaCannon");

    private void TickWandererSquads(int team, RaceConfig cfg, int tick)
    {
        MaintainWandererSquads(team, tick);
        TickBotBlueprintWatchdog(team);
        PlanWandererProduction(team, tick);
        foreach (var s in GetBrain(team).Squads.OrderBy(s => s.Index).ToArray())
            RunWandererSquad(team, s, tick);
        // Research is surplus spending, after expansion/production, at actual configured costs.
        var pd = RTS.World.Game.GetPlayerByTeam(team)?.PlayerData;
        if (pd != null && CountOwnUnits(team, "Builder") >= 4 &&
            pd.GetResource(ResourceType.Metal) >= 500)
            TickBotTech(team, cfg);
        if (BotLogTick(team, tick, 300))
            foreach (var s in GetBrain(team).Squads)
            {
                var leader=WandererUnit(team,s.ShepherdId);
                int mining=WandererMembers(team,s).Count(u=>FindEntityById(u.ID)?.Brain?.GetAction<RTS.Actions.Implementation.HarvestAction>("Harvest")?.IsHarvesting==true);
                GD.Print($"[WandererAI] T{team} tick={tick} squad={s.Index} {s.Kind} leader={s.ShepherdId} pos=({(int)(leader?.Position.X ?? FP.Zero)},{(int)(leader?.Position.Y ?? FP.Zero)}) home=({(int)s.Home.X},{(int)s.Home.Y}) builders={CountSquadType(team,s,"Builder")} mining={mining} fighters={CountSquadFighters(team,s)} pending={s.PendingUnit} advancing={s.Advancing} target={s.TargetId}");
            }
    }

    private void MaintainWandererSquads(int team, int tick)
    {
        var b = GetBrain(team);
        foreach (var s in b.Squads.ToArray())
        {
            s.MemberIds.RemoveWhere(id => WandererUnit(team, id) == null);
            if (WandererUnit(team, s.ShepherdId) == null) s.ShepherdId = -1;
            foreach (var pair in b.BlueprintSquadMap.Where(p => p.Value == s).ToArray())
            {
                if (World.FindSimEntity(pair.Key) is not SimStructure bp || bp.IsDead)
                {
                    b.BlueprintSquadMap.Remove(pair.Key);
                    continue;
                }
                // A lost controller must not leave its replacement blocked behind an unfinished fighter.
                s.PendingBuilderId = -1; // Main-thread spawn has completed; the real build action now owns the worker.
                if (s.ShepherdId < 0 && bp.StructureTypeId != "Blueprint_Shepherd")
                {
                    var abandoned = FindEntityById(bp.ID) as RTS.Units.Structure;
                    if (abandoned != null) SimEventQueue.EnqueueMain(() => abandoned.CancelConstruction());
                    b.BlueprintSquadMap.Remove(pair.Key);
                    s.PendingUnit = "";
                    continue;
                }
                // Replace a dead/interrupted constructor without ordering another blueprint.
                if (World.Units.Values.Any(u => !u.IsDead && u.TeamID == team &&
                    FindEntityById(u.ID)?.Brain?.GetActiveBuildTarget()?.LogicEntity?.ID == bp.ID)) continue;
                var worker = World.Units.Values.Where(u => WandererBuilderAvailable(team,u) &&
                    FPVector2.IsWithinRange(u.Position,bp.Position,(FP)(10*64)))
                    .OrderBy(u => FPVector2.DistanceSquared(u.Position,bp.Position)).ThenBy(u=>u.ID).FirstOrDefault();
                var node = worker != null ? FindEntityById(worker.ID) : null;
                if (node?.Brain?.GetAction<RTS.Actions.Implementation.BuildAction>("Build_"+bp.StructureTypeId)?.CanExecute()==true)
                {
                    node.Brain.StartAction("Build_"+bp.StructureTypeId,bp.Position,FindEntityById(bp.ID));
                    b.ReservedOrders.Add(worker.ID);
                }
            }
            if (s.PendingUnit.Length > 0 && tick - s.PendingSince > 60 &&
                !b.BlueprintSquadMap.Any(p => p.Value == s))
                s.PendingUnit = ""; // Failed/cancelled request; retry without multiplying squads.
            if (s.ShepherdId < 0 && s.MemberIds.Count == 0 && s.PendingUnit.Length == 0)
                b.Squads.Remove(s);
        }
        var assigned = new HashSet<int>(b.Squads.SelectMany(s => s.MemberIds));
        assigned.UnionWith(b.Squads.Select(s => s.ShepherdId));
        foreach (var u in World.Units.Values.Where(u => !u.IsDead && u.TeamID == team && u.UnitTypeId == "Shepherd").OrderBy(u => u.ID))
        {
            if (assigned.Contains(u.ID)) continue;
            var squad = b.Squads.FirstOrDefault(s => s.ShepherdId < 0 && s.PendingUnit != "Shepherd");
            if (squad == null)
            {
                squad = new WandererSquad { Kind = WandererSquadKind.Occupation, Home = u.Position,
                    Index = b.Squads.Count == 0 ? 1 : b.Squads.Max(s => s.Index) + 1 };
                b.Squads.Add(squad);
                if (b.Squads.Count == 1) b.HomeAnchor = u.Position;
            }
            squad.ShepherdId = u.ID;
            assigned.Add(u.ID);
        }
        // Only unassigned survivors are adopted. Living workers never change squads.
        foreach (var u in World.Units.Values.Where(u => !u.IsDead && u.TeamID == team && !assigned.Contains(u.ID)).OrderBy(u => u.ID))
        {
            if (u.UnitTypeId is not ("Builder" or "Hunter" or "Harvester" or "Tank" or "PlasmaCannon")) continue;
            var s = b.Squads.Where(s => WandererUnit(team, s.ShepherdId) != null)
                .OrderBy(s => u.UnitTypeId == "Builder" && s.Kind != WandererSquadKind.Occupation ? 1 : 0)
                .ThenBy(s => FPVector2.DistanceSquared(u.Position, WandererUnit(team,s.ShepherdId).Position))
                .ThenBy(s => s.Index).FirstOrDefault();
            s?.MemberIds.Add(u.ID);
        }
    }

    private void WandererAssignSpawnedUnit(int team, int blueprintSimId, IEntity ent)
    {
        if (ent?.LogicEntity is not SimUnit u || u.IsDead || u.TeamID != team) return;
        var b = GetBrain(team);
        if (!b.BlueprintSquadMap.Remove(blueprintSimId, out var s)) return;
        s.PendingUnit = "";
        if (!b.Squads.Contains(s)) b.Squads.Add(s);
        if (u.UnitTypeId == "Shepherd" && s.ShepherdId < 0) s.ShepherdId = u.ID;
        else s.MemberIds.Add(u.ID);
    }

    public bool IsWandererAttackingUnit(int team, int unitId)
        => GetBrain(team).Squads.Any(s => s.Kind != WandererSquadKind.Occupation &&
            s.Advancing && (s.ShepherdId == unitId || s.MemberIds.Contains(unitId)));
}
}
