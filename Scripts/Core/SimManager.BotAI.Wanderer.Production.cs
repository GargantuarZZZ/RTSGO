using System;
using System.Linq;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
public partial class SimManager
{
    private bool WandererBuilderAvailable(int team, SimUnit u)
    {
        var n = FindEntityById(u.ID);
        return u.UnitTypeId == "Builder" && !u.IsDead && u.TeamID == team &&
            u.CombatTargetId < 0 && n?.Brain != null && n.Brain.GetActiveBuildTarget() == null &&
            !GetBrain(team).Squads.Any(s => s.PendingUnit.Length > 0 && s.PendingBuilderId == u.ID) &&
            !GetBrain(team).ReservedOrders.Contains(u.ID);
    }

    private bool TryWandererBuild(int team, WandererSquad squad, string unitId, int tick)
    {
        if (squad.PendingUnit.Length > 0) return false;
        var cfg = ConfigDatabase.GetStructure("Blueprint_" + unitId);
        var pd = RTS.World.Game.GetPlayerByTeam(team)?.PlayerData;
        if (cfg == null || pd == null || !pd.HasResources(cfg.Costs)) return false;
        if (unitId != "Shepherd" && pd.GetUsedSupply() + cfg.SupplyUsed > pd.GetMaxSupply()) return false;
        var leader = WandererUnit(team, squad.ShepherdId);
        var b = GetBrain(team);
        // Borrow construction work, never membership. Production stays near a living controller.
        var workers = World.Units.Values.Where(u => WandererBuilderAvailable(team, u))
            .OrderBy(u => squad.MemberIds.Contains(u.ID) ? 0 : 1)
            .ThenBy(u => FPVector2.DistanceSquared(u.Position, leader?.Position ?? squad.Home)).ThenBy(u => u.ID);
        foreach (var worker in workers)
        {
            var controller = World.Units.Values.Where(u => !u.IsDead && u.TeamID == team && u.UnitTypeId == "Shepherd")
                .OrderBy(u => FPVector2.DistanceSquared(u.Position, worker.Position)).ThenBy(u => u.ID).FirstOrDefault();
            // Without a shepherd, surviving Builders can rebuild a controller near themselves.
            if (controller == null && unitId != "Shepherd") continue;
            if (controller != null && !FPVector2.IsWithinRange(worker.Position, controller.Position, (FP)(12 * 64))) continue;
            var n = FindEntityById(worker.ID);
            var action = n.Brain.GetAction<RTS.Actions.Implementation.BuildAction>("Build_Blueprint_" + unitId);
            if (action?.CanExecute() != true) continue;
            for (int ring = 1; ring <= 5; ring++)
            for (int side = 0; side < 8; side++)
            {
                var pos = worker.Position + new FPVector2(_buildCos8[side], _buildSin8[side]) * (FP)(ring * 64);
                if (controller != null && !IsBlueprintCoveredByControl(team, pos, cfg.GridWidth)) continue;
                if (!TryBotPlaceStructure(team, "Blueprint_" + unitId, pos, squad, n)) continue;
                squad.PendingUnit = unitId;
                squad.PendingSince = tick;
                squad.PendingBuilderId = worker.ID;
                return true;
            }
        }
        return false;
    }

    private void PlanWandererProduction(int team, int tick)
    {
        var b = GetBrain(team);
        var pd = RTS.World.Game.GetPlayerByTeam(team)?.PlayerData;
        if (pd == null) return;
        // Rebuild missing controllers before new expansion. Keep survivors in their old squads.
        foreach (var s in b.Squads.Where(s => s.ShepherdId < 0))
            if (TryWandererBuild(team, s, "Shepherd", tick)) return;
        if (b.Squads.Count == 0 && CountOwnUnits(team, "Builder") > 0)
        {
            var worker = World.Units.Values.First(u => !u.IsDead && u.TeamID == team && u.UnitTypeId == "Builder");
            var recovery = new WandererSquad { Kind = WandererSquadKind.Occupation, Home = worker.Position, Index = 1 };
            recovery.MemberIds.Add(worker.ID);
            b.Squads.Add(recovery);
            TryWandererBuild(team, recovery, "Shepherd", tick);
            return;
        }
        // Complete starter crews first. Count one reserved request per squad, not proximity.
        foreach (var s in b.Squads.Where(s => s.ShepherdId >= 0).OrderBy(s => s.Index))
        {
            int minimum = s.Kind == WandererSquadKind.Occupation ? 4 : 1;
            if (CountSquadType(team, s, "Builder") < minimum && s.PendingUnit.Length == 0)
            {
                if (TryWandererBuild(team, s, "Builder", tick)) return;
            }
        }
        int economy = b.Squads.Count(s => s.Kind == WandererSquadKind.Occupation);
        int attacks = b.Squads.Count(s => s.Kind == WandererSquadKind.Attack);
        bool creating = b.Squads.Any(s => s.ShepherdId < 0);
        var sponsor = b.Squads.FirstOrDefault(s => s.Kind == WandererSquadKind.Occupation &&
            WandererUnit(team, s.ShepherdId) != null && CountSquadType(team, s, "Builder") >= 4);
        if (!creating && sponsor != null)
        {
            var free = WandererNextMine(team, sponsor.Home, null);
            bool needSupply = pd.GetMaxSupply() - pd.GetUsedSupply() < 6 && pd.GetMaxSupply() < 200;
            // Two mining squads first, then alternate economy and pressure. Mines never forbid attack teams.
            bool wantEconomy = free.HasValue && economy < 4 && (economy < 2 || economy <= attacks);
            bool wantAttack = attacks < 3 && (economy >= 2 || !free.HasValue) &&
                b.Squads.Where(s => s.Kind == WandererSquadKind.Attack).All(s => CountSquadFighters(team,s) >= 5);
            if (wantEconomy || wantAttack || needSupply)
            {
                var s = new WandererSquad { Kind = wantEconomy ? WandererSquadKind.Occupation : WandererSquadKind.Attack,
                    Home = wantEconomy ? free.Value : WandererUnit(team,sponsor.ShepherdId).Position,
                    Index = b.Squads.Max(s => s.Index) + 1 };
                if (TryWandererBuild(team, s, "Shepherd", tick)) { b.Squads.Add(s); return; }
                // Save actual controller cost instead of consuming the last gas on army/research.
                if ((wantEconomy && economy < 2) || needSupply) return;
            }
        }
        // Round robin gives every front production; frontline and siege costs are reserved.
        foreach (var s in b.Squads.Where(s => s.Kind != WandererSquadKind.Occupation && s.ShepherdId >= 0)
            .OrderBy(s => (s.Index + tick / BotTickInterval) % Math.Max(1,b.Squads.Count)))
        {
            if (s.PendingUnit.Length > 0 || CountSquadFighters(team,s) >= (s.Kind == WandererSquadKind.Plasma ? 9 : 7)) continue;
            string desired = s.Kind == WandererSquadKind.Plasma && CountSquadType(team,s,"PlasmaCannon") == 0 ? "PlasmaCannon"
                : CountSquadType(team,s,"Hunter") < 3 ? "Hunter"
                : CountSquadType(team,s,"Tank") < 1 ? "Tank" : "Harvester";
            if (TryWandererBuild(team,s,desired,tick)) return;
            // Save for the durable frontline/siege unit; cheap fillers must not consume every slot first.
            if (desired is "Tank" or "PlasmaCannon") continue;
            if (desired != "Harvester" && TryWandererBuild(team,s,"Harvester",tick)) return;
        }
        // Late siege is a normal shepherd-led squad, never an unbounded pile of empty super squads.
        if (!creating && economy >= 2 && attacks >= 2 && !b.Squads.Any(s => s.Kind == WandererSquadKind.Plasma) &&
            pd.GetResource(ResourceType.Metal) >= 1000 && pd.GetResource(ResourceType.Gas) >= 600)
        {
            var s = new WandererSquad { Kind = WandererSquadKind.Plasma, Home = b.Squads[0].Home, Index = b.Squads.Max(s=>s.Index)+1 };
            if (TryWandererBuild(team,s,"Shepherd",tick)) b.Squads.Add(s);
        }
    }
}
}
