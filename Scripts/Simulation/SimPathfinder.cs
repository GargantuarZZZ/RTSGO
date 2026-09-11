//# File: res://Scripts/Simulation/SimPathfinder.cs
using System;
using System.Collections.Generic;
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	public struct SimVector2I : IEquatable<SimVector2I>
	{
		public int X, Y;
		public SimVector2I(int x, int y) { X = x; Y = y; }
		public bool Equals(SimVector2I other) => X == other.X && Y == other.Y;
		public override bool Equals(object obj) => obj is SimVector2I other && Equals(other);
		public override int GetHashCode() => (X * 73856) ^ (Y * 19349);
		public static bool operator ==(SimVector2I a, SimVector2I b) => a.Equals(b);
		public static bool operator !=(SimVector2I a, SimVector2I b) => !a.Equals(b);
		public static SimVector2I operator +(SimVector2I a, SimVector2I b) => new SimVector2I(a.X + b.X, a.Y + b.Y);
	}

	public class SimGrid
	{
		public int TileSize = 64;
		public HashSet<SimVector2I> StaticObstacles = new();
		// 合法地面格（由 MapGrid 从 TileMap 同步，供菌毯等逻辑判定使用）
		public HashSet<SimVector2I> TerrainCells = new();

		// 建筑占用格（烘焙结果）：由 SyncStructureBlocking 维护，避免每次寻路遍历全部建筑
		private readonly HashSet<SimVector2I> _structureCells = new();
		// 静态障碍 + 建筑占用并集（一次查询判定）
		private readonly HashSet<SimVector2I> _blockedCells = new();
		// 快速行走判定：预烘焙的扁平阻挡网格（0=可走，1=阻挡；越界按可走，保持与原 HashSet 语义一致）
		// 建筑与地形墙一致：按占用格方形阻挡，不做单位半径圆形膨胀
		private readonly List<byte[]> _radiusWalkGrids = new();
		private int _walkMinX, _walkMinY, _walkWidth, _walkHeight;
		// 静态版本号：StaticObstacles 变化后自增，参与 blocking fingerprint
		private int _staticVersion;
		// 阻挡指纹：唯一决定"哪些格阻挡"，必须覆盖 StaticObstacles 版本 + 建筑 ID/位置/尺寸/状态
		private int _blockingFingerprint = -1;

		// =========================================================
		// 蓝图覆盖层（与全局阻挡网格分离）
		//
		// 规则：蓝图只阻挡"自己人"（同队或同组盟友），对敌方单位完全不阻挡；
		// 完工建筑对所有队伍阻挡。因为阻挡是否生效取决于查询方的队伍，
		// 而扁平膨胀网格没有队伍维度（方案 B），所以蓝图不进 _blockedCells，
		// 改用这张平行格子网格：0 = 无蓝图，否则 = 占用该格的建筑 ID。
		//
		// 只有半径膨胀等级 0（蓝图自身占地格）需要它；膨胀等级 r>0 是 _blockedCells
		// 的 Chebyshev 膨胀结果，不含蓝图，因此大型单位不会被敌方蓝图顶开。
		// =========================================================
		private int[] _blueprintCells = System.Array.Empty<int>();
		private int _blueprintRevision;
		private readonly System.Collections.Generic.Dictionary<long, long> _blueprintQueryCache = new();

		/// <summary>蓝图覆盖层版本号：每 tick 对账用，变化即撤销单位进入蓝图的放行。</summary>
		public int BlueprintRevision => _blueprintRevision;

		// 自己人判定由宿主（SimManager 队伍分组）注入。默认按 TeamID 相等，
		// 保证逻辑层不依赖 NetworkManager。null 时退化为 TeamID 相等。
		public static System.Func<int, int, bool> TeamFriendlyResolver;

		// 队伍分组版本：分组变化会改变"谁被谁阻挡"，必须让阻挡指纹失效重烘焙
		private static int _teamRevision;

		public static void BumpTeamRevision() => _teamRevision++;

		/// <summary>自己人判定：同队或同组盟友。蓝图阻挡/推出/可见都以它为准。</summary>
		public static bool AreTeamsFriendly(int a, int b)
		{
			var resolver = TeamFriendlyResolver;
			return resolver != null ? resolver(a, b) : (a > 0 && b > 0 && a == b);
		}

		/// <summary>
		/// 该建筑对该队伍的碰撞/寻路阻挡语义：
		/// 完工建筑一律阻挡；蓝图只阻挡自己人（同队/盟友）。
		/// </summary>
		public static bool BlocksForTeam(SimStructure structure, int teamId)
		{
			if (structure == null || structure.IsDead)
				return false;

			if (structure.CurrentState != SimStructure.StructureState.Blueprint)
				return true;

			// 蓝图：只有自己人看得见、走得被挡；敌方单位直接穿过
			return teamId > 0 && AreTeamsFriendly(teamId, structure.TeamID);
		}

		/// <summary>清空蓝图查询缓存：一次 A* 搜索开始前调用，避免用到上一次搜索的结论。</summary>
		public void ResetBlueprintQueryCache() => _blueprintQueryCache.Clear();

		/// <summary>某个格子是否被"对该队伍生效的蓝图"占用；命中返回该蓝图。</summary>
		public SimStructure GetBlockingBlueprint(SimVector2I pos, IDictionary<int, SimStructure> structures, int teamId)
		{
			if (teamId <= 0 || _blueprintCells.Length == 0)
				return null;
			if (pos.X < _walkMinX || pos.Y < _walkMinY ||
				pos.X >= _walkMinX + _walkWidth || pos.Y >= _walkMinY + _walkHeight)
				return null;

			int id = _blueprintCells[(pos.X - _walkMinX) * _walkHeight + (pos.Y - _walkMinY)];
			if (id == 0)
				return null;
			if (!structures.TryGetValue(id, out var blueprint) || blueprint == null || blueprint.IsDead)
				return null;

			return AreTeamsFriendly(teamId, blueprint.TeamID) ? blueprint : null;
		}

		// A* 扁平节点数组（跨寻路复用，避免每路径新建字典）；越界格走 NodeOverflow 兜底
		internal SimPathfinder.Node[] NodeGrid;
		internal int NodeGridOffsetX, NodeGridOffsetY, NodeGridWidth, NodeGridHeight;
		internal int NodeGridStamp;
		internal readonly Dictionary<SimVector2I, SimPathfinder.Node> NodeOverflow = new();

		// 建筑增删/死亡后同步占用格（在每个 Tick 开始与建筑加入时调用）
		public void SyncStructureBlocking(IDictionary<int, SimStructure> structures)
		{
			// 指纹必须覆盖 CurrentState：蓝图→施工/完工、完工→Destroyed 都会改变
			// "是否阻挡"，若不入指纹则状态切换不会触发重烘焙，网格会停留在旧结论。
			// _teamRevision 同理：分组变化会改变蓝图的阻挡对象。
			int fp = _staticVersion;
			fp = unchecked(fp * 31 + _teamRevision);
			foreach (var s in structures.Values)
			{
				if (s == null || s.IsDead)
					continue;
				fp = unchecked(fp * 31 + s.ID);
				fp = unchecked(fp * 31 + s.GridPosition.X);
				fp = unchecked(fp * 31 + s.GridPosition.Y);
				fp = unchecked(fp * 31 + s.GridWidth);
				fp = unchecked(fp * 31 + s.GridHeight);
				fp = unchecked(fp * 31 + (int)s.CurrentState);
			}

			if (_blockingFingerprint != -1 && fp == _blockingFingerprint)
				return;

			_blockingFingerprint = fp;
			_structureCells.Clear();

			foreach (var s in structures.Values)
			{
				// 蓝图不进全局阻挡网格：它只对"自己人"生效，而扁平膨胀网格
				// 没有队伍维度。蓝图改走 _blueprintCells 覆盖层（见字段注释）。
				if (s == null || s.IsDead ||
					s.CurrentState == SimStructure.StructureState.Blueprint)
					continue;

				for (int x = s.GridPosition.X; x < s.GridPosition.X + s.GridWidth; x++)
				{
					for (int y = s.GridPosition.Y; y < s.GridPosition.Y + s.GridHeight; y++)
						_structureCells.Add(new SimVector2I(x, y));
				}
			}

			RebuildBlockingSets();
			RebuildBlueprintOverlay(structures);
		}

		// 重新对账蓝图覆盖层：只有在 RebuildWalkGrids 建立起地图尺寸后才可用。
		private void RebuildBlueprintOverlay(IDictionary<int, SimStructure> structures)
		{
			_blueprintRevision++;
			_blueprintQueryCache.Clear();

			if (_walkWidth <= 0 || _walkHeight <= 0)
			{
				_blueprintCells = System.Array.Empty<int>();
				return;
			}

			int needed = _walkWidth * _walkHeight;
			if (_blueprintCells.Length != needed)
				_blueprintCells = new int[needed];
			else
				System.Array.Clear(_blueprintCells, 0, needed);

			foreach (var s in structures.Values)
			{
				if (s == null || s.IsDead ||
					s.CurrentState != SimStructure.StructureState.Blueprint)
					continue;

				for (int x = s.GridPosition.X; x < s.GridPosition.X + s.GridWidth; x++)
				{
					for (int y = s.GridPosition.Y; y < s.GridPosition.Y + s.GridHeight; y++)
					{
						int bx = x - _walkMinX;
						int by = y - _walkMinY;
						if (bx < 0 || bx >= _walkWidth || by < 0 || by >= _walkHeight)
							continue;
						_blueprintCells[bx * _walkHeight + by] = s.ID;
					}
				}
			}
		}

		// 静态障碍变化后调用（地图初始化时同步一次；测试里直接改 StaticObstacles 后也要调用）
		public void SyncStaticBlocking()
		{
			_staticVersion++;
			RebuildBlockingSets();
		}

		private void RebuildBlockingSets()
		{
			_blockedCells.Clear();
			foreach (var c in StaticObstacles)
				_blockedCells.Add(c);
			foreach (var c in _structureCells)
				_blockedCells.Add(c);

			RebuildWalkGrids();
		}

		// 把阻挡集烘焙成扁平字节网格，A* 邻居判定从 HashSet 查表降为数组下标
		private void RebuildWalkGrids()
		{
			_radiusWalkGrids.Clear();
			_walkWidth = 0;
			_walkHeight = 0;

			if (!TerrainBoundsReady || TerrainCells.Count == 0)
				return;

			_walkMinX = TerrainMin.X;
			_walkMinY = TerrainMin.Y;
			_walkWidth = TerrainMax.X - TerrainMin.X + 1;
			_walkHeight = TerrainMax.Y - TerrainMin.Y + 1;

			if (_walkWidth <= 0 || _walkHeight <= 0 || _walkWidth > 8192 || _walkHeight > 8192)
			{
				_walkWidth = 0;
				_walkHeight = 0;
				return;
			}

			// 烘焙 0..4 级半径膨胀网格：level r = 该格被任一阻挡格（Chebyshev 距离 ≤ r）堵住。
			// 大型单位（2×2/3×3）按自身半径走对应层级，过墙角/窄缝不再被当 1×1 点卡住。
			const int MaxRadiusLevels = 5;
			for (int r = 0; r < MaxRadiusLevels; r++)
			{
				byte[] arr = new byte[_walkWidth * _walkHeight];
				foreach (var c in _blockedCells)
				{
					int bx = c.X - _walkMinX;
					int by = c.Y - _walkMinY;
					for (int dx = -r; dx <= r; dx++)
					{
						for (int dy = -r; dy <= r; dy++)
						{
							int x = bx + dx;
							int y = by + dy;
							if (x >= 0 && x < _walkWidth && y >= 0 && y < _walkHeight)
								arr[x * _walkHeight + y] = 1;
						}
					}
				}
				_radiusWalkGrids.Add(arr);
			}
		}

		// 快速行走判定（无 ignoreId 时使用）；越界按可走，与原 HashSet 语义一致
		// teamId：蓝图只对"自己人"生效，覆盖层查询需要它
		public bool IsWalkableFast(SimVector2I pos, int radiusTiles, int teamId = 0, IDictionary<int, SimStructure> structures = null)
		{
			// 蓝图不在扁平网格里（敌方不该被挡），按查询方队伍单独判定
			if (teamId > 0 && _blueprintCells.Length != 0 && structures != null &&
				GetBlockingBlueprintCached(pos, structures, teamId, -1) != null)
				return false;

			if (_walkWidth <= 0 || _radiusWalkGrids.Count == 0)
				return true;

			int x = pos.X - _walkMinX;
			int y = pos.Y - _walkMinY;
			if (x < 0 || x >= _walkWidth || y < 0 || y >= _walkHeight)
				return true;

			int level = radiusTiles < 0 ? 0 : (radiusTiles >= _radiusWalkGrids.Count ? _radiusWalkGrids.Count - 1 : radiusTiles);
			return _radiusWalkGrids[level][x * _walkHeight + y] == 0;
		}

		// 地图实际边界（格子坐标），用于限制空中单位
		public SimVector2I TerrainMin = new SimVector2I(int.MaxValue, int.MaxValue);
		public SimVector2I TerrainMax = new SimVector2I(int.MinValue, int.MinValue);
		public bool TerrainBoundsReady = false;

		public void RefreshTerrainBounds()
		{
			if (TerrainCells.Count == 0)
				return;

			TerrainMin = new SimVector2I(int.MaxValue, int.MaxValue);
			TerrainMax = new SimVector2I(int.MinValue, int.MinValue);

			foreach (var c in TerrainCells)
			{
				if (c.X < TerrainMin.X) TerrainMin.X = c.X;
				if (c.Y < TerrainMin.Y) TerrainMin.Y = c.Y;
				if (c.X > TerrainMax.X) TerrainMax.X = c.X;
				if (c.Y > TerrainMax.Y) TerrainMax.Y = c.Y;
			}

			TerrainBoundsReady = true;
		}

		public SimVector2I WorldToGrid(FPVector2 pos)
		{
			int x = (int)FP.Floor(pos.X / (FP)TileSize);
			int y = (int)FP.Floor(pos.Y / (FP)TileSize);
			return new SimVector2I(x, y);
		}

		// 建筑落点校验：必须在合法地面（TerrainCells）上且不能压墙（StaticObstacles）
		public bool IsAreaPlaceable(SimVector2I topLeft, int size)
		{
			for (int x = topLeft.X; x < topLeft.X + size; x++)
			{
				for (int y = topLeft.Y; y < topLeft.Y + size; y++)
				{
					var cell = new SimVector2I(x, y);
					if (!TerrainCells.Contains(cell) || StaticObstacles.Contains(cell))
						return false;
				}
			}
			return true;
		}

		public FPVector2 GridToWorldCentered(SimVector2I gridPos)
		{
			FP halfSize = (FP)TileSize / (FP)2m;
			return new FPVector2((FP)gridPos.X * (FP)TileSize + halfSize, (FP)gridPos.Y * (FP)TileSize + halfSize);
		}

		// 使用 IDictionary 以兼容 SortedDictionary
		// teamId：查询方队伍，用于判定"蓝图是否阻挡我"（蓝图只挡自己人）。
		public bool IsWalkable(SimVector2I pos, IDictionary<int, SimStructure> structures, int ignoreId = -1, int teamId = 0)
		{
			if (_blockedCells.Contains(pos))
			{
				// 被忽略的那座建筑（正在采集/建造的目标）本身不挡路
				if (ignoreId >= 0 && structures.TryGetValue(ignoreId, out var ignored) && !ignored.IsDead &&
					pos.X >= ignored.GridPosition.X && pos.X < ignored.GridPosition.X + ignored.GridWidth &&
					pos.Y >= ignored.GridPosition.Y && pos.Y < ignored.GridPosition.Y + ignored.GridHeight)
					return true;

				return false;
			}

			// 蓝图不在 _blockedCells 里（敌方不该被挡），单独查覆盖层
			return GetBlockingBlueprintCached(pos, structures, teamId, ignoreId) == null;
		}

		// 蓝图覆盖层查询带缓存：A* 在一次搜索里会反复查同一批格子。
		// 只缓存"命中"，避免在开阔地形上把每次未命中的格子都塞进缓存。
		//
		// 缓存键必须含 teamId：蓝图"挡不挡"是随查询方队伍变化的，
		// 只按格子缓存会把"挡自己人"的结论错误地复用给敌方。
		private SimStructure GetBlockingBlueprintCached(
			SimVector2I pos, IDictionary<int, SimStructure> structures, int teamId, int ignoreId)
		{
			if (teamId <= 0 || _blueprintCells.Length == 0)
				return null;

			long key = ((long)pos.X * 100000L + pos.Y) * 64L + (teamId & 63);
			if (_blueprintQueryCache.TryGetValue(key, out long cachedId))
			{
				if (cachedId == 0)
					return null;
				return structures.TryGetValue((int)cachedId, out var cached) ? cached : null;
			}

			var blueprint = GetBlockingBlueprint(pos, structures, teamId);
			if (blueprint == null || blueprint.ID == ignoreId)
				return null;

			_blueprintQueryCache[key] = blueprint.ID;
			return blueprint;
		}

		// 按单位半径做占用格方形膨胀：中心格及其 ±radiusTiles 邻格都必须可走，
		// 否则 2×2/3×3 单位会穿过 1 格缝或贴墙过角卡死
		public bool IsWalkableForRadius(SimVector2I pos, IDictionary<int, SimStructure> structures, int ignoreId, int radiusTiles, int teamId = 0)
		{
			for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
			{
				for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
				{
					if (!IsWalkable(new SimVector2I(pos.X + dx, pos.Y + dy), structures, ignoreId, teamId))
						return false;
				}
			}
			return true;
		}

		public bool HasLineOfSight(FPVector2 startWorld, FPVector2 endWorld, IDictionary<int, SimStructure> structures, int ignoreId = -1, int radiusTiles = 0, int teamId = 0)
		{
			FP distSq = FPVector2.DistanceSquared(startWorld, endWorld);
			if (distSq == FP.Zero) return true;

			bool useFast = ignoreId < 0 && radiusTiles <= 4;

			FP dist = FP.Sqrt(distSq);
			FPVector2 dir = (endWorld - startWorld) / dist;
			FP step = (FP)TileSize / (FP)3m;
			FP currentDist = FP.Zero;
			bool IsWalk(SimVector2I p) =>
				useFast ? IsWalkableFast(p, radiusTiles, teamId, structures) : IsWalkableForRadius(p, structures, ignoreId, radiusTiles, teamId);
			var previous = WorldToGrid(startWorld);
			bool CanCross(SimVector2I next) => IsWalk(next) &&
				(next.X == previous.X || next.Y == previous.Y ||
				 (IsWalk(new SimVector2I(previous.X, next.Y)) && IsWalk(new SimVector2I(next.X, previous.Y))));

			while (currentDist < dist)
			{
				FPVector2 checkPos = startWorld + dir * currentDist;
				SimVector2I gridPos = WorldToGrid(checkPos);
				if (!CanCross(gridPos)) return false;
				previous = gridPos;
				currentDist += step;
			}
			return CanCross(WorldToGrid(endWorld));
		}

		public SimVector2I GetNearestWalkableNode(SimVector2I startNode, IDictionary<int, SimStructure> structures, int maxRadius = 15, int ignoreId = -1, int radiusTiles = 0, int teamId = 0)
		{
			if (IsWalkableForRadius(startNode, structures, ignoreId, radiusTiles, teamId)) return startNode;

			Queue<SimVector2I> queue = new Queue<SimVector2I>();
			HashSet<SimVector2I> visited = new HashSet<SimVector2I>();
			queue.Enqueue(startNode);
			visited.Add(startNode);

			SimVector2I[] dirs = {
				new SimVector2I(0, -1), new SimVector2I(0, 1), new SimVector2I(-1, 0), new SimVector2I(1, 0),
				new SimVector2I(-1, -1), new SimVector2I(1, -1), new SimVector2I(-1, 1), new SimVector2I(1, 1)
			};

			while (queue.Count > 0)
			{
				SimVector2I curr = queue.Dequeue();
				if (IsWalkableForRadius(curr, structures, ignoreId, radiusTiles, teamId)) return curr;

				if (Math.Abs(curr.X - startNode.X) > maxRadius || Math.Abs(curr.Y - startNode.Y) > maxRadius) continue;

				foreach (var dir in dirs)
				{
					SimVector2I neighbor = curr + dir;
					if (!visited.Contains(neighbor))
					{
						visited.Add(neighbor);
						queue.Enqueue(neighbor);
					}
				}
			}
			return startNode;
		}
	}

	public static class SimPathfinder
	{
		internal class Node
		{
			public SimVector2I Pos;
			public int G, H;
			public int F => G + H;
			public Node Parent;
			public bool InOpen;
			public bool Closed;
			public int HeapIndex;
			public int Stamp;
		}

		// 二叉堆 OpenList：Pop 从 O(n) 全量扫描降为 O(log n)；比较固定为 (F, H, X, Y) 保证确定性
		private sealed class MinHeap
		{
			private readonly List<Node> _items = new();
			public int Count => _items.Count;

			public void Clear()
			{
				_items.Clear();
			}

			public void Add(Node n)
			{
				n.HeapIndex = _items.Count;
				_items.Add(n);
				BubbleUp(n);
			}

			public Node Pop()
			{
				Node root = _items[0];
				Node last = _items[_items.Count - 1];
				_items.RemoveAt(_items.Count - 1);
				root.InOpen = false;

				if (_items.Count > 0)
				{
					last.HeapIndex = 0;
					_items[0] = last;
					SiftDown(last);
				}

				return root;
			}

			public void BubbleUp(Node n)
			{
				while (n.HeapIndex > 0)
				{
					int parentIdx = (n.HeapIndex - 1) / 2;
					Node parent = _items[parentIdx];
					if (!Less(n, parent)) break;
					Swap(n, parent);
				}
			}

			private void SiftDown(Node n)
			{
				while (true)
				{
					int left = n.HeapIndex * 2 + 1;
					if (left >= _items.Count) break;

					int right = left + 1;
					int best = left;
					if (right < _items.Count && Less(_items[right], _items[left]))
						best = right;

					if (!Less(_items[best], n)) break;
					Swap(_items[best], n);
				}
			}

			private static bool Less(Node a, Node b)
			{
				int af = a.F, bf = b.F;
				if (af != bf) return af < bf;
				if (a.H != b.H) return a.H < b.H;
				if (a.Pos.X != b.Pos.X) return a.Pos.X < b.Pos.X;
				return a.Pos.Y < b.Pos.Y;
			}

			private void Swap(Node a, Node b)
			{
				int ai = a.HeapIndex, bi = b.HeapIndex;
				_items[ai] = b;
				_items[bi] = a;
				a.HeapIndex = bi;
				b.HeapIndex = ai;
			}
		}

		// 模拟线程单线程 + WorldLock 串行调用，堆可静态复用避免每路径分配
		private static readonly MinHeap _scratchHeap = new();

		public static List<FPVector2> FindPath(SimGrid grid, IDictionary<int, SimStructure> structures, FPVector2 startWorld, FPVector2 endWorld, int ignoreId = -1, bool ignoreObstacles = false, FP radius = default, bool skipInitialLos = false, int radiusTilesOverride = -1, int teamId = 0)
		{
			// 蓝图覆盖层查询缓存只对本次搜索有效（另一次搜索可能已有建筑完工/新蓝图）
			grid.ResetBlueprintQueryCache();

			// 空中单位：无视墙体与建筑，直接直线飞行
			if (ignoreObstacles)
				return new List<FPVector2> { startWorld, endWorld };

			// 单位半径膨胀：按占用格方形阻挡 + 半径膨胀（大型单位过墙角/窄缝不再卡住）
			FP radiusHalf = radius > FP.Zero ? radius / (FP)grid.TileSize - (FP)0.5m : FP.Zero;
			int radiusTiles = radiusTilesOverride >= 0
				? radiusTilesOverride
				: (radiusHalf > FP.Zero ? (int)FP.Ceiling(radiusHalf) : 0);
			var startNode = grid.WorldToGrid(startWorld);
			var endNode = grid.WorldToGrid(endWorld);

			// 起点不可走时同样要修正：否则 A* 会从墙内出发，短路径还会绕过平滑校验直接穿墙
			if (!grid.IsWalkableForRadius(startNode, structures, ignoreId, radiusTiles, teamId))
			{
				startNode = grid.GetNearestWalkableNode(startNode, structures, 15, ignoreId, radiusTiles, teamId);
				startWorld = grid.GridToWorldCentered(startNode);
			}

			if (!grid.IsWalkableForRadius(endNode, structures, ignoreId, radiusTiles, teamId))
			{
				endNode = grid.GetNearestWalkableNode(endNode, structures, 15, ignoreId, radiusTiles, teamId);
				endWorld = grid.GridToWorldCentered(endNode);
			}

			if (startNode == endNode) return new List<FPVector2> { endWorld };

			// 直线可达直接返回（与 A* + 平滑的最终路径一致），开阔地形下省掉整棵 A* 搜索
			if (!skipInitialLos && grid.HasLineOfSight(startWorld, endWorld, structures, ignoreId, radiusTiles, teamId))
				return new List<FPVector2> { startWorld, endWorld };

			PrepareNodeGrid(grid);

			_scratchHeap.Clear();
			var openList = _scratchHeap;
			Node start = GetNode(grid, startNode);
			start.Pos = startNode;
			start.G = 0;
			start.H = GetHeuristic(startNode, endNode);
			openList.Add(start);

			SimVector2I[] dirs = {
				new SimVector2I(0, -1), new SimVector2I(0, 1), new SimVector2I(-1, 0), new SimVector2I(1, 0),
				new SimVector2I(-1, -1), new SimVector2I(1, -1), new SimVector2I(-1, 1), new SimVector2I(1, 1)
			};

			// 迭代上限：地图 ~190×190 格，后期建筑密集时绕行搜索膨胀很快；
			// 1500 节点会被频繁截断 → 单位走一两步就停（走两步卡一下）。
			int loopLimit = 8000;
			Node closestNode = start;
			int minH = start.H;

			while (openList.Count > 0 && loopLimit-- > 0)
			{
				Node current = openList.Pop();
				current.Closed = true;

				if (current.H < minH)
				{
					minH = current.H;
					closestNode = current;
				}

				if (current.Pos == endNode)
				{
					return RetracePath(start, current, grid, endWorld, structures, ignoreId, radiusTiles, teamId);
				}

				foreach (var dir in dirs)
				{
					SimVector2I neighborPos = current.Pos + dir;
					// A diagonal cannot squeeze through the corner shared by blocked side cells.
					if (dir.X != 0 && dir.Y != 0 &&
						(!grid.IsWalkableForRadius(new SimVector2I(current.Pos.X + dir.X, current.Pos.Y), structures, ignoreId, radiusTiles, teamId) ||
						 !grid.IsWalkableForRadius(new SimVector2I(current.Pos.X, current.Pos.Y + dir.Y), structures, ignoreId, radiusTiles, teamId)))
						continue;
					if (ignoreId < 0 && radiusTiles <= 4)
					{
						if (!grid.IsWalkableFast(neighborPos, radiusTiles, teamId, structures)) continue;
					}
					else if (!grid.IsWalkableForRadius(neighborPos, structures, ignoreId, radiusTiles, teamId))
					{
						continue;
					}

					int moveCost = (dir.X == 0 || dir.Y == 0) ? 10 : 14;
					int newG = current.G + moveCost;

					Node neighbor = GetNode(grid, neighborPos);
					neighbor.Pos = neighborPos;
					if (neighbor.Closed) continue;
					if (newG >= neighbor.G) continue;

					neighbor.G = newG;
					neighbor.H = GetHeuristic(neighborPos, endNode);
					neighbor.Parent = current;

					if (!neighbor.InOpen)
					{
						neighbor.InOpen = true;
						openList.Add(neighbor);
					}
					else
					{
						openList.BubbleUp(neighbor);
					}
				}
			}

			FPVector2 closestWorldPos = grid.GridToWorldCentered(closestNode.Pos);
			return RetracePath(start, closestNode, grid, closestWorldPos, structures, ignoreId, radiusTiles, teamId);
		}

		// 准备可复用的扁平节点数组（按地图边界加 16 格余量），并推进代次标记
		private static void PrepareNodeGrid(SimGrid grid)
		{
			grid.NodeGridStamp++;
			grid.NodeOverflow.Clear();
			grid.NodeGridWidth = 0;

			if (grid.TerrainCells.Count == 0)
				return;

			if (!grid.TerrainBoundsReady)
				grid.RefreshTerrainBounds();

			const int margin = 16;
			int minX = grid.TerrainMin.X - margin;
			int maxX = grid.TerrainMax.X + margin;
			int minY = grid.TerrainMin.Y - margin;
			int maxY = grid.TerrainMax.Y + margin;
			int width = maxX - minX + 1;
			int height = maxY - minY + 1;

			if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
				return;

			if (grid.NodeGrid == null || grid.NodeGrid.Length != width * height)
				grid.NodeGrid = new Node[width * height];

			grid.NodeGridOffsetX = minX;
			grid.NodeGridOffsetY = minY;
			grid.NodeGridWidth = width;
			grid.NodeGridHeight = height;
		}

		// 取节点：数组内 O(1) 复用，数组外走兜底字典
		private static Node GetNode(SimGrid grid, SimVector2I pos)
		{
			int x = pos.X - grid.NodeGridOffsetX;
			int y = pos.Y - grid.NodeGridOffsetY;
			Node n;

			if (grid.NodeGridWidth > 0 && x >= 0 && x < grid.NodeGridWidth && y >= 0 && y < grid.NodeGridHeight)
			{
				int idx = x * grid.NodeGridHeight + y;
				n = grid.NodeGrid[idx];
				if (n != null && n.Stamp == grid.NodeGridStamp)
					return n;

				if (n == null)
				{
					n = new Node();
					grid.NodeGrid[idx] = n;
				}
			}
			else
			{
				if (!grid.NodeOverflow.TryGetValue(pos, out n))
				{
					n = new Node();
					grid.NodeOverflow[pos] = n;
				}
				else if (n.Stamp == grid.NodeGridStamp)
				{
					return n;
				}
			}

			n.Stamp = grid.NodeGridStamp;
			n.G = int.MaxValue;
			n.H = 0;
			n.Parent = null;
			n.InOpen = false;
			n.Closed = false;
			n.HeapIndex = -1;
			return n;
		}

		private static int GetHeuristic(SimVector2I a, SimVector2I b)
		{
			int dx = Math.Abs(a.X - b.X);
			int dy = Math.Abs(a.Y - b.Y);
			return 10 * (dx + dy) + (14 - 2 * 10) * Math.Min(dx, dy);
		}

		private static List<FPVector2> RetracePath(Node start, Node end, SimGrid grid, FPVector2 finalExactPos, IDictionary<int, SimStructure> structures, int ignoreId, int radiusTiles, int teamId)
		{
			var rawPath = new List<FPVector2>();
			Node current = end;
			while (current != start)
			{
				rawPath.Add(grid.GridToWorldCentered(current.Pos));
				current = current.Parent;
			}
			rawPath.Add(grid.GridToWorldCentered(start.Pos));
			rawPath.Reverse();

			if (rawPath.Count > 0) rawPath[rawPath.Count - 1] = finalExactPos;
			else rawPath.Add(finalExactPos);

			return SmoothPath(rawPath, grid, structures, ignoreId, radiusTiles, teamId);
		}

		private static List<FPVector2> SmoothPath(List<FPVector2> rawPath, SimGrid grid, IDictionary<int, SimStructure> structures, int ignoreId, int radiusTiles, int teamId)
		{
			if (rawPath.Count <= 2) return rawPath;

			List<FPVector2> smoothPath = new List<FPVector2>();
			smoothPath.Add(rawPath[0]);

			int currentIndex = 0;

			while (currentIndex < rawPath.Count - 1)
			{
				// 同一射线上视线具有单调性：能看到更远点则中间点必然可见，
				// 因此二分查找“最远可见路径点”与原先从末端往前扫的结果完全一致。
				int lo = currentIndex + 1;
				int hi = rawPath.Count - 1;
				int furthestIndex = lo;

				while (lo <= hi)
				{
					int mid = (lo + hi) / 2;
					if (grid.HasLineOfSight(rawPath[currentIndex], rawPath[mid], structures, ignoreId, radiusTiles, teamId))
					{
						furthestIndex = mid;
						lo = mid + 1;
					}
					else
					{
						hi = mid - 1;
					}
				}

				smoothPath.Add(rawPath[furthestIndex]);
				currentIndex = furthestIndex;
			}

			if (smoothPath.Count > 0) smoothPath.RemoveAt(0);
			return smoothPath;
		}
	}
}
