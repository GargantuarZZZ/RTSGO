using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using RTS.Data;
using RTS.Data.Maps;
using RTS.Simulation;
using RTS.Simulation.Scripting;
using FP = FixMath.NET.Fix64;

namespace RTS.MapEditing
{
	// =========================================================
	// 地图校验器
	//
	// 目标：作者在编辑器里就能知道"这张图进游戏会不会坏"，
	// 而不是开一局才发现出生点被墙围死、资源走不到、触发器写错。
	//
	// 两档严重度：
	//   Error   —— 明确会破坏对局（出生点不可达、出生点重叠、单位会卡死…）
	//   Warning —— 可能影响体验（出生点太近、资源离基地太远、缺保底资源…）
	//
	// 关键设计：连通性/可达性判定走**真实 SimGrid + SimPathfinder**，
	// 与游戏内寻路完全相同。自己再写一套 BFS 会得出"校验通过但游戏里卡住"
	// 的假阳性结论——那正是最坏的情况。
	// =========================================================

	public enum MapIssueSeverity { Warning = 0, Error = 1 }

	public sealed class MapIssue
	{
		public MapIssueSeverity Severity;
		public string Code = "";
		public string Message = "";
		/// <summary>出问题的格子（-1 = 与具体格子无关）。编辑器用它定位+高亮。</summary>
		public int GridX = -1;
		public int GridY = -1;

		public override string ToString() =>
			Severity == MapIssueSeverity.Error ? $"[错误] {Message}" : $"[警告] {Message}";
	}

	public sealed class MapValidationReport
	{
		public readonly List<MapIssue> Issues = new();

		public int ErrorCount
		{
			get
			{
				int n = 0;
				foreach (var i in Issues) if (i.Severity == MapIssueSeverity.Error) n++;
				return n;
			}
		}

		public int WarningCount => Issues.Count - ErrorCount;

		public bool IsValid => ErrorCount == 0;

		public void Error(string code, string message, int gx = -1, int gy = -1) =>
			Issues.Add(new MapIssue { Severity = MapIssueSeverity.Error, Code = code, Message = message, GridX = gx, GridY = gy });

		public void Warn(string code, string message, int gx = -1, int gy = -1) =>
			Issues.Add(new MapIssue { Severity = MapIssueSeverity.Warning, Code = code, Message = message, GridX = gx, GridY = gy });

		/// <summary>多行可读报告，写进地图的 LastValidationReport 或编辑器面板。</summary>
		public string ToText()
		{
			if (Issues.Count == 0)
				return "校验通过：未发现问题。";

			var sb = new StringBuilder();
			sb.Append(IsValid ? "校验通过（有警告）" : "校验失败");
			sb.Append($"：{ErrorCount} 个错误，{WarningCount} 个警告\n");

			// 错误优先，同类按格子顺序，便于作者逐条修
			Issues.Sort((a, b) =>
			{
				int s = b.Severity.CompareTo(a.Severity);
				if (s != 0) return s;
				int c = string.CompareOrdinal(a.Code, b.Code);
				if (c != 0) return c;
				if (a.GridX != b.GridX) return a.GridX.CompareTo(b.GridX);
				return a.GridY.CompareTo(b.GridY);
			});

			foreach (var i in Issues)
			{
				sb.Append("  ").Append(i);
				if (i.GridX >= 0) sb.Append($"  @({i.GridX},{i.GridY})");
				sb.Append('\n');
			}
			return sb.ToString();
		}
	}

	/// <summary>校验阈值。可由编辑器暴露给作者微调。</summary>
	public sealed class MapValidationSettings
	{
		/// <summary>出生点之间的最小间距（格）。太近会导致开局互相压制。</summary>
		public int MinSpawnSeparation = 12;

		/// <summary>出生点到最近资源的期望最大距离（格）。超过则警告。</summary>
		public int MaxDistanceToNearestResource = 24;

		/// <summary>每个出生点期望的保底铁矿数。</summary>
		public int ExpectedIronPerSpawn = 4;

		/// <summary>主区域至少要有多少可走格，否则地图太挤。</summary>
		public int MinFloorCells = 400;

		/// <summary>出生点周围多少格内必须空无障碍（保证基地能展开）。</summary>
		public int RequiredSpawnClearance = 3;
	}

	public static class MapValidator
	{
		/// <summary>
		/// 全面校验一张地图。不修改地图数据（只读）。
		/// 校验过程中会跑真实寻路，大图（256×256）可能需要几十毫秒。
		/// </summary>
		public static MapValidationReport Validate(RtsMapData map, MapValidationSettings settings = null)
		{
			settings ??= new MapValidationSettings();
			var report = new MapValidationReport();

			if (map == null)
			{
				report.Error("null_map", "地图数据为空。");
				return report;
			}

			ValidateIdentity(map, report);
			ValidateDimensions(map, report);

			if (!map.HasTerrain)
			{
				// 没有地形后面都没法查
				return report;
			}

			var grid = RTS.World.MapLoader.CreateDetachedGrid(map);

			ValidateFloorAndConnectivity(map, grid, settings, report);
			ValidateSpawns(map, grid, settings, report);
			ValidateEntities(map, grid, report);
			ValidateBuildMask(map, report);
			ValidateRegions(map, report);
			ValidateTriggers(map, report);

			return report;
		}

		// =========================================================
		// 标识与尺寸
		// =========================================================

		private static void ValidateIdentity(RtsMapData map, MapValidationReport report)
		{
			if (string.IsNullOrWhiteSpace(map.MapId))
				report.Error("missing_id", "地图缺少 MapId（存档/联机同步都用它，不能为空）。");
			else if (map.MapId.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
				report.Error("bad_id", $"MapId 含非法文件名字符：'{map.MapId}'。");

			if (string.IsNullOrWhiteSpace(map.DisplayName))
				report.Warn("missing_name", "地图没有显示名，主菜单会直接显示 MapId。");
		}

		private static void ValidateDimensions(RtsMapData map, MapValidationReport report)
		{
			if (map.Width <= 0 || map.Height <= 0)
			{
				report.Error("bad_size", $"地图尺寸非法：{map.Width}x{map.Height}。");
				return;
			}

			if (map.Width < 32 || map.Height < 32)
				report.Warn("small_map", $"地图偏小（{map.Width}x{map.Height}），8 人局可能展开不开。");

			if (map.Width > RtsMapData.MaxSize || map.Height > RtsMapData.MaxSize)
				report.Error("oversize", $"地图超过上限 {RtsMapData.MaxSize}。");

			// 非方形地图：对称生成器与部分布局假设方形，提醒作者
			if (map.Width != map.Height)
				report.Warn("non_square", $"地图非正方形（{map.Width}x{map.Height}），旋转对称会退化为镜像对称。");
		}

		// =========================================================
		// 地面连通性
		// =========================================================

		private static void ValidateFloorAndConnectivity(
			RtsMapData map, SimGrid grid, MapValidationSettings settings, MapValidationReport report)
		{
			int floor = grid.TerrainCells.Count;
			if (floor == 0)
			{
				report.Error("no_floor", "地图没有任何可走地面。");
				return;
			}
			if (floor < settings.MinFloorCells)
				report.Warn("tiny_floor", $"可走地面只有 {floor} 格，可能不足以容纳基地与部队。");

			// 边界必须封闭，否则单位会走到地图外
			bool borderOpen = false;
			for (int x = 0; x < map.Width && !borderOpen; x++)
				if (map.IsWalkableCell(x, 0) || map.IsWalkableCell(x, map.Height - 1)) borderOpen = true;
			for (int y = 0; y < map.Height && !borderOpen; y++)
				if (map.IsWalkableCell(0, y) || map.IsWalkableCell(map.Width - 1, y)) borderOpen = true;

			if (borderOpen)
				report.Warn("open_border", "地图边界存在可走地面，单位可能走出地图外（建议四周封墙）。");

			// 用真实寻路验证"地面是否单一连通"：找任意两处地面跑一次 A*
			var firstCell = FirstWalkableCell(map);
			if (firstCell.X < 0) return;

			// 找出离 firstCell 最远的一个地面格（按切比雪夫距离近似），
			// 作为"另一端"的代表，避免只测了两个相邻格。
			var farCell = FarthestWalkableCell(map, firstCell);

			var path = SimPathfinder.FindPath(
				grid, EmptyStructures, CellCenter(firstCell), CellCenter(farCell),
				ignoreId: -1, ignoreObstacles: false, radius: (FP)15, skipInitialLos: false,
				radiusTilesOverride: -1, teamId: 0);

			bool reachable = path != null && path.Count > 0 &&
				FPVector2.DistanceSquared(path[path.Count - 1], CellCenter(farCell)) <= (FP)(96 * 96);

			// 路径不得穿过不可走格（截断回退路径会穿墙）
			if (reachable && path != null)
			{
				foreach (var point in path)
				{
					var c = new SimVector2I(
						(int)((long)point.X / RtsMapData.TileSize),
						(int)((long)point.Y / RtsMapData.TileSize));
					if (!map_WalkableCell(grid, c)) { reachable = false; break; }
				}
			}

			if (!reachable)
				report.Error("floor_disconnected",
					$"地面被墙分割：({firstCell.X},{firstCell.Y}) 走不到 ({farCell.X},{farCell.Y})。请检查是否留下互不连通的区域。",
					farCell.X, farCell.Y);
		}

		private static SimVector2I FirstWalkableCell(RtsMapData map)
		{
			for (int y = 0; y < map.Height; y++)
				for (int x = 0; x < map.Width; x++)
					if (map.IsWalkableCell(x, y)) return new SimVector2I(x, y);
			return new SimVector2I(-1, -1);
		}

		private static SimVector2I FarthestWalkableCell(RtsMapData map, SimVector2I from)
		{
			var best = from;
			long bestDist = -1;
			for (int y = 0; y < map.Height; y++)
			{
				for (int x = 0; x < map.Width; x++)
				{
					if (!map.IsWalkableCell(x, y)) continue;
					long dx = x - from.X;
					long dy = y - from.Y;
					long d = dx * dx + dy * dy;
					if (d > bestDist) { bestDist = d; best = new SimVector2I(x, y); }
				}
			}
			return best;
		}

		private static FPVector2 CellCenter(SimVector2I cell) =>
			new FPVector2(
				(FP)cell.X * (FP)RtsMapData.TileSize + (FP)(RtsMapData.TileSize / 2),
				(FP)cell.Y * (FP)RtsMapData.TileSize + (FP)(RtsMapData.TileSize / 2));

		// =========================================================
		// 出生点
		// =========================================================

		private static void ValidateSpawns(
			RtsMapData map, SimGrid grid, MapValidationSettings settings, MapValidationReport report)
		{
			if (map.SpawnPoints == null || map.SpawnPoints.Count == 0)
			{
				report.Error("no_spawn", "地图没有任何出生点，无法开局。");
				return;
			}

			if (map.SpawnPoints.Count < 2)
				report.Warn("one_spawn", "只有 1 个出生点，只能单人/观战，无法对战。");

			var seenSlots = new HashSet<int>();
			var seenCells = new HashSet<long>();

			foreach (var sp in map.SpawnPoints)
			{
				if (sp == null) { report.Error("null_spawn", "出生点列表里有空条目。"); continue; }

				if (sp.TeamSlot <= 0)
					report.Error("bad_slot", $"出生点槽位号非法：{sp.TeamSlot}（必须 ≥ 1）。", sp.GridX, sp.GridY);
				else if (!seenSlots.Add(sp.TeamSlot))
					report.Error("dup_slot", $"出生点槽位 {sp.TeamSlot} 重复。", sp.GridX, sp.GridY);

				if (!map.InBounds(sp.GridX, sp.GridY))
				{
					report.Error("spawn_oob", $"出生点 ({sp.GridX},{sp.GridY}) 超出地图范围。", sp.GridX, sp.GridY);
					continue;
				}

				long key = (long)sp.GridX * 100000L + sp.GridY;
				if (!seenCells.Add(key))
					report.Error("dup_cell", $"两个出生点重叠在 ({sp.GridX},{sp.GridY})。", sp.GridX, sp.GridY);

				if (!map.IsWalkableCell(sp.GridX, sp.GridY))
				{
					report.Error("spawn_in_wall", $"出生点 ({sp.GridX},{sp.GridY}) 在墙里。", sp.GridX, sp.GridY);
					continue;
				}

				// 出生点周围必须留出空地，否则开局基地放不下
				int blockedNear = 0;
				int r = settings.RequiredSpawnClearance;
				for (int dx = -r; dx <= r; dx++)
				{
					for (int dy = -r; dy <= r; dy++)
					{
						int x = sp.GridX + dx;
						int y = sp.GridY + dy;
						if (!map.InBounds(x, y) || !map.IsWalkableCell(x, y)) blockedNear++;
					}
				}
				if (blockedNear > 0)
					report.Warn("spawn_tight",
						$"出生点 ({sp.GridX},{sp.GridY}) 周围 {r} 格内有 {blockedNear} 格不可走，基地展开空间偏紧。",
						sp.GridX, sp.GridY);
			}

			// 两两间距
			for (int i = 0; i < map.SpawnPoints.Count; i++)
			{
				for (int j = i + 1; j < map.SpawnPoints.Count; j++)
				{
					var a = map.SpawnPoints[i];
					var b = map.SpawnPoints[j];
					if (a == null || b == null) continue;
					int dx = a.GridX - b.GridX;
					int dy = a.GridY - b.GridY;
					int distSq = dx * dx + dy * dy;
					if (distSq < settings.MinSpawnSeparation * settings.MinSpawnSeparation)
						report.Warn("spawn_close",
							$"出生点 {a.TeamSlot} 与 {b.TeamSlot} 距离过近（{Math.Sqrt(distSq):F1} 格 < {settings.MinSpawnSeparation}）。",
							b.GridX, b.GridY);
				}
			}

			// 所有出生点必须互相可达（真实寻路）
			for (int i = 0; i < map.SpawnPoints.Count; i++)
			{
				for (int j = i + 1; j < map.SpawnPoints.Count; j++)
				{
					var a = map.SpawnPoints[i];
					var b = map.SpawnPoints[j];
					if (a == null || b == null) continue;
					if (!map.InBounds(a.GridX, a.GridY) || !map.InBounds(b.GridX, b.GridY)) continue;
					if (!map.IsWalkableCell(a.GridX, a.GridY) || !map.IsWalkableCell(b.GridX, b.GridY)) continue;

					if (!CanReach(grid, a.GridX, a.GridY, b.GridX, b.GridY))
						report.Error("spawn_unreachable",
							$"出生点 {a.TeamSlot}({a.GridX},{a.GridY}) 走不到出生点 {b.TeamSlot}({b.GridX},{b.GridY})。",
							b.GridX, b.GridY);
				}
			}
		}

		// =========================================================
		// 实体摆放
		// =========================================================

		private static void ValidateEntities(RtsMapData map, SimGrid grid, MapValidationReport report)
		{
			if (map.Entities == null || map.Entities.Count == 0)
			{
				report.Warn("no_entities", "地图没有摆放任何中立物或资源点。");
				return;
			}

			var occupied = new HashSet<long>();
			var perSpawnResourceCount = new Dictionary<int, int>();

			foreach (var spawn in map.SpawnPoints)
				if (spawn != null) perSpawnResourceCount[spawn.TeamSlot] = 0;

			foreach (var e in map.Entities)
			{
				if (e == null) { report.Error("null_entity", "实体列表里有空条目。"); continue; }

				if (string.IsNullOrWhiteSpace(e.EntityId))
				{
					report.Error("entity_no_id", $"实体缺少 EntityId @({e.GridX},{e.GridY})。", e.GridX, e.GridY);
					continue;
				}

				if (!map.InBounds(e.GridX, e.GridY))
				{
					report.Error("entity_oob", $"实体 {e.EntityId} 位于地图外 ({e.GridX},{e.GridY})。", e.GridX, e.GridY);
					continue;
				}

				long key = (long)e.GridX * 100000L + e.GridY;
				if (!occupied.Add(key))
					report.Error("entity_overlap",
						$"多个实体重叠在 ({e.GridX},{e.GridY})（后一个是 {e.EntityId}）。", e.GridX, e.GridY);

				// 资源点必须在可走地面上，否则工人永远走不到
				bool needsFloor = IsResource(e.EntityId) || e.Owner != MapEntityOwner.PlayerSlot;
				if (needsFloor && !map.IsWalkableCell(e.GridX, e.GridY))
					report.Error("entity_in_wall",
						$"实体 {e.EntityId} 在墙里 ({e.GridX},{e.GridY})，工人/单位无法到达。", e.GridX, e.GridY);
				else if (!map.IsWalkableCell(e.GridX, e.GridY))
					report.Warn("entity_on_wall",
						$"实体 {e.EntityId} 在不可走格上 ({e.GridX},{e.GridY})。", e.GridX, e.GridY);

				if (e.Owner == MapEntityOwner.PlayerSlot && e.TeamSlot <= 0)
					report.Error("entity_bad_slot",
						$"实体 {e.EntityId} 归属玩家槽位但槽位号非法（{e.TeamSlot}）。", e.GridX, e.GridY);

				if (e.Count <= 0)
					report.Warn("entity_bad_count", $"实体 {e.EntityId} 的 Count={e.Count}，不会生成任何东西。", e.GridX, e.GridY);
			}

			// 每个出生点附近是否有保底资源（按真实寻路判定"走得到"）
			foreach (var spawn in map.SpawnPoints)
			{
				if (spawn == null) continue;
				if (!map.InBounds(spawn.GridX, spawn.GridY)) continue;

				int nearestDist = int.MaxValue;
				int reachableResources = 0;

				foreach (var e in map.Entities)
				{
					if (e == null || !IsResource(e.EntityId)) continue;
					if (!map.InBounds(e.GridX, e.GridY)) continue;

					int dx = e.GridX - spawn.GridX;
					int dy = e.GridY - spawn.GridY;
					int dist = (int)Math.Sqrt(dx * dx + dy * dy);
					if (dist < nearestDist) nearestDist = dist;

					// 距离太远的资源不值得逐个跑 A*（会很慢），只验证近处的可达性
					if (dist <= 30 && CanReach(grid, spawn.GridX, spawn.GridY, e.GridX, e.GridY))
						reachableResources++;
				}

				if (nearestDist == int.MaxValue)
				{
					report.Error("spawn_no_resource",
						$"出生点 {spawn.TeamSlot}({spawn.GridX},{spawn.GridY}) 附近没有任何资源点，该玩家无法发展。",
						spawn.GridX, spawn.GridY);
				}
				else
				{
					if (nearestDist > 30)
						report.Error("resource_too_far",
							$"出生点 {spawn.TeamSlot} 最近的资源在 {nearestDist} 格之外，开局无法采集。",
							spawn.GridX, spawn.GridY);
					else if (nearestDist > 18)
						report.Warn("resource_far",
							$"出生点 {spawn.TeamSlot} 最近的资源在 {nearestDist} 格之外，开局采集效率偏低。",
							spawn.GridX, spawn.GridY);

					// 30 格内可达资源不足 = 没法持续发展
					if (reachableResources == 0)
						report.Error("spawn_resource_unreachable",
							$"出生点 {spawn.TeamSlot} 附近有资源但一个都走不到（被墙隔开）。",
							spawn.GridX, spawn.GridY);
					else if (reachableResources < 4)
						report.Warn("resource_scarce",
							$"出生点 {spawn.TeamSlot} 30 格内只有 {reachableResources} 个可达资源点，可能不够发展。",
							spawn.GridX, spawn.GridY);
				}
			}
		}

		public static bool IsResource(string entityId) => MapGenerator.IsResourceEntity(entityId);

		// =========================================================
		// 建造禁区
		// =========================================================

		private static void ValidateBuildMask(RtsMapData map, MapValidationReport report)
		{
			if (map.BuildBlocked == null || map.BuildBlocked.Length == 0)
				return; // 未使用，合法

			if (map.BuildBlocked.Length != map.Width * map.Height)
			{
				report.Error("mask_size",
					$"建造禁区掩码长度 {map.BuildBlocked.Length} 与地图 {map.Width}x{map.Height} 不匹配。");
				return;
			}

			// 出生点附近必须能放下基地（主基地普遍是 3x3 或更大）。
			// 只数"格子数"是不够的：资源点密布时会出现整片禁建格，
			// 但真正决定能不能开局的是"有没有一块连续的空地"。
			foreach (var spawn in map.SpawnPoints)
			{
				if (spawn == null || !map.InBounds(spawn.GridX, spawn.GridY)) continue;

				int bestFootprint = LargestFreeFootprintNear(map, spawn.GridX, spawn.GridY, searchRadius: 5);

				if (bestFootprint == 0)
					report.Error("spawn_no_build",
						$"出生点 {spawn.TeamSlot} 周围没有任何可建造格（被建造禁区封死）。",
						spawn.GridX, spawn.GridY);
				else if (bestFootprint < 3)
					report.Warn("spawn_tight_build",
						$"出生点 {spawn.TeamSlot} 周围最大可建造空地只有 {bestFootprint}x{bestFootprint}，放不下主基地。",
						spawn.GridX, spawn.GridY);
			}
		}

		/// <summary>
		/// 在 (cx,cy) 周围 searchRadius 格内，找最大的 "NxN 全可建造" 方块的边长。
		/// 返回 0 表示完全没有可建造格。
		/// 只看锚点在搜索范围内的方块，避免把远处空地算进来。
		/// </summary>
		private static int LargestFreeFootprintNear(RtsMapData map, int cx, int cy, int searchRadius, int maxSize = 6)
		{
			int best = 0;

			for (int ox = -searchRadius; ox <= searchRadius; ox++)
			{
				for (int oy = -searchRadius; oy <= searchRadius; oy++)
				{
					int ax = cx + ox;
					int ay = cy + oy;
					if (!map.InBounds(ax, ay)) continue;

					// 单格可建造即至少 1
					if (map.IsWalkableCell(ax, ay) && !map.IsBuildBlocked(ax, ay))
					{
						if (best < 1) best = 1;
					}
					else
					{
						continue;
					}

					for (int size = 2; size <= maxSize; size++)
					{
						if (size <= best) continue;
						if (!IsFreeSquare(map, ax, ay, size)) break;
						best = size;
					}
				}
			}

			return best;
		}

		private static bool IsFreeSquare(RtsMapData map, int ax, int ay, int size)
		{
			for (int x = 0; x < size; x++)
			{
				for (int y = 0; y < size; y++)
				{
					int px = ax + x;
					int py = ay + y;
					if (!map.InBounds(px, py)) return false;
					if (!map.IsWalkableCell(px, py)) return false;
					if (map.IsBuildBlocked(px, py)) return false;
				}
			}
			return true;
		}

		// =========================================================
		// 区域
		// =========================================================

		private static void ValidateRegions(RtsMapData map, MapValidationReport report)
		{
			if (map.Regions == null || map.Regions.Count == 0) return;

			var ids = new HashSet<string>(StringComparer.Ordinal);
			foreach (var r in map.Regions)
			{
				if (r == null) { report.Error("null_region", "区域列表里有空条目。"); continue; }

				if (string.IsNullOrWhiteSpace(r.RegionId))
				{
					report.Error("region_no_id", "区域缺少 RegionId（触发器按名字引用它）。", r.GridX, r.GridY);
					continue;
				}
				if (!ids.Add(r.RegionId))
					report.Error("region_dup_id", $"区域 ID 重复：'{r.RegionId}'。", r.GridX, r.GridY);

				if (r.HalfWidth < 0 || r.HalfHeight < 0 || r.Radius < 0)
					report.Error("region_bad_size", $"区域 '{r.RegionId}' 的尺寸为负。", r.GridX, r.GridY);

				if (!map.InBounds(r.GridX, r.GridY))
					report.Warn("region_oob", $"区域 '{r.RegionId}' 的中心 ({r.GridX},{r.GridY}) 在地图外。", r.GridX, r.GridY);

				// 中心是否落在墙里（区域仍可用，但多半不是作者想要的）
				if (map.InBounds(r.GridX, r.GridY) && !map.IsWalkableCell(r.GridX, r.GridY))
					report.Warn("region_in_wall", $"区域 '{r.RegionId}' 的中心在墙上。", r.GridX, r.GridY);
			}
		}

		// =========================================================
		// 触发器
		// =========================================================

		private static void ValidateTriggers(RtsMapData map, MapValidationReport report)
		{
			if (map.Triggers == null || map.Triggers.Count == 0) return;

			var ids = new HashSet<string>(StringComparer.Ordinal);
			var varNames = new HashSet<string>(StringComparer.Ordinal);

			if (map.Variables != null)
				foreach (var v in map.Variables)
					if (v != null && !string.IsNullOrWhiteSpace(v.Name))
						varNames.Add(v.Name);

			// 第一遍：收集 ID，供跨引用校验
			foreach (var t in map.Triggers)
			{
				if (t == null) continue;
				if (string.IsNullOrWhiteSpace(t.TriggerId)) continue;
				if (!ids.Add(t.TriggerId))
					report.Error("trigger_dup_id", $"触发器 ID 重复：'{t.TriggerId}'。");
			}

			foreach (var t in map.Triggers)
			{
				if (t == null) { report.Error("null_trigger", "触发器列表里有空条目。"); continue; }

				string label = string.IsNullOrWhiteSpace(t.TriggerId) ? "(未命名)" : t.TriggerId;

				if (string.IsNullOrWhiteSpace(t.TriggerId))
					report.Error("trigger_no_id", "触发器缺少 TriggerId。");

				if (t.CooldownTicks < 0)
					report.Error("trigger_bad_cooldown", $"触发器 '{label}' 的 CooldownTicks 为负。");

				if (t.IntervalTicks < 0)
					report.Error("trigger_bad_interval", $"触发器 '{label}' 的 IntervalTicks 为负。");

				if (t.Phase == TriggerPhase.Scheduled && t.IntervalTicks > 0 && t.Repeatable == false)
					report.Warn("trigger_interval_unused",
						$"触发器 '{label}' 设了 IntervalTicks 但 Repeatable=false，只会触发一次。");

				if (t.StartTick < 0)
					report.Error("trigger_bad_start", $"触发器 '{label}' 的 StartTick 为负。");

				// 条件：语法校验 + 引用的变量/区域是否存在
				if (t.Conditions != null)
				{
					foreach (var c in t.Conditions)
					{
						if (c == null) continue;
						if (string.IsNullOrWhiteSpace(c.Expression)) continue;

						string err = SimExpr.Validate(c.Expression);
						if (err != null)
						{
							report.Error("trigger_bad_expr", $"触发器 '{label}' 的条件语法错误：{err}");
							continue;
						}

						// 引用完整性：表达式里出现的 region("x") / 变量名
						CheckExpressionReferences(c.Expression, map, varNames, report, label, "条件");
					}
				}

				// 赋值
				if (t.Assignments != null)
				{
					foreach (var a in t.Assignments)
					{
						if (a == null) continue;
						if (string.IsNullOrWhiteSpace(a.Variable))
						{
							report.Error("trigger_no_var", $"触发器 '{label}' 有一条赋值没有写变量名。");
							continue;
						}
						varNames.Add(a.Variable);

						if (!string.IsNullOrWhiteSpace(a.ValueExpression))
						{
							string err = SimExpr.Validate(a.ValueExpression);
							if (err != null)
								report.Error("trigger_bad_expr", $"触发器 '{label}' 赋值 '{a.Variable}' 语法错误：{err}");
							else
								CheckExpressionReferences(a.ValueExpression, map, varNames, report, label, "赋值");
						}
					}
				}

				// 动作
				if (t.Actions != null)
				{
					foreach (var act in t.Actions)
					{
						if (act == null) continue;

						foreach (var expr in new[] { act.TargetExpression, act.AmountExpression })
						{
							if (string.IsNullOrWhiteSpace(expr)) continue;
							string err = SimExpr.Validate(expr);
							if (err != null)
							{
								report.Error("trigger_bad_expr", $"触发器 '{label}' 动作 {act.Kind} 语法错误：{err}");
								continue;
							}
							CheckExpressionReferences(expr, map, varNames, report, label, $"动作 {act.Kind}");
						}

						switch (act.Kind)
						{
							case TriggerActionKind.SpawnUnit:
								if (string.IsNullOrWhiteSpace(act.Id))
									report.Error("action_no_unit", $"触发器 '{label}' 的刷兵动作没有填单位 ID。");
								else if (!map.InBounds(act.GridX, act.GridY))
									report.Error("action_spawn_oob",
										$"触发器 '{label}' 的刷兵点 ({act.GridX},{act.GridY}) 在地图外。", act.GridX, act.GridY);
								else if (!map.IsWalkableCell(act.GridX, act.GridY))
									report.Warn("action_spawn_wall",
										$"触发器 '{label}' 的刷兵点 ({act.GridX},{act.GridY}) 在墙上，单位可能被卡住。",
										act.GridX, act.GridY);
								break;

							case TriggerActionKind.AddResource:
								if (string.IsNullOrWhiteSpace(act.Id))
									report.Error("action_no_resource", $"触发器 '{label}' 的加资源动作没有填资源类型。");
								else if (!IsKnownResourceName(act.Id))
									report.Warn("action_unknown_resource",
										$"触发器 '{label}' 引用未知资源类型 '{act.Id}'（应为 ResourceType 名，如 Metal/Gas/Wood）。");
								break;

							case TriggerActionKind.GrantTech:
								if (string.IsNullOrWhiteSpace(act.Id))
									report.Error("action_no_tech", $"触发器 '{label}' 的给科技动作没有填科技 ID。");
								break;

							case TriggerActionKind.SetVariable:
								if (string.IsNullOrWhiteSpace(act.Id))
									report.Error("action_no_var", $"触发器 '{label}' 的设置变量动作没有填变量名。");
								else
									varNames.Add(act.Id);
								break;

							case TriggerActionKind.SetTriggerEnabled:
								if (string.IsNullOrWhiteSpace(act.Id))
									report.Error("action_no_trigger", $"触发器 '{label}' 的启停动作没有填目标触发器 ID。");
								else if (!ids.Contains(act.Id))
									report.Error("action_unknown_trigger",
										$"触发器 '{label}' 要启停的触发器 '{act.Id}' 不存在。");
								break;

							case TriggerActionKind.EndMatch:
								if (!string.IsNullOrWhiteSpace(act.TargetExpression))
								{
									string err = SimExpr.Validate(act.TargetExpression);
									if (err != null)
										report.Error("trigger_bad_expr", $"触发器 '{label}' 的结束对局目标语法错误：{err}");
								}
								break;
						}
					}
				}

				// 一个永远不可能触发的组合
				if (!t.EnabledAtStart && t.Phase != TriggerPhase.EventDriven)
				{
					bool anyEnabler = false;
					foreach (var other in map.Triggers)
					{
						if (other == null || other == t || other.Actions == null) continue;
						foreach (var a in other.Actions)
							if (a != null && a.Kind == TriggerActionKind.SetTriggerEnabled && a.Id == t.TriggerId)
								anyEnabler = true;
					}
					if (!anyEnabler)
						report.Warn("trigger_never_enabled",
							$"触发器 '{label}' 初始禁用，但没有任何触发器会启用它——永远不会执行。");
				}
			}
		}

		/// <summary>
		/// 轻量引用检查：用正则扫出 region("x") / var_xxx / trigger_xxx_fired 这类引用，
		/// 确认它们对应的地图对象存在。不做完整语义分析（表达式引擎已保证语法正确）。
		/// </summary>
		private static void CheckExpressionReferences(
			string expression, RtsMapData map, HashSet<string> knownVars,
			MapValidationReport report, string triggerLabel, string where)
		{
			// units_in("region") / structures_in("region")
			foreach (System.Text.RegularExpressions.Match m in
				System.Text.RegularExpressions.Regex.Matches(expression, @"(?:units_in|structures_in)\s*\(\s*['""]([^'""]+)['""]"))
			{
				string regionId = m.Groups[1].Value;
				if (map.FindRegion(regionId) == null)
					report.Error("expr_unknown_region",
						$"触发器 '{triggerLabel}' 的{where}引用了不存在的区域 '{regionId}'。");
			}

			// trigger_<id>_fired / _count / _enabled
			foreach (System.Text.RegularExpressions.Match m in
				System.Text.RegularExpressions.Regex.Matches(expression, @"trigger_([A-Za-z0-9_]+?)_(?:fired|count|enabled)\b"))
			{
				string targetId = m.Groups[1].Value;
				if (map.FindTrigger(targetId) == null)
					report.Error("expr_unknown_trigger",
						$"触发器 '{triggerLabel}' 的{where}引用了不存在的触发器状态 'trigger_{targetId}_*'。");
			}

			// var_xxx：约定脚本变量以 var_ 前缀命名，未声明则提示
			foreach (System.Text.RegularExpressions.Match m in
				System.Text.RegularExpressions.Regex.Matches(expression, @"\bvar_([A-Za-z0-9_]+)\b"))
			{
				string varName = "var_" + m.Groups[1].Value;
				// 触发器的赋值可能稍后才声明，这里只在集合里也找不到时提示
				if (!knownVars.Contains(varName))
					report.Warn("expr_unknown_var",
						$"触发器 '{triggerLabel}' 的{where}引用了未声明的变量 '{varName}'（未定义变量按 0 处理）。");
			}
		}

		private static bool IsKnownResourceName(string name) =>
			Enum.TryParse(typeof(ResourceType), name, ignoreCase: true, out _);

		// =========================================================
		// 真实寻路可达性
		// =========================================================

		/// <summary>
		/// 用 SimPathfinder 判断两步之间是否真的走得到。
		///
		/// **不能只看"最后一个路径点接近目标"**：SimPathfinder 在 A* 迭代超限时会
		/// 返回"到最接近节点"的截断路径（SimPathfinder.cs 的 loopLimit 分支），
		/// 这种路径可能从墙里穿过去、却仍然把终点写成目标格附近——直接信它会把
		/// 被墙分割的地图误判为连通。
		///
		/// 这里做两道独立校验：
		///   1. 路径上**每个点**都必须在可走格内（截断路径会穿过阻挡格）；
		///   2. 最后一个点必须真正落在目标格附近（与 SimUnit.CommandMove 的到点判定一致）。
		/// </summary>
		private static bool CanReach(SimGrid grid, int fromX, int fromY, int toX, int toY)
		{
			var from = CellCenter(new SimVector2I(fromX, fromY));
			var to = CellCenter(new SimVector2I(toX, toY));

			var path = SimPathfinder.FindPath(
				grid, EmptyStructures, from, to,
				ignoreId: -1, ignoreObstacles: false, radius: (FP)15, skipInitialLos: false,
				radiusTilesOverride: -1, teamId: 0);

			if (path == null || path.Count == 0) return false;

			// (1) 路径不得穿过不可走格
			foreach (var point in path)
			{
				var cell = new SimVector2I(
					(int)((long)point.X / RtsMapData.TileSize),
					(int)((long)point.Y / RtsMapData.TileSize));

				if (!map_WalkableCell(grid, cell))
					return false;
			}

			// (2) 真正到达：1.5 格容差，与 SimUnit.CommandMove 的到点判定一致
			return FPVector2.DistanceSquared(path[path.Count - 1], to) <= (FP)(96 * 96);
		}

		/// <summary>格子是否可走（只认 SimGrid，与游戏内判定同源）。</summary>
		private static bool map_WalkableCell(SimGrid grid, SimVector2I cell) =>
			grid.TerrainCells.Contains(cell) && !grid.StaticObstacles.Contains(cell);

		/// <summary>校验期没有真实建筑，用一个空的建筑表跑寻路。</summary>
		private static readonly Dictionary<int, SimStructure> EmptyStructures = new();
	}
}
