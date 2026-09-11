using System;
using System.Collections.Generic;
using Godot;
using RTS.Data.Maps;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.MapEditing
{
	// =========================================================
	// 程序化地图生成器
	//
	// 确定性要求（关键）：
	//   生成结果是**被写进地图数据的**，运行时不再重新生成，所以生成器本身
	//   不需要跨端同步。但同一个种子必须在同一次编辑会话里给出同样结果，
	//   否则作者点两次"生成"得到不同地图、也无法复现别人的地图。
	//   因此：只用 SimRandom（LCG），绝不用 GD.Rand / System.Random / 时间。
	//
	// 生成管线（固定顺序）：
	//   1. 铺底            全 Grass
	//   2. 边界墙          四周 StampBorder
	//   3. 障碍            散布 / 房间 / 洞穴 / 隘口，按 Style 分支
	//   4. 对称            水平/垂直/旋转180/四向镜像
	//   5. 连通性修复      把与主区域不连通的空腔填成墙（否则会产生"死区"）
	//   6. 出生点          均匀分布在边缘环上，并清理周围障碍
	//   7. 资源与中立物    按 Richness 在可达区域撒矿/气/中立塔/圣地
	//   8. 建造禁区        出生点与资源点周围标记禁建
	// =========================================================

	public enum MapSymmetry
	{
		None = 0,
		/// <summary>左右镜像（1v1 标准）。</summary>
		HorizontalMirror = 1,
		/// <summary>上下镜像。</summary>
		VerticalMirror = 2,
		/// <summary>中心旋转 180°（最公平，适合 1v1）。</summary>
		Rotational180 = 3,
		/// <summary>四向全对称（2v2 / 4 人）。</summary>
		QuadMirror = 4,
	}

	public enum MapStyle
	{
		/// <summary>开阔战场：少量散落障碍。</summary>
		Open = 0,
		/// <summary>走廊迷宫：房间 + 连通走廊。</summary>
		Rooms = 1,
		/// <summary>洞穴：元胞自动机生成的有机空腔。</summary>
		Caves = 2,
		/// <summary>隘口：中央与侧翼的带状阻挡，制造进攻路线选择。</summary>
		Chokepoints = 3,
	}

	/// <summary>生成参数。全部可序列化，作者可以保存/复用一套配置。</summary>
	[GlobalClass]
	public partial class MapGenSettings : Resource
	{
		[Export] public int Width { get; set; } = 120;
		[Export] public int Height { get; set; } = 120;

		/// <summary>随机种子。同种子 + 同参数 = 同地图（可复现）。</summary>
		[Export] public int Seed { get; set; } = 12345;

		[Export] public MapStyle Style { get; set; } = MapStyle.Open;
		[Export] public MapSymmetry Symmetry { get; set; } = MapSymmetry.Rotational180;

		/// <summary>边界墙厚度（格）。0 = 不封边（不推荐，单位会走出地图）。</summary>
		[Export] public int BorderThickness { get; set; } = 2;

		/// <summary>障碍密度 0..1（Open 风格使用）。</summary>
		[Export] public float ObstacleDensity { get; set; } = 0.12f;

		/// <summary>Rooms 风格：房间数量尝试值。</summary>
		[Export] public int RoomCount { get; set; } = 14;
		[Export] public int RoomMinSize { get; set; } = 5;
		[Export] public int RoomMaxSize { get; set; } = 14;

		/// <summary>Caves 风格：元胞自动机迭代次数与初始填充率。</summary>
		[Export] public int CaveIterations { get; set; } = 4;
		[Export] public float CaveFillPercent { get; set; } = 0.45f;

		/// <summary>出生点数量（2..8）。</summary>
		[Export] public int SpawnPointCount { get; set; } = 2;

		/// <summary>出生点距边缘的格数（越大越靠内，留给基地展开）。</summary>
		[Export] public int SpawnEdgeMargin { get; set; } = 8;

		/// <summary>贫富度 0..1：控制额外资源簇与中立塔数量。</summary>
		[Export] public float Richness { get; set; } = 0.5f;

		/// <summary>每个出生点保底铁矿数。</summary>
		[Export] public int BaseIronPerSpawn { get; set; } = 8;

		/// <summary>每个出生点保底气泉数。</summary>
		[Export] public int BaseGasPerSpawn { get; set; } = 2;

		/// <summary>中立防御塔数量（0 = 不生成）。</summary>
		[Export] public int NeutralTowerCount { get; set; } = 4;

		/// <summary>中立圣地数量。</summary>
		[Export] public int ShrineCount { get; set; } = 2;

		/// <summary>清理出生点周围半径（格）内的障碍，避免基地被堵。</summary>
		[Export] public int SpawnClearRadius { get; set; } = 6;

		/// <summary>是否在资源点周围标记建造禁区。</summary>
		[Export] public bool MarkBuildBlocked { get; set; } = true;

		/// <summary>资源点禁建半径（格）。与运行时 IsAreaNearResourceNode(...,2) 的意图对齐。</summary>
		[Export] public int ResourceBuildBanRadius { get; set; } = 2;
	}

	/// <summary>生成结果统计，供编辑器展示与校验。</summary>
	public struct MapGenReport
	{
		public int Width;
		public int Height;
		public int WallCells;
		public int FloorCells;
		public int FilledIsolatedCells;
		public int SpawnPointsPlaced;
		public int ResourcesPlaced;
		public int NeutralStructuresPlaced;
		/// <summary>最终兜底修补的连通块数量（0 = 本来就连通）。</summary>
		public int IsolatedComponentsFixed;
	}

	public static class MapGenerator
	{
		/// <summary>
		/// 按参数生成一张完整地图（含出生点、资源、中立物）。
		/// 同一个 settings 必然给出同一结果，可反复调用。
		/// </summary>
		public static RtsMapData Generate(MapGenSettings settings, out MapGenReport report)
		{
			settings ??= new MapGenSettings();
			report = new MapGenReport();

			var map = new RtsMapData
			{
				MapId = "generated",
				DisplayName = "Generated",
			};
			map.Resize(settings.Width, settings.Height);
			map.SpawnPoints.Clear();
			map.Entities.Clear();

			int w = map.Width;
			int h = map.Height;

			// 单一随机源：任何额外随机都必须从这里取，否则不可复现
			var rng = new SimRandom((uint)settings.Seed);

			// ---- 1. 铺底 ----
			map.Fill(RtsMapData.SourceGrass);

			// ---- 2. 边界墙 ----
			if (settings.BorderThickness > 0)
				MapDataOps.StampBorder(map, settings.BorderThickness);

			// ---- 3. 障碍（只在边界内区域生成）----
			int interiorMinX = settings.BorderThickness;
			int interiorMinY = settings.BorderThickness;
			int interiorMaxX = w - settings.BorderThickness;
			int interiorMaxY = h - settings.BorderThickness;

			switch (settings.Style)
			{
				case MapStyle.Open:
					ScatterObstacles(map, rng, interiorMinX, interiorMinY, interiorMaxX, interiorMaxY,
						settings.ObstacleDensity);
					break;
				case MapStyle.Rooms:
					CarveRooms(map, rng, interiorMinX, interiorMinY, interiorMaxX, interiorMaxY, settings);
					break;
				case MapStyle.Caves:
					GenerateCaves(map, rng, interiorMinX, interiorMinY, interiorMaxX, interiorMaxY, settings);
					break;
				case MapStyle.Chokepoints:
					GenerateChokepoints(map, interiorMinX, interiorMinY, interiorMaxX, interiorMaxY);
					break;
			}

			// ---- 4. 对称（障碍之后、出生点之前）----
			ApplySymmetry(map, settings.Symmetry);

			// ---- 5. 连通性修复 ----
			report.FilledIsolatedCells = FillIsolatedPockets(map);

			// 连通性修复是按"最大连通块"填的，本身不带对称性；
			// 再套一次对称把结果拉回对称（否则测试会实测出地图左右不一致）。
			ApplySymmetry(map, settings.Symmetry);

			// ---- 6. 出生点 ----
			// 刻意的顺序：**地形全部定型之后**再放出生点，紧随其后套一次对称。
			// 曾把出生点放在对称之前，最后那次 ApplySymmetry 会重新引入分割
			// （镜像把两块本来连通的地面切成互不相通的两半），出生点就落在
			// 不同连通块里——校验器实测报 spawn_unreachable。
			// 现在的做法：在"对称基本单元"内落点并逐个用真实寻路确认连通，
			// 再把地形与出生点一起交给对称复制到其余象限。
			report.SpawnPointsPlaced = PlaceSpawnPoints(map, settings);
			ApplySymmetry(map, settings.Symmetry);

			// ---- 7. 资源与中立物 ----
			int resources = 0;
			int neutrals = 0;
			PlaceResourcesAndNeutrals(map, rng, settings, ref resources, ref neutrals);
			report.ResourcesPlaced = resources;
			report.NeutralStructuresPlaced = neutrals;

			// ---- 8. 建造禁区 ----
			if (settings.MarkBuildBlocked)
				MarkBuildBanZones(map, settings);

			// ---- 9. 最终连通性兜底 ----
			// 前面的对称操作理论上保持连通，但 Rooms 风格在"象限内最大连通块"
			// 被复制到四个象限后仍可能出现互不相通的副本（实测过：QuadMirror 下
			// 地面被分成 3 块）。这里做一次确定性修补：把每个非主连通块
			// 用一条走廊接到主区域，再套一次对称保持对称性。
			report.IsolatedComponentsFixed = EnsureFloorConnected(map, settings.Symmetry);

			// ---- 统计 ----
			report.Width = w;
			report.Height = h;
			for (int i = 0; i < map.Terrain.Length; i++)
			{
				if (RtsMapData.DecodeSource(map.Terrain[i]) == RtsMapData.SourceWall) report.WallCells++;
				else report.FloorCells++;
			}

			map.MaxPlayers = Math.Clamp(settings.SpawnPointCount, 2, 8);
			map.RecommendedPlayers = map.MaxPlayers;
			map.Description = $"程序生成 · {settings.Style} · {settings.Symmetry} · 种子 {settings.Seed}";
			return map;
		}

		// =========================================================
		// 3a. 开阔战场：散落障碍簇
		// =========================================================

		private static void ScatterObstacles(RtsMapData map, SimRandom rng,
			int minX, int minY, int maxX, int maxY, float density)
		{
			if (maxX <= minX || maxY <= minY) return;

			int area = (maxX - minX) * (maxY - minY);
			int targetCells = (int)(area * Math.Clamp(density, 0f, 0.60f));
			int placed = 0;
			int guard = targetCells * 8 + 64;

			while (placed < targetCells && guard-- > 0)
			{
				int cx = minX + (int)(rng.Next() % (uint)(maxX - minX));
				int cy = minY + (int)(rng.Next() % (uint)(maxY - minY));
				int size = 1 + (int)(rng.Next() % 3u);

				for (int x = cx; x < cx + size && x < maxX; x++)
				{
					for (int y = cy; y < cy + size && y < maxY; y++)
					{
						if (map.IsWallCell(x, y)) continue;
						map.SetCell(x, y, RtsMapData.SourceWall);
						placed++;
					}
				}
			}
		}

		// =========================================================
		// 3b. 房间 + 走廊
		// =========================================================

		private static void CarveRooms(RtsMapData map, SimRandom rng,
			int minX, int minY, int maxX, int maxY, MapGenSettings settings)
		{
			if (maxX <= minX || maxY <= minY) return;

			// 先整片填墙，再挖房间与走廊（迷宫观感）
			for (int x = minX; x < maxX; x++)
				for (int y = minY; y < maxY; y++)
					map.SetCell(x, y, RtsMapData.SourceWall);

			int rooms = Math.Max(1, settings.RoomCount);
			int minSize = Math.Max(3, Math.Min(settings.RoomMinSize, settings.RoomMaxSize));
			int maxSize = Math.Max(minSize, settings.RoomMaxSize);

			int prevCenterX = -1;
			int prevCenterY = -1;

			for (int i = 0; i < rooms; i++)
			{
				int rw = minSize + (int)(rng.Next() % (uint)(maxSize - minSize + 1));
				int rh = minSize + (int)(rng.Next() % (uint)(maxSize - minSize + 1));
				if (maxX - minX <= rw + 2 || maxY - minY <= rh + 2) continue;

				int rx = minX + 1 + (int)(rng.Next() % (uint)(maxX - minX - rw - 2));
				int ry = minY + 1 + (int)(rng.Next() % (uint)(maxY - minY - rh - 2));

				for (int x = rx; x < rx + rw; x++)
					for (int y = ry; y < ry + rh; y++)
						map.SetCell(x, y, RtsMapData.SourceGrass);

				int cx = rx + rw / 2;
				int cy = ry + rh / 2;

				// 与上一个房间挖一条 L 形走廊，保证整体连通
				if (prevCenterX >= 0)
					CarveCorridor(map, prevCenterX, prevCenterY, cx, cy, 1);

				prevCenterX = cx;
				prevCenterY = cy;
			}
		}

		private static void CarveCorridor(RtsMapData map, int x0, int y0, int x1, int y1, int halfWidth)
		{
			int stepX = x0 <= x1 ? 1 : -1;
			for (int x = x0; x != x1 + stepX; x += stepX)
				for (int d = -halfWidth; d <= halfWidth; d++)
					map.SetCell(x, y0 + d, RtsMapData.SourceGrass);

			int stepY = y0 <= y1 ? 1 : -1;
			for (int y = y0; y != y1 + stepY; y += stepY)
				for (int d = -halfWidth; d <= halfWidth; d++)
					map.SetCell(x1 + d, y, RtsMapData.SourceGrass);
		}

		// =========================================================
		// 3c. 洞穴：元胞自动机
		// =========================================================

		private static void GenerateCaves(RtsMapData map, SimRandom rng,
			int minX, int minY, int maxX, int maxY, MapGenSettings settings)
		{
			if (maxX <= minX || maxY <= minY) return;

			int w = maxX - minX;
			int h = maxY - minY;
			var grid = new bool[w, h]; // true = 墙

			float fill = Math.Clamp(settings.CaveFillPercent, 0.30f, 0.62f);
			var fillFp = (FixMath.NET.Fix64)fill;
			for (int x = 0; x < w; x++)
				for (int y = 0; y < h; y++)
					grid[x, y] = rng.NextFP() < fillFp;

			int iterations = Math.Max(0, settings.CaveIterations);
			for (int i = 0; i < iterations; i++)
				grid = SmoothCaves(grid, w, h);

			for (int x = 0; x < w; x++)
				for (int y = 0; y < h; y++)
					map.SetCell(minX + x, minY + y,
						grid[x, y] ? RtsMapData.SourceWall : RtsMapData.SourceGrass);
		}

		/// <summary>经典 4-5 规则：邻居墙超过 4 变墙，少于 4 变空，等于 4 保持。</summary>
		private static bool[,] SmoothCaves(bool[,] src, int w, int h)
		{
			var dst = new bool[w, h];
			for (int x = 0; x < w; x++)
			{
				for (int y = 0; y < h; y++)
				{
					int walls = 0;
					for (int dx = -1; dx <= 1; dx++)
					{
						for (int dy = -1; dy <= 1; dy++)
						{
							if (dx == 0 && dy == 0) continue;
							int nx = x + dx;
							int ny = y + dy;
							// 越界视为墙：让洞穴自然收缩到边界内
							if (nx < 0 || ny < 0 || nx >= w || ny >= h) walls++;
							else if (src[nx, ny]) walls++;
						}
					}

					if (walls > 4) dst[x, y] = true;
					else if (walls < 4) dst[x, y] = false;
					else dst[x, y] = src[x, y];
				}
			}
			return dst;
		}

		// =========================================================
		// 3d. 隘口：中央与侧翼带状阻挡
		// =========================================================

		private static void GenerateChokepoints(RtsMapData map,
			int minX, int minY, int maxX, int maxY)
		{
			int w = maxX - minX;
			int h = maxY - minY;
			if (w < 16 || h < 16) return;

			int midX = minX + w / 2;

			// 中央竖墙，两条错开的缺口形成 S 形主通道
			int gapAStart = minY + h / 4;
			int gapAEnd = minY + h / 4 + h / 6;
			int gapBStart = minY + h * 3 / 5;
			int gapBEnd = minY + h * 3 / 5 + h / 6;

			for (int y = minY; y < maxY; y++)
			{
				bool inGapA = y >= gapAStart && y <= gapAEnd;
				bool inGapB = y >= gapBStart && y <= gapBEnd;
				if (inGapA || inGapB) continue;
				map.SetCell(midX, y, RtsMapData.SourceWall);
			}

			// 两条侧翼墙各留一个缺口，制造三线选择
			int flankOffset = Math.Max(4, w / 5);
			for (int y = minY; y < maxY; y++)
			{
				if (!(y >= minY + h / 3 && y <= minY + h / 2))
					map.SetCell(midX - flankOffset, y, RtsMapData.SourceWall);

				if (!(y >= minY + h / 2 && y <= minY + h * 2 / 3))
					map.SetCell(midX + flankOffset, y, RtsMapData.SourceWall);
			}
		}

		// =========================================================
		// 4. 对称
		// =========================================================

		/// <summary>
		/// 把地图的某一侧（或象限）镜像到其余部分。
		/// 必须在出生点/资源之前做，否则出生点会被镜像成两份。
		///
		/// 坐标系：格子 (x,y)，x 向右、y 向下，中心在 (w/2, h/2)。
		/// 所有变换都是"以地图中心为基准"的，因此对偶数边长地图，
		/// 中心线两侧的格子是 x 与 w-1-x 的配对关系。
		///
		/// 幂等性：同一变换重复应用不会改变已对称的地图
		/// （因为每个目标格都从"源象限"取值，而源象限本身不被覆盖）。
		/// </summary>
		public static void ApplySymmetry(RtsMapData map, MapSymmetry symmetry)
		{
			if (symmetry == MapSymmetry.None || !map.HasTerrain) return;

			int w = map.Width;
			int h = map.Height;

			// 先把"源"整片拷出来：边写边读会让结果依赖遍历顺序。
			var src = (byte[])map.Terrain.Clone();

			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					int sx = x;
					int sy = y;

					switch (symmetry)
					{
						case MapSymmetry.HorizontalMirror:
							// 左半为源，右半是左半的镜像
							sx = x < w / 2 ? x : w - 1 - x;
							break;

						case MapSymmetry.VerticalMirror:
							// 上半为源
							sy = y < h / 2 ? y : h - 1 - y;
							break;

						case MapSymmetry.Rotational180:
							// 绕地图中心旋转 180°：源在"左上到中心"的象限，
							// 四象限各自按中心线镜像映射回源象限。
							sx = x < w / 2 ? x : w - 1 - x;
							sy = y < h / 2 ? y : h - 1 - y;
							break;

						case MapSymmetry.QuadMirror:
							// 与 Rotational180 相同的四象限约束（两者都是"四向一致"），
							// 保留独立枚举值是为了以后加入真正的翻转差异（如手性）。
							sx = x < w / 2 ? x : w - 1 - x;
							sy = y < h / 2 ? y : h - 1 - y;
							break;
					}

					map.Terrain[y * w + x] = src[sy * w + sx];
				}
			}
		}

		// =========================================================
		// 5. 连通性修复
		// =========================================================

		/// <summary>
		/// 找出所有地面格，取最大连通块作为"主区域"，把其余小块填成墙。
		/// 不做这步的话程序生成很容易产出被墙完全包死的空腔——
		/// 玩家会把建筑或单位放进去然后永远出不来。
		/// 返回被填掉的格子数。
		/// </summary>
		public static int FillIsolatedPockets(RtsMapData map)
		{
			if (!map.HasTerrain) return 0;

			int w = map.Width;
			int h = map.Height;
			var visited = new bool[w * h];
			var components = new System.Collections.Generic.List<System.Collections.Generic.List<int>>();
			var queue = new System.Collections.Generic.Queue<int>();

			for (int start = 0; start < w * h; start++)
			{
				if (visited[start]) continue;
				if (RtsMapData.DecodeSource(map.Terrain[start]) != RtsMapData.SourceGrass)
				{
					visited[start] = true;
					continue;
				}

				var component = new System.Collections.Generic.List<int>();
				queue.Clear();
				queue.Enqueue(start);
				visited[start] = true;

				while (queue.Count > 0)
				{
					int idx = queue.Dequeue();
					component.Add(idx);
					int x = idx % w;
					int y = idx / w;

					// 4 邻域：比寻路的 8 邻域更保守，只保留"确定能走通"的连通块。
					// 对角缝隙留给寻路算法判断，不在这里算作连通。
					TryEnqueue(map, visited, queue, x - 1, y);
					TryEnqueue(map, visited, queue, x + 1, y);
					TryEnqueue(map, visited, queue, x, y - 1);
					TryEnqueue(map, visited, queue, x, y + 1);
				}

				components.Add(component);
			}

			if (components.Count == 0) return 0;

			int best = 0;
			for (int i = 1; i < components.Count; i++)
				if (components[i].Count > components[best].Count) best = i;

			int filled = 0;
			for (int i = 0; i < components.Count; i++)
			{
				if (i == best) continue;
				foreach (int idx in components[i])
				{
					map.Terrain[idx] = (byte)RtsMapData.Encode(RtsMapData.SourceWall, 0);
					filled++;
				}
			}

			return filled;
		}

		private static void TryEnqueue(RtsMapData map, bool[] visited,
			System.Collections.Generic.Queue<int> queue, int x, int y)
		{
			if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) return;
			int idx = y * map.Width + x;
			if (visited[idx]) return;
			if (RtsMapData.DecodeSource(map.Terrain[idx]) != RtsMapData.SourceGrass) return;
			visited[idx] = true;
			queue.Enqueue(idx);
		}

		// =========================================================
		// 6. 出生点
		// =========================================================

		/// <summary>
		/// 把出生点铺在"边缘环"上，并清理周围障碍。
		///
		/// 三个硬性约束（都踩过）：
		///   1. **必须互相连通**——地形可能被墙切成互不相通的两半，直接按角度
		///      落点会产出"出生点 1 走不到出生点 2"的死图。
		///   2. **必须仍然对称**——出生点清理会改地形，之后不再套对称就会出现
		///      只有一侧被清空的不对称。
		///   3. **对称不会复制出生点**——ApplySymmetry 只作用于地形字节，
		///      出生点是独立列表；把落点算在"基本单元"里再指望对称去复制，
		///      结果就是出生点数量不对。所以这里直接按**每个象限各放一个**
		///      的方式落点：同一相对位置在不同象限天然构成对称布局。
		///
		/// 做法：以地图中心为极坐标原点，按 360°/count 等分角度，
		/// 每个象限落在自己的四分之一椭圆环上，天然对称；
		/// 再用真实寻路确认与"主区域"连通，不连通就在附近找替代格。
		/// </summary>
		public static int PlaceSpawnPoints(RtsMapData map, MapGenSettings settings)
		{
			int count = Math.Clamp(settings.SpawnPointCount, 2, 8);
			int w = map.Width;
			int h = map.Height;

			int clearRadius = Math.Max(2, settings.SpawnClearRadius);
			int margin = Math.Max(settings.BorderThickness + 2 + clearRadius,
				Math.Min(settings.SpawnEdgeMargin, Math.Min(w, h) / 2 - 2));

			double cx = w / 2.0;
			double cy = h / 2.0;
			double radiusX = Math.Max(2, cx - margin);
			double radiusY = Math.Max(2, cy - margin);

			var grid = RTS.World.MapLoader.CreateDetachedGrid(map);

			int placed = 0;
			SimVector2I? reference = null;

			for (int i = 0; i < count; i++)
			{
				// 绕地图中心等分：count=2 得到正上/正下（或左右），
				// count=4 得到四个象限各一个，天然对称。
				double angle = -Math.PI / 2.0 + 2.0 * Math.PI * i / count;

				int gx = (int)Math.Round(cx + Math.Cos(angle) * radiusX);
				int gy = (int)Math.Round(cy + Math.Sin(angle) * radiusY);

				gx = Math.Clamp(gx, margin, w - 1 - margin);
				gy = Math.Clamp(gy, margin, h - 1 - margin);

				MapDataOps.StampCircle(map, gx, gy, clearRadius, RtsMapData.SourceGrass, 0);
				grid = RTS.World.MapLoader.CreateDetachedGrid(map);

				var cell = new SimVector2I(gx, gy);

				if (reference.HasValue && !AreConnected(grid, reference.Value, cell))
				{
					if (TryFindConnectedCellNear(grid, reference.Value, gx, gy, clearRadius + 8, out var alt))
						cell = alt;
				}

				reference ??= cell;

				map.SpawnPoints.Add(new MapSpawnPoint
				{
					TeamSlot = i + 1,
					GridX = cell.X,
					GridY = cell.Y,
					FacingDegrees = (float)(angle * 180.0 / Math.PI + 90.0),
					Comment = $"生成出生点 {i + 1}",
				});
				placed++;
			}

			return placed;
		}

		/// <summary>两点之间能否用真实寻路走通（含路径不穿墙校验）。</summary>
		private static bool AreConnected(SimGrid grid, SimVector2I a, SimVector2I b)
		{
			FPVector2 Center(SimVector2I c) => new FPVector2(
				(FP)c.X * (FP)RtsMapData.TileSize + (FP)(RtsMapData.TileSize / 2),
				(FP)c.Y * (FP)RtsMapData.TileSize + (FP)(RtsMapData.TileSize / 2));

			var path = SimPathfinder.FindPath(
				grid, EmptyStructureTable, Center(a), Center(b),
				ignoreId: -1, ignoreObstacles: false, radius: (FP)15, skipInitialLos: false,
				radiusTilesOverride: -1, teamId: 0);

			if (path == null || path.Count == 0) return false;

			// 截断回退路径会穿墙，必须逐点校验
			foreach (var p in path)
			{
				var c = new SimVector2I(
					(int)((long)p.X / RtsMapData.TileSize),
					(int)((long)p.Y / RtsMapData.TileSize));
				if (!grid.TerrainCells.Contains(c) || grid.StaticObstacles.Contains(c))
					return false;
			}

			return FPVector2.DistanceSquared(path[path.Count - 1], Center(b)) <= (FP)(96 * 96);
		}

		/// <summary>
		/// 在 (nearX,nearY) 周围环形搜索一个"可走且与 reference 连通"的格子。
		/// 按半径递增、角度固定顺序扫描，保证确定性。
		/// </summary>
		private static bool TryFindConnectedCellNear(SimGrid grid, SimVector2I reference,
			int nearX, int nearY, int maxRadius, out SimVector2I found)
		{
			found = default;

			for (int r = 0; r <= maxRadius; r++)
			{
				for (int dx = -r; dx <= r; dx++)
				{
					for (int dy = -r; dy <= r; dy++)
					{
						// 只看当前环
						if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;

						var c = new SimVector2I(nearX + dx, nearY + dy);
						if (!grid.TerrainCells.Contains(c) || grid.StaticObstacles.Contains(c)) continue;
						if (!AreConnected(grid, reference, c)) continue;

						found = c;
						return true;
					}
				}
			}
			return false;
		}

		/// <summary>生成期没有真实建筑，用空的建筑表跑寻路。</summary>
		private static readonly System.Collections.Generic.Dictionary<int, SimStructure> EmptyStructureTable = new();

		// =========================================================
		// 7. 资源与中立物
		// =========================================================

		private static void PlaceResourcesAndNeutrals(RtsMapData map, SimRandom rng,
			MapGenSettings settings, ref int resourcesPlaced, ref int neutralsPlaced)
		{
			float richness = Math.Clamp(settings.Richness, 0f, 1f);

			// 7a. 每个出生点附近一组保底资源
			foreach (var spawn in map.SpawnPoints)
			{
				if (spawn == null) continue;
				resourcesPlaced += PlaceScattered(map, rng, "IronOre",
					spawn.GridX, spawn.GridY, settings.BaseIronPerSpawn, 4, 9);
				resourcesPlaced += PlaceScattered(map, rng, "GasSpring",
					spawn.GridX, spawn.GridY, settings.BaseGasPerSpawn, 4, 9);
			}

			// 7b. 中立塔（塔周围资源由运行时的 ScatterResources 生成，保持与旧版一致）
			int towerCount = Math.Max(0, (int)Math.Round(settings.NeutralTowerCount * (0.5 + richness)));
			for (int i = 0; i < towerCount; i++)
			{
				if (!TryFindOpenSpot(map, rng, out int tx, out int ty, minDistanceFromSpawns: 12))
					break;

				map.Entities.Add(new MapEntityPlacement
				{
					EntityId = "Tower",
					Owner = MapEntityOwner.HostileNeutral,
					GridX = tx,
					GridY = ty,
					ScatterResources = true,
					Comment = "生成中立塔",
				});
				neutralsPlaced++;
			}

			// 7c. 圣地（中立不可攻击的争夺点）
			int shrineCount = Math.Max(0, settings.ShrineCount);
			for (int i = 0; i < shrineCount; i++)
			{
				if (!TryFindOpenSpot(map, rng, out int sx, out int sy, minDistanceFromSpawns: 8))
					break;

				map.Entities.Add(new MapEntityPlacement
				{
					EntityId = "Shrine",
					Owner = MapEntityOwner.Neutral,
					GridX = sx,
					GridY = sy,
					Comment = "生成圣地",
				});
				neutralsPlaced++;
			}

			// ---- 7d. 地图中部的额外资源簇（贫富度越高越多）----
			// 注意：这些落点**不清理地形**（IsSpotFree 已经保证是空地），
			// 否则会在第 8 步的最终对称之前再次破坏对称性。
			int extraClusters = (int)Math.Round(2 + richness * 6);
			for (int i = 0; i < extraClusters; i++)
			{
				if (!TryFindOpenSpot(map, rng, out int ex, out int ey, minDistanceFromSpawns: 6))
					break;

				resourcesPlaced += PlaceScattered(map, rng, "IronOre", ex, ey, 3, 2, 4);
				if (rng.NextFP() < (FixMath.NET.Fix64)0.5m)
					resourcesPlaced += PlaceScattered(map, rng, "GasSpring", ex, ey, 1, 2, 4);
			}

			// ---- 8. 最终对称 ----
			// 必须放在最后一步：前面所有会改地形的操作（连通性填洞、出生点清障）
			// 都是一次性、不对称的，这一步把它们的结果拉回严格的对称布局。
			ApplySymmetry(map, settings.Symmetry);
		}

		/// <summary>
		/// 在一个中心点周围的环形带里撒 count 个资源，落点必须在可走地面上。
		/// 全部随机从 rng 取，保证可复现。
		/// </summary>
		private static int PlaceScattered(RtsMapData map, SimRandom rng, string entityId,
			int cx, int cy, int count, int minRadius, int maxRadius)
		{
			int placed = 0;
			for (int i = 0; i < count; i++)
			{
				bool ok = false;
				for (int attempt = 0; attempt < 48; attempt++)
				{
					int ring = minRadius + (int)(rng.Next() % (uint)Math.Max(1, maxRadius - minRadius + 1));
					int angleStep = (int)(rng.Next() % 360u);
					double rad = angleStep * Math.PI / 180.0;
					int x = cx + (int)Math.Round(Math.Cos(rad) * ring);
					int y = cy + (int)Math.Round(Math.Sin(rad) * ring);

					if (!IsSpotFree(map, x, y)) continue;

					map.Entities.Add(new MapEntityPlacement
					{
						EntityId = entityId,
						Owner = MapEntityOwner.Neutral,
						GridX = x,
						GridY = y,
						Comment = "生成资源",
					});
					placed++;
					ok = true;
					break;
				}
				if (!ok) break;
			}
			return placed;
		}

		/// <summary>找一个远离所有出生点的空地（用于中立物）。</summary>
		private static bool TryFindOpenSpot(RtsMapData map, SimRandom rng,
			out int x, out int y, int minDistanceFromSpawns)
		{
			x = 0;
			y = 0;
			int margin = 4;
			for (int attempt = 0; attempt < 240; attempt++)
			{
				int px = margin + (int)(rng.Next() % (uint)Math.Max(1, map.Width - margin * 2));
				int py = margin + (int)(rng.Next() % (uint)Math.Max(1, map.Height - margin * 2));
				if (!IsSpotFree(map, px, py)) continue;

				bool farEnough = true;
				foreach (var spawn in map.SpawnPoints)
				{
					if (spawn == null) continue;
					int dx = px - spawn.GridX;
					int dy = py - spawn.GridY;
					if (dx * dx + dy * dy < minDistanceFromSpawns * minDistanceFromSpawns)
					{
						farEnough = false;
						break;
					}
				}
				if (!farEnough) continue;

				x = px;
				y = py;
				return true;
			}
			return false;
		}

		/// <summary>该格可走、四周留余量、且未被已有实体/出生点占用。</summary>
		public static bool IsSpotFree(RtsMapData map, int x, int y)
		{
			if (map == null || !map.InBounds(x, y)) return false;
			if (!map.IsWalkableCell(x, y)) return false;

			// 留出 1 格余量，免得资源贴墙导致工人卡位
			for (int dx = -1; dx <= 1; dx++)
				for (int dy = -1; dy <= 1; dy++)
					if (!map.InBounds(x + dx, y + dy) || !map.IsWalkableCell(x + dx, y + dy))
						return false;

			foreach (var e in map.Entities)
			{
				if (e == null) continue;
				if (e.GridX == x && e.GridY == y) return false;
			}
			foreach (var sp in map.SpawnPoints)
			{
				if (sp == null) continue;
				if (Math.Abs(sp.GridX - x) <= 2 && Math.Abs(sp.GridY - y) <= 2) return false;
			}
			return true;
		}

		// =========================================================
		// 8. 建造禁区
		// =========================================================

		private static void MarkBuildBanZones(RtsMapData map, MapGenSettings settings)
		{
			int r = Math.Max(0, settings.ResourceBuildBanRadius);

			// 资源点周围禁建（与运行时 IsAreaNearResourceNode(...,2) 的意图一致）
			foreach (var e in map.Entities)
			{
				if (e == null) continue;
				if (!IsResourceEntity(e.EntityId)) continue;
				for (int x = e.GridX - r - 1; x <= e.GridX + r + 1; x++)
					for (int y = e.GridY - r - 1; y <= e.GridY + r + 1; y++)
						map.SetBuildBlocked(x, y, true);
			}

			// 出生点附近清出"可建造基地区"：
			// 资源点各自带一圈 8x8 禁建，密集时会连成一片把出生点整个包住 →
			// 玩家开局放不下主基地。"能开局"优先于资源点保护，所以最后统一清一块空地。
			// 注意不要反过来把出生点本身标成禁建——那会挡掉基地与防御塔。
			const int BaseClearRadius = 4;   // 清 9x9，足够放 3x3 主基地并留出扩展位
			foreach (var sp in map.SpawnPoints)
			{
				if (sp == null) continue;
				for (int x = sp.GridX - BaseClearRadius; x <= sp.GridX + BaseClearRadius; x++)
					for (int y = sp.GridY - BaseClearRadius; y <= sp.GridY + BaseClearRadius; y++)
						map.SetBuildBlocked(x, y, false);
			}
		}

		public static bool IsResourceEntity(string entityId) =>
			entityId == "ore_iron" || entityId == "ore_gas" ||
			entityId == "IronOre" || entityId == "GasSpring" ||
			entityId == "MetalMine" || entityId == "WildFruit";

		// =========================================================
		// 9. 最终连通性兜底
		// =========================================================

		/// <summary>
		/// 保证可走地面是一个连通块（确定性）。
		///
		/// 为什么要兜底：`FillIsolatedPockets` 只保留**全局最大**连通块，
		/// 但对称是"把基本单元复制到各象限"——如果基本单元里还有第二块地面，
		/// 复制后就会得到多个互不相通的副本（实测 QuadMirror + Rooms 会出现 3 块）。
		///
		/// 做法：找出所有连通块，把每个非最大块用一条**直线走廊**接到最大块上，
		/// 连接点取两块之间距离最近的一对格子（确定性）。返回修好的块数。
		/// </summary>
		public static int EnsureFloorConnected(RtsMapData map, MapSymmetry symmetry)
		{
			if (!map.HasTerrain) return 0;

			int fixedCount = 0;

			// 最多迭代 8 轮，防止异常地形导致死循环
			for (int pass = 0; pass < 8; pass++)
			{
				var floor = CollectFloorCells(map);
				if (floor.Count == 0) break;

				var components = FindComponents(map, floor);
				if (components.Count <= 1) break;

				// 找最大块
				int best = 0;
				for (int i = 1; i < components.Count; i++)
					if (components[i].Count > components[best].Count) best = i;

				// 把每个非最大块接到最大块：取距离最近的一对格子，挖直线走廊
				bool changed = false;
				for (int i = 0; i < components.Count; i++)
				{
					if (i == best) continue;

					if (TryFindClosestPair(components[best], components[i], out var a, out var b))
					{
						CarveCorridor(map, a.X, a.Y, b.X, b.Y, 0);
						fixedCount++;
						changed = true;
					}
				}

				if (!changed) break;
			}

			// 修补本身是不对称的，套一次对称把它拉回对称布局
			if (fixedCount > 0)
				ApplySymmetry(map, symmetry);

			return fixedCount;
		}

		private static List<SimVector2I> CollectFloorCells(RtsMapData map)
		{
			var list = new List<SimVector2I>(map.Width * map.Height / 2);
			for (int y = 0; y < map.Height; y++)
				for (int x = 0; x < map.Width; x++)
					if (map.IsWalkableCell(x, y)) list.Add(new SimVector2I(x, y));
			return list;
		}

		/// <summary>把所有可走格按 4 邻域分成连通块。</summary>
		private static List<List<SimVector2I>> FindComponents(RtsMapData map, List<SimVector2I> floor)
		{
			var index = new Dictionary<long, int>();
			for (int i = 0; i < floor.Count; i++)
				index[(long)floor[i].X * 100000L + floor[i].Y] = i;

			var visited = new bool[floor.Count];
			var result = new List<List<SimVector2I>>();
			var queue = new Queue<int>();

			for (int start = 0; start < floor.Count; start++)
			{
				if (visited[start]) continue;

				var comp = new List<SimVector2I>();
				queue.Clear();
				queue.Enqueue(start);
				visited[start] = true;

				while (queue.Count > 0)
				{
					int idx = queue.Dequeue();
					var c = floor[idx];
					comp.Add(c);

					TryVisit(index, visited, queue, c.X - 1, c.Y);
					TryVisit(index, visited, queue, c.X + 1, c.Y);
					TryVisit(index, visited, queue, c.X, c.Y - 1);
					TryVisit(index, visited, queue, c.X, c.Y + 1);
				}

				result.Add(comp);
			}

			return result;
		}

		private static void TryVisit(Dictionary<long, int> index, bool[] visited,
			Queue<int> queue, int x, int y)
		{
			if (!index.TryGetValue((long)x * 100000L + y, out int i)) return;
			if (visited[i]) return;
			visited[i] = true;
			queue.Enqueue(i);
		}

		/// <summary>
		/// 找两块之间"距离最近的一对格子"。平方距离比较，平局按坐标排序，保证确定性。
		/// </summary>
		private static bool TryFindClosestPair(List<SimVector2I> a, List<SimVector2I> b,
			out SimVector2I pa, out SimVector2I pb)
		{
			pa = default;
			pb = default;
			long bestDist = long.MaxValue;
			bool found = false;

			foreach (var ca in a)
			{
				foreach (var cb in b)
				{
					long dx = ca.X - cb.X;
					long dy = ca.Y - cb.Y;
					long d = dx * dx + dy * dy;
					if (d < bestDist)
					{
						bestDist = d;
						pa = ca;
						pb = cb;
						found = true;
					}
				}
			}

			return found;
		}
	}
}
