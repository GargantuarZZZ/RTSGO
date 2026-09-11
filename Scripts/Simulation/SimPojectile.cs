using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	// 弹体运动方式（由 WeaponConfig.ProjectileBehavior 映射而来）
	public enum ProjectileMotion
	{
		Homing = 0, // 跟踪弹道：追击目标当前位置
		Fixed = 1,  // 延迟直线：朝开火时目标方向直飞，不追踪，超程脱靶
		Lob = 2     // 抛物线抛射：固定飞行时间落到目标点/落点
	}

	public class SimProjectile
	{
		public FPVector2 Position;
		public FP Speed;
		public FP HitRadius;
		public SimEntity Target;
		// 对地面目标点射击（清菌毯/电浆落点等）：没有实体目标时飞向固定落点
		public FPVector2 TargetPoint;
		public bool HasPointTarget = false;
		// 弹道运动方式
		public ProjectileMotion Motion = ProjectileMotion.Homing;
		// 延迟直线弹道的最大飞行距离（超程脱靶）
		public FP MaxTravel = FP.Zero;
		private FPVector2 _fixedDir;
		private bool _dirInit = false;
		private FP _traveled = FP.Zero;

		public FP Damage;
		public int DamageType;
		public FP BonusDamage;
		public int BonusDamageType;
		public FP AreaRadius = FP.Zero;
		public int AreaEdgeDamagePercent = 100;
		public int SourceId = -1;
		public SimEntity Source;

		// 命中覆盖（电浆炮类）：按目标长宽² 结算伤害并减速
		public bool FootprintScaledDamage = false;
		public FP ImpactRadius = FP.Zero;
		public FP ImpactDamagePerFootprintSq = FP.Zero;
		public FP ImpactSlowMoveMultiplier = FP.Zero;
		public FP ImpactSlowAttackMultiplier = FP.Zero;
		public FP ImpactSlowSeconds = FP.Zero;

		// 抛物线曲射参数：固定飞行时间，XZ 直线插值，高度由表现层画弧
		public FP LobDuration = FP.Zero;
		public FP LobHeight = FP.Zero;
		public FP LobStartX;
		public FP LobStartY;
		public FP LobTime = FP.Zero;

		public bool IsDead;
		public bool HasHit;

		// 表现层兼容：HomingProjectile 用它画弧线高度
		public bool ParabolicLob => Motion == ProjectileMotion.Lob;

		public SimProjectile(FPVector2 startPos, SimEntity target, FP speed, FP hitRadius, FP damage, int damageType,
			FP bonusDamage = default, int bonusDamageType = 0,
			FP areaRadius = default, int areaEdgeDamagePercent = 100, int sourceId = -1)
		{
			Position = startPos;
			Target = target;
			Speed = speed;
			HitRadius = hitRadius;
			Damage = damage;
			DamageType = damageType;
			BonusDamage = bonusDamage;
			BonusDamageType = bonusDamageType;
			AreaRadius = areaRadius;
			AreaEdgeDamagePercent = areaEdgeDamagePercent;
			SourceId = sourceId;
		}

		public void LogicTick(FP delta)
		{
			if (IsDead) return;
			if (Target == null && !HasPointTarget)
			{
				IsDead = true;
				return;
			}

			// 抛物线抛射（电浆炮）：朝固定落点飞行，按时间推进
			if (Motion == ProjectileMotion.Lob && HasPointTarget && LobDuration > FP.Zero)
			{
				LobTime += delta;
				FP t = LobTime / LobDuration;

				if (t >= FP.One)
				{
					HasHit = true;
					IsDead = true;
					// 自动锁定：落地瞬间追踪目标当前位置；手动固定点按原落点
					Position = Target != null && !Target.IsDead ? Target.Position : TargetPoint;
					return;
				}

				Position = new FPVector2(
					LobStartX + (TargetPoint.X - LobStartX) * t,
					LobStartY + (TargetPoint.Y - LobStartY) * t);
				return;
			}

			// 延迟直线：朝开火时目标方向直飞，不追踪
			if (Motion == ProjectileMotion.Fixed && Target != null)
			{
				if (!_dirInit)
				{
					_fixedDir = (Target.Position - Position).Normalized();
					_dirInit = true;
				}

				FP fixedStep = Speed * delta;
				_traveled += fixedStep;

				if (MaxTravel > FP.Zero && _traveled >= MaxTravel)
				{
					IsDead = true; // 脱靶：超过射程仍未命中
					return;
				}

				FP fixedDistSq = FPVector2.DistanceSquared(Position, Target.Position);
				FP fixedHitRadius = HitRadius + Target.Radius;

				if (fixedDistSq <= fixedHitRadius * fixedHitRadius)
				{
					HasHit = true;
					IsDead = true;
					Target.TakeDamage(Damage, DamageType, BonusDamage, BonusDamageType, Source);
					return;
				}

				Position += _fixedDir * fixedStep;
				return;
			}

			// 跟踪弹道 / 地面点：飞向目标当前位置或固定落点
			FPVector2 dest = HasPointTarget ? TargetPoint : Target.Position;
			FP distSq = FPVector2.DistanceSquared(Position, dest);
			FP moveStep = Speed * delta;

			// 命中半径必须加上目标自身的物理半径，否则子弹会穿透建筑边缘
			FP totalHitRadius = HasPointTarget ? HitRadius : HitRadius + Target.Radius;

			if (distSq <= totalHitRadius * totalHitRadius || distSq <= moveStep * moveStep)
			{
				HasHit = true;
				IsDead = true;

				// 命中瞬间，逻辑层绝对扣血
				if (!HasPointTarget && Target != null)
					Target.TakeDamage(Damage, DamageType, BonusDamage, BonusDamageType, Source);
				return;
			}

			FPVector2 dir = (dest - Position).Normalized();
			Position += dir * moveStep;
		}
	}
}
