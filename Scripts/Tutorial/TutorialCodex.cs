using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using RTS.Data;
using RTS.Data.Configs;

namespace RTS.Tutorial
{
	// =========================================================
	// 教程图鉴生成器（进阶教程的数据来源）
	//
	// 核心原则：**不手写数值**。
	//
	// 进阶教程要讲"每个单位是什么、有什么机制、科技树怎么点"。
	// 如果把这些写死在教程文本里，改一次平衡（比如步枪兵血量 100→110）
	// 教程就变成错的，而且没人会记得同步。
	//
	// 所以这里直接从 RaceConfig 拿到该阵营的
	//     AvailableUnitIds / AvailableStructureIds / AvailableTechIds
	// 再逐个查 ConfigDatabase，用**实际配置字段**生成讲解文本：
	//
	//     UnitConfig.MaxHp / MoveSpeed / SupplyCost / Role / Tags
	//     UnitConfig.CannotBeHealed / LifespanSeconds / ControlRangeTiles / ...
	//     WeaponConfig.Damage / Range / DamageType
	//     TechConfig.DisplayName / Description / Category / Cost / ResearchTimeSeconds
	//
	// 结果：图鉴里的每个数字都等于游戏里真正生效的数字。
	// 只有"这个机制为什么重要"这类**设计意图**才由人工补写
	// （见 TutorialMechanics），因为那是配置里没有的信息。
	//
	// Godot 依赖：本文件引用 Resource 层，因此**只能从 UI/Data 侧访问**，
	// 绝不能从 Simulation 层调用（模拟层必须能在无 Godot 环境跑）。
	// =========================================================

	public static class TutorialCodex
	{
		/// <summary>ConfigDatabase 是否已经载入——没载入时生成的图鉴会是空的。</summary>
		public static bool Ready => ConfigDatabase.GetRace("Union") != null;

		// =========================================================
		// 挂载图鉴到教程定义
		// =========================================================

		/// <summary>
		/// 给每套进阶教程挂上完整图鉴：
		/// 阵营总览 → 经济来源 → 核心机制（人工） → 单位档案 → 建筑档案 → 科技树。
		///
		/// 为什么放在这里而不是 TutorialDefinitions：
		///   TutorialDefinitions.cs 会被无头测试工程编译（测试要验证教程地图），
		///   而测试工程没有 Godot 配置层。把 ConfigDatabase 依赖留在本文件，
		///   教程定义就能保持"只依赖地图数据"，测试工程照常编译。
		///
		/// 幂等：已经挂过的教程不会重复挂（按 Pages 是否为空判断）。
		/// </summary>
		public static void HydrateAll(IReadOnlyList<TutorialDefinition> defs)
		{
			if (defs == null) return;
			ConfigDatabase.LoadAll();

			foreach (var t in defs)
			{
				if (t == null || t.Tier != TutorialTier.Advanced) continue;
				if (t.Pages.Count > 0) continue; // 已挂载
				Hydrate(t);
			}
		}

		/// <summary>给单套进阶教程挂图鉴。</summary>
		public static void Hydrate(TutorialDefinition t)
		{
			if (t == null) return;

			// 1. 总览 + 经济来源（数据驱动）
			t.Pages.Add(BuildRacePage(t.RaceId));
			t.Pages.Add(BuildEconomyPage(t.RaceId));

			// 2. 核心机制（人工撰写：配置里没有"为什么"）
			foreach (var page in TutorialMechanics.BuildPages(t.RaceId))
				t.Pages.Add(page);

			// 3. 单位 / 建筑 / 科技（全部数据驱动）
			foreach (var page in BuildUnitPages(t.RaceId))
				t.Pages.Add(page);
			foreach (var page in BuildStructurePages(t.RaceId))
				t.Pages.Add(page);
			foreach (var page in BuildTechPages(t.RaceId))
				t.Pages.Add(page);
		}

		// =========================================================
		// 阵营总览
		// =========================================================

		/// <summary>
		/// 经济来源页（数据驱动）。
		///
		/// 各族的经济机制差别很大，而且**不像"工人采矿"那么直观**：
		///   联盟/泰伦/恶魔/洞穴：工人采集 + 交付点
		///   纳米：菌毯格数换纳米机器人（CarpetIncomeCellsPerResource）
		///   植物：菌毯格数换木材
		///   多足：工人采集，但"数据"资源只能靠击杀获得
		/// 这些规则光看 UI 是看不出来的，必须写进教程。
		/// </summary>
		public static TutorialPage BuildEconomyPage(string raceId)
		{
			var page = new TutorialPage { Section = "核心机制", Title = "经济来源" };
			var cfg = ConfigDatabase.GetRace(raceId);
			if (cfg == null)
			{
				page.Lines.Add($"(找不到阵营配置 '{raceId}')");
				return page;
			}

			// 1. 采集型工人（带具体产能与能采什么资源）
			bool hasHarvester = false;
			var workerLines = new List<string>();
			if (cfg.AvailableUnitIds != null)
			{
				foreach (string id in cfg.AvailableUnitIds)
				{
					var u = ConfigDatabase.GetUnit(id);
					if (u == null || !u.CanHarvest) continue;
					hasHarvester = true;

					var res = new List<string>();
					if (u.HarvestableResources != null)
						foreach (var r in u.HarvestableResources) res.Add(ResourceName(r));

					string line = $"· {EntityName(id)}：每 {u.HarvestCycleTicks / 20f:0.##} 秒采 " +
						$"{u.HarvestAmountPerCycle}，携带上限 {u.CarryCapacity}";
					if (res.Count > 0) line += $"，可采 {string.Join("/", res)}";
					if (u.AutoSubmitHarvest) line += "，自动提交（不用回建筑交付）";
					else line += "，需要送回交付点";
					workerLines.Add(line);
				}
			}

			// 2. 交付点（工人型经济的关键：资源要送回去才入库）
			var dropOffs = new List<string>();
			if (cfg.AvailableStructureIds != null)
			{
				foreach (string id in cfg.AvailableStructureIds)
				{
					var s = ConfigDatabase.GetStructure(id);
					if (s != null && s.IsDropOffPoint) dropOffs.Add(EntityName(id));
				}
			}

			// 3. 采集型建筑（范围自动采集）
			var harvestStructures = new List<string>();
			if (cfg.AvailableStructureIds != null)
			{
				foreach (string id in cfg.AvailableStructureIds)
				{
					var s = ConfigDatabase.GetStructure(id);
					if (s != null && s.AutoHarvestRadiusTiles > 0 && s.AutoHarvestPerSecondPerNode > 0f)
						harvestStructures.Add($"{EntityName(id)}（{s.AutoHarvestRadiusTiles} 格内自动采集，" +
							$"每点每秒 {s.AutoHarvestPerSecondPerNode:0.#}）");
				}
			}

			// 4. 菌毯经济
			bool creepEconomy = cfg.CarpetIncomeCellsPerResource > 0 &&
				raceId is "Nano" or "Plant";

			if (hasHarvester)
			{
				page.Lines.Add("采集单位：");
				foreach (string w in workerLines)
					page.Lines.Add(w);

				if (dropOffs.Count > 0)
					page.Lines.Add($"交付点：{string.Join("、", dropOffs)}（资源送到这里才算入库）");
			}

			if (harvestStructures.Count > 0)
			{
				page.Lines.Add("自动采集建筑（不用工人）：");
				foreach (string s in harvestStructures)
					page.Lines.Add("  · " + s);
			}

			// 5. 其他收入来源（护盾/能量等特殊项不写，只写真实的资源产出建筑）
			var income = new List<string>();
			if (cfg.AvailableStructureIds != null)
			{
				foreach (string id in cfg.AvailableStructureIds)
				{
					var s = ConfigDatabase.GetStructure(id);
					if (s == null || !s.ProvidesResourceIncome || s.IncomeAmountPerCycle <= 0) continue;
					income.Add($"{EntityName(id)} 每 {s.IncomeCycleTicks / 20f:0.##} 秒产 " +
						$"{s.IncomeAmountPerCycle} {ResourceName(s.IncomeResourceType)}");
				}
			}
			if (income.Count > 0)
			{
				page.Lines.Add("资源产出建筑：");
				foreach (string i in income)
					page.Lines.Add("  · " + i);
			}

			if (creepEconomy)
			{
				string resName = raceId == "Plant" ? "木材" : "纳米机器人";
				page.Lines.Add("★ 菌毯经济：不用工人采集。**菌毯铺到的格数**直接换资源——");
				page.Lines.Add($"  每 {cfg.CarpetIncomeCellsPerResource} 格菌毯每秒产出 1 {resName}。");
				page.Lines.Add("★ 所以对这类阵营来说，**铺菌毯就是搞经济**：菌毯越广，收入越高。");
				page.Lines.Add("  反过来，菌毯被对手清掉（清毯）就等于直接砍你的收入。");
			}

			if (!hasHarvester && harvestStructures.Count == 0 && !creepEconomy && income.Count == 0)
				page.Lines.Add("(该阵营没有配置采集单位或菌毯经济，请检查 RaceConfig)");

			// 6. 数据资源（多足专属：只能靠击杀）
			//    只对多足写这一条——别的族根本没有"数据"这条收入线，
			//    对所有族都打印会变成误导（早先就犯过这个错）。
			if (raceId == "Wanderer")
				page.Lines.Add("★「数据」资源无法采集，只能靠击杀敌方单位获得（多足的高级科技依赖它）。");

			// 7. 通用结论：把"钱从哪来"收成一句话，方便玩家记住
			page.Lines.Add("——");
			if (creepEconomy)
				page.Lines.Add("小结：收入 = 菌毯面积。扩张就是经济，被清毯就是掉收入。");
			else if (harvestStructures.Count > 0 && hasHarvester)
				page.Lines.Add("小结：工人负责早期节奏，采集建筑负责后期自动化；两者都要铺开。");
			else if (hasHarvester)
				page.Lines.Add("小结：收入 = 工人数量 × 采集效率。工人越多、往返越短，收入越高。");
			else
				page.Lines.Add("小结：该阵营经济不走常规工人路线，留意上面的特殊产出。");

			return page;
		}

		/// <summary>生成"阵营总览"页：定位数值 + 初始配置 + 人口上限。</summary>
		public static TutorialPage BuildRacePage(string raceId)		{
			var page = new TutorialPage { Section = "核心机制", Title = "阵营总览" };
			var cfg = ConfigDatabase.GetRace(raceId);
			if (cfg == null)
			{
				page.Lines.Add($"(找不到阵营配置 '{raceId}')");
				return page;
			}

			page.Lines.Add($"名称：{cfg.DisplayName}（{cfg.RaceId}）");
			page.Lines.Add($"定位：进攻 {cfg.Offense}/5 · 防守 {cfg.Defense}/5 · 经济 {cfg.Economy}/5 · " +
				$"辅助 {cfg.Support}/5 · 科技 {cfg.Technology}/5 · 上手难度 {cfg.Difficulty}/5");
			page.Lines.Add($"人口上限：{cfg.MaxSupplyCap} · 初始能量：{cfg.MaxEnergy:0.#}");
			page.Lines.Add($"可用单位 {cfg.AvailableUnitIds?.Count ?? 0} 种 · " +
				$"建筑 {cfg.AvailableStructureIds?.Count ?? 0} 种 · " +
				$"科技 {cfg.AvailableTechIds?.Count ?? 0} 项");

			// 初始配置：把 Kind 翻译成人话，玩家一眼知道自己开局有什么
			if (cfg.StartingResources != null && cfg.StartingResources.Count > 0)
			{
				var parts = new List<string>();
				foreach (var r in cfg.StartingResources)
				{
					if (r == null) continue;
					parts.Add($"{ResourceName(r.Type)} {r.Amount:0.#}");
				}
				if (parts.Count > 0) page.Lines.Add("初始资源：" + string.Join(" · ", parts));
			}

			if (cfg.StartingEntities != null && cfg.StartingEntities.Count > 0)
			{
				var counts = new Dictionary<string, int>(StringComparer.Ordinal);
				var order = new List<string>();
				foreach (var e in cfg.StartingEntities)
				{
					if (e == null || string.IsNullOrEmpty(e.EntityId)) continue;
					if (IsMapDecoration(e.Kind)) continue; // 矿/气不算"我的单位"
					if (!counts.ContainsKey(e.EntityId)) { counts[e.EntityId] = 0; order.Add(e.EntityId); }
					counts[e.EntityId]++;
				}
				if (order.Count > 0)
				{
					var parts = new List<string>();
					foreach (string id in order)
						parts.Add(counts[id] > 1 ? $"{EntityName(id)} ×{counts[id]}" : EntityName(id));
					page.Lines.Add("开局拥有：" + string.Join(" · ", parts));
				}
			}

			return page;
		}

		/// <summary>StartingEntityConfig.Kind 里属于"地图资源"的项（不作为开局单位展示）。</summary>
		private static bool IsMapDecoration(StartingEntityKind kind) =>
			kind == StartingEntityKind.Neutral || kind == StartingEntityKind.ResourceNode;

		// =========================================================
		// 单位档案
		// =========================================================

		/// <summary>为该阵营每个可用单位生成一页档案。</summary>
		public static List<TutorialPage> BuildUnitPages(string raceId)
		{
			var pages = new List<TutorialPage>();
			var cfg = ConfigDatabase.GetRace(raceId);
			if (cfg?.AvailableUnitIds == null) return pages;

			foreach (string id in cfg.AvailableUnitIds)
			{
				if (IsTierGate(id)) continue; // T1~T4 是等级门标记，不是单位
				var unit = ConfigDatabase.GetUnit(id);
				if (unit == null) continue;
				pages.Add(BuildUnitPage(id, unit));
			}
			return pages;
		}

		private static TutorialPage BuildUnitPage(string id, UnitConfig u)
		{
			var page = new TutorialPage { Section = "单位档案", Title = $"{EntityName(id)}（{id}）" };

			var traits = new List<string>();
			if (u.IsWorker) traits.Add("工人");
			if (u.CanBuild) traits.Add("可建造");
			if (u.CanRepair) traits.Add("可修理");
			if (u.CanHarvest) traits.Add("可采集");
			if (u.CanHeal) traits.Add("可治疗");
			if (u.IsAir) traits.Add("飞行");
			if (u.IsHero) traits.Add("英雄");
			if (u.IsSummoned) traits.Add("召唤物");

			page.Lines.Add($"定位：{RoleName(u.Role)}" +
				(traits.Count > 0 ? " · " + string.Join(" · ", traits) : ""));

			// 标签参与武器"对某类单位加成"的判定，是真实战斗规则的一部分
			if (u.Tags != null && u.Tags.Count > 0)
				page.Lines.Add($"标签：{string.Join(" · ", u.Tags)}");
			page.Lines.Add($"生命 {u.MaxHp:0.#} · 移速 {u.MoveSpeed} · 人口 {u.SupplyCost}" +
				(u.SupplyProvided > 0 ? $"（提供 {u.SupplyProvided}）" : ""));
			AppendCostLine(page, u);

			AppendWeaponLine(page, u);

			// 建造能力：工人能造什么
			if (u.CanBuild && u.BuildableStructureIds != null && u.BuildableStructureIds.Count > 0)
			{
				var names = new List<string>();
				foreach (string s in u.BuildableStructureIds) names.Add(EntityName(s));
				page.Lines.Add($"可建造：{string.Join("、", names)}");
				if (u.BuildPowerPercent != 100)
					page.Lines.Add($"建造效率：{u.BuildPowerPercent}%（100 = 标准速度）");
			}
			if (u.BuildRangeTiles > 0)
				page.Lines.Add($"远程建造：{u.BuildRangeTiles} 格（不用贴到蓝图旁边）");

			// 采集能力
			if (u.CanHarvest)
			{
				string extra = u.AutoSubmitHarvest ? " · 自动提交（无需回建筑交付）" : "";
				page.Lines.Add($"采集：每 {u.HarvestCycleTicks / 20f:0.##} 秒 {u.HarvestAmountPerCycle}，" +
					$"携带上限 {u.CarryCapacity}{extra}");
			}
			if (u.AutoHarvestRadiusTiles > 0)
				page.Lines.Add($"范围自动采集：{u.AutoHarvestRadiusTiles} 格内每点每秒 {u.AutoHarvestPerSecondPerNode:0.##}");

			// 修理/治疗
			if (u.CanRepair)
				page.Lines.Add($"修理：每秒 {u.RepairPerSecond:0.#} 点，消耗 {u.RepairCostPerHp:0.###} 资源/点");
			if (u.CanHeal && u.HealPerSecond > 0f)
				page.Lines.Add($"治疗：{u.HealRangeTiles} 格内每秒 {u.HealPerSecond:0.#} 点");

			// 专属机制：只在该字段真的非默认值时才出现，避免每页都塞一堆"无"
			AppendMechanicLines(page, u);

			// 解锁条件
			if (u.RequiredTechIds != null && u.RequiredTechIds.Count > 0)
			{
				var names = new List<string>();
				foreach (string t in u.RequiredTechIds) names.Add(TechName(t));
				page.Lines.Add($"解锁条件：{string.Join("、", names)}");
			}

			return page;
		}

		/// <summary>花费与建造时间一行（配置里 Costs 是字典，按资源顺序输出）。</summary>
		private static void AppendCostLine(TutorialPage page, EntityConfig e)
		{
			string cost = FormatCost(e.Costs);
			if (cost.Length == 0)
			{
				if (e.BuildTime > 0f) page.Lines.Add($"生产时间：{e.BuildTime:0.#} 秒");
				return;
			}

			string line = $"花费：{cost}";
			if (e.BuildTime > 0f) line += $" · 时间 {e.BuildTime:0.#} 秒";
			page.Lines.Add(line);
		}

		/// <summary>把武器配置翻译成一行可读描述（射程/伤害/冷却/伤害类型）。</summary>
		private static void AppendWeaponLine(TutorialPage page, EntityConfig e)
		{
			// WeaponIds 在 UnitConfig / StructureConfig 上各有一份（不在基类），
			// 所以按实际类型取，避免为了一个字段去改公共基类。
			Godot.Collections.Array<string> weaponIds = e switch
			{
				UnitConfig u => u.WeaponIds,
				StructureConfig s => s.WeaponIds,
				_ => null,
			};

			if (weaponIds == null || weaponIds.Count == 0)
			{
				if (e is UnitConfig uu && uu.AllowAttackMoveWithoutWeapon)
					page.Lines.Add("武器：无（可参与攻击移动但不主动开火）");
				return;
			}

			var parts = new List<string>();
			foreach (string wid in weaponIds)
			{
				var w = ConfigDatabase.GetWeapon(wid);
				if (w == null) { parts.Add(wid); continue; }

				// 配置里射程是世界单位、冷却是 tick；换算成"格 / 秒"玩家才看得懂
				float rangeTiles = w.AttackRange / 64f;
				float cooldownSec = w.CooldownTicks / 20f;
				string line = $"{w.Damage:0.#} {DamageTypeName(w.DamageType)}伤害 / {rangeTiles:0.#} 格";
				if (cooldownSec > 0f) line += $" / {cooldownSec:0.##} 秒一次";
				if (w.HasAreaDamage) line += " / 范围伤害";
				if (w.BonusDamage > 0) line += $" / 对{w.BonusDamageVsArmorType} +{w.BonusDamage}";
				// 标签加成是战斗规则的一部分（例如"对空 +N"），不写玩家会误判能不能打空中
				if (w.BonusDamageTags != null && w.BonusDamageTags.Count > 0)
					line += $" / 对{string.Join("、", w.BonusDamageTags)} +{w.BonusDamage}";
				parts.Add(line);
			}
			page.Lines.Add("武器：" + string.Join("；", parts));
		}

		/// <summary>
		/// 专属机制：只在配置字段真的启用时才输出。
		/// 这里覆盖的是"看单位面板看不出来、但不写玩家就会吃亏"的机制。
		/// </summary>
		private static void AppendMechanicLines(TutorialPage page, UnitConfig u)
		{
			if (u.CannotBeHealed)
				page.Lines.Add("★ 无法被治疗：任何治疗/维修手段对它无效");
			if (u.LifespanSeconds > 0f)
				page.Lines.Add($"★ 寿命 {u.LifespanSeconds:0.#} 秒：到期自动死亡" +
					"；离开己方立场时寿命消耗 ×3");
			if (u.Kamikaze)
				page.Lines.Add("★ 自杀冲锋：自动冲向最近敌人，接触即自爆");
			if (u.NoControlNeeded)
				page.Lines.Add("★ 无需控制：脱离控制范围也能被选中和指挥");
			if (u.ControlRangeTiles > 0)
				page.Lines.Add($"★ 控制范围 {u.ControlRangeTiles} 格：范围内的友军才能被选中操作");
			if (u.IsTechBuilding)
				page.Lines.Add("★ 可作科技建筑：本身就是研究科技的场所");
			if (u.CreepDefenseBonus > 0f)
				page.Lines.Add($"★ 站在己方菌毯上：全防御 +{u.CreepDefenseBonus:0.#}");
			if (u.AutoClearCreep)
				page.Lines.Add("★ 闲置时自动清除敌方菌毯");
			if (u.HasLeap)
				page.Lines.Add($"★ 腾跃：自动跳到 {u.LeapRangeTiles} 格内敌人身前，冷却 {u.LeapCooldownSeconds:0.#} 秒" +
					(string.IsNullOrEmpty(u.LeapTechId) ? "" : $"（需科技 {TechName(u.LeapTechId)}）"));
			if (u.HasPigeon)
				page.Lines.Add("★ 信鸽：死亡时揭示周围 20 格视野 3 秒" +
					(string.IsNullOrEmpty(u.PigeonTechId) ? "" : $"（需科技 {TechName(u.PigeonTechId)}）"));
			if (u.FieldRadius > 0)
				page.Lines.Add($"★ 自带立场：半径 {u.FieldRadius} 格");
			if (u.MultiShotTargets > 1)
				page.Lines.Add($"★ 多重攻击：一轮同时打 {u.MultiShotTargets} 个目标");
			if (u.DefaultAttackMove)
				page.Lines.Add("★ 右键默认攻击移动（边走边自动索敌）");
			if (u.AttackMoveFiresWhileMoving)
				page.Lines.Add("★ 移动中开火（跑打）");
			if (u.CanDeploy)
			{
				string dep = $"★ 可架设：停下 {u.DeployTimeSeconds:0.#} 秒后进入架设状态";
				if (u.DeployedMaxHpMultiplier > 1f) dep += $"，生命 ×{u.DeployedMaxHpMultiplier:0.#}";
				if (u.DeployedAttackRangeBonusTiles > 0f) dep += $"，射程 +{u.DeployedAttackRangeBonusTiles:0.#} 格";
				if (u.DeployedAoeRadiusTiles > 0f) dep += $"，攻击变 {u.DeployedAoeRadiusTiles:0.##} 格范围伤害";
				page.Lines.Add(dep);
			}
			if (u.DeployOnlyWeapon)
				page.Lines.Add("★ 只有架设后才能开火（移动中不能攻击）");
			if (u.MaxAmmo > 0)
				page.Lines.Add($"★ 弹药机制：上限 {u.MaxAmmo}" +
					(u.EnergyRegenInAmmoRange ? "；在弹药补给范围内每秒 +1 能量" : ""));
			if (u.CanSwitchAmmoMode)
				page.Lines.Add("★ 可切换弹种（高爆 / 穿甲）");
			if (u.CanGarrison)
				page.Lines.Add("★ 可驻扎进建筑");
			if (u.HeroEnergyMax > 0f)
				page.Lines.Add($"★ 英雄能量：上限 {u.HeroEnergyMax:0.#}，每秒回复 {u.HeroEnergyRegenPerSecond:0.#}");

			if (!string.IsNullOrEmpty(u.Skill1Name))
			{
				string s = $"★ 技能1「{u.Skill1Name}」";
				if (u.Skill1Cost > 0f) s += $"，消耗 {u.Skill1Cost:0.#}";
				if (u.Skill1RangeTiles > 0) s += $"，射程 {u.Skill1RangeTiles} 格";
				if (u.Skill1DurationSeconds > 0f) s += $"，持续 {u.Skill1DurationSeconds:0.#} 秒";
				page.Lines.Add(s);
			}
			if (!string.IsNullOrEmpty(u.Skill2Name))
			{
				string s = $"★ 技能2「{u.Skill2Name}」";
				if (u.Skill2Cost > 0f) s += $"，消耗 {u.Skill2Cost:0.#}";
				if (u.Skill2RangeTiles > 0) s += $"，射程 {u.Skill2RangeTiles} 格";
				if (u.Skill2DurationSeconds > 0f) s += $"，持续 {u.Skill2DurationSeconds:0.#} 秒";
				if (u.Skill2Damage > 0f) s += $"，伤害 {u.Skill2Damage:0.#}";
				page.Lines.Add(s);
			}
			if (u.PlasmaCooldownSeconds > 0f && u.Skill1Kind == 2)
				page.Lines.Add($"★ 电浆炮：前摇 {u.PlasmaWindupSeconds:0.#}s / 后摇 {u.PlasmaRecoverySeconds:0.#}s / " +
					$"冷却 {u.PlasmaCooldownSeconds:0.#}s，自动索敌 {u.PlasmaAutoTargetRangeTiles} 格");

			if (u.MobilityDurationSeconds > 0f)
				page.Lines.Add($"★ 机动模式：加速 ×{u.MobilitySpeedMultiplier:0.#} 持续 {u.MobilityDurationSeconds:0.#}s，" +
					$"冷却 {u.MobilityCooldownSeconds:0.#}s（期间无法攻击）");
		}

		// =========================================================
		// 建筑档案
		// =========================================================

		public static List<TutorialPage> BuildStructurePages(string raceId)
		{
			var pages = new List<TutorialPage>();
			var cfg = ConfigDatabase.GetRace(raceId);
			if (cfg?.AvailableStructureIds == null) return pages;

			foreach (string id in cfg.AvailableStructureIds)
			{
				if (IsTierGate(id)) continue;
				var s = ConfigDatabase.GetStructure(id);
				if (s == null) continue;
				pages.Add(BuildStructurePage(id, s));
			}
			return pages;
		}

		private static TutorialPage BuildStructurePage(string id, StructureConfig s)
		{
			var page = new TutorialPage { Section = "建筑档案", Title = $"{EntityName(id)}（{id}）" };

			page.Lines.Add($"生命 {s.MaxHp:0.#}" +
				(s.SupplyProvided > 0 ? $" · 提供人口 {s.SupplyProvided}" : "") +
				(s.IsDropOffPoint ? " · 资源交付点" : "") +
				(s.IsUnique ? " · 全阵营唯一" : ""));
			AppendCostLine(page, s);
			AppendWeaponLine(page, s);

			// 能造什么兵
			if (s.TrainableUnitIds != null && s.TrainableUnitIds.Count > 0)
			{
				var names = new List<string>();
				foreach (string u in s.TrainableUnitIds) names.Add(EntityName(u));
				page.Lines.Add("可生产：" + string.Join("、", names));
			}

			// 能研究什么科技
			if (s.ResearchableTechIds != null && s.ResearchableTechIds.Count > 0)
			{
				var names = new List<string>();
				foreach (string t in s.ResearchableTechIds) names.Add(TechName(t));
				page.Lines.Add("可研究：" + string.Join("、", names));
			}

			if (s.RequiredTechIds != null && s.RequiredTechIds.Count > 0)
			{
				var names = new List<string>();
				foreach (string t in s.RequiredTechIds) names.Add(TechName(t));
				page.Lines.Add($"建造前置：{string.Join("、", names)}");
			}

			// 蓝图/施工：多足的所有"建筑"其实都是蓝图（AutoBuild 表示拍下即自动施工）
			if (s.AutoBuild)
				page.Lines.Add("★ 自动施工：拍下蓝图后无需工人，自己会建成");

			if (s.CreepSpreadRadius > 0)
				page.Lines.Add($"★ 菌毯源：蔓延半径 {s.CreepSpreadRadius} 格");
			if (s.FieldRadius > 0)
				page.Lines.Add($"★ 提供立场：半径 {s.FieldRadius} 格");
			if (s.AutoProduceUnitIds != null && s.AutoProduceUnitIds.Count > 0)
			{
				var names = new List<string>();
				foreach (string u in s.AutoProduceUnitIds) names.Add(EntityName(u));
				string interval = s.AutoProduceIntervalSeconds > 0f
					? $"每 {s.AutoProduceIntervalSeconds:0.#} 秒一个"
					: "持续自动生产";
				page.Lines.Add($"★ 免费自动生产：{string.Join("、", names)}（{interval}，不消耗资源）");
			}
			if (s.GarrisonCapacity > 0)
				page.Lines.Add($"★ 可驻扎：容量 {s.GarrisonCapacity} 个单位");
			if (s.PassiveHpRegenPerSecond > 0f)
				page.Lines.Add($"★ 每秒自回血 {s.PassiveHpRegenPerSecond:0.#}");
			if (s.GlobalVision)
				page.Lines.Add("★ 提供全图视野");
			if (s.RequiresCreep)
				page.Lines.Add($"★ 必须建在菌毯上（{s.RequiredCreepType}）");

			return page;
		}

		// =========================================================
		// 科技树
		// =========================================================

		public static List<TutorialPage> BuildTechPages(string raceId)
		{
			var pages = new List<TutorialPage>();
			var cfg = ConfigDatabase.GetRace(raceId);
			if (cfg?.AvailableTechIds == null) return pages;

			foreach (string id in cfg.AvailableTechIds)
			{
				if (IsTierGate(id)) continue;
				var t = ConfigDatabase.GetTech(id);
				if (t == null) continue;
				pages.Add(BuildTechPage(id, t));
			}
			return pages;
		}

		private static TutorialPage BuildTechPage(string id, TechConfig t)
		{
			var page = new TutorialPage
			{
				Section = "科技树",
				Title = string.IsNullOrEmpty(t.DisplayName) ? id : $"{t.DisplayName}（{id}）",
			};

			if (!string.IsNullOrEmpty(t.Description)) page.Lines.Add(t.Description);

			string cost = FormatCost(t.Cost);
			page.Lines.Add($"类别：{CategoryName(t.Category)} · 研究 {t.ResearchTimeSeconds:0.#} 秒" +
				(cost.Length > 0 ? $" · 花费 {cost}" : ""));

			if (t.RequiredTechIds != null && t.RequiredTechIds.Count > 0)
			{
				var names = new List<string>();
				foreach (string r in t.RequiredTechIds) names.Add(TechName(r));
				page.Lines.Add($"前置：{string.Join("、", names)}");
			}
			if (!string.IsNullOrEmpty(t.ExclusiveGroup))
				page.Lines.Add($"★ 二选一：与同组科技「{t.ExclusiveGroup}」只能选一个");
			if (t.IsRepeatable)
				page.Lines.Add($"★ 可重复研究：每次价格 ×{t.RepeatCostMultiplier:0.#}");

			// 解锁内容
			if (t.UnlockStructureIds != null && t.UnlockStructureIds.Count > 0)
			{
				var names = new List<string>();
				foreach (string s in t.UnlockStructureIds) names.Add(EntityName(s));
				page.Lines.Add($"解锁建筑：{string.Join("、", names)}");
			}
			if (t.UnlockSkillIds != null && t.UnlockSkillIds.Count > 0)
				page.Lines.Add($"解锁技能：{string.Join("、", t.UnlockSkillIds)}");

			AppendTechEffects(page, t);
			return page;
		}

		/// <summary>把 TechConfig 的效果字段翻译成可读条目（只输出非默认值）。</summary>
		private static void AppendTechEffects(TutorialPage page, TechConfig t)
		{
			if (t.HpBonusPercent != 0f) page.Lines.Add($"效果：生命 +{t.HpBonusPercent * 100f:0.#}%");
			if (t.FlatHpBonus != 0) page.Lines.Add($"效果：生命 +{t.FlatHpBonus}");
			if (t.FlatDamageBonus != 0) page.Lines.Add($"效果：攻击 +{t.FlatDamageBonus}");
			if (t.AttackSpeedMultiplier != 1f) page.Lines.Add($"效果：攻速 ×{t.AttackSpeedMultiplier:0.##}");
			if (t.MoveSpeedMultiplier != 1f) page.Lines.Add($"效果：移速 ×{t.MoveSpeedMultiplier:0.##}");
			if (t.AttackRangeBonus != 0f) page.Lines.Add($"效果：射程 +{t.AttackRangeBonus / 64f:0.#} 格");
			if (t.ProductionTimeMultiplier != 1f) page.Lines.Add($"效果：生产时间 ×{t.ProductionTimeMultiplier:0.##}");
			if (t.UnitHpRegenPerSecond != 0f) page.Lines.Add($"效果：每秒回血 {t.UnitHpRegenPerSecond:0.#}");

			// 抗性（五种伤害类型）
			var resists = new List<string>();
			if (t.KineticResist != 0f) resists.Add($"动能 +{t.KineticResist * 100f:0.#}%");
			if (t.ThermalResist != 0f) resists.Add($"热能 +{t.ThermalResist * 100f:0.#}%");
			if (t.ExplosiveResist != 0f) resists.Add($"爆炸 +{t.ExplosiveResist * 100f:0.#}%");
			if (t.EmResist != 0f) resists.Add($"电磁 +{t.EmResist * 100f:0.#}%");
			if (t.BeamResist != 0f) resists.Add($"光束 +{t.BeamResist * 100f:0.#}%");
			if (resists.Count > 0) page.Lines.Add("效果：抗性 " + string.Join(" · ", resists));
			if (t.DefenseType >= 0 && t.IsRepeatable)
				page.Lines.Add($"★ 循环强化：每级 +1 点{DefenseTypeName(t.DefenseType)}抗性");

			// 恶魔
			if (t.FieldRadiusBonus != 0) page.Lines.Add($"效果：立场半径 +{t.FieldRadiusBonus} 格");
			if (t.FieldMoveSpeedBonus != 0f) page.Lines.Add($"效果：立场上移速 +{t.FieldMoveSpeedBonus * 100f:0.#}%");
			if (t.AutoProduceIntervalMultiplier != 1f)
				page.Lines.Add($"效果：自动生产速度 ×{(1f / t.AutoProduceIntervalMultiplier):0.##}");
			if (t.FireDamageBonus != 0) page.Lines.Add($"效果：火焰伤害 +{t.FireDamageBonus}");
			if (t.CreepIncomeMultiplier != 1f) page.Lines.Add($"效果：菌毯收益 ×{t.CreepIncomeMultiplier:0.##}");
			if (t.HarvesterIncomeMultiplier != 1f) page.Lines.Add($"效果：采集收益 ×{t.HarvesterIncomeMultiplier:0.##}");

			// 植物
			if (t.StructureHpBonusPercent != 0f)
				page.Lines.Add($"效果：建筑生命 +{t.StructureHpBonusPercent * 100f:0.#}%");
			if (t.CreepSpreadRadiusBonus != 0) page.Lines.Add($"效果：菌毯蔓延 +{t.CreepSpreadRadiusBonus} 格");
			if (t.LifestealPercent != 0f) page.Lines.Add($"效果：攻击吸血 {t.LifestealPercent * 100f:0.#}%");
			if (t.HitSlowMoveMultiplier != 1f)
				page.Lines.Add($"效果：命中减速至 ×{t.HitSlowMoveMultiplier:0.##}");
			if (t.SelfCreepRadius != 0) page.Lines.Add($"效果：自身携带菌毯半径 {t.SelfCreepRadius} 格");

			// 泰伦
			if (t.GrantKnockbackAttacks) page.Lines.Add($"效果：攻击附带击退 {t.KnockbackTiles:0.#} 格");
			if (t.DeployDamageReduction != 0f)
				page.Lines.Add($"效果：架设时受伤 -{t.DeployDamageReduction * 100f:0.#}%");
			if (t.DeployTimeMultiplier != 1f) page.Lines.Add($"效果：架设时间 ×{t.DeployTimeMultiplier:0.##}");
			if (t.AmmoCapMultiplier != 1f) page.Lines.Add($"效果：弹药上限 ×{t.AmmoCapMultiplier:0.##}");
			if (t.AmmoRefundOnKill) page.Lines.Add("效果：击杀返还弹药");
			if (t.AoeRadiusBonusTiles != 0f) page.Lines.Add($"效果：所有范围伤害半径 +{t.AoeRadiusBonusTiles:0.#} 格");
			if (t.AllowStructureTargeting) page.Lines.Add("效果：允许攻击建筑");
			if (t.BonusDamageVsStructure != 0) page.Lines.Add($"效果：对建筑额外 +{t.BonusDamageVsStructure} 伤害");

			// 联盟
			if (t.StimDurationSeconds > 0f)
				page.Lines.Add($"效果：兴奋剂持续 {t.StimDurationSeconds:0.#} 秒，" +
					$"攻速 ×{t.StimAttackSpeedMultiplier:0.##} / 移速 ×{t.StimMoveSpeedMultiplier:0.##}，" +
					$"自伤 {t.StimSelfDamage:0.#}");

			// 主动技能（离子吐息等）
			if (t.SkillDamage > 0f)
				page.Lines.Add($"★ 主动技能：{t.SkillDamage:0.#} 伤害 / {t.SkillProjectileRangeTiles} 格 / " +
					$"冷却 {t.SkillCooldownSeconds:0.#}s");
			if (t.AuraRadiusTiles > 0)
			{
				string aura = $"★ 光环 {t.AuraRadiusTiles} 格：";
				var bits = new List<string>();
				if (t.AuraAttackSpeedBonus != 0f) bits.Add($"攻速 +{t.AuraAttackSpeedBonus * 100f:0.#}%");
				if (t.AuraHpRegenPerSecond != 0f) bits.Add($"回血 {t.AuraHpRegenPerSecond:0.#}/s");
				if (t.AuraEnemyRangeReductionTiles != 0) bits.Add($"敌方射程 -{t.AuraEnemyRangeReductionTiles} 格");
				if (t.AuraBurnDamagePerSecond != 0f) bits.Add($"灼烧 {t.AuraBurnDamagePerSecond:0.#}/s");
				if (bits.Count == 0) bits.Add("(无数值)");
				page.Lines.Add(aura + string.Join(" · ", bits));
			}

			if (t.GrantWeaponIds != null && t.GrantWeaponIds.Count > 0)
				page.Lines.Add($"追加武器：{string.Join("、", t.GrantWeaponIds)} ×{t.GrantWeaponCount}");
		}

		// =========================================================
		// 名称解析：把 ID 显示成玩家看得懂的名字
		// =========================================================

		/// <summary>
		/// 实体显示名：走本地化表 `name.&lt;id&gt;`（Localization/zh_CN.csv 里有全部单位、
		/// 建筑、科技的中文名），查不到才回退 ID。
		///
		/// 这样图鉴里的名字与游戏内 UI 显示的名字**天然一致**——
		/// 都是查同一张表，不可能出现"教程叫步枪兵、界面上叫 RifleMan"。
		/// </summary>
		public static string EntityName(string id)
		{
			if (string.IsNullOrEmpty(id)) return "";

			// 蓝图统一成"XX蓝图"，不然 Blueprint_Hunter 这一串 ID 很劝退
			if (id.StartsWith("Blueprint_", StringComparison.Ordinal))
			{
				string inner = id.Substring("Blueprint_".Length);
				return LookupName(inner) + "蓝图";
			}

			return LookupName(id);
		}

		/// <summary>
		/// 查 `name.&lt;id&gt;`。
		///
		/// 必须先 Has() 再 Tr()：Localization.Tr 对**找不到的 key 会原样返回 key**，
		/// 于是 TrName("Foo") 会得到 "name.Foo" 这种垃圾字符串。
		/// 早先直接用它，图鉴里就出现了 "name.CaveSandwormMid"。
		/// </summary>
		private static string LookupName(string id)
		{
			string key = "name." + id;
			if (RTS.Settings.Localization.Has(key))
				return RTS.Settings.Localization.Tr(key);

			// 没有本地化条目：退回 ID（绝不编造中文名，避免和 UI 不一致）
			return id;
		}

		public static string TechName(string id)
		{
			if (string.IsNullOrEmpty(id)) return "";

			// 科技优先用配置里的 DisplayName（.tres 里手写的），
			// 没有再查本地化表，最后才回退 ID。
			var t = ConfigDatabase.GetTech(id);
			if (t != null && !string.IsNullOrEmpty(t.DisplayName)) return t.DisplayName;

			return LookupName(id);
		}

		private static string FormatCost(Godot.Collections.Dictionary<ResourceType, float> cost)
		{
			if (cost == null || cost.Count == 0) return "";
			var parts = new List<string>();
			foreach (var kv in cost)
			{
				if (kv.Value <= 0f) continue;
				parts.Add($"{ResourceName(kv.Key)} {kv.Value:0.#}");
			}
			return string.Join(" + ", parts);
		}

		private static string RoleName(UnitRoleType role) => role switch
		{
			UnitRoleType.Worker => "工人",
			UnitRoleType.Fighter => "近战",
			UnitRoleType.Tank => "坦克",
			UnitRoleType.Ranged => "远程",
			UnitRoleType.Siege => "攻坚",
			UnitRoleType.Scout => "侦察",
			UnitRoleType.Support => "辅助",
			UnitRoleType.Healer => "治疗",
			UnitRoleType.Controller => "控制",
			UnitRoleType.Hero => "英雄",
			UnitRoleType.Summon => "召唤",
			UnitRoleType.Neutral => "中立",
			_ => "基础",
		};

		private static string CategoryName(TechCategory c) => c switch
		{
			TechCategory.Combat => "战斗",
			TechCategory.Defense => "防御",
			TechCategory.Mobility => "机动",
			TechCategory.Economy => "经济",
			_ => "通用",
		};

		private static string DefenseTypeName(int t) => t switch
		{
			0 => "动能",
			1 => "热能",
			2 => "爆炸",
			3 => "电磁",
			4 => "光束",
			_ => "未知",
		};

		/// <summary>资源显示名（与 UI 保持一致）。</summary>
		public static string ResourceName(ResourceType type) => type switch
		{
			ResourceType.Metal => "金属",
			ResourceType.Gas => "瓦斯",
			ResourceType.Data => "数据",
			ResourceType.Energy => "能量",
			_ => type.ToString(),
		};

		private static string DamageTypeName(DamageType t) => t switch
		{
			DamageType.Kinetic => "动能",
			DamageType.Thermal => "热能",
			DamageType.Explosive => "爆炸",
			DamageType.EM => "电磁",
			DamageType.Beam => "光束",
			_ => t.ToString(),
		};

		/// <summary>T1~T4 / UnionT2 这类等级门标记不是真实单位或科技。</summary>
		private static bool IsTierGate(string id)
		{
			if (string.IsNullOrEmpty(id)) return false;
			if (id is "T1" or "T2" or "T3" or "T4") return true;
			return id.Length >= 2 && id[^1] >= '1' && id[^1] <= '4' && id[^2] == 'T';
		}
	}
}
