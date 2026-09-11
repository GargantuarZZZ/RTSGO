using Godot;
using System;
using System.Collections.Generic;
using RTS.Data.Maps;
using RTS.Simulation;

namespace RTS.World
{
	// =========================================================
	// 地图加载器：RtsMapData → TileMapLayer（视觉）+ SimGrid（逻辑）
	//
	// 这是编辑器与游戏共用的唯一转换点。两边都走这里，才能保证
	// "编辑器里显示的可走/阻挡" 与 "游戏里寻路的可走/阻挡" 必然一致。
	//
	// 关键：SimGrid 只吃 Terrain 的低 4 位（source），高 4 位（贴图变体）
	// 完全不参与逻辑——换皮不改变寻路结果。
	// =========================================================

	public static class MapLoader
	{
		/// <summary>
		/// 把地图数据写进 TileMapLayer（视觉层）。
		/// tileset 必须与 St.tres 同构（source 0 = 可走地面，source 1 = 阻挡墙）。
		/// 必须在主线程调用（碰 TileMapLayer）。
		///
		/// 坐标系：格坐标原样写入，**不做偏移** —— 与 InitSimGrid 的
		/// `cell.X → SimVector2I(cell.X)` 以及 WorldToGrid 的 `world/64` 保持一致，
		/// 这样视觉层与逻辑层共用同一套格坐标。
		/// </summary>
		public static void ApplyToTileMap(RtsMapData map, TileMapLayer layer)
		{
			if (map == null || layer == null || !map.HasTerrain)
				return;

			layer.Clear();
			for (int y = 0; y < map.Height; y++)
			{
				for (int x = 0; x < map.Width; x++)
				{
					byte cell = map.Terrain[map.Index(x, y)];
					int source = RtsMapData.DecodeSource(cell);
					int variant = RtsMapData.DecodeVariant(cell);
					layer.SetCell(new Vector2I(x, y), source, VariantToAtlas(variant));
				}
			}
		}

		/// <summary>
		/// 变体索引 → atlas 坐标。实现已移到 MapDataOps（纯数据层，可无头测试）。
		/// </summary>
		public static Vector2I VariantToAtlas(int variant) => MapDataOps.VariantToAtlas(variant);

		/// <summary>atlas 坐标 → 变体索引（编辑器从 TileMap 读回时用）。</summary>
		public static int AtlasToVariant(Vector2I atlas) => MapDataOps.AtlasToVariant(atlas);

		/// <summary>
		/// 把地图数据同步进确定性 SimGrid。这是逻辑层唯一的地形来源。
		///
		/// 刻意**不**通过 TileMapLayer.GetCellTileData 反推：
		///   - 反推依赖 tileset 的物理碰撞多边形，作者改 tileset 会静默改变游戏性；
		///   - 反推要在主线程跑 TileMap API，无法用于编辑器后台校验。
		/// 直接读 Terrain 字节，语义明确且可脱离 Godot 测试。
		/// </summary>
		public static void ApplyToSimGrid(RtsMapData map, SimGrid grid)
		{
			if (map == null || grid == null || !map.HasTerrain)
				return;

			FillGridFromMap(map, grid);
		}

		/// <summary>
		/// 从地图数据造一个**独立的** SimGrid（不挂在 SimWorld 上）。
		///
		/// 地图校验器用它跑真实寻路：校验"出生点互相可达""资源走得到"
		/// 必须和游戏里用的是同一套 A*，否则校验通过的地图进游戏仍会卡住。
		/// 这里只依赖 SimGrid 本身，不碰场景树，因此可以在无头环境跑。
		/// </summary>
		public static SimGrid CreateDetachedGrid(RtsMapData map)
		{
			var grid = new SimGrid();
			if (map == null || !map.HasTerrain)
				return grid;

			FillGridFromMap(map, grid);
			return grid;
		}

		/// <summary>地图字节 → SimGrid 的唯一实现，供上面两个入口共用。</summary>
		private static void FillGridFromMap(RtsMapData map, SimGrid grid)
		{
			grid.TileSize = RtsMapData.TileSize;
			grid.StaticObstacles.Clear();
			grid.TerrainCells.Clear();

			for (int y = 0; y < map.Height; y++)
			{
				for (int x = 0; x < map.Width; x++)
				{
					int source = RtsMapData.DecodeSource(map.Terrain[map.Index(x, y)]);
					var cell = new SimVector2I(x, y);
					if (source == RtsMapData.SourceWall)
						grid.StaticObstacles.Add(cell);
					else
						grid.TerrainCells.Add(cell);
				}
			}

			grid.RefreshTerrainBounds();
			// SyncStaticBlocking 会递增 staticVersion、重建 _blockedCells 与
			// 0..4 级半径膨胀网格——蓝图覆盖层也在这里面对账。
			grid.SyncStaticBlocking();
		}

		/// <summary>
		/// 从已有的 TileMapLayer 反向导出地图数据（用于把现存的 main.tscn / main_1v1.tscn
		/// 迁移成 .tres 地图，作者不必重画）。
		/// 判定规则与旧 MapGrid.InitSimGrid 一致：有物理碰撞多边形 = 墙。
		/// </summary>
		public static RtsMapData ExportFromTileMap(TileMapLayer layer, string mapId, int fallbackWidth = 120, int fallbackHeight = 120)
		{
			var map = new RtsMapData { MapId = mapId, DisplayName = mapId };

			if (layer == null)
			{
				map.Resize(fallbackWidth, fallbackHeight);
				map.Fill(RtsMapData.SourceGrass);
				return map;
			}

			var used = layer.GetUsedCells();

			// 先量出实际范围（TileMap 的 cell 可能是负坐标，导出时要平移到 0 起点）
			int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
			foreach (var cell in used)
			{
				if (cell.X < minX) minX = cell.X;
				if (cell.Y < minY) minY = cell.Y;
				if (cell.X > maxX) maxX = cell.X;
				if (cell.Y > maxY) maxY = cell.Y;
			}

			if (used.Count == 0 || minX > maxX)
			{
				map.Resize(fallbackWidth, fallbackHeight);
				map.Fill(RtsMapData.SourceGrass);
				return map;
			}

			int width = maxX - minX + 1;
			int height = maxY - minY + 1;
			map.Resize(width, height);
			// 导出的空白格默认是不可走的墙，保证"没画 = 走不过去"
			map.Fill(RtsMapData.SourceWall);

			// 记录平移量：出生点标记导出时要减掉同样的偏移，才能与地形坐标系一致
			map.ExportOffsetX = minX;
			map.ExportOffsetY = minY;

			foreach (var cell in used)
			{
				var tileData = layer.GetCellTileData(cell);
				if (tileData == null) continue;

				bool blocking = tileData.GetCollisionPolygonsCount(0) > 0;
				int source = blocking ? RtsMapData.SourceWall : RtsMapData.SourceGrass;
				int variant = AtlasToVariant(layer.GetCellAtlasCoords(cell));

				int x = cell.X - minX;
				int y = cell.Y - minY;
				map.SetCell(x, y, source, variant);
			}

			GD.Print($"[MapLoader] 从 TileMap 导出 '{mapId}': {width}x{height}, 已用格 {used.Count}");
			return map;
		}

		/// <summary>
		/// 在地图上盖一层矩形边界墙（作者常用的"围一圈"操作）。
		/// 实际实现已移到 MapDataOps（纯数据操作，可脱离 Godot 测试），这里保留转发以免旧调用点失效。
		/// </summary>
		public static void StampBorder(RtsMapData map, int thickness = 1, int variant = 0)
			=> RTS.Data.Maps.MapDataOps.StampBorder(map, thickness, variant);
	}
}
