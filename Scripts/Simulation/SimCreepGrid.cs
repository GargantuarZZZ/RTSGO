using System;
using System.Collections.Generic;
using RTS.Data;

namespace RTS.Simulation
{
	public class SimCreepGrid
	{
		public struct CreepCellData
		{
			public CreepType Type;
			public int OwnerTeam;
		}

		// 核心数据：在纯后台记录每个格子的菌毯类型
		private Dictionary<SimVector2I, CreepCellData> _creepCells = new();
		// 纳米菌毯格数缓存：TickNanoEconomy 每 tick 读取，避免每次拷贝整表
		private int _nanoCreepCount = 0;
		public int NanoCreepCount => _nanoCreepCount;
		// 植物菌毯格数缓存（植物木材收入用）
		private int _plantCreepCount = 0;
		public int PlantCreepCount => _plantCreepCount;

		// 核心事件：当逻辑层的菌毯发生变化时，通知表现层 (CreepManager) 去更新 Godot 的 TileMap
		public event Action<int, int, CreepType> OnCreepChanged;

		public void AddCreep(int x, int y, CreepType type, int ownerTeam = 0)
		{
			var pos = new SimVector2I(x, y);

			if (_creepCells.TryGetValue(pos, out var existing))
			{
				// 不同种菌毯互相阻挡，不能覆盖
				if (existing.Type != type)
					return;

				// 敌我同种菌毯也不能互相覆盖（纳米虫 vs 纳米虫）
				if (existing.OwnerTeam != ownerTeam)
					return;

				// 同种菌毯：只更新归属，不重置血量
				existing.OwnerTeam = ownerTeam;
				_creepCells[pos] = existing;
				return;
			}

			_creepCells[pos] = new CreepCellData
			{
				Type = type,
				OwnerTeam = ownerTeam
			};
			if (type == CreepType.NanoCreep)
				_nanoCreepCount++;
			else if (type == CreepType.PlantCreep)
				_plantCreepCount++;
			OnCreepChanged?.Invoke(x, y, type);
		}

		public void RemoveCreep(int x, int y, CreepType type)
		{
			var pos = new SimVector2I(x, y);
			if (_creepCells.TryGetValue(pos, out var cell) && cell.Type == type)
			{
				_creepCells.Remove(pos);
				if (type == CreepType.NanoCreep)
					_nanoCreepCount--;
				else if (type == CreepType.PlantCreep)
					_plantCreepCount--;
				// 移除后，通知表现层将该地块设为 None
				OnCreepChanged?.Invoke(x, y, CreepType.None);
			}
		}

		// 玩家/中立建筑直接攻击菌毯：纳米菌毯被攻击一次即摧毁，其它种菌毯不受伤害
		public bool DestroyNanoCreep(int x, int y)
		{
			var pos = new SimVector2I(x, y);

			if (!_creepCells.TryGetValue(pos, out var cell))
				return false;

			if (cell.Type != CreepType.NanoCreep)
				return false;

			_creepCells.Remove(pos);
			_nanoCreepCount--;
			OnCreepChanged?.Invoke(x, y, CreepType.None);
			return true;
		}

		// 溅射清菌毯：以 (cx,cy) 为中心，半径 radius 格内的敌方纳米菌毯全部摧毁
		public int DestroyNanoCreepRadius(int cx, int cy, int radius, int excludeOwnerTeam)
		{
			return DestroyNanoCreepRadius(cx, cy, radius, excludeOwnerTeam, null);
		}

		// isFriendlyOwner 为空时按“与 excludeOwnerTeam 不同队”排除；
		// 传入后改为“满足谓词 = 友方，不清除”（2v2 盟友菌毯不误伤）
		public int DestroyNanoCreepRadius(
			int cx, int cy, int radius, int excludeOwnerTeam,
			System.Func<int, bool> isFriendlyOwner)
		{
			int destroyed = 0;
			int radiusSq = radius * radius;

			for (int x = cx - radius; x <= cx + radius; x++)
			{
				for (int y = cy - radius; y <= cy + radius; y++)
				{
					// 溅射清菌毯也是圆形范围
					int dx = x - cx;
					int dy = y - cy;
					if (dx * dx + dy * dy > radiusSq)
						continue;

					var pos = new SimVector2I(x, y);
					if (_creepCells.TryGetValue(pos, out var cell) &&
						cell.Type == CreepType.NanoCreep &&
						(isFriendlyOwner == null
							? cell.OwnerTeam != excludeOwnerTeam
							: !isFriendlyOwner(cell.OwnerTeam)))
					{
						_creepCells.Remove(pos);
						_nanoCreepCount--;
						OnCreepChanged?.Invoke(x, y, CreepType.None);
						destroyed++;
					}
				}
			}

			return destroyed;
		}

		public CreepType GetActiveCreep(int x, int y)
		{
			var pos = new SimVector2I(x, y);
			return _creepCells.TryGetValue(pos, out var cell) ? cell.Type : CreepType.None;
		}

		public int GetOwner(int x, int y)
		{
			var pos = new SimVector2I(x, y);
			return _creepCells.TryGetValue(pos, out var cell) ? cell.OwnerTeam : 0;
		}

		public bool IsValidIndex(int x, int y)
		{
			// 如果你的地图有明确的网格边界限制，可以在这里加上判断
			// 目前默认所有展开的坐标都是合法的
			return true;
		}

		// 供表现层轮询读取（不依赖事件时序）
		public Dictionary<SimVector2I, CreepType> GetAllCells()
		{
			var result = new Dictionary<SimVector2I, CreepType>();

			foreach (var kvp in _creepCells)
				result[kvp.Key] = kvp.Value.Type;

			return result;
		}

		public Dictionary<SimVector2I, CreepCellData> GetAllCellData()
		{
			return new Dictionary<SimVector2I, CreepCellData>(_creepCells);
		}

		// 菌毯状态哈希：按单元格独立 FNV 混合后用 XOR 汇总，顺序无关，
		// 避免依赖 Dictionary 的枚举顺序（保证跨端一致）
		public long GetStateHash()
		{
			long combined = 0;

			foreach (var kvp in _creepCells)
			{
				long cellHash = 1469598103934665603L;

				unchecked
				{
					cellHash ^= kvp.Key.X;
					cellHash *= 1099511628211L;
					cellHash ^= kvp.Key.Y;
					cellHash *= 1099511628211L;
					cellHash ^= (long)kvp.Value.Type;
					cellHash *= 1099511628211L;
					cellHash ^= kvp.Value.OwnerTeam;
					cellHash *= 1099511628211L;
				}

				combined ^= cellHash;
			}

			return combined;
		}
	}
}
