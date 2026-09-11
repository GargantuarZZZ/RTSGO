// File: res://Scripts/Simulation/SimEntity.cs
using FixMath.NET;
using FP = FixMath.NET.Fix64;
using System.Collections.Generic;

namespace RTS.Simulation
{
	public abstract class SimEntity
	{
		public int ID;
		public int TeamID;
		public FPVector2 Position;
		public FP Radius;
		public bool IsDead;

		// 所属世界引用（伤害重定向等需要查找其他实体）
		public SimWorld World;

		// 禁疗：恶魔单位不可被治疗/维修/回血
		public bool CannotBeHealed;

		// 最后一次受到伤害的来源（击杀数据统计用）
		public int LastDamageSourceId = -1;

		// 视野半径（世界单位）：用于友军视野索敌判定
		public FP VisionRange = FP.Zero;

		public FP MaxHp;
		public FP Hp;
		public FP MaxShield;
		public FP Shield;

		public Dictionary<int, FP> Defenses = new();

		//  经济与采集系统定点数数据
		public FP ResourceAmount;  // 作为矿脉时的剩余量
		public int ResourceType;   // 资源类型枚举

		// 攻防/Buff
		public int ArmorType;      // ArmorType 枚举（护甲类型，参与伤害类型修正）
		public BuffContainer Buffs;

		public FP CargoAmount;     // 作为矿工时的背包当前装载量
		public FP CargoCapacity;   // 背包最大容量
		public int CargoType;      // 当前正在搬运的资源类型


		public virtual string GetDebugState()
		{
			return $"ID:{ID}|Pos:({(long)(Position.X * (FP)1000m)},{(long)(Position.Y * (FP)1000m)})|HP:{(long)(Hp * (FP)100m)}|Res:{(long)(ResourceAmount * (FP)10m)}";
		}



		public SimEntity(int id, int teamId, FPVector2 startPos)
		{
			ID = id;
			TeamID = teamId;
			Position = startPos;
			Buffs = new BuffContainer(this);
		}

		public virtual void TakeDamage(FP rawDamage, int damageType, FP bonusDamage = default, int bonusArmorType = 0, SimEntity source = null)
		{
			if (IsDead) return;

			// 沙虫潜地：整条地下状态受伤减半（头或节段都查组头）
			if (this is SimUnit sandUnit && sandUnit.IsSandwormBody)
			{
				bool burrowed = sandUnit.SegmentGroupId <= 0
					? sandUnit.IsBurrowed
					: World?.FindSimEntity(sandUnit.SegmentGroupId) is SimUnit head && head.IsBurrowed;
				if (burrowed)
					rawDamage *= (FP)0.5m;
			}

			// 堡垒守卫：受到所有伤害减半
			if (this is SimUnit guardSelf && guardSelf.UnitTypeId == "FortGuard")
				rawDamage *= (FP)0.5m;

			// 堡垒守卫替队友承伤：3 格内最近友军受到的伤害转移到守卫
			if (this is SimUnit victim && victim.UnitTypeId != "FortGuard" && World != null)
			{
				SimUnit shield = FindNearestFortGuard(victim);

				if (shield != null)
				{
					shield.TakeDamage(rawDamage, damageType, bonusDamage, bonusArmorType, source);
					return;
				}
			}

			// 走统一攻防判定管线（伤害类型修正 / 护甲 / Buff 倍率）
			FP actualDamage = DamageResolver.Resolve(
				new DamageContext
				{
					DamageType = damageType,
					BaseDamage = rawDamage,
					BonusDamageType = bonusArmorType,
					BonusDamage = bonusDamage,
					SourceId = source?.ID ?? -1,
					TargetId = ID
				},
				this,
				source
			);

			if (Shield > FP.Zero)
			{
				if (Shield >= actualDamage)
				{
					Shield -= actualDamage;
					actualDamage = FP.Zero;
				}
				else
				{
					actualDamage -= Shield;
					Shield = FP.Zero;
				}
			}

			if (actualDamage > FP.Zero)
			{
				LastDamageSourceId = source?.ID ?? -1;
				Hp -= actualDamage;
				if (Hp <= FP.Zero)
				{
					Hp = FP.Zero;
					IsDead = true;

					// 泰伦弹药回收科技：击杀敌人回 1 弹药
					if (source is SimUnit shooter && shooter.AmmoRefundOnKill && shooter.MaxAmmo > FP.Zero)
					{
						shooter.Ammo += FP.One;
						if (shooter.Ammo > shooter.MaxAmmo)
							shooter.Ammo = shooter.MaxAmmo;
					}

					// 植物汲取：攻击吸血（按实际造成伤害比例回血）
					if (source is SimUnit lifestealer && lifestealer.TeamID > 0)
					{
						FP leechPct = World?.Rules.GetLifestealFraction(lifestealer.TeamID) ?? FP.Zero;
						if (leechPct > FP.Zero)
						{
							lifestealer.Hp += actualDamage * leechPct;
							if (lifestealer.Hp > lifestealer.MaxHp)
								lifestealer.Hp = lifestealer.MaxHp;
						}
					}
				}
			}
		}

		private SimUnit FindNearestFortGuard(SimUnit victim)
		{
			if (World == null)
				return null;

			FP range = (FP)192m; // 3 格
			return World.FindNearestUnit(
				victim.Position,
				range * range,
				u => u.TeamID == victim.TeamID && u.UnitTypeId == "FortGuard");
		}

		public abstract void LogicTick(FP fixedDelta);
		public virtual long GetStateHash()
		{
			long hash = ID;
			hash ^= TeamID;
			hash ^= ArmorType;
			hash = MixCoord(hash, (long)(Position.X * (FP)1000m), (long)(Position.Y * (FP)1000m));
			hash ^= (long)(Hp * (FP)100m);
			hash ^= (long)(Shield * (FP)100m);
			hash ^= (long)(ResourceAmount * (FP)10m);
			hash ^= (long)(CargoAmount * (FP)10m);
			hash ^= CargoType;
			hash ^= Buffs?.GetStateHash() ?? 0;
			return hash;
		}

		// 坐标对混合：避免 X/Y 相同（或互为抵消）时纯 XOR 导致哈希不敏感
		protected static long MixCoord(long hash, long x, long y)
		{
			unchecked
			{
				hash ^= x * unchecked((long)0x9E3779B97F4A7C15UL);
				hash ^= y * unchecked((long)0xC2B2AE3D27D4EB4FUL);
				return hash;
			}
		}
	}
}
