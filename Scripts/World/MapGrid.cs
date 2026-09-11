// File: res://Scripts/World/MapGrid.cs
using Godot;
using System.Collections.Generic;
using RTS.Core;
using RTS.Data; // 引入菌毯枚举
using RTS.Units;

namespace RTS.World
{
	[GlobalClass]
	public partial class MapGrid : Node2D
	{
		[Export] public TileMapLayer BaseMapLayer { get; set; }
		[Export] public NavigationRegion2D NavRegion { get; set; }
		// 地图预设：0 = 经典大地图（场景内置瓦片），1 = 1v1 小图（代码生成地形+中央断墙）
		[Export] public int MapPresetId = 0;

		private Dictionary<Vector2I, Node> _occupiedCells = new();
		public static MapGrid Instance { get; private set; }

		// 伪 3D 世界边界：覆盖全部出生点与地图活动区域（逻辑世界坐标）
		public const float WorldMin = -10000f;
		public const float WorldSize = 20000f;
		public static Rect2 WorldBounds => new Rect2(WorldMin, WorldMin, WorldSize, WorldSize);

		private bool _isBaking = false;
		private bool _bakeQueued = false;

		public override void _EnterTree()
		{
			Instance = this;
		}

		public override void _Ready()
		{
			if (NavRegion == null) NavRegion = GetParentOrNull<NavigationRegion2D>();
			if (NavRegion != null) NavRegion.BakeFinished += OnBakeFinished;

			// 伪 3D：2D 地图层只作逻辑数据源，绝不渲染到屏幕
			if (BaseMapLayer != null)
				BaseMapLayer.Visible = false;

			// 1v1 小图：用代码铺地形和中央断墙（必须在 InitSimGrid 前完成）
			if (MapPresetId == 1)
				BuildSmallMap1v1();
			else if (MapPresetId == 2)
				BuildDebugMap();

			// 将地图墙壁数据同步到定点数网格
			CallDeferred(MethodName.InitSimGrid);
		}

		// =========================================================
		// 1v1 小地图：100 x 100 格
		// 结构（180° 旋转 + 左右镜像对称）：
		//   外围一圈墙
		//   中央断墙 x=0，中间 35 格大缺口放两个圣地
		//   左右两道镜像侧墙，缺口一南一北，形成 S 形通道
		//   四片斜向残垣点缀中段，避免单调
		// =========================================================
		private void BuildSmallMap1v1()
		{
			if (BaseMapLayer == null || BaseMapLayer.TileSet == null)
				return;

			BaseMapLayer.Clear();

			const int halfW = 50;
			const int halfH = 50;
			Vector2I grass = Vector2I.Zero;
			Vector2I wall = Vector2I.Zero;

			void AddWall(int x, int y) => BaseMapLayer.SetCell(new Vector2I(x, y), 1, wall);

			// 草地铺满（source 0 = 草地，atlas (0,0)）
			for (int x = -halfW; x < halfW; x++)
			{
				for (int y = -halfH; y < halfH; y++)
					BaseMapLayer.SetCell(new Vector2I(x, y), 0, grass);
			}

			// 场景外围一整圈墙：最外一圈全部堵死，内部才是可活动区域
			for (int x = -halfW; x < halfW; x++)
			{
				AddWall(x, -halfH);
				AddWall(x, halfH - 1);
			}

			for (int y = -halfH; y < halfH; y++)
			{
				AddWall(-halfW, y);
				AddWall(halfW - 1, y);
			}

			// 中央断墙：沿 x=0 竖墙，中间 y∈[-17,17] 留 35 格大缺口放两个圣地
			for (int y = -43; y <= 43; y++)
			{
				if (y >= -17 && y <= 17)
					continue;

				AddWall(0, y);
			}

			// 缺口两端错位墙头
			AddWall(-2, -18);
			AddWall(2, 18);

			// 左右两道镜像侧墙：缺口一南一北，形成 S 形主通道
			for (int y = -43; y <= 43; y++)
			{
				if (y >= -9 && y <= -1)
					continue;
				AddWall(-26, y);
			}

			for (int y = -43; y <= 43; y++)
			{
				if (y >= 1 && y <= 9)
					continue;
				AddWall(26, y);
			}

			// 四片斜向残垣（180° + 左右镜像对称）
			for (int i = 0; i < 3; i++)
			{
				AddWall(34 + i, 30 + i);
				AddWall(-34 - i, 30 + i);
				AddWall(34 + i, -30 - i);
				AddWall(-34 - i, -30 - i);
			}
		}

		// =========================================================
		// 调试地图：大面积纯草地，不生成任何墙（供快速测试全部单位/建筑）
		// =========================================================
		private void BuildDebugMap()
		{
			if (BaseMapLayer == null || BaseMapLayer.TileSet == null)
				return;

			BaseMapLayer.Clear();

			const int half = 60; // 120 x 120 格
			Vector2I grass = Vector2I.Zero;

			for (int x = -half; x < half; x++)
			{
				for (int y = -half; y < half; y++)
					BaseMapLayer.SetCell(new Vector2I(x, y), 0, grass);
			}
		}

		// --- 1. 基础坐标转换 ---
		/// <summary>
		/// 如果本局有地图数据（ActiveMap），由它同时决定逻辑网格与可见层。
		/// 返回 true 表示已经处理完毕，调用方不要再走 TileMap 反推。
		///
		/// 顺序很重要：**先写可见层，再写逻辑层**。
		/// 反过来的话，一旦渲染失败，玩家会看到"旧地图能走、有的地方明明没墙却走不过去"。
		///
		/// 坐标系刻意不做任何偏移：
		///   - 旧路径（从 TileMap 反推）是 `cell.X → SimVector2I(cell.X)`，即格坐标 == 逻辑坐标；
		///   - `WorldToGrid` 也是 `world / 64`，世界坐标原点 == 格坐标原点。
		///   所以"地图数据格坐标 == 世界格坐标"是唯一自洽的选择：
		///   地形、出生点、中立物、寻路判定全在同一坐标系里，不需要任何换算。
		///   （曾考虑加"居中偏移"，那会让渲染层与逻辑层差一个常量，
		///     只要有一处忘了补偿就又是"看得见的不是真的"。）
		/// </summary>
		/// <summary>
		/// 地形数据版本号：每次把地图数据应用到可见层时 +1。
		///
		/// 用途：小地图等"依赖地形范围"的 UI 在 _Ready 时算过一次范围，
		/// 但那时地图数据还没应用（_ready 是子先父后），算出来的是旧地图的大小。
		/// 让它们监听这个版本号就能在地形真正变化后重新计算，
		/// 而不是依赖"谁先 _Ready"这种脆弱顺序。
		/// </summary>
		public static int DataRevision { get; private set; }

		/// <summary>把地图数据同时应用到**可见层**与**逻辑层**。
		///
		/// 为什么不放在 InitSimGrid 里自动调用：Godot 的 `_ready()` 是**子节点先于父节点**，
		/// 所以 `MapGrid._Ready` 先跑、`Game._Ready` 后跑。等 Game 在 SetupMatch 里
		/// 设好 ActiveMap 时，InitSimGrid 早就执行完了（实测日志：
		/// `ActiveMap=null preset=1`）——这就是"逻辑换了新地图、屏幕还是旧地图"的根因。
		///
		/// 所以改成由 Game 在拿到地图数据后**显式调用**，顺序不再依赖引擎的 ready 顺序。
		/// 返回 true 表示已应用（调用方不要再走 TileMap 反推）。
		/// </summary>
		public bool ApplyDataMap(RTS.Data.Maps.RtsMapData data)
		{
			if (data == null || !data.HasTerrain)
				return false;

			if (BaseMapLayer == null || BaseMapLayer.TileSet == null)
			{
				GD.PrintErr($"[MapGrid] 有地图数据 '{data.MapId}' 但无法渲染：BaseMapLayer/TileSet 为空。" +
							"（逻辑层仍会用数据地图，此时视觉会与逻辑不一致）");
				return false;
			}

			// 1) 可见层（复用 MapLoader，单一实现）
			RTS.World.MapLoader.ApplyToTileMap(data, BaseMapLayer);

			// 2) 逻辑层（与地图校验器同一段代码，保证"编辑器里能走" == "游戏里能走"）
			var simGrid = RTS.Core.SimManager.Instance?.World?.Grid;
			if (simGrid != null)
				RTS.World.MapLoader.ApplyToSimGrid(data, simGrid);

			GD.Print($"[MapGrid] 地图 '{data.MapId}' 已同时应用到可见层与逻辑层：{data.Width}x{data.Height}");

			// 通知依赖地形范围的 UI（小地图等）重新计算
			DataRevision++;

			// 地形变了，导航必须重烘焙（否则 Godot 导航仍按旧地图走）
			RequestBake();
			return true;
		}

		private void InitSimGrid()
		{
			// 只负责"旧路径"：从场景 TileMapLayer 反推地形。
			// 有地图数据时由 Game 显式调用 ApplyDataMap（见那里的注释：
			// _ready 顺序是子先父后，这里跑的时候 ActiveMap 还没被设好）。
			if (BaseMapLayer != null && RTS.Core.SimManager.Instance != null)
			{
				var simGrid = RTS.Core.SimManager.Instance.World.Grid;
				simGrid.TileSize = BaseMapLayer.TileSet.TileSize.X;
				simGrid.StaticObstacles.Clear();
				simGrid.TerrainCells.Clear();

				var cells = BaseMapLayer.GetUsedCells();
				foreach (var cell in cells)
				{
					TileData tileData = BaseMapLayer.GetCellTileData(cell);
					// 自动识别墙壁：如果有 Godot 原生物理碰撞体，就在后台判定为墙
					if (tileData != null && tileData.GetCollisionPolygonsCount(0) > 0)
					{
						simGrid.StaticObstacles.Add(new RTS.Simulation.SimVector2I(cell.X, cell.Y));
					}
					else
					{
						// 只有真正的可走地面格才允许铺菌毯/放置建筑（墙外空白一律不算地形）
						simGrid.TerrainCells.Add(new RTS.Simulation.SimVector2I(cell.X, cell.Y));
					}
				}
				simGrid.RefreshTerrainBounds();
				simGrid.SyncStaticBlocking();
				GD.Print("[MapGrid] 定点数网格寻路数据同步完成！");
			}
		}
		public Vector2I WorldToGrid(Vector2 worldPos)
		{
			// 纯数学换算：与 SimGrid（64 格、原点对齐）一致。
			// 不能用 BaseMapLayer.ToLocal()——那是主线程专用 Godot 节点 API，
			// 模拟线程调用会返回 (0,0)，导致面板建造落点校验全错（纳米/植物放不了塔）
			return new Vector2I(
				(int)Mathf.Floor(worldPos.X / 64f),
				(int)Mathf.Floor(worldPos.Y / 64f));
		}

		public Vector2 GridToWorldCentered(Vector2I gridPos)
		{
			// 同上：纯数学，避免模拟线程碰 Godot 节点
			return new Vector2(gridPos.X * 64f + 32f, gridPos.Y * 64f + 32f);
		}

		// --- 2. 中心 -> 左上角 -> 世界对齐 ---

		public Vector2I GetTopLeftFromCenter(Vector2I centerGrid, int size)
		{
			int offset = size / 2;
			return centerGrid - new Vector2I(offset, offset);
		}

		public Vector2 GetAlignedWorldPos(Vector2I topLeftGrid, int size)
		{
			Vector2 startWorld = GridToWorldCentered(topLeftGrid);
			float offsetVal = 32.0f * (size - 1);
			return startWorld + new Vector2(offsetVal, offsetVal);
		}

		// --- 3. 占用管理 (标准：左上 -> 右下) ---

		public bool IsCellEmpty(Vector2I gridPos) => !_occupiedCells.ContainsKey(gridPos);
		public string GetOccupantName(Vector2I gridPos)
			=> _occupiedCells.TryGetValue(gridPos, out var node) && GodotObject.IsInstanceValid(node)
				? node.Name
				: "?";

		/// <summary>
		/// 当前生效的地图数据（由 MapLoader 在开局/编辑器载入时设置）。
		/// 用它来执行作者在编辑器里画的"建造禁区"掩码——否则 BuildBlocked
		/// 只是一份没人读的元数据，"资源点周围禁建"这类作者意图不会真正生效。
		/// </summary>
		public static RTS.Data.Maps.RtsMapData ActiveMap { get; set; }

		/// <summary>
		/// 载入一张地图并让"建造禁区"真正生效。
		/// 必须同时设置 ActiveMap（表现层建造校验用）与 SimWorld.BuildBlockedQuery
		/// （模拟层/AI 的建造校验用）——两处走不同代码路径，只设一个会导致
		/// 玩家被挡住但 AI 照样往禁区里拍建筑。
		/// </summary>
		public static void SetActiveMap(RTS.Data.Maps.RtsMapData map)
		{
			ActiveMap = map;

			if (map == null || !map.HasTerrain)
			{
				RTS.Simulation.SimWorld.BuildBlockedQuery = null;
				return;
			}

			RTS.Simulation.SimWorld.BuildBlockedQuery =
				cell => map.InBounds(cell.X, cell.Y) && map.IsBuildBlocked(cell.X, cell.Y);
		}

		public bool IsAreaEmpty(Vector2I topLeft, int size)
		{
			for (int x = 0; x < size; x++)
			{
				for (int y = 0; y < size; y++)
				{
					if (_occupiedCells.ContainsKey(topLeft + new Vector2I(x, y)))
						return false;
				}
			}

			// 墙体/墙外空白不能放建筑：必须落在合法地面格上且不压墙
			var simGrid = RTS.Core.SimManager.Instance?.World?.Grid;
			if (simGrid != null)
			{
				for (int x = 0; x < size; x++)
				{
					for (int y = 0; y < size; y++)
					{
						var cell = new RTS.Simulation.SimVector2I(topLeft.X + x, topLeft.Y + y);
						if (!simGrid.TerrainCells.Contains(cell) || simGrid.StaticObstacles.Contains(cell))
							return false;
					}
				}
			}

			// 作者在编辑器里标记的建造禁区（资源点保护、通路保留等）
			var activeMap = ActiveMap;
			if (activeMap != null && activeMap.HasTerrain)
			{
				for (int x = 0; x < size; x++)
				{
					for (int y = 0; y < size; y++)
					{
						if (activeMap.IsBuildBlocked(topLeft.X + x, topLeft.Y + y))
							return false;
					}
				}
			}

			return true;
		}

		public bool RegisterStructure(Vector2I topLeft, int size, Node structure)
		{
			if (!IsAreaEmpty(topLeft, size)) return false;

			for (int x = 0; x < size; x++)
			{
				for (int y = 0; y < size; y++)
				{
					_occupiedCells[topLeft + new Vector2I(x, y)] = structure;
				}
			}
			RequestBake();
			return true;
		}

		public void UnregisterStructure(Vector2I topLeft, int size)
		{
			bool changed = false;
			for (int x = 0; x < size; x++)
			{
				for (int y = 0; y < size; y++)
				{
					Vector2I pos = topLeft + new Vector2I(x, y);
					if (_occupiedCells.ContainsKey(pos))
					{
						_occupiedCells.Remove(pos);
						changed = true;
					}
				}
			}
			if (changed) RequestBake();
		}
		public bool IsValidTerrainForCreep(Vector2I cell)
		{
			// 1. 虚空检测：如果这格连基础地板图都没有，不能长菌毯
			if (BaseMapLayer == null || BaseMapLayer.GetCellSourceId(cell) == -1)
				return false;

			// 2. 墙壁/障碍检测：读取 TileSet 数据
			TileData tileData = BaseMapLayer.GetCellTileData(cell);
			if (tileData != null)
			{
				// 自动识别墙壁：TileSet 物理层（Physics Layer 0）
				// 只要这个地砖有碰撞，就认为它是墙壁，不允许长菌毯！
				if (tileData.GetCollisionPolygonsCount(0) > 0)
				{
					return false;
				}

				// (可选) 如果你是通过 Custom Data 层来标记墙壁的，可以把下面这行解开：
				// if (tileData.GetCustomData("IsWall").AsBool()) return false;
			}

			return true;
		}
		// --- 4. 蓝图检测 (标准 AABB) ---

		public bool IsPositionAvailableForBlueprint(
			Vector2I topLeft,
			int size,
			CreepType requiredCreep = CreepType.Any,
			int requiredCreepOwner = 0)
		{
			// 1. 实体占用
			if (!IsAreaEmpty(topLeft, size)) return false;

			// 2. 蓝图重叠 (Standard AABB)
			int myLeft = topLeft.X;
			int myRight = topLeft.X + size;
			int myTop = topLeft.Y;
			int myBottom = topLeft.Y + size;

			var allStructures = GetTree().GetNodesInGroup("structures");
			foreach (Node node in allStructures)
			{
				if (node is Structure s && !s.IsDeadOrNull())
				{
					if (s.CurrentState == Structure.StructureState.Blueprint)
					{
						int otherLeft = s.GridPosition.X;
						int otherRight = s.GridPosition.X + s.GridSize;
						int otherTop = s.GridPosition.Y;
						int otherBottom = s.GridPosition.Y + s.GridSize;

						bool overlap = (myLeft < otherRight && myRight > otherLeft &&
										myTop < otherBottom && myBottom > otherTop);

						if (overlap) return false;
					}
				}
			}

			// 3. 菌毯判定
			if (requiredCreep != CreepType.Any && CreepManager.Instance != null)
			{
				for (int x = 0; x < size; x++)
				{
					for (int y = 0; y < size; y++)
					{
						CreepType currentCreep = CreepManager.Instance.GetActiveCreep(topLeft + new Vector2I(x, y));

						// 如果当前地块跟你要求的地表不一致，就不能建
						if (currentCreep != requiredCreep)
							return false;

						// 需要区分敌我菌毯时：必须是自己种的菌毯才能建（植物等不区分可传 0 跳过）
						if (requiredCreepOwner != 0)
						{
							int owner = RTS.Core.SimManager.Instance != null
								? RTS.Core.SimManager.Instance.World.CreepGrid.GetOwner(topLeft.X + x, topLeft.Y + y)
								: 0;

							if (owner != requiredCreepOwner)
								return false;
						}
					}
				}
			}

			// 4. 资源点外 2 格内禁止建造（与 SimGrid 判定一致，模拟线程可安全调用）
			var simWorld = RTS.Core.SimManager.Instance?.World;
			if (simWorld != null &&
				simWorld.IsAreaNearResourceNode(
					new RTS.Simulation.SimVector2I(topLeft.X, topLeft.Y), size, 2))
				return false;

			return true;
		}

		// --- 烘焙 ---
		private void RequestBake()
		{
			if (NavRegion == null) return;
			// 可能在模拟线程触发（蓝图取消/建筑注册）：导航烘培必须回主线程，
			// 否则跨线程 IsBaking/CallDeferred 会原生崩溃（蓝图大量取消时闪退）
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (!GodotObject.IsInstanceValid(NavRegion)) return;
				if (NavRegion.IsBaking() || _isBaking) { _bakeQueued = true; return; }
				StartBake();
			});
		}
		private void StartBake()
		{
			_isBaking = true;
			NavRegion.CallDeferred(NavigationRegion2D.MethodName.BakeNavigationPolygon);
		}
		private void OnBakeFinished()
		{
			_isBaking = false;
			if (_bakeQueued) { _bakeQueued = false; StartBake(); }
		}
		public override void _ExitTree()
		{
			if (NavRegion != null) NavRegion.BakeFinished -= OnBakeFinished;
		}
	}
}
