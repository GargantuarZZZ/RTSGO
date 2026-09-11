using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RTS.Core;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Test
{
public partial class RaceAISmokeTest
{
    private static object Field(object obj,string name) => obj.GetType().GetField(name).GetValue(obj);
    private static void Set(object obj,string name,object value) => obj.GetType().GetField(name).SetValue(obj,value);
    private void TestWandererRewrite()
    {
        const int team=8;
        var player=new Player { Name="WandererRegression",TeamId=team };
        player.AddChild(new ConfiguredRace("Wanderer") {Name="Race"});
        player.AddChild(new PlayerData {Name="PlayerData"});
        AddChild(player);
        var map=(Dictionary<int,Player>)typeof(RTS.World.Game).GetField("_playerMap",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        map[team]=player;
        foreach (ResourceType r in Enum.GetValues<ResourceType>()) player.PlayerData.AddResource(r,(FP)10000);
        Set(Brain(team),"RaceCfg",ConfigDatabase.GetRace("Wanderer"));
        var leader=(Unit)Spawn("Shepherd",team,-4000,-4000);
        var workers=new List<Unit>();
        for (int i=0;i<4;i++) workers.Add((Unit)Spawn("Builder",team,-4100+i*64,-4000));
        Spawn("IronOre",-1,-3600,-4000);
        Spawn("GasSpring",-1,-3600,-3700);
        Spawn("IronOre",-1,-1000,-4000);
        Spawn("GasSpring",-1,-1000,-3700);
        Call("MaintainWandererSquads",team,0);
        var squads=(IList)Field(Brain(team),"Squads");
        Check(squads.Count==1 && ((HashSet<int>)Field(squads[0],"MemberIds")).Count==4,"Wanderer bootstraps owned economic crew");
        var home=squads[0];
        Check(Call("WandererNextMine",team,leader.SimUnitData.Position,null)!=null,
            "economic expansion finds an unclaimed safe resource site");
        Call("WandererHarvest",team,home,leader.SimUnitData);
        Check(workers.Any(w=>w.Brain.GetActiveHarvestSource() is ResourceStructure r && ConfigDatabase.GetStructure(r.StructureName).ResourceType==ResourceType.Gas) &&
            workers.Any(w=>w.Brain.GetActiveHarvestSource() is ResourceStructure r && ConfigDatabase.GetStructure(r.StructureName).ResourceType==ResourceType.Metal),
            "Wanderer keeps metal and gas income inside its own squad");
        Set(home,"Home",new FPVector2((FP)(-1000),(FP)(-4000)));
        Call("RunWandererSquad",team,home,30);
        Check(FPVector2.DistanceSquared(leader.SimUnitData.FinalTargetPosition,(FPVector2)Field(home,"Home"))==FP.Zero,
            "Wanderer sends the complete destination to pathfinding instead of a projection into walls");
        Check(workers.All(w=>w.Brain.GetActiveHarvestSource()==null),"nomadic relocation interrupts old mining orders without losing crew ownership");
        var workerPosition=workers[0].SimUnitData.Position;
        workers[0].SimUnitData.Position=workerPosition+new FPVector2((FP)2000,FP.Zero);
        Call("RunWandererSquad",team,home,60);
        Check(!leader.SimUnitData.HasTarget,"full-route movement still waits for lagging squad members");
        workers[0].SimUnitData.Position=workerPosition;
        Set(home,"Home",leader.SimUnitData.Position);
        var attack=Activator.CreateInstance(home.GetType(),true);
        Set(attack,"Kind",Enum.Parse(Field(home,"Kind").GetType(),"Attack"));
        Set(attack,"Index",2);
        var leader2=(Unit)Spawn("Shepherd",team,-4000,-4400);
        Set(attack,"ShepherdId",leader2.SimUnitData.ID);
        Set(attack,"Home",leader2.SimUnitData.Position);
        var builder=(Unit)Spawn("Builder",team,-4100,-4400);
        ((HashSet<int>)Field(attack,"MemberIds")).Add(builder.SimUnitData.ID);
        squads.Add(attack);
        Check((bool)Call("TryWandererBuild",team,attack,"Hunter",60),"Wanderer attack squad actually issues a fighter blueprint");
        Check(!(bool)Call("TryWandererBuild",team,attack,"Hunter",60),"pending request blocks duplicate production before spawn callback");
        Call("DrainSimEvents");
        var blueprints=(IDictionary)Field(Brain(team),"BlueprintSquadMap");
        int blueprintId=(int)blueprints.Keys.Cast<object>().Single();
        var hunter=(Unit)Spawn("Hunter",team,-4000,-4400);
        Call("WandererAssignSpawnedUnit",team,blueprintId,hunter);
        Check(((HashSet<int>)Field(attack,"MemberIds")).Contains(hunter.SimUnitData.ID) && (string)Field(attack,"PendingUnit")=="",
            "completed unit joins its recorded squad and clears pending production");
        leader2.SimUnitData.IsDead=true;
        for (int i=0;i<4;i++)
        {
            var fighter=(Unit)Spawn("Harvester",team,-4000+i*32,-4300);
            ((HashSet<int>)Field(attack,"MemberIds")).Add(fighter.SimUnitData.ID);
        }
        leader2.SimUnitData.IsDead=false;
        Set(attack,"PendingUnit","Hunter");
        Call("RunWandererSquad",team,attack,80);
        Check(!(bool)Field(attack,"WaitingRecruits"),"reinforcement does not recall an already ready attack squad");
        Set(attack,"PendingUnit","");
        leader2.SimUnitData.IsDead=true;
        Call("MaintainWandererSquads",team,90);
        Check(squads.Contains(attack) && (int)Field(attack,"ShepherdId")==-1 && ((HashSet<int>)Field(attack,"MemberIds")).Contains(hunter.SimUnitData.ID),
            "leader loss preserves survivors and squad identity");
        leader2.SimUnitData.IsDead=false;
        Set(attack,"ShepherdId",leader2.SimUnitData.ID);
        Spawn("CommandCenter",2,-4000,0);
        Spawn("CommandCenter",2,0,-4400);
        var other=Activator.CreateInstance(home.GetType(),true);
        Set(other,"Kind",Field(attack,"Kind")); Set(other,"Index",3);
        squads.Add(other);
        Call("ChooseWandererMission",team,attack,leader2.SimUnitData,120);
        Call("ChooseWandererMission",team,other,leader2.SimUnitData,120);
        Check((int)Field(attack,"TargetId")!=(int)Field(other,"TargetId"),"multiple attack squads select separate hostile sites");
    }
}
}
