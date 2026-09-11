// File: res://Scripts/Simulation/SimSpatialGrid.cs
using System.Collections.Generic;
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	// 单位空间索引：把每 Tick 的 O(n²) 邻近查询降为 O(候选数)。
	// 只做查询加速，不改变任何受力/碰撞公式与遍历确定性（候选统一按 ID 排序）。
	public sealed class SimSpatialGrid
	{
		private const int CellSize = 256; // 4 格
		private readonly Dictionary<long, List<SimUnit>> _cells = new();
		private int _maxUnitRadius;
		private int _maxVisionRange;

		public int MaxUnitRadius => _maxUnitRadius;
		public int MaxVisionRange => _maxVisionRange;

		public void Rebuild(IEnumerable<SimUnit> units)
		{
			_cells.Clear();
			_maxUnitRadius = 0;
			_maxVisionRange = 0;

			foreach (var u in units)
			{
				if (u == null || u.IsDead)
					continue;

				int r = (int)FP.Ceiling(u.Radius);
				if (r > _maxUnitRadius)
					_maxUnitRadius = r;

				int vr = (int)FP.Ceiling(u.VisionRange);
				if (vr > _maxVisionRange)
					_maxVisionRange = vr;

				long key = GetCellKey(u.Position);
				if (!_cells.TryGetValue(key, out var list))
				{
					list = new List<SimUnit>();
					_cells[key] = list;
				}
				list.Add(u);
			}
		}

		public void QueryNeighbors(FPVector2 pos, FP queryRadius, List<SimUnit> results)
		{
			results.Clear();

			int minCX = CellCoord(pos.X - queryRadius);
			int maxCX = CellCoord(pos.X + queryRadius);
			int minCY = CellCoord(pos.Y - queryRadius);
			int maxCY = CellCoord(pos.Y + queryRadius);

			for (int cx = minCX; cx <= maxCX; cx++)
			{
				for (int cy = minCY; cy <= maxCY; cy++)
				{
					if (_cells.TryGetValue(GetCellKey(cx, cy), out var list))
						results.AddRange(list);
				}
			}
		}

		private static long GetCellKey(FPVector2 pos)
		{
			int cx = (int)FP.Floor(pos.X / (FP)CellSize);
			int cy = (int)FP.Floor(pos.Y / (FP)CellSize);
			return GetCellKey(cx, cy);
		}

		private static long GetCellKey(int cx, int cy)
		{
			unchecked
			{
				return ((long)cx << 32) ^ (uint)cy;
			}
		}

		private static int CellCoord(FP v)
		{
			return (int)FP.Floor(v / (FP)CellSize);
		}
	}
}
