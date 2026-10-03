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
	private readonly string[] _races = { "Union", "Terran", "Demon", "Nano", "Plant", "Cave", "Wanderer", "Wizard", "AICommand" };
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
			TestAIDeployGridAlignment();
			TestActionSlots();
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

	/// <summary>
	/// AI 基地车坐地必须是"变成一座对齐网格的建筑"，不是原地架设。
	///
	/// 这条以前是错的：HandleAIDeploy 直接把车的位置原样传给 SpawnEntity，
	/// 既不吸附格子也不做放置校验 —— 车停在墙边/别的建筑上，照样会变出一座
	/// 压上去的基地，把阻挡烘焙和寻路带坏。
	/// </summary>
	private void TestAIDeployGridAlignment()
	{
		int team = 9;  // AICommand
		var cfgUnit = ConfigDatabase.GetUnit("AIBaseCar");
		var cfgCore = ConfigDatabase.GetStructure("AICore");
		int size = System.Math.Max(cfgCore.GridWidth, cfgCore.GridHeight);

		// ---- 正面：开阔地按格吸附 ----
		// 故意给个非整格坐标（格 20,20 中心是 1312；偏 +21 让它落在格内偏右）
		var car = (Unit)Spawn("AIBaseCar", team, 20 * 64 + 53, 20 * 64 + 17);
		int carId = car.SimUnitData.ID;
		int structuresBefore = _sim.World.Structures.Count;

		Call("HandleAIDeploy", new RTS.Network.NetAction
		{
			PlayerID = team, ActionId = "AIDeploy",
			EntityIDs = new[] { carId }, TargetEntityID = -1,
		});
		Call("DrainSimEvents");

		Check(!_sim.World.Units.ContainsKey(carId), "坐地后基地车实体已退场");
		Check(_sim.World.Structures.Count == structuresBefore + 1, "坐地后多出一座建筑");

		var core = _sim.World.Structures.Values.First(s => s.TeamID == team && s.StructureTypeId == "AICore");

		// 占地左上角必须是 size 对齐的格子（GetTopLeftFromCenter 的结果）
		int expLeft = 20 - size / 2;
		Check(core.GridPosition.X == expLeft && core.GridPosition.Y == expLeft,
			$"建筑左上角格对齐：期望 ({expLeft},{expLeft})，实际 ({core.GridPosition.X},{core.GridPosition.Y})");
		Check(core.GridWidth == size && core.GridHeight == size,
			$"建筑占地写到模拟层：{core.GridWidth}x{core.GridHeight}（期望 {size}x{size}）");

		// 位置必须落在"整块占地的中心"：左上格中心 + 32*(size-1)
		long expX = expLeft * 64 + 32 + 32 * (size - 1);
		long expY = expLeft * 64 + 32 + 32 * (size - 1);
		Check((long)core.Position.X == expX && (long)core.Position.Y == expY,
			$"建筑位置吸附到格中心：期望 ({expX},{expY})，实际 ({(long)core.Position.X},{(long)core.Position.Y})");
		Check(core.CurrentState == SimStructure.StructureState.Active, "坐地生成的建筑直接可用");

		// ---- 反面：被墙围死时拒绝转换，保持车形态 ----
		(int bx, int by) = (60, 60);
		// 用车所在格为中心，把 13x13 全设成静态障碍（车无处可放）
		for (int dx = -6; dx <= 6; dx++)
			for (int dy = -6; dy <= 6; dy++)
				_sim.World.Grid.StaticObstacles.Add(new SimVector2I(bx + dx, by + dy));

		var car2 = (Unit)Spawn("AIBaseCar", team, bx * 64 + 32, by * 64 + 32);
		int car2Id = car2.SimUnitData.ID;
		int structuresBefore2 = _sim.World.Structures.Count;

		Call("HandleAIDeploy", new RTS.Network.NetAction
		{
			PlayerID = team, ActionId = "AIDeploy",
			EntityIDs = new[] { car2Id }, TargetEntityID = -1,
		});
		Call("DrainSimEvents");

		Check(_sim.World.Units.ContainsKey(car2Id), "落点非法时基地车保持不变（不凭空变出建筑）");
		Check(_sim.World.Structures.Count == structuresBefore2, "落点非法时不产生新建筑");

		// 清掉测试墙，别影响后面的用例
		for (int dx = -6; dx <= 6; dx++)
			for (int dy = -6; dy <= 6; dy++)
				_sim.World.Grid.StaticObstacles.Remove(new SimVector2I(bx + dx, by + dy));
	}

	/// <summary>
	/// 命令卡槽位不能冲突：ActionPanel.Refresh 用 `map[SlotIndex] = a`，
	/// 同槽后加的会覆盖先加的，玩家点到的动作全看添加顺序。
	///
	/// 实测踩过：AI 基地车同时满足 CanBuild 和 CanDeploy，建造动作从 5 连排 7 个、
	/// DeployAction 占 7、AIDeployAction 占 8 —— 点"坐地"命中泰伦架设
	/// （只改 DeployState），单位永远不变建筑。
	/// </summary>
	private void TestActionSlots()
	{
		void AssertNoSlotCollision(Unit u, string label)
		{
			var acts = u.GetAvailableActions()
				.Where(a => a.SlotIndex >= 0)
				.ToList();
			var dup = acts.GroupBy(a => a.SlotIndex).Where(g => g.Count() > 1).ToList();
			if (dup.Count > 0)
				foreach (var g in dup)
					GD.Print($"[SlotDump] {label} 槽 {g.Key}: " +
						string.Join(" / ", g.Select(a => a.ActionId)));
			Check(dup.Count == 0, $"{label} 命令卡槽位无冲突" +
				(dup.Count == 0 ? "" : "，冲突槽：" + string.Join(",", dup.Select(g => g.Key))));
		}

		// 基地车：建造 + 坐地 + 生产 挤在一张卡上，最容易撞
		var car = (Unit)Spawn("AIBaseCar", 9, 3000, 3000);
		Call("DrainSimEvents");
		AssertNoSlotCollision(car, "AIBaseCar");
		// 坐地动作故意不进命令卡（SlotIndex = -1），所以在 brain 里查而不是卡片上
		Check(car.Brain.HasCachedAction("AIDeploy"), "基地车有坐地动作（右键自己触发）");
		Check(!car.Brain.HasCachedAction("Deploy"),
			"配了 DeployStructureId 的单位不再挂泰伦架设（两种转换互斥）");
		var carActs = car.GetAvailableActions();
		int buildCount = carActs.Count(a => a.ActionId.StartsWith("Build_"));
		Check(buildCount == 7, $"基地车 7 个建造动作都在卡上（实际 {buildCount} 个）");

		// 泰伦单位反过来：只该有架设，不该有 AI 坐地
		// （Artillery/HeavyInfantry/Liberator/Marine/MissileVehicle 都是 CanDeploy，
		//   但没配 DeployStructureId，所以走泰伦架设而不是换实体）
		var arty = (Unit)Spawn("Artillery", 2, -3000, 3000);
		Call("DrainSimEvents");
		Check(arty.Brain.HasCachedAction("Deploy"), "泰伦可架设单位仍有架设动作");
		Check(!arty.Brain.HasCachedAction("AIDeploy"), "泰伦单位不挂 AI 坐地动作");
		AssertNoSlotCollision(arty, "Artillery");

		// 指挥核心要能生产（TrainUnitAction 的 ActionId 就是单位名）
		var core = (Structure)Spawn("AICore", 9, 4000, 4000);
		Call("DrainSimEvents");
		Check(core.Brain.HasCachedAction("AIBaseCar"), "指挥核心有生产动作（生产链没断）");
		var coreActs = core.GetAvailableActions();
		Check(coreActs.Any(a => a.ActionId == "AIBaseCar"), "指挥核心的生产按钮在命令卡上");

		// 不只是按钮在：真的点下去要能进生产队列（排队→出单位）
		//
		// 注意 tick 次数：TrainUnitAction.OnUpdate 累加的是 World.FixedDelta（20Hz），
		// 不是传进去的 delta。所以要按 BuildTime / FixedDelta 算，否则单位还没造完
		// 就断言，会误判成"生产坏了"。
		int unitsBefore = _sim.World.Units.Count;
		core.Brain.StartAction("AIBaseCar");
		Check(core.Brain.GetActiveProductionAction() != null,
			"点生产后核心进入生产队列（不是亮着不响应）");

		double fixedDelta = (double)_sim.World.FixedDelta;
		int needTicks = (int)(45.0 / System.Math.Max(fixedDelta, 0.001)) + 20;
		for (int i = 0; i < needTicks && core.Brain.GetActiveProductionAction() != null; i++)
		{
			core.Brain.LogicTick(fixedDelta);
			Call("DrainSimEvents");
		}
		Check(_sim.World.Units.Count > unitsBefore,
			$"生产真的产出单位（前 {unitsBefore}，后 {_sim.World.Units.Count}）");
	}
}
}
