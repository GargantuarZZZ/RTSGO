using System;
using FixMath.NET;
using FP = FixMath.NET.Fix64;
using System.Collections.Generic;

namespace RTS.Simulation
{
	public class SimStructure : SimEntity
	{
		public SimVector2I GridPosition; // 改为纯逻辑层坐标
		// 建筑真实长宽（格）：阻挡/碰撞/占地一律按这两个值，不再用正方形边长延申
		public int GridWidth;
		public int GridHeight;
		// 兼容旧字段（正方形建筑 = GridWidth）
		public int GridSize;

		// 恶魔自动生产：定时器（逻辑 tick 计数）与当前模式
		public FP AutoProduceTimer = FP.Zero;
		public int AutoProduceMode = 0;
		// 植物：花田能量 / 森林蔓延冷却与一次性标记
		public FP FlowerEnergy = FP.Zero;
		public FP FlowerEnergyMax = (FP)100m;
		public FP ForestSpreadCooldown = FP.Zero;
		public bool ForestSpreadUsed = false;
		// 狂热武装：自动生产速度加成与剩余时间
		public FP AutoProduceBoostMultiplier = FP.One;
		public FP AutoProduceBoostTimer = FP.Zero;
		// 立场半径（格，含科技加成后的有效值）
		public int FieldRadius = 0;
		// 限时建筑（旗帜）剩余寿命
		public FP LifespanTimer = FP.Zero;
		// 当前自动生产间隔（生产条显示用）
		public FP AutoProduceIntervalCurrent = FP.Zero;
		// 建成后待生成的单位（蓝图结果/赠送单位）：避免在单位遍历中直接插入集合
		public readonly List<string> PendingSpawnUnitIds = new();
		public enum StructureState
		{
			Blueprint,
			Constructing,
			Active,
			Destroyed
		}

		public StructureState CurrentState { get; set; } = StructureState.Blueprint;
		public FP ConstructionProgress { get; set; } = FP.Zero;
		// 建筑类型 ID（供人口/效果查询，确定性数据）
		public string StructureTypeId = "";
		// 泰伦：弹药补给范围（格，0=不提供）
		public int AmmoRangeTiles = 0;
		public bool AmmoRangeAffectsAir = false;
		// 空中建筑（未来可能有）：对空武器可以打
		public bool IsAir = false;
		// 泰伦：藻类工厂驻扎的工程师数量
		public int GarrisonedCount = 0;
		// 洞穴民兵化：驻扎工兵提供的远程射击总伤害（0 = 无武器）
		public int MilitiaGarrisonDamage = 0;
		// 洞穴残骸：重建目标建筑 ID 与重建进度（0~1）
		public string RebuildTargetId = "";
		public FP RebuildProgress = FP.Zero;
		// 地壳裂解器：地震波充能（当前次数 / 恢复计时）
		public int SeismicCharges = 0;
		public FP SeismicChargeTimer = FP.Zero;
		// 虫洞核心：制造虫洞冷却计时
		public FP WormholeCooldown = FP.Zero;
		// 地动仪：被动雷达脉冲计时（纯表现层）
		public FP RadarPulseTimer = FP.Zero;

		public SimStructure(int id, int teamId, FPVector2 centerPos, SimVector2I gridPos, int gridSize)
			: this(id, teamId, centerPos, gridPos, gridSize, gridSize)
		{
		}

		public SimStructure(int id, int teamId, FPVector2 centerPos, SimVector2I gridPos, int gridWidth, int gridHeight)
			: base(id, teamId, centerPos)
		{
			GridPosition = gridPos;
			GridWidth = gridWidth;
			GridHeight = gridHeight;
			GridSize = gridWidth;
			Radius = (FP)(Math.Min(gridWidth, gridHeight) * 32.0m);
		}

		public override void LogicTick(FP fixedDelta) { }

		public override long GetStateHash()
		{
			long hash = base.GetStateHash();
			hash ^= (long)CurrentState;
			hash ^= (long)(ConstructionProgress * (FP)1000m);
			hash ^= GridPosition.X * 73856093;
			hash ^= GridPosition.Y * 19349663;
			hash ^= GridWidth * 104729;
			hash ^= GridHeight * 15485863;
			hash ^= GridSize;
			hash ^= (long)(AutoProduceTimer * (FP)1000m);
			hash ^= AutoProduceMode;
			hash ^= (long)(AutoProduceBoostMultiplier * (FP)1000m);
			hash ^= (long)(AutoProduceBoostTimer * (FP)1000m);
			hash ^= FieldRadius;
			hash ^= GarrisonedCount;
			hash ^= MilitiaGarrisonDamage * 32452843;
			hash ^= (long)(RebuildProgress * (FP)1000m);
			hash ^= SeismicCharges;
			hash ^= (long)(SeismicChargeTimer * (FP)1000m);
			hash ^= (long)(WormholeCooldown * (FP)1000m);
			hash ^= (long)(RadarPulseTimer * (FP)1000m);
			hash ^= IsAir ? 1 : 0;
			hash ^= (long)(LifespanTimer * (FP)1000m);
			hash ^= (long)(AutoProduceIntervalCurrent * (FP)1000m);
			hash ^= (long)(FlowerEnergy * (FP)1000m);
			hash ^= (long)(FlowerEnergyMax * (FP)1000m);
			hash ^= (long)(ForestSpreadCooldown * (FP)1000m);
			hash ^= ForestSpreadUsed ? 1 : 0;
			foreach (string id in PendingSpawnUnitIds)
			{
				foreach (char c in id)
				{
					hash ^= c;
					hash *= 1099511628211L;
				}
			}
			foreach (char c in StructureTypeId ?? "")
			{
				hash ^= c;
				hash *= 1099511628211L;
			}
			foreach (char c in RebuildTargetId ?? "")
			{
				hash ^= c;
				hash *= 1099511628211L;
			}
			return hash;
		}
	}
}
