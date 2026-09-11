using Godot;
using System;
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
// Real Godot actions/configuration, no fake research/production modules.
public partial class RaceAISmokeTest : Node
{
	private SimManager _sim;
	private EntitySpawner _spawner;
	private readonly string[] _races = { "Union", "Terran", "Demon", "Nano", "Plant", "Cave", "Wanderer" };
	private readonly Dictionary<int, Player> _players = new();
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	private object Call(string name, params object[] args) => typeof(SimManager).GetMethod(name, Private).Invoke(_sim, args);
	private object Brain(int team) => Call("GetBrain", team);
	private object Strategy(int team) => Call("GetBotStrategy", team);
	private object StrategyCall(int team, string method, params object[] args)
		=> Strategy(team).GetType().GetMethod(method).Invoke(Strategy(team), args);
	private static void Check(bool value, string message)
	{
		if (!value) throw new Exception(message);
		GD.Print("[AI PASS] " + message);
	}
	private IEntity Spawn(string id, int team, int x, int y) => _spawner.SpawnEntity(id, team, new FPVector2((FP)x, (FP)y));
	public override void _Ready() => CallDeferred(nameof(Run));

	private void Run()
	{
		try
		{
			_sim = SimManager.Instance;
			_sim.IsRunning = false;
			_sim.StopSimThread();
			ConfigDatabase.LoadAll();
			AddChild(new RTS.World.MapGrid());
			_spawner = new EntitySpawner();
			AddChild(_spawner);
			for (int y = -100; y <= 100; y++)
				for (int x = -100; x <= 100; x++)
					_sim.World.Grid.TerrainCells.Add(new SimVector2I(x, y));
			var map = (Dictionary<int, Player>)typeof(RTS.World.Game).GetField("_playerMap", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
			for (int i = 0; i < _races.Length; i++)
			{
				int team = i + 1;
				var player = new Player { Name = "TestPlayer" + team, TeamId = team };
				player.AddChild(new ConfiguredRace(_races[i]) { Name = "Race" });
				player.AddChild(new PlayerData { Name = "PlayerData" });
				AddChild(player);
				map[team] = player;
				_players[team] = player;
				foreach (ResourceType resource in Enum.GetValues<ResourceType>())
					player.PlayerData.AddResource(resource, (FP)10000);
				Brain(team).GetType().GetField("RaceCfg").SetValue(Brain(team), ConfigDatabase.GetRace(_races[i]));
				Check(Strategy(team)?.GetType().Name == _races[i] + "BotStrategy", _races[i] + " has an explicit strategy");
				int phase = team * 7 % 30;
				foreach (int interval in new[] { 60, 90, 150, 300 })
					Check((bool)Call("BotLogTick", team, interval - phase, interval), _races[i] + " periodic task is reachable at " + interval);
				foreach (var entry in new[] { (1, (FP)0.7m), (2, FP.One), (3, (FP)1.5m) })
				{
					_sim.SetBotDifficulty(team, entry.Item1);
					Check(_sim.GetBotResourceMultiplier(team) == entry.Item2, _races[i] + " difficulty " + entry.Item1 + " income multiplier");
				}
			}
			TestResearch();
			TestOrders();
			TestForest();
			TestCaveRebuild();
			TestSupplyAndProduction();
			TestWandererRewrite();
			TestPlasmaAutoFire();
			TestResourceDepletion();
			// Every race executes a real decision against the mechanism fixtures above.
			for (int i = 0; i < _races.Length; i++)
			{
				int team = i + 1;
				StrategyCall(team, "Tick", _sim, Brain(team), 600 - team * 7 % 30);
				Call("DrainSimEvents");
				Check(true, _races[i] + " strategy executes against real modules");
			}
			Call("ResetBotState");
			Check(((System.Collections.IDictionary)typeof(SimManager).GetField("_botBrains", Private).GetValue(_sim)).Count == 0,
				"rematch clears race state");
			GD.Print("RACE_AI_SMOKE_PASS");
			GetTree().Quit();
		}
		catch (Exception error)
		{
			GD.PrintErr("RACE_AI_SMOKE_FAIL: " + error);
			GetTree().Quit(1);
		}
	}

	private void TestResearch()
	{
		Spawn("Academy", 1, -3000, -3000);
		Call("TickBotTech", 1, ConfigDatabase.GetRace("Union"));
		Check(_players[1].PlayerData.IsResearching, "Union research actually starts, not just its UI action");
		Spawn("Shepherd", 7, 3000, -3000);
		Call("TickBotTech", 7, ConfigDatabase.GetRace("Wanderer"));
		Check(_players[7].PlayerData.ResearchingTechId == "WandererTech_AutoHarvest", "mobile Shepherd can research");
		Spawn("NanoHarvester", 4, 0, 2000);
		var pd = _players[4].PlayerData;
		pd.GrantTech("NanoTech_CombatB"); // Preferred CombatA is mutually exclusive.
		Call("TickBotTech", 4, ConfigDatabase.GetRace("Nano"));
		Check(pd.ResearchingTechId == "NanoTech_MobilityA", "Nano skips excluded tech and starts next available branch");
	}

	private void TestOrders()
	{
		var marine = (Unit)Spawn("Marine", 2, -2000, 0);
		Spawn("FrontlineCamp", 2, -3000, 0);
		marine.SimUnitData.Ammo = FP.Zero;
		bool handled = (bool)StrategyCall(2, "HandleUnit", _sim, 2, marine, marine.SimUnitData, true);
		var target = new FPVector2((FP)1000, FP.Zero);
		Check(handled && !(bool)Call("CanIssueBotArmyOrder", 2, marine.SimUnitData, target), "Terran resupply overrides wave and raid movement");
		marine.SimUnitData.Ammo = marine.SimUnitData.MaxAmmo / (FP)2;
		StrategyCall(2, "HandleUnit", _sim, 2, marine, marine.SimUnitData, true);
		Check(!(bool)Call("CanIssueBotArmyOrder", 2, marine.SimUnitData, target), "Terran stays resupplying at 50 percent ammo");
		marine.SimUnitData.Ammo = marine.SimUnitData.MaxAmmo;
		StrategyCall(2, "HandleUnit", _sim, 2, marine, marine.SimUnitData, true);
		Check((bool)Call("CanIssueBotArmyOrder", 2, marine.SimUnitData, target), "Terran can rejoin at full ammo");

		var dog = (Unit)Spawn("DemonDog", 3, 0, -2000);
		Check(!(bool)StrategyCall(3, "CanOrder", _sim, 3, dog.SimUnitData, target), "Demon timed troops cannot be sent beyond the field");
		Check(!(bool)StrategyCall(7, "CanOrder", _sim, 7, marine.SimUnitData, target), "global waves cannot take over Wanderer squads");

		var nano = (Unit)Spawn("NanoBehemoth", 4, 32, 32);
		var nanoTarget = new FPVector2((FP)160, (FP)32);
		Check(!(bool)StrategyCall(4, "CanOrder", _sim, 4, nano.SimUnitData, nanoTarget), "Nano does not cross bare ground");
		for (int x = 0; x <= 2; x++) _sim.World.CreepGrid.AddCreep(x, 0, CreepType.NanoCreep, 4);
		Check((bool)StrategyCall(4, "CanOrder", _sim, 4, nano.SimUnitData, nanoTarget), "Nano can move on connected own carpet");
		nano.SimUnitData.Hp = nano.SimUnitData.MaxHp / (FP)2;
		Check(!(bool)StrategyCall(4, "CanOrder", _sim, 4, nano.SimUnitData, nanoTarget), "damaged irreplaceable Nano behemoth is held back");

		var rifle = (Unit)Spawn("RifleMan", 1, -1000, -1000);
		Call("BotPushWithWave", 1, target, "Union", 0, false);
		Check(rifle.SimUnitData.HasTarget, "first gathering tick issues a move order");
		var b = Brain(1);
		b.GetType().GetField("WaveMode").SetValue(b, 2);
		rifle.Brain.StartAction("Stop");
		Call("BotPushWithWave", 1, target, "Union", 30, false);
		Check(rifle.Brain.GetAction<RTS.Actions.UnitAction>("AttackMove").IsActive,
			"ongoing wave issues orders outside the gather-to-push transition");
	}

	private void TestForest()
	{
		var tree = (Structure)Spawn("PlantLifeTree", 5, 2500, 2500);
		Call("TickBotPlantSpread", 5, 145); // Team 5 phase is 5.
		Check(tree.SimStructureData.ForestSpreadCooldown > FP.Zero, "Plant spreads at its scheduled phase");
		Call("DrainSimEvents");
		var node = _sim.World.Structures.Values.First(s => s.TeamID == 5 && s.StructureTypeId == "PlantForestNode");
		((Structure)_sim.FindEntityById(node.ID)).AdvanceProgress(1f);
		node.ForestSpreadCooldown = FP.Zero;
		Call("TickBotPlantSpread", 5, 295);
		Check(node.ForestSpreadUsed, "Plant expansion chains from a frontier node beyond the original tree");
		Call("DrainSimEvents");
	}

	private void TestCaveRebuild()
	{
		var worker = (Unit)Spawn("CaveWorker", 6, 3500, 0);
		var wreck = (Structure)Spawn("CaveWreckage", 6, 3900, 0);
		wreck.SimStructureData.RebuildTargetId = "CaveDen";
		Call("TickBotCaveRebuild", 6, ConfigDatabase.GetRace("Cave"), 48);
		Check(worker.Brain.GetAction<RTS.Actions.Implementation.RebuildWreckageAction>("RebuildWreckage").IsActive,
			"Cave assigns a worker to rebuild remains");
		Check((bool)Call("IsBotWorkerBusy", worker, worker.SimUnitData), "rebuilding worker is protected from economy reassignment");
	}

	private void TestSupplyAndProduction()
	{
		for (int y = 24; y < 58; y++)
			for (int x = 24; x < 58; x++) _sim.World.CreepGrid.AddCreep(x, y, CreepType.PlantCreep, 5);
		for (int i = 0; i < 8; i++) Spawn("PlantLeafDog", 5, 3500 + i * 32, 3500);
		Call("TickBotBuild", 5, ConfigDatabase.GetRace("Plant"), 0);
		Call("DrainSimEvents");
		Check(_sim.World.Structures.Values.Any(s => s.TeamID == 5 && s.StructureTypeId == "PlantBranchTree"),
			"Plant builds population through its workerless panel path");
		var pd = _players[5].PlayerData;
		pd.AddResource(ResourceType.Wood, (FP)50 - (FP)pd.GetResource(ResourceType.Wood));
		Check(!(bool)StrategyCall(5, "CanTrain", _sim, 5, ConfigDatabase.GetUnit("PlantLeafDog")),
			"Plant production preserves its forest expansion budget");
		pd.AddResource(ResourceType.Wood, (FP)10000);
		for (int i = 0; i < 3; i++) Spawn("SupplyDepot", 1, -4500 + i * 300, 3000);
		Check((string)Call("PickSupplyStructure", 1, ConfigDatabase.GetRace("Union")) == "SupplyDepot",
			"Union can build supply beyond the old three-building limit");
		var barracks = (Structure)Spawn("BB", 1, -3000, 0);
		Call("TickBotProduction", 1, ConfigDatabase.GetRace("Union"), 0);
		Check(barracks.Brain.GetActiveProductionAction() != null, "paid production starts through real action queue");
	}
}
}
