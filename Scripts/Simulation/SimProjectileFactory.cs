using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	// 弹体统一工厂：武器开火与技能弹体都从这里按 WeaponConfig 生成逻辑弹体。
	// 新增弹道/命中行为只需改配置表，不再需要每个发射点手写 SimProjectile。
	public static class SimProjectileFactory
	{
		public static SimProjectile Create(
			ProjectileSpec spec,
			SimEntity source,
			SimEntity target,
			FPVector2 targetPoint,
			bool hasPointTarget,
			FP damage,
			int damageType,
			FP bonusDamage,
			int bonusDamageType,
			FP areaRadius,
			int areaEdgeDamagePercent)
		{
			var startPos = source != null ? source.Position : targetPoint;

			return new SimProjectile(
				startPos,
				target,
				spec.Speed,
				spec.HitRadius,
				damage,
				damageType,
				bonusDamage,
				bonusDamageType,
				areaRadius,
				areaEdgeDamagePercent,
				source?.ID ?? -1)
			{
				Source = source,
				Motion = spec.Motion,
				HasPointTarget = hasPointTarget,
				TargetPoint = targetPoint,
				MaxTravel = spec.MaxTravel,
				LobDuration = spec.LobDuration,
				LobHeight = spec.LobHeight,
				LobStartX = startPos.X,
				LobStartY = startPos.Y,
				FootprintScaledDamage = spec.FootprintScaledDamage,
				ImpactRadius = spec.ImpactRadius,
				ImpactDamagePerFootprintSq = spec.ImpactDamagePerFootprintSq,
				ImpactSlowMoveMultiplier = spec.ImpactSlowMoveMultiplier,
				ImpactSlowAttackMultiplier = spec.ImpactSlowAttackMultiplier,
				ImpactSlowSeconds = spec.ImpactSlowSeconds
			};
		}
	}
}
