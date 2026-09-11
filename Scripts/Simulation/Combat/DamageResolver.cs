using FixMath.NET;
using FP = FixMath.NET.Fix64;
using System.Collections.Generic;
using RTS.Data;

namespace RTS.Simulation
{
	// 伤害判定上下文：把“打谁、用什么打、附加参数”封装成可扩展结构
	public struct DamageContext
	{
		public int DamageType;      // DamageType 枚举
		public FP BaseDamage;       // 面板基础伤害
		public int BonusDamageType; // 针对的护甲类型（寿衣）
		public FP BonusDamage;      // 命中该寿衣时追加的伤害
		public FP Pierce;           // 护甲穿透（直接抵扣护甲值）
		public int SourceId;        // 攻击者逻辑实体 ID（查攻击者 Buff）
		public int TargetId;        // 目标逻辑实体 ID
		public bool CanCrit;        // 是否可暴击（留接口）
		public FP CritChance;       // 暴击率（留接口）
		public FP CritMultiplier;   // 暴击倍率（留接口）
	}

	// 攻防判定管线：
	// 基础伤害 -> 伤害类型 x 护甲类型修正 -> 护甲值(扣穿透) -> Buff 倍率 -> 下限 1
	// 自定义空间：RegisterTypeModifier 可注册任意 (伤害类型, 护甲类型) 修正规则
	public static class DamageResolver
	{
		private static readonly Dictionary<(int DamageType, int ArmorType), FP> _typeModifiers = new()
		{
			{ ((int)DamageType.Kinetic, (int)ArmorType.Biological), (FP)1.25m },
			{ ((int)DamageType.Kinetic, (int)ArmorType.Armored), (FP)0.85m },
			{ ((int)DamageType.Explosive, (int)ArmorType.Light), (FP)1.5m },
			{ ((int)DamageType.Explosive, (int)ArmorType.Armored), (FP)1.25m },
			{ ((int)DamageType.EM, (int)ArmorType.Light), (FP)1.5m },
			{ ((int)DamageType.EM, (int)ArmorType.Mechanical), (FP)2m },
			{ ((int)DamageType.Thermal, (int)ArmorType.Biological), (FP)1.5m },
			{ ((int)DamageType.Thermal, (int)ArmorType.Mechanical), (FP)0.8m },
			{ ((int)DamageType.Beam, (int)ArmorType.Structure), (FP)1.25m }
		};

		public static void RegisterTypeModifier(int damageType, int armorType, FP multiplier)
		{
			_typeModifiers[(damageType, armorType)] = multiplier;
		}

		public static FP GetTypeModifier(int damageType, int armorType)
		{
			return _typeModifiers.GetValueOrDefault((damageType, armorType), FP.One);
		}

		public static FP Resolve(DamageContext ctx, SimEntity target, SimEntity source)
		{
			FP damage = ctx.BaseDamage;

			// 0. 针对寿衣增伤（如“30+40(重甲)”）
			if (target != null && ctx.BonusDamage > FP.Zero && target.ArmorType == ctx.BonusDamageType)
				damage += ctx.BonusDamage;

			// 1. 伤害类型 x 护甲类型修正
			if (target != null)
				damage *= GetTypeModifier(ctx.DamageType, target.ArmorType);

			// 2. 护甲值（可被穿透抵扣）
			if (target != null && target.Defenses.TryGetValue(ctx.DamageType, out FP armor))
			{
				FP effectiveArmor = armor - ctx.Pierce;

				// Buff 护甲加成（如“增强护甲”）
				if (target.Buffs != null)
					effectiveArmor += target.Buffs.GetArmorBonus();

				if (effectiveArmor > FP.Zero)
					damage -= effectiveArmor;
			}

			// 3. Buff 倍率：攻击者“造成伤害” x 目标“受到伤害”
			if (source?.Buffs != null)
			{
				damage *= source.Buffs.GetDamageMultiplier();
				damage += source.Buffs.GetFlatDamageBonus();
			}

			if (target?.Buffs != null)
				damage *= target.Buffs.GetIncomingDamageMultiplier();

			// 3.5 按伤害类型抗性（防御科技/光环）
			if (target?.Buffs != null)
				damage *= target.Buffs.GetDamageResistMultiplier(ctx.DamageType);

			// 4. 暴击判定留给调用方（避免在解析器内引入随机数，保证确定性）
			//    ctx.CanCrit / CritChance / CritMultiplier 已预留字段

			// 5. 下限：至少 1 点（与旧行为一致）
			if (damage < FP.One)
				damage = FP.One;

			return damage;
		}
	}
}
