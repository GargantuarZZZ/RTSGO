using Godot;
using RTS.Core;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Units
{
	// 自动范围采集：从周围资源点按“每秒每点速率”收集（纳米采集器 / 先进采集器巨兽）。
	// 独立文件：场景预制体按文件路径挂脚本时必须能命中具体类（不能挂在抽象基类文件上）。
	public partial class AutoHarvestBehavior : HarvestBehavior
	{
		[Export] public int RadiusTiles = 0;
		[Export] public float RatePerSecondPerNode = 0f;

		public override void Tick(SimWorld world, SimEntity entity, PlayerData owner)
		{
			if (world == null || entity == null || owner == null ||
				RadiusTiles <= 0 || RatePerSecondPerNode <= 0f)
				return;

			// 巨兽模块常挂，但由科技启用（BehemothCanHarvest 为 true 才采集）
			if (entity is SimUnit su && !su.BehemothCanHarvest)
				return;

			FP radiusSq = (FP)(RadiusTiles * world.Grid.TileSize);
			radiusSq *= radiusSq;
			FP ratePerNode = (FP)RatePerSecondPerNode * world.FixedDelta;
			FP multiplier = (FP)TechEffects.GetHarvesterIncomeMultiplier(owner);

			foreach (var res in world.Structures.Values)
			{
				if (res.IsDead || res.ResourceAmount <= FP.Zero)
					continue;

				if (!FPVector2.IsWithinRangeSq(entity.Position, res.Position, radiusSq))
					continue;

				FP amount = ratePerNode;
				if (amount > res.ResourceAmount)
					amount = res.ResourceAmount;

				res.ResourceAmount -= amount;
				// P1-1：难度=资源倍率，自动采集同样生效
				owner.AddResource(
					ResourceType.NanoBots,
					amount * multiplier * (RTS.Core.SimManager.Instance?.GetBotResourceMultiplier(entity.TeamID) ?? FP.One));
			}
		}
	}
}
