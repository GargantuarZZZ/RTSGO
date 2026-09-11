using Godot;
using System.Text;
using RTS.Data.Configs;

namespace RTS.Data
{
	/// <summary>
	/// 单位战斗力评分：综合 DPS、有效生命、射程、移速、技能与特殊机制，
	/// 全部从配置表读取（数据驱动），用于单位卡展示、AI 选兵/估价。
	/// </summary>
	public static class CombatRating
	{
		// 命中率修正：短射程/近战命中差，远射程稳定
		private static float HitRate(float rangeTiles)
			=> Mathf.Clamp(0.55f + rangeTiles * 0.04f, 0.55f, 0.95f);

		// 单把武器纸面 DPS：伤害×攻速 + 命中率 + 锥形/AOE/足迹修正。
		// 单位评分与建筑评分共用同一公式，调整伤害口径只改这一处。
		private static float WeaponDps(WeaponConfig w, bool applyHitRate)
		{
			float wdps = RawWeaponDps(w);
			if (applyHitRate)
				wdps *= HitRate(w.AttackRange / 64f);
			if (w.ConeAngleDegrees > 0f)
				wdps *= 1f + w.ConeAngleDegrees / 180f;
			if (w.HasAreaDamage && w.AreaRadius > 0f)
				wdps *= 1f + Mathf.Min(w.AreaRadius / 192f, 2f);
			if (w.FootprintScaledDamage)
				wdps *= 1.5f;
			return wdps;
		}

		// 单把武器裸 DPS：只算伤害×攻速，不含命中率/AOE/锥形（报表“纸面 DPS”口径）
		private static float RawWeaponDps(WeaponConfig w)
		{
			float rate = 20f / Mathf.Max(1, w.CooldownTicks);
			return w.Damage * rate + w.BonusDamage * 0.5f * rate;
		}

		/// <summary>估算单个单位战斗力（100 ≈ 标准战斗单位）。</summary>
		public static float ComputeUnitRating(UnitConfig cfg)
		{
			if (cfg == null)
				return 0f;

			// 纯辅助单位（无武器，靠治疗/维修）：用支援能力折算，不走战斗公式
			bool supportOnly = cfg.WeaponIds.Count == 0 && (cfg.CanHeal || cfg.CanRepair || cfg.EnergyRegenInAmmoRange);
			if (supportOnly)
			{
				float support = (cfg.CanHeal ? cfg.HealPerSecond : 0f)
					+ (cfg.CanRepair ? cfg.RepairPerSecond * 0.3f : 0f)
					+ (cfg.EnergyRegenInAmmoRange ? 8f : 0f);
				return Mathf.Round(15f + support * 1.5f);
			}

			// 1. 有效生命：HP + 护盾，护甲减伤修正，架设血量加成折半计入
			float ehp = cfg.MaxHp + cfg.MaxShield;
			float armorMult = 1f + (cfg.DefKinetic + cfg.DefThermal + cfg.DefExplosive + cfg.DefEM + cfg.DefBeam) * 0.05f;
			ehp *= armorMult;
			if (cfg.DeployedMaxHpMultiplier > 1f)
				ehp *= 1f + (cfg.DeployedMaxHpMultiplier - 1f) * 0.6f;

			// 2. DPS：全部武器，含额外伤害/AOE/扇形/多目标/命中率
			float dps = 0f;
			float maxRange = 0f;
			bool canAir = false;
			bool canGround = false;
			dps += PlasmaSkillDps(cfg);
			if (cfg.MultiShotTargets > 1)
			{
				// 齐射模式（MultiShot>1）：一轮打 N 发，武器循环取炮，
				// 发数 = MultiShotTargets，单发伤害按主武器（避免“多炮管×多目标”双重计数）
				var main = cfg.WeaponIds.Count > 0 ? ConfigDatabase.GetWeapon(cfg.WeaponIds[0]) : null;
				if (main != null)
				{
					float wdps = WeaponDps(main, true) * cfg.MultiShotTargets;
					if (main.CanTargetAir)
						canAir = true;
					if (main.CanTargetGround)
						canGround = true;
					dps += wdps;
					maxRange = main.AttackRange;
				}
			}
			else
			{
				// 普通模式：每把武器独立开火，DPS 累加
				foreach (string wid in cfg.WeaponIds)
				{
					var w = ConfigDatabase.GetWeapon(wid);
					if (w == null)
						continue;
					float wdps = WeaponDps(w, true);
					if (w.CanTargetAir)
						canAir = true;
					if (w.CanTargetGround)
						canGround = true;
					dps += wdps;
					maxRange = Mathf.Max(maxRange, w.AttackRange);
				}
			}
			if (canAir != canGround && (canAir || canGround))
				dps *= 0.95f;

			// 3. 射程系数（含架设射程加成折半）
			float rangeMult = 1f + (maxRange / 64f) * 0.06f;
			if (cfg.CanDeploy && cfg.DeployedAttackRangeBonusTiles > 0f)
				rangeMult += cfg.DeployedAttackRangeBonusTiles * 0.03f;

			// 4. 移速系数（风筝/走位）
			float speedMult = 1f + (cfg.MoveSpeed - 60f) / 400f;

			// 5. 技能与特殊机制
			float skillBonus = 0f;
			if (cfg.HeroEnergyMax > 0f) skillBonus += 0.35f;
			if (cfg.Skill1Cost > 0f) skillBonus += 0.15f;
			if (cfg.Skill2Cost > 0f || cfg.Skill2Damage > 0f) skillBonus += 0.20f;
			if (cfg.HasLeap) skillBonus += 0.15f;
			if (cfg.CanHeal) skillBonus += 0.25f;
			if (cfg.CanRepair) skillBonus += 0.15f;
			if (cfg.IsAir) skillBonus += 0.15f;
			if (cfg.CanDeploy) skillBonus += 0.10f;
			if (cfg.CanSwitchAmmoMode) skillBonus += 0.10f;
			if (cfg.MaxAmmo > 0) skillBonus -= 0.10f; // 泰伦弹药限制

			// 6. 几何加权综合：100 ≈ 标准步兵（步枪兵）
			float score = Mathf.Pow(Mathf.Max(ehp, 1f), 0.45f)
				* Mathf.Pow(Mathf.Max(dps, 0.01f), 0.7f)
				* rangeMult * speedMult * (1f + skillBonus);
			score *= 3.45f;
			if (cfg.IsWorker || (cfg.CanHarvest && !cfg.IsHero))
				score *= 0.25f; // 经济单位战斗力打折
			return Mathf.Round(score);
		}

		/// <summary>建筑战力（中立塔/防御塔）：EHP×DPS×射程，口径与单位一致。</summary>
		public static float ComputeStructureRating(StructureConfig cfg)
		{
			if (cfg == null)
				return 0f;
			float ehp = cfg.MaxHp + cfg.MaxShield;
			float armorMult = 1f + (cfg.DefKinetic + cfg.DefThermal + cfg.DefExplosive + cfg.DefEM + cfg.DefBeam) * 0.05f;
			ehp *= armorMult;

			float dps = 0f;
			float maxRange = 0f;
			foreach (string wid in cfg.WeaponIds)
			{
				var w = ConfigDatabase.GetWeapon(wid);
				if (w == null)
					continue;
				float wdps = WeaponDps(w, true);
				dps += wdps;
				maxRange = Mathf.Max(maxRange, w.AttackRange);
			}

			float score = Mathf.Pow(Mathf.Max(ehp, 1f), 0.45f)
				* Mathf.Pow(Mathf.Max(dps, 0.01f), 0.7f)
				* (1f + (maxRange / 64f) * 0.06f)
				* 3.45f;
			return Mathf.Round(score);
		}

		/// <summary>全单位评分表（控制台/演示用），按分数降序。</summary>
		public static string BuildRatingTable()
		{
			var sb = new StringBuilder();
			var list = new System.Collections.Generic.List<(string Id, float Dps, float Score)>();
			foreach (var kv in ConfigDatabase.GetAllUnits())
			{
				var u = kv.Value;
				if (u == null)
					continue;
				list.Add((kv.Key, ComputeRawDps(u), ComputeUnitRating(u)));
			}
			list.Sort((a, b) => b.Score.CompareTo(a.Score));
			sb.AppendLine("== 单位 DPS / 战斗力评分 ==");
			sb.AppendLine($"{"单位",-22} {"DPS",8} {"战力",7}");
			foreach (var (id, dps, score) in list)
				sb.AppendLine($"{id,-22} {dps,8:F1} {score,7}");
			return sb.ToString();
		}

		/// <summary>纸面总 DPS：武器伤害×攻速之和（多炮管去重、多目标开方），不含命中率/AOE 修正。</summary>
		public static float ComputeRawDps(UnitConfig cfg)
		{
			if (cfg == null)
				return 0f;
			float plasma = PlasmaSkillDps(cfg);
			if (cfg.MultiShotTargets > 1)
			{
				var main = cfg.WeaponIds.Count > 0 ? ConfigDatabase.GetWeapon(cfg.WeaponIds[0]) : null;
				if (main == null)
					return 0f;
				return (main.Damage + main.BonusDamage * 0.5f)
					* cfg.MultiShotTargets
					* (20f / Mathf.Max(1, main.CooldownTicks))
					+ plasma;
			}
			float dps = 0f;
			foreach (string wid in cfg.WeaponIds)
			{
				var w = ConfigDatabase.GetWeapon(wid);
				if (w == null)
					continue;
				dps += RawWeaponDps(w);
			}
			if (cfg.MultiShotTargets > 1)
				dps *= cfg.MultiShotTargets;
			return dps + plasma;
		}

		// 电浆炮技能（PlasmaArtillery）：基础伤害 + 长宽²伤害（按平均 2×2 目标折算）÷ 完整循环
		private static float PlasmaSkillDps(UnitConfig cfg)
		{
			if (cfg.PlasmaCooldownSeconds <= 0f)
				return 0f;
			var pa = ConfigDatabase.GetWeapon("PlasmaArtillery");
			if (pa == null)
				return 0f;
			float cycle = Mathf.Max(1f, cfg.PlasmaWindupSeconds + cfg.PlasmaRecoverySeconds + cfg.PlasmaCooldownSeconds);
			return (pa.Damage + pa.ImpactDamagePerFootprintSq * 4f) / cycle;
		}
	}
}
