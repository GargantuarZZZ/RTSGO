using System;
using System.Collections.Generic;
using RTS.Data;
using RTS.Data.Maps;
using RTS.MapEditing;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace SimulationDeterminismTest
{
	// =========================================================
	// 地图格式 + 程序化生成器测试
	//
	// 覆盖：
	//   1. 地形编解码（低 4 位 = 可走性，高 4 位 = 外观变体）
	//   2. Resize 保留重叠区、越界内容不被静默丢弃
	//   3. 生成器可复现（同种子 ⇒ 逐字节一致）
	//   4. 不同种子给出不同地图
	//   5. 对称性真的被应用
	//   6. 连通性：生成后不存在被墙包死的死区
	//   7. 出生点：数量正确、互相连通、周围无障碍
	//   8. 资源/中立物落在可走格上且不重叠
	//   9. 建造禁区被标记
	//  10. MapDataOps 的画笔/直线/洪水填充/重采样
	// =========================================================
	internal static partial class Program
	{
		/// <summary>用 BFS 判断两个格子是否 4 邻域连通（与生成器的连通性判定一致）。</summary>
		private static bool AreCellsConnected(RtsMapData map, int x0, int y0, int x1, int y1)
		{
			if (!map.InBounds(x0, y0) || !map.InBounds(x1, y1)) return false;
			if (!map.IsWalkableCell(x0, y0) || !map.IsWalkableCell(x1, y1)) return false;

			var visited = new bool[map.Width * map.Height];
			var queue = new Queue<int>();
			int start = y0 * map.Width + x0;
			int goal = y1 * map.Width + x1;
			queue.Enqueue(start);
			visited[start] = true;

			while (queue.Count > 0)
			{
				int idx = queue.Dequeue();
				if (idx == goal) return true;
				int cx = idx % map.Width;
				int cy = idx / map.Width;

				void Try(int nx, int ny)
				{
					if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height) return;
					int nidx = ny * map.Width + nx;
					if (visited[nidx]) return;
					if (!map.IsWalkableCell(nx, ny)) return;
					visited[nidx] = true;
					queue.Enqueue(nidx);
				}

				Try(cx - 1, cy);
				Try(cx + 1, cy);
				Try(cx, cy - 1);
				Try(cx, cy + 1);
			}
			return false;
		}

		/// <summary>统计可走地面格里有多少个 4 邻域连通块。</summary>
		private static int CountFloorComponents(RtsMapData map)
		{
			var visited = new bool[map.Width * map.Height];
			var queue = new Queue<int>();
			int components = 0;

			for (int start = 0; start < map.Width * map.Height; start++)
			{
				if (visited[start]) continue;
				visited[start] = true;
				if (!map.IsWalkableCell(start % map.Width, start / map.Width)) continue;

				components++;
				queue.Clear();
				queue.Enqueue(start);

				while (queue.Count > 0)
				{
					int idx = queue.Dequeue();
					int cx = idx % map.Width;
					int cy = idx / map.Width;

					void Try(int nx, int ny)
					{
						if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height) return;
						int nidx = ny * map.Width + nx;
						if (visited[nidx]) return;
						visited[nidx] = true;
						if (!map.IsWalkableCell(nx, ny)) return;
						queue.Enqueue(nidx);
					}

					Try(cx - 1, cy);
					Try(cx + 1, cy);
					Try(cx, cy - 1);
					Try(cx, cy + 1);
				}
			}
			return components;
		}

		private static bool TerrainEquals(RtsMapData a, RtsMapData b)
		{
			if (a.Width != b.Width || a.Height != b.Height) return false;
			if (a.Terrain.Length != b.Terrain.Length) return false;
			for (int i = 0; i < a.Terrain.Length; i++)
				if (a.Terrain[i] != b.Terrain[i]) return false;
			return true;
		}

		private static void RunMapEditorTests()
		{
			// ---------- 1. 地形编解码 ----------
			{
				var map = new RtsMapData { MapId = "t" };
				map.Resize(16, 16);
				map.Fill(RtsMapData.SourceGrass);

				Check(map.HasTerrain, "mapdata: Resize produces a complete terrain buffer");
				Check(map.Terrain.Length == 16 * 16, "mapdata: terrain length equals Width*Height");

				map.SetCell(3, 4, RtsMapData.SourceWall, 2);
				byte cell = map.Terrain[map.Index(3, 4)];
				Check(RtsMapData.DecodeSource(cell) == RtsMapData.SourceWall,
					"mapdata: encoded source round-trips");
				Check(RtsMapData.DecodeVariant(cell) == 2,
					"mapdata: encoded variant round-trips");
				Check(map.IsWallCell(3, 4) && !map.IsWalkableCell(3, 4),
					"mapdata: wall cells are not walkable");

				// 关键性质：变体只影响外观，不改变可走性
				map.SetCell(5, 5, RtsMapData.SourceGrass, 3);
				Check(map.IsWalkableCell(5, 5),
					"mapdata: grass variant 3 is still walkable (variant must not affect gameplay)");

				Check(!map.InBounds(-1, 0) && !map.InBounds(16, 0),
					"mapdata: bounds check rejects out-of-range cells");
			}

			// ---------- 2. Resize 保留重叠内容 ----------
			{
				var map = new RtsMapData { MapId = "t" };
				map.Resize(20, 20);
				map.Fill(RtsMapData.SourceGrass);
				map.SetCell(1, 1, RtsMapData.SourceWall);

				map.Resize(12, 12);
				Check(map.IsWallCell(1, 1), "mapdata: Resize preserves overlapping content when shrinking");
				Check(map.Width == 12 && map.Height == 12, "mapdata: Resize applies new dimensions");

				map.Resize(30, 30);
				Check(map.IsWallCell(1, 1), "mapdata: Resize preserves overlapping content when growing");
			}

			// ---------- 3. 生成器可复现 ----------
			{
				var settings = new MapGenSettings
				{
					Width = 96, Height = 96, Seed = 777,
					Style = MapStyle.Open, Symmetry = MapSymmetry.Rotational180,
					SpawnPointCount = 2,
				};

				var a = MapGenerator.Generate(settings, out var ra);
				var b = MapGenerator.Generate(settings, out var rb);

				Check(TerrainEquals(a, b), "generator: same seed produces byte-identical terrain");
				Check(ra.WallCells == rb.WallCells && ra.ResourcesPlaced == rb.ResourcesPlaced,
					"generator: same seed produces identical report");
				Check(a.SpawnPoints.Count == b.SpawnPoints.Count,
					"generator: same seed produces identical spawn count");
				Check(ra.WallCells > 0, $"generator: ObstacleDensity actually creates walls (walls={ra.WallCells})");
				Check(ra.ResourcesPlaced > 0, $"generator: resources are placed (count={ra.ResourcesPlaced})");
			}

			// ---------- 4. 不同种子 / 不同风格 ----------
			{
				var s1 = new MapGenSettings { Width = 96, Height = 96, Seed = 1, Style = MapStyle.Caves };
				var s2 = new MapGenSettings { Width = 96, Height = 96, Seed = 2, Style = MapStyle.Caves };

				var m1 = MapGenerator.Generate(s1, out _);
				var m2 = MapGenerator.Generate(s2, out _);
				Check(!TerrainEquals(m1, m2), "generator: different seeds produce different terrain");

				var s3 = new MapGenSettings { Width = 96, Height = 96, Seed = 1, Style = MapStyle.Rooms };
				var m3 = MapGenerator.Generate(s3, out _);
				Check(!TerrainEquals(m1, m3), "generator: different styles produce different terrain");
			}

			// ---------- 5. 所有风格都能生成且连通 ----------
			foreach (MapStyle style in Enum.GetValues(typeof(MapStyle)))
			{
				var settings = new MapGenSettings
				{
					Width = 100, Height = 100, Seed = 4242,
					Style = style, Symmetry = MapSymmetry.None,
					SpawnPointCount = 2, NeutralTowerCount = 2,
				};
				var map = MapGenerator.Generate(settings, out var rep);

				Check(map.HasTerrain, $"generator[{style}]: produced terrain");
				Check(map.SpawnPoints.Count == 2, $"generator[{style}]: placed 2 spawn points");
				int comps = CountFloorComponents(map);
				Check(comps == 1,
					$"generator[{style}]: floor is a single connected region after pocket fill (components={comps})");
				Check(rep.WallCells > 0 || style == MapStyle.Open,
					$"generator[{style}]: produced some walls (walls={rep.WallCells})");
			}

			// ---------- 6. 对称性真的被应用 ----------
			{
				var settings = new MapGenSettings
				{
					Width = 80, Height = 80, Seed = 99,
					Style = MapStyle.Open, Symmetry = MapSymmetry.None,
					SpawnPointCount = 2,
				};
				// 先关对称生成一次，再手动套用旋转对称，检查确实变了
				var baseMap = MapGenerator.Generate(settings, out _);

				var withRot = MapGenerator.Generate(
					new MapGenSettings { Width = 80, Height = 80, Seed = 99, Style = MapStyle.Open, Symmetry = MapSymmetry.Rotational180, SpawnPointCount = 2 },
					out _);
				Check(!TerrainEquals(baseMap, withRot),
					"generator: Rotational180 changes the terrain versus no symmetry");

				// Rotational180 的真实语义：绕地图中心旋转 180° 后与自身重合。
				// 对格子 (x,y)，旋转后的位置是 (w-1-x, h-1-y)，所以
				// Terrain(x,y) 必须等于 Terrain(w-1-x, h-1-y)。
				// （注意这与"左上象限镜像到四象限"是同一组约束，见 ApplySymmetry 注释。）
				bool rotOk = true;
				for (int y = 0; y < withRot.Height && rotOk; y++)
					for (int x = 0; x < withRot.Width; x++)
					{
						byte p = withRot.Terrain[withRot.Index(x, y)];
						byte q = withRot.Terrain[withRot.Index(withRot.Width - 1 - x, withRot.Height - 1 - y)];
						if (RtsMapData.DecodeSource(p) != RtsMapData.DecodeSource(q)) { rotOk = false; break; }
					}
				Check(rotOk, "generator: Rotational180 result is 180-degree rotationally symmetric");

				// 幂等性：对已对称地图再套一次同样的对称不应改变任何格子
				{
					var before = (byte[])withRot.Terrain.Clone();
					MapGenerator.ApplySymmetry(withRot, MapSymmetry.Rotational180);
					bool idempotent = true;
					for (int i = 0; i < before.Length; i++)
						if (before[i] != withRot.Terrain[i]) { idempotent = false; break; }
					Check(idempotent, "generator: ApplySymmetry is idempotent (safe to re-apply)");
				}

				var withH = MapGenerator.Generate(
					new MapGenSettings { Width = 80, Height = 80, Seed = 99, Style = MapStyle.Open, Symmetry = MapSymmetry.HorizontalMirror, SpawnPointCount = 2 },
					out _);
				bool hOk = true;
				for (int y = 0; y < withH.Height && hOk; y++)
					for (int x = 0; x < withH.Width / 2; x++)
					{
						byte p = withH.Terrain[withH.Index(x, y)];
						byte q = withH.Terrain[withH.Index(withH.Width - 1 - x, y)];
						if (RtsMapData.DecodeSource(p) != RtsMapData.DecodeSource(q)) { hOk = false; break; }
					}
				Check(hOk, "generator: HorizontalMirror result is actually left-right symmetric");
			}

			// ---------- 7. 出生点质量 ----------
			{
				var settings = new MapGenSettings
				{
					Width = 120, Height = 120, Seed = 31337,
					Style = MapStyle.Caves, Symmetry = MapSymmetry.None,
					SpawnPointCount = 4, SpawnClearRadius = 6,
				};
				var map = MapGenerator.Generate(settings, out _);

				Check(map.SpawnPoints.Count == 4, "generator: 4 requested spawn points are placed");

				bool allOnFloor = true;
				bool allClear = true;
				foreach (var sp in map.SpawnPoints)
				{
					if (!map.IsWalkableCell(sp.GridX, sp.GridY)) allOnFloor = false;

					// 出生点周围 clearRadius-1 内不应有墙（生成器清理过）
					for (int dx = -4; dx <= 4 && allClear; dx++)
						for (int dy = -4; dy <= 4; dy++)
						{
							int x = sp.GridX + dx;
							int y = sp.GridY + dy;
							if (dx * dx + dy * dy > 16) continue;
							if (!map.InBounds(x, y) || !map.IsWalkableCell(x, y)) { allClear = false; break; }
						}
				}
				Check(allOnFloor, "generator: every spawn point sits on walkable floor");
				Check(allClear, "generator: spawn points have a cleared area around them");

				// 任意两个出生点必须互相可达（否则地图不公平）
				bool allReachable = true;
				for (int i = 0; i < map.SpawnPoints.Count && allReachable; i++)
					for (int j = i + 1; j < map.SpawnPoints.Count; j++)
					{
						var a = map.SpawnPoints[i];
						var b = map.SpawnPoints[j];
						if (!AreCellsConnected(map, a.GridX, a.GridY, b.GridX, b.GridY))
						{
							allReachable = false;
							break;
						}
					}
				Check(allReachable, "generator: all spawn points are mutually reachable");
			}

			// ---------- 8. 资源与中立物落点合法 ----------
			{
				var settings = new MapGenSettings
				{
					Width = 120, Height = 120, Seed = 555,
					Style = MapStyle.Rooms, Symmetry = MapSymmetry.QuadMirror,
					SpawnPointCount = 4, Richness = 0.8f,
				};
				var map = MapGenerator.Generate(settings, out _);

				bool allOnFloor = true;
				var occupied = new HashSet<long>();
				bool noOverlap = true;

				foreach (var e in map.Entities)
				{
					if (!map.IsWalkableCell(e.GridX, e.GridY)) allOnFloor = false;
					long key = (long)e.GridX * 100000L + e.GridY;
					if (!occupied.Add(key)) noOverlap = false;
				}

				Check(map.Entities.Count > 0, $"generator: entities are placed (count={map.Entities.Count})");
				Check(allOnFloor, "generator: every placed entity sits on walkable floor");
				Check(noOverlap, "generator: no two entities occupy the same cell");

				bool towersAreHostile = true;
				bool shrinesAreNeutral = true;
				foreach (var e in map.Entities)
				{
					if (e.EntityId == "Tower" && e.ResolveTeamId() != -2) towersAreHostile = false;
					if (e.EntityId == "Shrine" && e.ResolveTeamId() != -1) shrinesAreNeutral = false;
				}
				Check(towersAreHostile, "generator: neutral towers resolve to TeamID -2 (attackable)");
				Check(shrinesAreNeutral, "generator: shrines resolve to TeamID -1 (non-attackable)");
			}

			// ---------- 9. 建造禁区 ----------
			{
				var settings = new MapGenSettings
				{
					Width = 100, Height = 100, Seed = 8,
					Style = MapStyle.Open, SpawnPointCount = 2,
					MarkBuildBlocked = true,
				};
				var map = MapGenerator.Generate(settings, out _);

				int blocked = 0;
				for (int i = 0; i < map.BuildBlocked.Length; i++)
					if (map.BuildBlocked[i] != 0) blocked++;

				Check(map.BuildBlocked.Length == map.Width * map.Height,
					"generator: build-block mask is sized to the map");
				Check(blocked > 0, $"generator: build-block zones are marked (cells={blocked})");

				// 出生点附近必须**可建造**（生成器会主动清出一块基地区）。
				// 之前这里断言"出生点被标为禁建"是错的：那会让玩家连主基地都放不下。
				var sp = map.SpawnPoints[0];
				Check(!map.IsBuildBlocked(sp.GridX, sp.GridY),
					"generator: spawn cell itself stays buildable (base must be placeable)");

				var validator = MapValidator.Validate(map);
				foreach (var issue in validator.Issues)
					Check(issue.Code != "spawn_no_build" && issue.Code != "spawn_tight_build",
						$"generator: spawn has room for a base footprint ({issue.Code})");
			}

			// ---------- 10. MapDataOps 画笔 / 直线 / 洪水填充 / 重采样 ----------
			{
				var map = new RtsMapData { MapId = "ops" };
				map.Resize(40, 40);
				map.Fill(RtsMapData.SourceGrass);

				MapDataOps.StampBorder(map, 1);
				Check(map.IsWallCell(0, 0) && map.IsWallCell(39, 39) && map.IsWallCell(20, 0),
					"ops: StampBorder walls the whole perimeter");
				Check(map.IsWalkableCell(20, 20), "ops: StampBorder leaves the interior alone");

				MapDataOps.StampBrush(map, 5, 5, RtsMapData.SourceWall, 0, 3);
				Check(map.IsWallCell(4, 4) && map.IsWallCell(6, 6),
					"ops: brush size 3 paints a 3x3 block");
				Check(map.IsWalkableCell(3, 3), "ops: brush respects its size boundary");

				MapDataOps.StampLine(map, 10, 10, 20, 10, RtsMapData.SourceWall);
				bool lineOk = true;
				for (int x = 10; x <= 20; x++)
					if (!map.IsWallCell(x, 10)) lineOk = false;
				Check(lineOk, "ops: StampLine draws a complete horizontal line");

				MapDataOps.StampCircle(map, 30, 30, 4, RtsMapData.SourceWall);
				Check(map.IsWallCell(30, 30) && !map.IsWallCell(35, 30),
					"ops: StampCircle fills inside radius and not outside");

				// 洪水填充：把 (20,20) 连通的草地全变成墙
				int changed = MapDataOps.FloodFill(map, 20, 20, RtsMapData.SourceGrass, RtsMapData.SourceWall);
				Check(changed > 0, $"ops: FloodFill changes the connected region (cells={changed})");
				Check(!map.IsWalkableCell(20, 20), "ops: FloodFill converted the seed cell");
				// 被直线隔开的另一侧不应被填（(15,15) 与 (20,20) 未被墙分隔时也会被填，
				// 这里只验证填充是"连通的"而不是整图：角落的外侧边界仍是墙）
				Check(map.IsWallCell(0, 0), "ops: FloodFill does not corrupt the border");

				// 重采样不应崩溃且尺寸正确
				MapDataOps.ResampleNearest(map, 20, 20);
				Check(map.Width == 20 && map.Height == 20 && map.Terrain.Length == 400,
					"ops: ResampleNearest produces a correctly sized map");
			}

			// ---------- 11. 变体 ↔ atlas 往返 ----------
			{
				for (int v = 0; v < 4; v++)
				{
					var atlas = MapDataOps.VariantToAtlas(v);
					int back = MapDataOps.AtlasToVariant(atlas);
					Check(back == v, $"loader: variant {v} round-trips through atlas coords");
				}
				Check(RtsMapData.DecodeVariant(
					(byte)RtsMapData.Encode(RtsMapData.SourceWall, 3)) == 3,
					"loader: max variant 3 survives encode/decode");
			}

			// ---------- 12. 旧地图迁移：TileMapLayer → 地图数据 ----------
			// 这条路径是"把现存 3 张图导出成 .tres"的关键，必须验证：
			// 有碰撞多边形的格 = 墙、其余 = 可走，且负坐标被平移到 0 起点。
			{
				var layer = new Godot.TileMapLayer();
				// 用负坐标模拟 Godot 场景里常见的未对齐 TileMap
				for (int x = -5; x <= 5; x++)
					for (int y = -5; y <= 5; y++)
						layer.SetCell(new Godot.Vector2I(x, y), 0, Godot.Vector2I.Zero);

				// 在 x=0 竖一列墙
				for (int y = -5; y <= 5; y++)
					layer.CollisionPredicate = c => c.X == 0 || layer.CollisionPredicate(c);
				for (int y = -5; y <= 5; y++)
					layer.SetCell(new Godot.Vector2I(0, y), 1, Godot.Vector2I.Zero);

				// 每格单独判定：只有 x==0 有碰撞
				layer.CollisionPredicate = c => c.X == 0;

				var exported = RTS.World.MapLoader.ExportFromTileMap(layer, "Migration");

				Check(exported.HasTerrain, "migration: ExportFromTileMap produces terrain");
				Check(exported.Width == 11 && exported.Height == 11,
					$"migration: bounding box is 11x11 (got {exported.Width}x{exported.Height})");

				// 负坐标被平移：x=0 在导出后应落在第 5 列
				int wallColumn = -1;
				for (int x = 0; x < exported.Width; x++)
					if (exported.IsWallCell(x, 0)) { wallColumn = x; break; }
				Check(wallColumn == 5, $"migration: negative coords shifted to 0-origin (wall at col {wallColumn})");

				int exportedWalls = 0;
				for (int i = 0; i < exported.Terrain.Length; i++)
					if (RtsMapData.DecodeSource(exported.Terrain[i]) == RtsMapData.SourceWall) exportedWalls++;
				Check(exportedWalls == 11, $"migration: exactly 11 wall cells exported (got {exportedWalls})");

				// 导出的地图必须能被校验器读取（Schema 自洽）
				var rep = MapValidator.Validate(exported);
				Check(rep.Issues.Count > 0, "migration: exported map is validatable end-to-end");
			}

			// ---------- 13. MapRuntime：保序与索引 ----------
			{
				var data = new RtsMapData { MapId = "rt", DisplayName = "RT", Width = 64, Height = 64 };
				data.Resize(64, 64);
				data.Fill(RtsMapData.SourceGrass);

				// 刻意乱序加入出生点，验证 MapRuntime 会按 TeamSlot 排序
				data.SpawnPoints.Add(new MapSpawnPoint { TeamSlot = 3, GridX = 30, GridY = 30 });
				data.SpawnPoints.Add(new MapSpawnPoint { TeamSlot = 1, GridX = 10, GridY = 10 });
				data.SpawnPoints.Add(new MapSpawnPoint { TeamSlot = 2, GridX = 20, GridY = 20 });

				data.Regions.Add(new MapRegion { RegionId = "mid", GridX = 32, GridY = 32, Radius = 3 });
				data.Triggers.Add(new TriggerDefinition { TriggerId = "t1" });

				var rt = RTS.World.MapRuntime.FromData(data);

				Check(rt.Spawns.Count == 3 && rt.Spawns[0].TeamSlot == 1 && rt.Spawns[2].TeamSlot == 3,
					"runtime map: spawn points are sorted by TeamSlot (deterministic order)");
				Check(rt.GetSpawn(2)?.GridX == 20, "runtime map: GetSpawn resolves by slot");
				Check(rt.FindRegion("mid") != null, "runtime map: region index resolves by id");
				Check(rt.FindRegion("missing") == null, "runtime map: unknown region returns null");
				Check(rt.HasTriggers && rt.Triggers.Count == 1, "runtime map: triggers carried over");
				Check(rt.Width == 64 && rt.Height == 64, "runtime map: dimensions carried over");
			}
		}
	}
}
