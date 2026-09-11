using FixMath.NET;
using FP = FixMath.NET.Fix64;
using System.Collections.Generic;

namespace RTS.Simulation
{
	// 运行时 Buff 实例（纯逻辑、确定性数据）
	public class BuffInstance
	{
		public string BuffId;
		public int Stacks;
		public int MaxStacks = 1;
		public FP RemainingTime;       // <=0 表示永久
		public FP TotalDuration;       // 供 UI 显示 Buff 条比例（<=0 表示永久，不进状态哈希）
		public int SourceEntityId = -1;

		// 数值修改器（由 BuffConfig 展开传入）
		public FP DamageMultiplier = FP.One;
		public FP IncomingDamageMultiplier = FP.One;
		public FP ArmorBonus;
		public FP MoveSpeedMultiplier = FP.One;
		public FP HpRegenPerSecond;
		public FP ShieldRegenPerSecond;

		// 按伤害类型抗性（1 = 无抗性，0.75 = 减免 25%）
		public FP KineticResistMultiplier = FP.One;
		public FP ThermalResistMultiplier = FP.One;
		public FP ExplosiveResistMultiplier = FP.One;
		public FP EmResistMultiplier = FP.One;
		public FP BeamResistMultiplier = FP.One;

		// 攻速倍率 / 射程修正（光环用；射程修正可为负）
		public FP AttackSpeedMultiplier = FP.One;
		public FP AttackRangeBonus = FP.Zero;
		// 固定伤害加成（恶魔烈火/灵魂火）
		public FP FlatDamageBonus = FP.Zero;

		public long GetStateHash()
		{
			long hash = 1469598103934665603L;

			void Mix(long value)
			{
				unchecked
				{
					hash ^= value;
					hash *= 1099511628211L;
				}
			}

			if (BuffId != null)
			{
				foreach (char c in BuffId)
					Mix(c);
			}

			Mix(Stacks);
			Mix((long)(RemainingTime * (FP)100m));
			Mix(SourceEntityId);
			Mix((long)(DamageMultiplier * (FP)1000m));
			Mix((long)(IncomingDamageMultiplier * (FP)1000m));
			Mix((long)(ArmorBonus * (FP)100m));
			Mix((long)(MoveSpeedMultiplier * (FP)1000m));
			Mix((long)(KineticResistMultiplier * (FP)1000m));
			Mix((long)(ThermalResistMultiplier * (FP)1000m));
			Mix((long)(ExplosiveResistMultiplier * (FP)1000m));
			Mix((long)(EmResistMultiplier * (FP)1000m));
			Mix((long)(BeamResistMultiplier * (FP)1000m));
			Mix((long)(AttackSpeedMultiplier * (FP)1000m));
			Mix((long)(AttackRangeBonus * (FP)100m));
			Mix((long)(FlatDamageBonus * (FP)100m));
			return hash;
		}
	}

	// Buff 容器：挂在 SimEntity 上，负责叠加 / 到期 / 持续效果 / 修改器查询
	public class BuffContainer
	{
		private readonly SimEntity _owner;
		private readonly List<BuffInstance> _buffs = new();

		public IReadOnlyList<BuffInstance> Buffs => _buffs;

		public BuffContainer(SimEntity owner)
		{
			_owner = owner;
		}

		public void AddBuff(
			string buffId,
			int stacks,
			FP duration,
			FP damageMultiplier,
			FP incomingDamageMultiplier,
			FP armorBonus,
			FP moveSpeedMultiplier,
			FP hpRegenPerSecond,
			FP shieldRegenPerSecond,
			int sourceId,
			int maxStacks = 1)
		{
			if (string.IsNullOrEmpty(buffId) || stacks <= 0)
				return;

			// 同 ID 叠层：刷新持续时间取较长的
			foreach (var existing in _buffs)
			{
				if (existing.BuffId == buffId)
				{
					existing.Stacks = System.Math.Min(existing.Stacks + stacks, existing.MaxStacks);

					if (duration > FP.Zero && (existing.RemainingTime <= FP.Zero || duration > existing.RemainingTime))
						existing.RemainingTime = duration;
					if (duration > FP.Zero && duration > existing.TotalDuration)
						existing.TotalDuration = duration;

					return;
				}
			}

			_buffs.Add(new BuffInstance
			{
				BuffId = buffId,
				Stacks = System.Math.Min(stacks, maxStacks),
				MaxStacks = maxStacks,
				RemainingTime = duration,
				TotalDuration = duration,
				SourceEntityId = sourceId,
				DamageMultiplier = damageMultiplier,
				IncomingDamageMultiplier = incomingDamageMultiplier,
				ArmorBonus = armorBonus,
				MoveSpeedMultiplier = moveSpeedMultiplier,
				HpRegenPerSecond = hpRegenPerSecond,
				ShieldRegenPerSecond = shieldRegenPerSecond
			});
		}

		public void RemoveBuff(string buffId)
		{
			_buffs.RemoveAll(b => b.BuffId == buffId);
		}

		// 按伤害类型抗性 Buff（科技/光环用，叠加时取各 Buff 相乘）
		public void AddResistBuff(
			string buffId,
			int stacks,
			FP duration,
			FP kinetic,
			FP thermal,
			FP explosive,
			FP em,
			FP beam,
			int sourceId)
		{
			if (string.IsNullOrEmpty(buffId) || stacks <= 0)
				return;

			foreach (var existing in _buffs)
			{
				if (existing.BuffId == buffId)
				{
					existing.Stacks = System.Math.Min(existing.Stacks + stacks, existing.MaxStacks);
					return;
				}
			}

			_buffs.Add(new BuffInstance
			{
				BuffId = buffId,
				Stacks = stacks,
				MaxStacks = stacks,
				RemainingTime = duration,
				TotalDuration = duration,
				SourceEntityId = sourceId,
				DamageMultiplier = FP.One,
				IncomingDamageMultiplier = FP.One,
				ArmorBonus = FP.Zero,
				MoveSpeedMultiplier = FP.One,
				HpRegenPerSecond = FP.Zero,
				ShieldRegenPerSecond = FP.Zero,
				KineticResistMultiplier = kinetic,
				ThermalResistMultiplier = thermal,
				ExplosiveResistMultiplier = explosive,
				EmResistMultiplier = em,
				BeamResistMultiplier = beam
			});
		}

		// 通用数值 Buff（光环/技能用）：攻速倍率与射程修正
		public void AddStatBuff(
			string buffId,
			int stacks,
			FP duration,
			FP attackSpeedMultiplier,
			FP attackRangeBonus,
			FP hpRegenPerSecond,
			int sourceId)
		{
			if (string.IsNullOrEmpty(buffId) || stacks <= 0)
				return;

			foreach (var existing in _buffs)
			{
				if (existing.BuffId == buffId)
				{
					existing.Stacks = System.Math.Min(existing.Stacks + stacks, existing.MaxStacks);

					if (duration > FP.Zero && (existing.RemainingTime <= FP.Zero || duration > existing.RemainingTime))
						existing.RemainingTime = duration;
					if (duration > FP.Zero && duration > existing.TotalDuration)
						existing.TotalDuration = duration;

					return;
				}
			}

			_buffs.Add(new BuffInstance
			{
				BuffId = buffId,
				Stacks = stacks,
				MaxStacks = stacks,
				RemainingTime = duration,
				TotalDuration = duration,
				SourceEntityId = sourceId,
				DamageMultiplier = FP.One,
				IncomingDamageMultiplier = FP.One,
				ArmorBonus = FP.Zero,
				MoveSpeedMultiplier = FP.One,
				HpRegenPerSecond = hpRegenPerSecond,
				ShieldRegenPerSecond = FP.Zero,
				AttackSpeedMultiplier = attackSpeedMultiplier,
				AttackRangeBonus = attackRangeBonus
			});
		}

		// 固定伤害加成 Buff（烈火/灵魂火）
		public void AddFlatDamageBuff(string buffId, int stacks, FP duration, FP flatBonus, int sourceId)
		{
			if (string.IsNullOrEmpty(buffId) || stacks <= 0)
				return;

			foreach (var existing in _buffs)
			{
				if (existing.BuffId == buffId)
				{
					existing.Stacks = System.Math.Min(existing.Stacks + stacks, existing.MaxStacks);
					return;
				}
			}

			_buffs.Add(new BuffInstance
			{
				BuffId = buffId,
				Stacks = stacks,
				MaxStacks = stacks,
				RemainingTime = duration,
				TotalDuration = duration,
				SourceEntityId = sourceId,
				FlatDamageBonus = flatBonus
			});
		}

		public FP GetFlatDamageBonus()
		{
			FP total = FP.Zero;

			foreach (var buff in _buffs)
				total += buff.FlatDamageBonus * (FP)buff.Stacks;

			return total;
		}

		public void RemoveAll()
		{
			_buffs.Clear();
		}

		public bool HasBuff(string buffId)
		{
			foreach (var b in _buffs)
			{
				if (b.BuffId == buffId)
					return true;
			}

			return false;
		}

		public BuffInstance GetBuff(string buffId)
		{
			foreach (var b in _buffs)
			{
				if (b.BuffId == buffId)
					return b;
			}

			return null;
		}

		public int GetStacks(string buffId)
		{
			foreach (var b in _buffs)
			{
				if (b.BuffId == buffId)
					return b.Stacks;
			}

			return 0;
		}

		// 每逻辑 Tick：到期清理 + 持续回血/回盾
		public void Tick(FP delta)
		{
			for (int i = _buffs.Count - 1; i >= 0; i--)
			{
				var buff = _buffs[i];

				if (buff.RemainingTime > FP.Zero)
				{
					buff.RemainingTime -= delta;

					if (buff.RemainingTime <= FP.Zero)
					{
						_buffs.RemoveAt(i);
						continue;
					}
				}

				if (_owner == null || _owner.IsDead)
					continue;

				FP regen = buff.HpRegenPerSecond * (FP)buff.Stacks * delta;
				if (regen != FP.Zero)
				{
					if (!_owner.CannotBeHealed)
					{
						_owner.Hp += regen;
						if (_owner.Hp > _owner.MaxHp)
							_owner.Hp = _owner.MaxHp;
					}
				}

				FP shieldRegen = buff.ShieldRegenPerSecond * (FP)buff.Stacks * delta;
				if (shieldRegen != FP.Zero)
				{
					if (!_owner.CannotBeHealed)
					{
						_owner.Shield += shieldRegen;
						if (_owner.Shield > _owner.MaxShield)
							_owner.Shield = _owner.MaxShield;
					}
				}
			}
		}

		// 修改器查询（按层数线性缩放：1 + (倍率-1) * 层数）
		public FP GetDamageMultiplier()
		{
			FP value = FP.One;
			foreach (var b in _buffs)
				value *= FP.One + (b.DamageMultiplier - FP.One) * (FP)b.Stacks;
			return value;
		}

		public FP GetIncomingDamageMultiplier()
		{
			FP value = FP.One;
			foreach (var b in _buffs)
				value *= FP.One + (b.IncomingDamageMultiplier - FP.One) * (FP)b.Stacks;
			return value;
		}

		public FP GetMoveSpeedMultiplier()
		{
			FP value = FP.One;
			foreach (var b in _buffs)
				value *= FP.One + (b.MoveSpeedMultiplier - FP.One) * (FP)b.Stacks;
			return value;
		}

		public FP GetArmorBonus()
		{
			FP value = FP.Zero;
			foreach (var b in _buffs)
				value += b.ArmorBonus * (FP)b.Stacks;
			return value;
		}

		public FP GetDamageResistMultiplier(int damageType)
		{
			FP value = FP.One;

			foreach (var b in _buffs)
			{
				FP mult = damageType switch
				{
					(int)RTS.Data.DamageType.Kinetic => b.KineticResistMultiplier,
					(int)RTS.Data.DamageType.Thermal => b.ThermalResistMultiplier,
					(int)RTS.Data.DamageType.Explosive => b.ExplosiveResistMultiplier,
					(int)RTS.Data.DamageType.EM => b.EmResistMultiplier,
					(int)RTS.Data.DamageType.Beam => b.BeamResistMultiplier,
					_ => FP.One
				};

				value *= FP.One + (mult - FP.One) * (FP)b.Stacks;
			}

			return value;
		}

		public FP GetAttackSpeedMultiplier()
		{
			FP value = FP.One;

			foreach (var b in _buffs)
				value *= FP.One + (b.AttackSpeedMultiplier - FP.One) * (FP)b.Stacks;

			return value;
		}

		public FP GetAttackRangeBonus()
		{
			FP value = FP.Zero;

			foreach (var b in _buffs)
				value += b.AttackRangeBonus * (FP)b.Stacks;

			return value;
		}

		public long GetStateHash()
		{
			long hash = 1469598103934665603L;

			foreach (var buff in _buffs)
			{
				unchecked
				{
					hash ^= buff.GetStateHash();
					hash *= 1099511628211L;
				}
			}

			return hash;
		}
	}
}
