using Godot;
using System.Collections.Generic;
using System.Linq;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;
using RTS.World;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
	// 科技效果统一入口：效果全部按 TechConfig 字段驱动，
	// 新增科技只需填配置表（目标 tag + 效果数值），无需再写分支代码。
	public static class TechEffects
	{
		// 防御科技只作用于纳米巨兽与炮台（按设计文档）
		private static readonly string[] NanoTurretNames =
		{
			"NanoTurret", "NanoSniper", "NanoAA", "NanoActiveTower", "NanoSmokeTower"
		};

		public static void ApplyToPlayer(Player player)
		{
			if (player?.PlayerData == null || SimManager.Instance?.World == null)
				return;

			foreach (var sim in SimManager.Instance.World.Units.Values)
			{
				var node = SimManager.Instance.FindEntityById(sim.ID);
				if (node != null && node.TeamID == player.TeamId)
					ApplyToEntity(node, player.PlayerData);
			}

			foreach (var sim in SimManager.Instance.World.Structures.Values)
			{
				var node = SimManager.Instance.FindEntityById(sim.ID);
				if (node != null && node.TeamID == player.TeamId)
					ApplyToEntity(node, player.PlayerData);
			}
		}

		// 新实体生成时调用（单位/建筑都走 EntitySpawner）
		public static void ApplyToEntity(IEntity entity, PlayerData pd)
		{
			if (entity?.LogicEntity == null || pd == null)
				return;

			ApplyGenericTechs(entity, pd);

			bool isNanoBehemoth = entity is Unit u && u.UnitName == "NanoBehemoth";
			bool isNanoTurret = entity is Structure s &&
				System.Array.IndexOf(NanoTurretNames, s.StructureName) >= 0;

			if (!isNanoBehemoth && !isNanoTurret)
				return;

			// 纳米防御科技（血肉/抗性）：按设计只作用于巨兽与炮台
			foreach (string techId in SortedTechs(pd))
			{
				var cfg = ConfigDatabase.GetTech(techId);
				if (cfg == null || (!HasResist(cfg) && cfg.HpBonusPercent <= 0f))
					continue;

				ApplyHpAndResistTech(entity, cfg);
			}

			if (isNanoBehemoth && entity.LogicEntity is SimUnit sim)
			{
				ApplyMobilityTechs(sim, pd);
				ApplyFortressWeapons((Unit)entity, pd);
			}
		}

		// 通用科技应用（联盟/恶魔/多足）：按目标 tag 与效果字段驱动。
		// 循环按科技 ID 排序，保证双端应用顺序确定。
		private static void ApplyGenericTechs(IEntity entity, PlayerData pd)
		{
			var logic = entity.LogicEntity;
			var unitCfg = ConfigDatabase.GetUnit(entity.DisplayName);
			var structCfg = unitCfg == null ? ConfigDatabase.GetStructure(entity.DisplayName) : null;

			bool isDemonUnit = unitCfg != null && unitCfg.Tags.Contains("Demon");
			bool isDemonBuilding = structCfg != null && structCfg.FieldRadius > 0;
			bool isDemon = isDemonUnit || isDemonBuilding;

			int fireBonus = 0;

			foreach (string techId in SortedTechs(pd))
			{
				var cfg = ConfigDatabase.GetTech(techId);
				if (cfg == null || !MatchesTargetTag(cfg.TargetTag, unitCfg, structCfg))
					continue;
				// 泰伦精确目标单位：只作用于指定单位 ID
				if (cfg.TargetUnitIds.Count > 0 && !cfg.TargetUnitIds.Contains(entity.DisplayName))
					continue;

				// 联盟数值科技：再生钢（机械回血）/ 流线型（飞行加速）/ 光学瞄具（生物射程）
				if (cfg.UnitHpRegenPerSecond > 0f && !logic.Buffs.HasBuff(techId + "_Regen"))
					logic.Buffs.AddBuff(
						techId + "_Regen", 1, FP.Zero,
						FP.One, FP.One, FP.Zero, FP.One,
						(FP)cfg.UnitHpRegenPerSecond, FP.Zero, -1);

				if (logic is SimUnit sim && cfg.MoveSpeedMultiplier != 1f && !logic.Buffs.HasBuff(techId + "_Speed"))
					logic.Buffs.AddBuff(
						techId + "_Speed", 1, FP.Zero,
						FP.One, FP.One, FP.Zero, (FP)cfg.MoveSpeedMultiplier,
						FP.Zero, FP.Zero, -1);

				if (cfg.AttackRangeBonus != 0f && !logic.Buffs.HasBuff(techId + "_Range"))
					logic.Buffs.AddStatBuff(
						techId + "_Range", 1, FP.Zero,
						FP.One, (FP)cfg.AttackRangeBonus, FP.Zero, -1);

				// 洞穴科技：被动攻速倍率（按精确目标单位）
				if (cfg.AttackSpeedMultiplier != 1f && logic is SimUnit asUnit &&
					!logic.Buffs.HasBuff(techId + "_AS"))
					logic.Buffs.AddStatBuff(
						techId + "_AS", 1, FP.Zero,
						(FP)cfg.AttackSpeedMultiplier, FP.Zero, FP.Zero, -1);

				// 洞穴科技：固定生命加成
				if (cfg.FlatHpBonus > 0)
					ApplyFlatHpBonus(entity, cfg, techId + "_FlatHp");

				// 洞穴科技：固定攻击力加成
				if (cfg.FlatDamageBonus > 0 && !logic.Buffs.HasBuff(techId + "_FlatDmg"))
					logic.Buffs.AddFlatDamageBuff(
						techId + "_FlatDmg", 1, FP.Zero, (FP)cfg.FlatDamageBonus, -1);

				// 植物科技：建筑生命加成（纤维装甲）
				if (cfg.StructureHpBonusPercent > 0f && entity is Structure structEntity)
					ApplyHpBonus(entity, cfg, techId + "_StructHp");

				// 植物科技：菌毯蔓延半径/速度升级（菌毯增殖）
				if (cfg.CreepSpreadRadiusBonus > 0 && entity is Structure spreadStruct &&
					spreadStruct.CreepModule != null)
					spreadStruct.CreepModule.ApplyUpgrade(
						cfg.CreepSpreadRadiusBonus, cfg.CreepSpreadTimeMultiplier);

				// 植物科技：命中减速（粘液蛛网）——修改武器配置，双端确定性一致
				if (cfg.HitSlowMoveMultiplier < 1f && unitCfg != null)
				{
					foreach (string wid in unitCfg.WeaponIds)
					{
						var wcfg = ConfigDatabase.GetWeapon(wid);
						if (wcfg != null)
							wcfg.ImpactSlowMoveMultiplier = cfg.HitSlowMoveMultiplier;
					}
				}

				// 科技追加武器（洞穴民兵化等）：显式指定目标单位才走通用授予，
				// 纳米要塞化仍走专门的 ApplyFortressWeapons（全单位 tag 为空）。
				if (cfg.GrantWeaponIds.Count > 0 && cfg.GrantWeaponCount > 0 &&
					cfg.TargetUnitIds.Count > 0 && entity is Unit grantUnit &&
					!logic.Buffs.HasBuff(techId + "_Guns"))
					GrantTechWeapons(grantUnit, logic, techId, cfg);

				// 恶魔科技：血肉 / 烈火·灵魂火 / 急行军 / 地狱扩散
				if (isDemon)
				{
					if (cfg.HpBonusPercent > 0f && isDemonUnit)
						ApplyHpBonus(entity, cfg, techId + "_Hp");

					fireBonus += cfg.FireDamageBonus;

					if (cfg.FieldMoveSpeedBonus > 0f && logic is SimUnit su)
						su.FieldSpeedMultiplier = (FP)(1f + cfg.FieldMoveSpeedBonus);

					if (cfg.FieldRadiusBonus > 0)
					{
						if (logic is SimUnit su2)
							su2.FieldRadius = (unitCfg?.FieldRadius ?? 0) + cfg.FieldRadiusBonus;
						else if (logic is SimStructure ss)
							ss.FieldRadius = (structCfg?.FieldRadius ?? 0) + cfg.FieldRadiusBonus;
					}
				}

				// 多足：采集自动化（无需控制 + 视野）
				if (cfg.GrantNoControlNeeded && unitCfg != null)
				{
					if (logic is SimUnit nsu)
						nsu.NoControlNeeded = true;

					if (entity is Unit nu && nu.VisionRange < 256f && cfg.VisionBonus > 0f)
						nu.VisionRange += cfg.VisionBonus;
				}

				// 多足：针对防御（可循环，每级 +1 固定防御）
				if (cfg.DefenseType >= 0 && unitCfg != null)
					ApplyRepeatableDefense(entity, pd, techId, cfg.DefenseType);

				// =================================================
				// 泰伦科技
				// =================================================
				if (logic is SimUnit terranUnit)
				{
					if (cfg.GrantKnockbackAttacks)
					{
						terranUnit.HasKnockbackAttacks = true;
						terranUnit.KnockbackTiles = (FP)cfg.KnockbackTiles;
					}

					if (cfg.DeployDamageReduction > 0f)
						terranUnit.DeployDamageReduction = (FP)cfg.DeployDamageReduction;

					if (cfg.DeployTimeMultiplier != 1f)
						terranUnit.DeployTimeMultiplier = (FP)cfg.DeployTimeMultiplier;

					if ((cfg.AmmoCapMultiplier > 1f || cfg.AmmoRefundOnKill) && terranUnit.MaxAmmo > FP.Zero)
					{
						if (!logic.Buffs.HasBuff(techId + "_Ammo"))
						{
							logic.Buffs.AddBuff(techId + "_Ammo", 1, FP.Zero, FP.One, FP.One, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);
							terranUnit.MaxAmmo = terranUnit.BaseMaxAmmo * (FP)cfg.AmmoCapMultiplier;
							if (terranUnit.Ammo > terranUnit.MaxAmmo)
								terranUnit.Ammo = terranUnit.MaxAmmo;
						}

						terranUnit.AmmoRefundOnKill |= cfg.AmmoRefundOnKill;
					}

					if (cfg.AoeRadiusBonusTiles > 0f)
						terranUnit.AoeRadiusBonus += (FP)(cfg.AoeRadiusBonusTiles * 64f);

					if (cfg.AllowStructureTargeting)
						terranUnit.CanHitStructuresOverride = true;

					if (cfg.BonusDamageVsStructure > 0)
						terranUnit.BonusDamageVsStructure = (FP)cfg.BonusDamageVsStructure;
				}
			}

			// 烈火 / 灵魂火：聚合为单一固定火焰伤害加成
			if (fireBonus > 0 && isDemonUnit && !logic.Buffs.HasBuff("DemonTech_FireBonus"))
				logic.Buffs.AddFlatDamageBuff("DemonTech_FireBonus", fireBonus, FP.Zero, (FP)fireBonus, -1);
		}

		private static IEnumerable<string> SortedTechs(PlayerData pd)
		{
			return pd.ResearchedTechs.OrderBy(id => id, System.StringComparer.Ordinal);
		}

		private static bool MatchesTargetTag(string tag, UnitConfig unitCfg, StructureConfig structCfg)
		{
			if (string.IsNullOrEmpty(tag))
				return true;

			if (unitCfg != null)
			{
				if (tag == "Air")
					return unitCfg.IsAir;

				return unitCfg.Tags.Contains(tag);
			}

			// 建筑没有 Tags：恶魔建筑靠立场半径识别（地狱扩散）
			return tag == "Demon" && structCfg != null && structCfg.FieldRadius > 0;
		}

		private static bool HasResist(TechConfig cfg)
		{
			return cfg.KineticResist > 0f || cfg.ThermalResist > 0f ||
				cfg.ExplosiveResist > 0f || cfg.EmResist > 0f || cfg.BeamResist > 0f;
		}

		private static void ApplyHpAndResistTech(IEntity entity, TechConfig cfg)
		{
			var logic = entity.LogicEntity;
			string techId = cfg.TechId;

			if (cfg.HpBonusPercent > 0f)
				ApplyHpBonus(entity, cfg, techId + "_Hp");

			if (HasResist(cfg) && !logic.Buffs.HasBuff(techId + "_Resist"))
			{
				logic.Buffs.AddResistBuff(
					techId + "_Resist",
					1,
					FP.Zero,
					(FP)(1f - cfg.KineticResist),
					(FP)(1f - cfg.ThermalResist),
					(FP)(1f - cfg.ExplosiveResist),
					(FP)(1f - cfg.EmResist),
					(FP)(1f - cfg.BeamResist),
					-1
				);
			}
		}

		private static void ApplyHpBonus(IEntity entity, TechConfig cfg, string buffId)
		{
			var logic = entity.LogicEntity;
			if (logic.Buffs.HasBuff(buffId))
				return;

			FP bonus = (FP)cfg.HpBonusPercent;
			FP oldMax = logic.MaxHp;
			logic.MaxHp = oldMax + oldMax * bonus;
			logic.Hp += oldMax * bonus;

			if (logic.Hp > logic.MaxHp)
				logic.Hp = logic.MaxHp;

			// 标记 Buff 保证幂等（已加过就不再重复加）
			logic.Buffs.AddBuff(buffId, 1, FP.Zero, FP.One, FP.One, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);

			// 同步表现层最大血量（血条/治疗量读取）
			if (entity is Unit un && un.LifeModule != null)
				un.LifeModule.MaxHp = (float)logic.MaxHp;
			else if (entity is Structure st && st.LifeModule != null)
				st.LifeModule.MaxHp = (float)logic.MaxHp;
		}

		// 洞穴科技：固定生命加成（与百分比加成同幂等机制）
		private static void ApplyFlatHpBonus(IEntity entity, TechConfig cfg, string buffId)
		{
			var logic = entity.LogicEntity;
			if (logic.Buffs.HasBuff(buffId))
				return;

			FP bonus = (FP)cfg.FlatHpBonus;
			FP oldMax = logic.MaxHp;
			logic.MaxHp = oldMax + bonus;
			logic.Hp += bonus;

			if (logic.Hp > logic.MaxHp)
				logic.Hp = logic.MaxHp;

			logic.Buffs.AddBuff(buffId, 1, FP.Zero, FP.One, FP.One, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);

			if (entity is Unit un && un.LifeModule != null)
				un.LifeModule.MaxHp = (float)logic.MaxHp;
			else if (entity is Structure st && st.LifeModule != null)
				st.LifeModule.MaxHp = (float)logic.MaxHp;
		}

		// 通用科技武器授予：给指定单位挂载配置武器（与纳米要塞化同机制）
		private static void GrantTechWeapons(Unit unit, SimEntity logic, string techId, TechConfig cfg)
		{
			logic.Buffs.AddBuff(techId + "_Guns", 1, FP.Zero, FP.One, FP.One, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);

			var mount = unit.GetNodeOrNull<Node3D>("Visuals/WeaponMount");
			if (mount == null || unit.CombatModule == null)
				return;

			int index = 0;
			for (int i = 0; i < cfg.GrantWeaponCount; i++)
			{
				foreach (string weaponId in cfg.GrantWeaponIds)
				{
					var weapon = EntityFactory3D.CreateWeaponNode(weaponId);
					weapon.Position = new Vector3(index * 30f, 30f, 0f);
					mount.AddChild(weapon);
					unit.CombatModule.AddWeapon(weapon);
					index++;
				}
			}
		}

		// 多足：针对防御（可循环，每级 +1 固定防御）
		private static void ApplyRepeatableDefense(IEntity entity, PlayerData pd, string techId, int dmgType)
		{
			int level = pd.GetTechLevel(techId);
			if (level <= 0)
				return;

			var logic = entity.LogicEntity;
			var buff = logic.Buffs.GetBuff(techId + "_Def");

			if (buff != null)
			{
				if (buff.Stacks != level)
				{
					logic.Defenses.TryGetValue(dmgType, out FP current);
					logic.Defenses[dmgType] = current + (FP)(level - buff.Stacks);
					buff.Stacks = level;
				}

				return;
			}

			logic.Defenses.TryGetValue(dmgType, out FP baseVal);
			logic.Defenses[dmgType] = baseVal + (FP)level;
			logic.Buffs.AddBuff(techId + "_Def", level, FP.Zero, FP.One, FP.One, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);
		}

		// 恶魔：大裂隙自动生产间隔倍率（科技连乘）
		public static float GetAutoProduceIntervalMultiplier(PlayerData pd)
		{
			float value = 1f;

			if (pd == null)
				return value;

			foreach (string techId in pd.ResearchedTechs)
			{
				var cfg = ConfigDatabase.GetTech(techId);
				if (cfg != null)
					value *= cfg.AutoProduceIntervalMultiplier;
			}

			return value;
		}

		private static void ApplyMobilityTechs(SimUnit sim, PlayerData pd)
		{
			var mobilityA = ConfigDatabase.GetTech("NanoTech_MobilityA");
			var mobilityB = ConfigDatabase.GetTech("NanoTech_MobilityB");
			var advanced = ConfigDatabase.GetTech("NanoTech_AdvancedHarvester");

			if (pd.HasTech("NanoTech_MobilityA") && mobilityA != null)
				sim.SelfCreepRadius = mobilityA.SelfCreepRadius;

			if (pd.HasTech("NanoTech_MobilityB") && mobilityB != null)
				sim.SpeedOnCreepMultiplier = (FP)mobilityB.SpeedOnCreepMultiplier;

			if (pd.HasTech("NanoTech_AdvancedHarvester") && advanced != null)
				sim.BehemothCanHarvest = advanced.BehemothCanHarvest;
		}

		// 战斗A 要塞化：给巨兽追加配置的加强火炮/防空飞弹
		private static void ApplyFortressWeapons(Unit behemoth, PlayerData pd)
		{
			if (!pd.HasTech("NanoTech_CombatA"))
				return;

			var cfg = ConfigDatabase.GetTech("NanoTech_CombatA");
			if (cfg == null || cfg.GrantWeaponIds.Count == 0 || cfg.GrantWeaponCount <= 0)
				return;

			var sim = behemoth.LogicEntity;
			if (sim == null || sim.Buffs.HasBuff("NanoTech_CombatA_Guns"))
				return;

			// 幂等标记：重复应用不会重复加武器
			sim.Buffs.AddBuff("NanoTech_CombatA_Guns", 1, FP.Zero, FP.One, FP.One, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);

			var mount = behemoth.GetNodeOrNull<Node3D>("Visuals/WeaponMount");
			if (mount == null || behemoth.CombatModule == null)
				return;

			int total = cfg.GrantWeaponIds.Count * cfg.GrantWeaponCount;
			int index = 0;

			for (int i = 0; i < cfg.GrantWeaponIds.Count; i++)
			{
				for (int k = 0; k < cfg.GrantWeaponCount; k++)
				{
					float angle = index * Mathf.Pi * 2f / total;
					var weapon = EntityFactory3D.CreateWeaponNode(cfg.GrantWeaponIds[i]);
					weapon.Position = new Vector3(Mathf.Cos(angle) * 105f, 150f, Mathf.Sin(angle) * 105f);
					mount.AddChild(weapon);
					behemoth.CombatModule.AddWeapon(weapon);
					index++;
				}
			}
		}

		// 经济倍率：按已研究科技连乘（数值全在配置表）
		public static float GetCreepIncomeMultiplier(PlayerData pd) =>
			GetMultiplier(pd, t => t.CreepIncomeMultiplier);

		public static float GetSpreadCostMultiplier(PlayerData pd) =>
			GetMultiplier(pd, t => t.SpreadCostMultiplier);

		public static float GetSpreadCooldownMultiplier(PlayerData pd) =>
			GetMultiplier(pd, t => t.SpreadCooldownMultiplier);

		public static float GetHarvesterIncomeMultiplier(PlayerData pd) =>
			GetMultiplier(pd, t => t.HarvesterIncomeMultiplier);

		public static float GetProductionTimeMultiplier(PlayerData pd) =>
			GetMultiplier(pd, t => t.ProductionTimeMultiplier);

		// 洞穴智者驻扎：科技建筑内驻扎 → 研究速度 ×1.5；生产建筑内驻扎 → 造价 ×0.8
		public static float GetResearchSpeedMultiplier(PlayerData pd)
		{
			if (pd == null)
				return 1f;
			return HasGarrisonedCaveBuilding(pd, true) ? 1.5f : 1f;
		}

		public static float GetProductionCostMultiplier(PlayerData pd)
		{
			if (pd == null)
				return 1f;
			return HasGarrisonedCaveBuilding(pd, false) ? 0.8f : 1f;
		}

		// 植物汲取：攻击吸血比例（已研究科技连加）
		public static float GetLifestealPercent(PlayerData pd)
		{
			if (pd == null)
				return 0f;
			float total = 0f;
			foreach (string techId in pd.ResearchedTechs)
			{
				var cfg = ConfigDatabase.GetTech(techId);
				if (cfg != null)
					total += cfg.LifestealPercent;
			}
			return total;
		}

		private static bool HasGarrisonedCaveBuilding(PlayerData pd, bool tech)
		{
			var world = SimManager.Instance?.World;
			if (world == null || pd?.GetParent<Player>() == null)
				return false;

			int team = pd.GetParent<Player>().TeamId;
			foreach (var sim in world.Structures.Values)
			{
				if (sim == null || sim.IsDead || sim.TeamID != team ||
					sim.CurrentState != SimStructure.StructureState.Active ||
					sim.GarrisonedCount <= 0)
					continue;

				var cfg = ConfigDatabase.GetStructure(sim.StructureTypeId);
				if (cfg == null)
					continue;
				if (tech && (cfg.IsTechBuilding || cfg.ResearchableTechIds.Count > 0))
					return true;
				if (!tech && cfg.TrainableUnitIds.Count > 0)
					return true;
			}
			return false;
		}

		private static float GetMultiplier(PlayerData pd, System.Func<TechConfig, float> selector)
		{
			float value = 1f;

			foreach (string techId in pd.ResearchedTechs)
			{
				var cfg = ConfigDatabase.GetTech(techId);
				if (cfg != null)
					value *= selector(cfg);
			}

			return value;
		}
	}
}
