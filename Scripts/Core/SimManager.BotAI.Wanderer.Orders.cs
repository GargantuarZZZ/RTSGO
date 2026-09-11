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
    private IEnumerable<SimStructure> WandererResources(int team, FPVector2 pos, int tiles)
        => World.Structures.Values.Where(r => !r.IsDead && r.TeamID <= 0 && r.ResourceAmount > FP.Zero &&
            SimWorld.IsResourceNodeType(r.StructureTypeId) &&
            ConfigDatabase.GetStructure(r.StructureTypeId)?.ResourceType is ResourceType.Metal or ResourceType.Gas &&
            FPVector2.IsWithinRange(r.Position,pos,(FP)(tiles*64))).OrderBy(r=>r.ID);

    private FPVector2? WandererNextMine(int team, FPVector2 from, WandererSquad moving)
    {
        var candidates = new List<FPVector2>();
        foreach (var r in World.Structures.Values.Where(r => !r.IsDead && r.TeamID <= 0 && r.ResourceAmount > FP.Zero &&
            SimWorld.IsResourceNodeType(r.StructureTypeId)).OrderBy(r=>r.ID))
        {
            if (candidates.Any(p=>FPVector2.IsWithinRange(p,r.Position,(FP)(10*64)))) continue;
            var nodes = WandererResources(team,r.Position,8).ToArray();
            if (!nodes.Any(n=>ConfigDatabase.GetStructure(n.StructureTypeId).ResourceType==ResourceType.Metal)) continue;
            // Use what remains, rather than declaring an entire metal-rich site dead at 399 gas.
            FPVector2 center = new FPVector2(nodes.Aggregate(FP.Zero,(sum,n)=>sum+n.Position.X)/(FP)nodes.Length,
                nodes.Aggregate(FP.Zero,(sum,n)=>sum+n.Position.Y)/(FP)nodes.Length);
            if (GetBrain(team).Squads.Any(s=>s!=moving && s.Kind==WandererSquadKind.Occupation &&
                FPVector2.IsWithinRange(s.Home,center,(FP)(10*64)))) continue;
            if (moving != null && FPVector2.IsWithinRange(moving.Home,center,(FP)(6*64))) continue;
            if (HasEnemyNear(team,center,12) || HasLiveTowerNear(center,14)) continue;
            candidates.Add(center);
        }
        return candidates.Count == 0 ? null : candidates.OrderBy(p=>FPVector2.DistanceSquared(p,from)).ThenBy(p=>p.X).ThenBy(p=>p.Y).First();
    }

    private void WandererHarvest(int team, WandererSquad s, SimUnit leader)
    {
        var workers = WandererMembers(team,s).Where(u=>u.UnitTypeId=="Builder").ToArray();
        var resources = WandererResources(team,leader.Position,10).ToArray();
        var pd = RTS.World.Game.GetPlayerByTeam(team)?.PlayerData;
        if (pd == null) return;
        int gasWorkers = pd.GetResource(ResourceType.Gas) > pd.GetResource(ResourceType.Metal)+400 ? 0 : Math.Max(1,workers.Length/3);
        var assigned = new Dictionary<int,int>();
        for (int i=0;i<workers.Length;i++)
        {
            var u=workers[i];
            if (!WandererBuilderAvailable(team,u)) continue;
            var n=FindEntityById(u.ID);
            var kind=i<gasWorkers ? ResourceType.Gas : ResourceType.Metal;
            var resource=resources.OrderBy(r=>ConfigDatabase.GetStructure(r.StructureTypeId).ResourceType==kind ? 0:1)
                .ThenBy(r=>assigned.GetValueOrDefault(r.ID)).ThenBy(r=>FPVector2.DistanceSquared(r.Position,u.Position)).ThenBy(r=>r.ID).FirstOrDefault();
            if (resource==null) continue;
            assigned[resource.ID]=assigned.GetValueOrDefault(resource.ID)+1;
            var target=FindEntityById(resource.ID) as RTS.Units.ResourceStructure;
            if (target!=null && n.Brain.GetActiveHarvestSource()!=target) EntityExtensions.CommandHarvest(n,target);
        }
    }

    private void ChooseWandererMission(int team,WandererSquad s,SimUnit leader,int tick)
    {
        var old=World.FindSimEntity(s.TargetId);
        if (old!=null && !old.IsDead && AreTeamsHostile(team,old.TeamID) && tick-s.TargetTick<600)
        { s.Mission=old.Position; s.HasMission=true; return; }
        // Spread fronts across different hostile sites. Reserve a site, not every nearby building.
        var targets=World.Structures.Values.Where(t=>!t.IsDead && (t.TeamID>0 && AreTeamsHostile(team,t.TeamID) ||
            t.TeamID==-2 && (t.StructureTypeId=="Tower" || t.StructureTypeId=="NanoCore"))).ToArray();
        var chosen=targets.OrderBy(t=>GetBrain(team).Squads.Any(other=>other!=s && other.HasMission &&
                FPVector2.IsWithinRange(other.Mission,t.Position,(FP)(12*64))) ? 1:0)
            .ThenBy(t=>GetBrain(team).Squads.Count(s=>s.Kind==WandererSquadKind.Occupation)<2 && t.TeamID!=-2 ? 1:0)
            .ThenBy(t=>FPVector2.DistanceSquared(t.Position,leader.Position)).ThenBy(t=>t.ID).FirstOrDefault();
        SimEntity target=chosen;
        target ??= World.Units.Values.Where(u=>!u.IsDead && u.TeamID>0 && AreTeamsHostile(team,u.TeamID))
            .OrderBy(u=>FPVector2.DistanceSquared(u.Position,leader.Position)).ThenBy(u=>u.ID).FirstOrDefault();
        s.TargetId=target?.ID ?? -1;
        s.TargetTick=tick;
        s.HasMission=target!=null;
        if (target!=null) s.Mission=target.Position;
    }

    private void RunWandererSquad(int team,WandererSquad s,int tick)
    {
        var leader=WandererUnit(team,s.ShepherdId);
        if (leader==null) return;
        var leaderNode=FindEntityById(leader.ID);
        if (leaderNode?.Brain==null) return;
        var members=WandererMembers(team,s).ToArray();
        bool constructing=members.Any(u=>FindEntityById(u.ID)?.Brain?.GetActiveBuildTarget()!=null);
        bool economy=s.Kind==WandererSquadKind.Occupation;
        bool ready=economy ? CountSquadType(team,s,"Builder")>=2 : CountSquadFighters(team,s)>=5;
        if (!economy && s.Advancing) ready=CountSquadFighters(team,s)>=2;
        if (economy && !WandererResources(team,s.Home,10).Any())
        {
            var next=WandererNextMine(team,leader.Position,s);
            if (next.HasValue) s.Home=next.Value;
        }
        if (!economy && ready) ChooseWandererMission(team,s,leader,tick);
        var destination=economy ? s.Home : s.HasMission ? s.Mission : leader.Position;
        bool danger=leader.Hp < leader.MaxHp/(FP)2;
        if (danger)
        {
            var refuge=GetBrain(team).Squads.Where(other=>other!=s && other.Kind==WandererSquadKind.Occupation && WandererUnit(team,other.ShepherdId)!=null)
                .OrderBy(other=>FPVector2.DistanceSquared(other.Home,leader.Position)).FirstOrDefault();
            destination=refuge?.Home ?? s.Home;
        }
        FP reach=(FP)((ConfigDatabase.GetUnit(leader.UnitTypeId)?.ControlRangeTiles ?? 15)*64);
        // Keep the irreplaceable controller behind the fighters, outside a neutral tower's 800 range.
        FP standOff=economy || danger ? (FP)(2*64) : reach-(FP)64;
        bool arrived=FPVector2.IsWithinRange(leader.Position,destination,standOff);
        bool lagging=members.Any(u=>!FPVector2.IsWithinRange(u.Position,leader.Position,reach*(FP)0.7m));
        // Reinforcement must not recall a fighting army. Only the constructor/controller pauses.
        s.WaitingRecruits=!ready;
        s.Advancing=!economy && ready && !danger;
        var direction=(destination-leader.Position).Normalized();
        // Keep the real destination so A* can plan around thick walls. The lagging/arrival gates pause the leader.
        bool travel=ready && !arrived && !constructing && !s.WaitingRecruits && !lagging;
        if (danger) travel=!arrived;
        if (travel) EntityExtensions.CommandMoveTo(leaderNode,destination);
        else if (leader.HasTarget) leaderNode.Brain.StartAction("Stop");
        if (!travel && leader.CombatTargetId<0 && leaderNode.CombatModule!=null)
            EntityExtensions.TryAutoAttack(leaderNode,leaderNode.CombatModule.GetAttackRange());
        if (economy && arrived && !danger) WandererHarvest(team,s,leader);
        foreach (var u in members)
        {
            var n=FindEntityById(u.ID);
            if (n?.Brain==null || GetBrain(team).ReservedOrders.Contains(u.ID) || n.Brain.GetActiveBuildTarget()!=null) continue;
            bool worker=u.UnitTypeId=="Builder";
            if (economy && arrived && !danger && worker) continue;
            // Migration deliberately replaces old harvesting orders; workers remain in the same squad.
            if (u.CombatTargetId>=0 && !danger && FPVector2.IsWithinRange(u.Position,leader.Position,reach*(FP)0.98m)) continue;
            var target=leader.Position+direction*(FP)((worker ? 1:5)*64);
            // Follow the controller's actual route, not a straight projection into the same wall.
            if (travel) target=leader.Position;
            if (!worker && !economy && FPVector2.IsWithinRange(destination,leader.Position,reach-(FP)32)) target=destination;
            if (s.WaitingRecruits || danger || (!travel && economy)) target=leader.Position;
            if (FPVector2.IsWithinRange(u.Position,target,(FP)(2*64)) && n.Brain.GetActiveHarvestSource()==null) continue;
            if (!worker && !danger && !s.WaitingRecruits) EntityExtensions.CommandAttackMove(n,target);
            else EntityExtensions.CommandMoveTo(n,target);
        }
    }
}
}
