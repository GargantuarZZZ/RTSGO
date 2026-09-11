using System.Collections.Generic;
using FixMath.NET;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace SimulationDeterminismTest
{
	// 蓝图规则（帝国时代 4 式）验证：
	//   1. 蓝图只阻挡自己人（同队/盟友），对敌方完全不阻挡
	//   2. 自己人站在蓝图里会被强制插队一个移出指令
	//   3. 单位离开后撤销让路状态；蓝图完工后对所有队伍阻挡
	//   4. 蓝图查询的团队维度不会污染全局阻挡网格
	internal static partial class Program
	{
		private static SimWorld CreateFlatWorld(int extent = 10)
		{
			var world = new SimWorld();
			for (int x = -extent; x <= extent; x++)
				for (int y = -extent; y <= extent; y++)
					world.Grid.TerrainCells.Add(new SimVector2I(x, y));
			world.Grid.RefreshTerrainBounds();
			world.Grid.SyncStaticBlocking();
			return world;
		}

		private static SimStructure AddBlueprint(SimWorld world, int teamId, int gridX, int gridY,
			int w = 2, int h = 2, SimStructure.StructureState state = SimStructure.StructureState.Blueprint)
		{
			FP tile = (FP)64m;
			var center = new FPVector2(
				(FP)gridX * tile + tile,
				(FP)gridY * tile + tile);
			var s = new SimStructure(world.GetNextEntityId(), teamId, center, new SimVector2I(gridX, gridY), w, h)
			{
				CurrentState = state
			};
			world.AddStructure(s);
			world.Grid.SyncStructureBlocking(world.Structures);
			return s;
		}

		private static SimUnit AddUnit(SimWorld world, int teamId, FPVector2 pos, string type = "RifleMan")
		{
			var u = new SimUnit(world.GetNextEntityId(), teamId, pos)
			{
				UnitTypeId = type,
				Radius = (FP)15,
				MaxSpeed = (FP)200,
				Hp = (FP)100,
				MaxHp = (FP)100
			};
			world.AddUnit(u);
			return u;
		}

		private static void RunBlueprintTests()
		{
			// 队伍分组优先于 IsSimulationRules 默认实现：用注入的解析器模拟"2v2 盟友"
			// team 1 / 2 = 盟友，team 3 = 敌方
			SimGrid.TeamFriendlyResolver = (a, b) =>
				a > 0 && b > 0 && ((a == b) || (a <= 2 && b <= 2));
			SimGrid.BumpTeamRevision();

			// ---------- 1. 蓝图只挡自己人 ----------
			{
				var world = CreateFlatWorld();
				AddBlueprint(world, 1, -2, -2);

				// 自己人：被挡
				bool ownerBlocked = !world.Grid.IsWalkable(new SimVector2I(-2, -2), world.Structures, -1, 1);
				Check(ownerBlocked, "blueprint blocks its owner's pathing");

				// 盟友：被挡（同组视为自己人）
				bool allyBlocked = !world.Grid.IsWalkable(new SimVector2I(-2, -2), world.Structures, -1, 2);
				Check(allyBlocked, "blueprint blocks allied team pathing");

				// 敌方：不挡
				bool enemyBlocked = !world.Grid.IsWalkable(new SimVector2I(-2, -2), world.Structures, -1, 3);
				Check(!enemyBlocked, "blueprint does NOT block enemy pathing");

				// 中立/无队伍查询：不挡
				bool neutralBlocked = !world.Grid.IsWalkable(new SimVector2I(-2, -2), world.Structures, -1, 0);
				Check(!neutralBlocked, "blueprint does NOT block team-less query");
			}

			// ---------- 2. 敌方寻路可以穿过蓝图，己方必须绕行 ----------
			{
				var world = CreateFlatWorld();
				// 蓝图横跨中间，把地图切成两半
				for (int y = -10; y <= 10; y++)
					AddBlueprint(world, 1, 0, y, 1, 1);

				FP tile = (FP)64m;
				var from = new FPVector2((FP)(-5) * tile, FP.Zero);
				var to = new FPVector2((FP)5 * tile, FP.Zero);

				var enemyPath = SimPathfinder.FindPath(world.Grid, world.Structures, from, to,
					ignoreId: -1, ignoreObstacles: false, radius: (FP)15, skipInitialLos: false,
					radiusTilesOverride: -1, teamId: 3);
				Check(enemyPath != null && enemyPath.Count == 2,
					$"enemy gets straight-line path through blueprint wall (points={enemyPath?.Count})");

				var ownerPath = SimPathfinder.FindPath(world.Grid, world.Structures, from, to,
					ignoreId: -1, ignoreObstacles: false, radius: (FP)15, skipInitialLos: false,
					radiusTilesOverride: -1, teamId: 1);
				Check(ownerPath != null && ownerPath.Count > 2,
					$"owner cannot take straight line through own blueprint (points={ownerPath?.Count})");
			}

			// ---------- 3. 蓝图覆盖层不污染全局阻挡网格 ----------
			{
				var world = CreateFlatWorld();
				AddBlueprint(world, 1, -2, -2);

				// radiusTiles=0 的快速网格（不含蓝图）应仍认为该格可走：
				// 这证明蓝图没有混进 _blockedCells（否则敌方会被膨胀层挡住）
				Check(world.Grid.IsWalkableFast(new SimVector2I(-2, -2), 0, 0, world.Structures),
					"blueprint stays out of the global blocked set");
				Check(!world.Grid.IsWalkableFast(new SimVector2I(-2, -2), 0, 1, world.Structures),
					"blueprint blocks owner in the fast grid query");
			}

			// ---------- 4. 完工后对所有队伍阻挡 ----------
			{
				var world = CreateFlatWorld();
				AddBlueprint(world, 1, -2, -2, 2, 2, SimStructure.StructureState.Active);

				Check(!world.Grid.IsWalkable(new SimVector2I(-2, -2), world.Structures, -1, 1),
					"completed structure blocks its owner");
				Check(!world.Grid.IsWalkable(new SimVector2I(-2, -2), world.Structures, -1, 3),
					"completed structure blocks enemies");
				Check(!SimGrid.BlocksForTeam(null, 1), "null structure never blocks");
			}

			// ---------- 5. 自己人站在蓝图里被强制移出 ----------
			{
				var world = CreateFlatWorld();
				var bp = AddBlueprint(world, 1, -1, -1, 2, 2);
				// 单位站进蓝图内部（格 (-1,-1) 的中心）
				var unit = AddUnit(world, 1, new FPVector2(FP.Zero, FP.Zero));

				Check(SimWorld.IsInsideStructureFootprint(bp, unit.Position),
					"unit starts inside the blueprint footprint");
				Check(!world.IsFootprintClearOfUnits(bp),
					"footprint reports occupied before eviction");

				world.TickBlueprintEvictions();

				Check(unit.EvictStructureId == bp.ID,
					$"friendly unit on blueprint is flagged for eviction (evict={unit.EvictStructureId}, bp={bp.ID})");
				Check(unit.HasTarget,
					"eviction inserted a move order ahead of the unit's current orders");

				// 敌方站在同一蓝图里：不应被移出
				var enemy = AddUnit(world, 3, new FPVector2((FP)32m, FP.Zero));
				world.TickBlueprintEvictions();
				Check(enemy.EvictStructureId == 0,
					"enemy unit on blueprint is never evicted (blueprint is invisible to it)");
			}

			// ---------- 6. 单位离开后撤销让路状态 ----------
			{
				var world = CreateFlatWorld();
				var bp = AddBlueprint(world, 1, -1, -1, 2, 2);
				FP tile = (FP)64m;
				var unit = AddUnit(world, 1, new FPVector2(FP.Zero, FP.Zero));

				world.TickBlueprintEvictions();
				Check(unit.EvictStructureId == bp.ID, "unit flagged before walking out");

				// 手动把它挪到蓝图外，再对账
				unit.Position = new FPVector2((FP)6 * tile, (FP)6 * tile);
				world.TickBlueprintEvictions();
				Check(unit.EvictStructureId == 0, "eviction state clears once the unit is clear");
				Check(world.IsFootprintClearOfUnits(bp), "footprint reports clear after the unit left");
			}

			// ---------- 7. 施工闸门：占用时不清空 ----------
			{
				var world = CreateFlatWorld();
				var bp = AddBlueprint(world, 1, -1, -1, 2, 2);
				AddUnit(world, 1, new FPVector2(FP.Zero, FP.Zero));
				Check(!world.IsFootprintClearOfUnits(bp),
					"construction gate blocks while a unit overlaps the footprint");

				// 单位仍在原地时，即使蓝图已经变成完工也照样报告占用
				bp.CurrentState = SimStructure.StructureState.Active;
				world.Grid.SyncStructureBlocking(world.Structures);
				Check(!world.IsFootprintClearOfUnits(bp),
					"gate still reports occupied while the unit is physically there");
			}

			// ---------- 8. 大型单位不被敌方蓝图顶开 ----------
			{
				var world = CreateFlatWorld();
				AddBlueprint(world, 1, -1, -1, 2, 2);
				// 敌方 3x3 大单位（radiusTiles=1）路径应仍能直穿蓝图区
				var from = new FPVector2((FP)(-5) * (FP)64m, FP.Zero);
				var to = new FPVector2((FP)5 * (FP)64m, FP.Zero);
				var enemyBig = SimPathfinder.FindPath(world.Grid, world.Structures, from, to,
					ignoreId: -1, ignoreObstacles: false, radius: (FP)96, skipInitialLos: false,
					radiusTilesOverride: -1, teamId: 3);
				Check(enemyBig != null && enemyBig.Count == 2,
					$"big enemy unit path is unaffected by enemy blueprint (points={enemyBig?.Count})");
			}

			// ---------- 9. 角落/墙边：落点必须是真正站得住且走得到的地方 ----------
			{
				var world = CreateFlatWorld();
				// 把所有地形填成墙，只在 (2,2) 留一个口袋，把蓝图贴进角落。
				// 旧的"取矩形边中点"实现会把单位钉在墙上或墙外走不到的地方。
				for (int x = -10; x <= 10; x++)
					for (int y = -10; y <= 10; y++)
						world.Grid.StaticObstacles.Add(new SimVector2I(x, y));
				world.Grid.StaticObstacles.Remove(new SimVector2I(2, 2));
				world.Grid.StaticObstacles.Remove(new SimVector2I(3, 3));
				world.Grid.SyncStaticBlocking();

				var bp = AddBlueprint(world, 1, 2, 2, 1, 1);
				var unit = AddUnit(world, 1, new FPVector2((FP)(2 * 64 + 32), (FP)(2 * 64 + 32)));

				world.TickBlueprintEvictions();

				// 落点必须不在墙里（忽略该蓝图后仍可通行）
				Check(unit.HasTarget, "cornered unit still receives an eviction move");
				if (unit.Path != null && unit.Path.Count > 0)
				{
					var goal = unit.Path[unit.Path.Count - 1];
					var goalCell = world.Grid.WorldToGrid(goal);
					bool standable = world.Grid.IsWalkableForRadius(
						goalCell, world.Structures, bp.ID, 0, unit.TeamID);
					Check(standable,
						$"cornered eviction target is a standable, reachable cell (cell={goalCell.X},{goalCell.Y})");
					Check(!world.Grid.StaticObstacles.Contains(goalCell) || goalCell == new SimVector2I(2, 2),
						$"cornered eviction target is not inside a wall (cell={goalCell.X},{goalCell.Y})");
				}
				else
				{
					Check(false, "cornered eviction produced a path");
				}
			}

			// ---------- 10. 四周全堵死：不要下无意义的移动目标 ----------
			{
				var world = CreateFlatWorld();
				// 除蓝图自身与单位所在格，全部填墙 —— 没有任何可站立的外部格
				for (int x = -10; x <= 10; x++)
					for (int y = -10; y <= 10; y++)
						world.Grid.StaticObstacles.Add(new SimVector2I(x, y));
				world.Grid.StaticObstacles.Remove(new SimVector2I(-1, -1));
				world.Grid.SyncStaticBlocking();

				var bp = AddBlueprint(world, 1, -1, -1, 1, 1);
				var unit = AddUnit(world, 1, new FPVector2((FP)(-1 * 64 + 32), (FP)(-1 * 64 + 32)));

				world.TickBlueprintEvictions();

				// 找不到落点时不能留下一个指向墙里的移动目标
				Check(!unit.HasTarget,
					"fully enclosed blueprint leaves no bogus move target");
				Check(unit.Path == null || unit.Path.Count == 0,
					"fully enclosed blueprint leaves no stale path");
			}

			SimGrid.TeamFriendlyResolver = null;
			SimGrid.BumpTeamRevision();
		}
	}
}
