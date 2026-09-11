using Godot;

namespace RTS.Data.Maps
{
	// =========================================================
	// 地图数据的纯几何操作（不碰 Godot 场景节点）
	//
	// 刻意与 MapLoader 分开：
	//   MapLoader 依赖 TileMapLayer（必须在主线程、必须有 Godot 运行时），
	//   本文件只操作 RtsMapData 的字节数组，因此可以被无头测试直接调用。
	//   程序化生成器与地图编辑器都用这里的方法，避免"编辑器能画、测试跑不了"。
	// =========================================================

	public static class MapDataOps
	{
		/// <summary>
		/// 地形变体数量：128×128 图集按 64px 切成 2×2 = 4 个变体。
		/// 放在数据层是因为"变体 → atlas 坐标"是地图格式的一部分，
		/// 而 MapLoader 依赖 TileMapLayer（无法在无头测试里跑）。
		/// </summary>
		public const int VariantColumns = 2;

		/// <summary>变体索引 → atlas 坐标。越界时回落到 (0,0)。</summary>
		public static Vector2I VariantToAtlas(int variant)
		{
			if (variant < 0) variant = 0;
			return new Vector2I(variant % VariantColumns, variant / VariantColumns);
		}

		/// <summary>atlas 坐标 → 变体索引。</summary>
		public static int AtlasToVariant(Vector2I atlas) => atlas.Y * VariantColumns + atlas.X;

		/// <summary>在地图四周盖一圈边界墙。</summary>
		public static void StampBorder(RtsMapData map, int thickness = 1, int variant = 0)
		{
			if (map == null || !map.HasTerrain) return;
			thickness = System.Math.Max(1, thickness);
			thickness = System.Math.Min(thickness, System.Math.Min(map.Width, map.Height) / 2);

			byte wall = (byte)RtsMapData.Encode(RtsMapData.SourceWall, variant);

			for (int t = 0; t < thickness; t++)
			{
				for (int x = 0; x < map.Width; x++)
				{
					map.Terrain[map.Index(x, t)] = wall;
					map.Terrain[map.Index(x, map.Height - 1 - t)] = wall;
				}
				for (int y = 0; y < map.Height; y++)
				{
					map.Terrain[map.Index(t, y)] = wall;
					map.Terrain[map.Index(map.Width - 1 - t, y)] = wall;
				}
			}
		}

		/// <summary>画一条直线（Bresenham，确定性）。用于树墙/走廊手绘。</summary>
		public static void StampLine(RtsMapData map, int x0, int y0, int x1, int y1,
			int source, int variant = 0, int brushSize = 1)
		{
			if (map == null || !map.HasTerrain) return;

			int dx = System.Math.Abs(x1 - x0);
			int dy = -System.Math.Abs(y1 - y0);
			int sx = x0 < x1 ? 1 : -1;
			int sy = y0 < y1 ? 1 : -1;
			int err = dx + dy;

			// 上限保护：坏输入不至于死循环
			int guard = dx - dy + 4;

			while (guard-- > 0)
			{
				StampBrush(map, x0, y0, source, variant, brushSize);
				if (x0 == x1 && y0 == y1) break;

				int e2 = 2 * err;
				if (e2 >= dy) { err += dy; x0 += sx; }
				if (e2 <= dx) { err += dx; y0 += sy; }
			}
		}

		/// <summary>画笔：以 (cx,cy) 为中心的方形刷子。</summary>
		public static void StampBrush(RtsMapData map, int cx, int cy,
			int source, int variant = 0, int brushSize = 1)
		{
			if (map == null || !map.HasTerrain) return;
			int half = System.Math.Max(0, brushSize / 2);
			byte encoded = (byte)RtsMapData.Encode(source, variant);

			for (int x = cx - half; x <= cx + half; x++)
				for (int y = cy - half; y <= cy + half; y++)
					if (map.InBounds(x, y))
						map.Terrain[map.Index(x, y)] = encoded;
		}

		/// <summary>矩形填充。</summary>
		public static void StampRect(RtsMapData map, int x0, int y0, int x1, int y1,
			int source, int variant = 0, bool filled = true)
		{
			if (map == null || !map.HasTerrain) return;

			if (x0 > x1) (x0, x1) = (x1, x0);
			if (y0 > y1) (y0, y1) = (y1, y0);

			byte encoded = (byte)RtsMapData.Encode(source, variant);

			for (int x = x0; x <= x1; x++)
			{
				for (int y = y0; y <= y1; y++)
				{
					if (!map.InBounds(x, y)) continue;
					bool onEdge = x == x0 || x == x1 || y == y0 || y == y1;
					if (!filled && !onEdge) continue;
					map.Terrain[map.Index(x, y)] = encoded;
				}
			}
		}

		/// <summary>圆形填充（整数判定，确定性）。</summary>
		public static void StampCircle(RtsMapData map, int cx, int cy, int radius,
			int source, int variant = 0)
		{
			if (map == null || !map.HasTerrain || radius < 0) return;

			byte encoded = (byte)RtsMapData.Encode(source, variant);
			int r2 = radius * radius;

			for (int x = cx - radius; x <= cx + radius; x++)
			{
				for (int y = cy - radius; y <= cy + radius; y++)
				{
					int dx = x - cx;
					int dy = y - cy;
					if (dx * dx + dy * dy > r2) continue;
					if (!map.InBounds(x, y)) continue;
					map.Terrain[map.Index(x, y)] = encoded;
				}
			}
		}

		/// <summary>洪水填充：把与 (x,y) 连通的同类型格子换成另一类型。</summary>
		public static int FloodFill(RtsMapData map, int x, int y, int targetSource, int newSource,
			int newVariant = 0)
		{
			if (map == null || !map.HasTerrain || !map.InBounds(x, y)) return 0;

			int start = map.Index(x, y);
			if (RtsMapData.DecodeSource(map.Terrain[start]) != targetSource) return 0;

			byte replacement = (byte)RtsMapData.Encode(newSource, newVariant);
			var stack = new System.Collections.Generic.Stack<int>();
			var visited = new bool[map.Width * map.Height];
			stack.Push(start);
			visited[start] = true;
			int changed = 0;

			while (stack.Count > 0)
			{
				int idx = stack.Pop();
				if (RtsMapData.DecodeSource(map.Terrain[idx]) != targetSource) continue;

				map.Terrain[idx] = replacement;
				changed++;

				int cx = idx % map.Width;
				int cy = idx / map.Width;
				Push(map, visited, stack, cx - 1, cy);
				Push(map, visited, stack, cx + 1, cy);
				Push(map, visited, stack, cx, cy - 1);
				Push(map, visited, stack, cx, cy + 1);
			}

			return changed;
		}

		private static void Push(RtsMapData map, bool[] visited,
			System.Collections.Generic.Stack<int> stack, int x, int y)
		{
			if (!map.InBounds(x, y)) return;
			int idx = map.Index(x, y);
			if (visited[idx]) return;
			visited[idx] = true;
			stack.Push(idx);
		}

		/// <summary>把整张地图按比例缩放（最近邻，确定性）。用于快速调整尺寸。</summary>
		public static void ResampleNearest(RtsMapData map, int newWidth, int newHeight)
		{
			if (map == null || !map.HasTerrain || newWidth <= 0 || newHeight <= 0) return;

			var old = (byte[])map.Terrain.Clone();
			int ow = map.Width;
			int oh = map.Height;

			map.Resize(newWidth, newHeight);

			for (int y = 0; y < map.Height; y++)
			{
				int sy = System.Math.Clamp(y * oh / map.Height, 0, oh - 1);
				for (int x = 0; x < map.Width; x++)
				{
					int sx = System.Math.Clamp(x * ow / map.Width, 0, ow - 1);
					map.Terrain[map.Index(x, y)] = old[sy * ow + sx];
				}
			}
		}
	}
}
