using Godot;

namespace RTS.Data.Configs
{
	/// <summary>
	/// 建筑静态配置。
	///
	/// 数据驱动原则：
	/// - 不直接引用 UnitConfig / WeaponConfig / TechConfig / BuffConfig。
	/// - 全部通过字符串 ID 连接。
	/// - 运行时由 ConfigDatabase 解析 ID。
	///
	/// 适用对象：
	/// - 主基地
	/// - 兵营 / 工厂 / 科技建筑
	/// - 防御塔
	/// - 资源建筑
	/// - 交互建筑基础配置
	/// </summary>
	[GlobalClass]
	public partial class StructureConfig : EntityConfig
	{
		// =========================================================
		// 建筑尺寸 / 放置
		// =========================================================

		[ExportGroup("Placement")]
		[Export(PropertyHint.Range, "1,20,1")]
		public int GridWidth { get; set; } = 2;

		[Export(PropertyHint.Range, "1,20,1")]
		public int GridHeight { get; set; } = 2;

		[Export] public bool RequiresCreep { get; set; } = false;
		[Export] public CreepType RequiredCreepType { get; set; } = CreepType.Any;

		// 建筑自动铺菌毯（CreepSource）：半径 / 蔓延时长 / 菌毯类型
		// 默认 0 = 不铺毯；只有显式配置半径的建筑才挂 CreepSource（曾因默认 10 导致
		// 所有 RequiresCreep 的植物建筑意外铺出蓝色纳米毯）
		[Export(PropertyHint.Range, "0,999,1")]
		public float CreepSpreadRadius { get; set; } = 0f;

		[Export(PropertyHint.Range, "0.1,60,0.1")]
		public float CreepSpreadTime { get; set; } = 1f;

		[Export]
		public CreepType GeneratedCreepType { get; set; } = CreepType.NanoCreep;

		// 森林蔓延施法范围（格）：植物生命树/分支树/森林节点各自不同（玩家与 AI 共用）
		[Export(PropertyHint.Range, "1,32,1")]
		public int ForestSpreadRangeTiles { get; set; } = 12;

		[Export] public bool BlocksMovement { get; set; } = true;
		[Export] public bool BlocksBuildingPlacement { get; set; } = true;

		// 空中建筑（未来可能有）：会被对空武器命中
		[Export] public bool IsAir { get; set; } = false;

		// =========================================================
		// 自动采集（采集器类建筑）
		// =========================================================

		[ExportGroup("Auto Harvest")]
		[Export(PropertyHint.Range, "0,99,1")]
		public int AutoHarvestRadiusTiles { get; set; } = 0;

		[Export(PropertyHint.Range, "0,999,0.5")]
		public float AutoHarvestPerSecondPerNode { get; set; } = 0f;

	[Export] public bool IsDropOffPoint { get; set; } = false;

	// 可接收的交付资源（配合 IsDropOffPoint 使用）
	[Export]
	public Godot.Collections.Array<ResourceType> AcceptableResourceTypes { get; set; } = new();

		// =========================================================
		// 建筑类型
		// =========================================================

		[ExportGroup("Structure Type")]
		[Export] public bool IsMainBase { get; set; } = false;
		[Export] public bool IsProductionBuilding { get; set; } = false;
		[Export] public bool IsTechBuilding { get; set; } = false;
		[Export] public bool IsDefenseBuilding { get; set; } = false;
		[Export] public bool IsResourceBuilding { get; set; } = false;
		[Export] public bool IsInteractiveBuilding { get; set; } = false;

		// 唯一建筑：同一玩家同时只能拥有一座（轨道控制中心）
		[Export] public bool IsUnique { get; set; } = false;

		// 立场半径（格）：恶魔建筑自带立场
		[Export(PropertyHint.Range, "0,99,1")]
		public int FieldRadius { get; set; } = 0;

		// 献祭建造：工人走到蓝图旁自我删除，建筑自动施工
		[Export] public bool RequiresSacrifice { get; set; } = false;

		// 自动施工：无需工人，按建造时间自动推进（纳米建筑/恶魔献祭建筑）
		[Export] public bool AutoBuild { get; set; } = false;

		// 全局可见：脱离视野也始终显示/可被看到（中立塔等）
		[Export] public bool GlobalVision { get; set; } = false;

		// 建成后被动回血（每秒，0 = 不回血）
		[Export(PropertyHint.Range, "0,999,0.5")]
		public float PassiveHpRegenPerSecond { get; set; } = 0f;

		// 自动生产（恶魔传送门类）：定时免费产出，不消耗资源
		[Export] public Godot.Collections.Array<string> AutoProduceUnitIds { get; set; } = new();
		[Export(PropertyHint.Range, "0.1,999,0.1")]
		public float AutoProduceIntervalSeconds { get; set; } = 0f;

		// 自动生产绑定上限（地狱城最多绑定 12 个怨灵；0 = 不限制）
		[Export(PropertyHint.Range, "0,999,1")]
		public int AutoProduceMaxBound { get; set; } = 0;

		// 大裂隙等可切换模式：第二套产出列表 + 按钮名
		[Export] public Godot.Collections.Array<string> AutoProduceModeUnitIds { get; set; } = new();
		[Export] public string AutoProduceModeName { get; set; } = "";

		// 是否吃“裂痕扩张/贪婪涌出”产速科技（只有大裂隙吃）
		[Export] public bool AutoProduceAffectedByRiftTechs { get; set; } = false;

		// 建成时赠送的单位（太空船坞送战巡 / 恶魔祭坛送英雄）
		[Export] public Godot.Collections.Array<string> BonusUnitOnCompleteIds { get; set; } = new();

		// 限时建筑（旗帜等）：存活指定秒数后自动销毁
		[Export(PropertyHint.Range, "0,999,0.5")]
		public float LifespanSeconds { get; set; } = 0f;

		// =========================================================
		// 泰伦：弹药范围 / 驻扎
		// =========================================================

		[ExportGroup("Terran")]
		// 弹药补给范围（格，0 = 不提供）：范围内单位每秒回 10% 弹药
		[Export(PropertyHint.Range, "0,99,1")]
		public int AmmoRangeTiles { get; set; } = 0;
		// 该弹药范围是否只给飞行单位（机场）
		[Export] public bool AmmoRangeAffectsAir { get; set; } = false;

		// 可驻扎工程师数量（藻类工厂）
		[Export(PropertyHint.Range, "0,20,1")]
		public int GarrisonCapacity { get; set; } = 0;
		// 每名驻扎工程师每秒产出的肉（生物质）
		[Export(PropertyHint.Range, "0,999,0.5")]
		public float GarrisonIncomePerSecondPerUnit { get; set; } = 0f;

		// =========================================================
		// 生产
		// =========================================================

		[ExportGroup("Production")]

		// 可训练单位 ID。
		// 例如：
		// - "union_worker"
		// - "union_rifleman"
		// - "union_tank"
		[Export] public Godot.Collections.Array<string> TrainableUnitIds { get; set; } = new();

		[Export(PropertyHint.Range, "1,99,1")]
		public int ProductionQueueSize { get; set; } = 5;

		[Export] public bool CanSetRallyPoint { get; set; } = true;

		// =========================================================
		// 科技
		// =========================================================

		[ExportGroup("Tech")]

		// 可研究科技 ID。
		[Export] public Godot.Collections.Array<string> ResearchableTechIds { get; set; } = new();

		// 建造完成后提供的解锁 ID / 科技 ID / 建筑标签。
		[Export] public Godot.Collections.Array<string> ProvidesTechIds { get; set; } = new();

		// 建造该建筑需要的科技 ID。
		[Export] public Godot.Collections.Array<string> RequiredTechIds { get; set; } = new();

		// =========================================================
		// 武器
		// =========================================================

		[ExportGroup("Combat")]

		// 建筑武器 ID。
		// 例如：
		// - "union_turret_gun"
		// - "base_laser"
		[Export] public Godot.Collections.Array<string> WeaponIds { get; set; } = new();

		// =========================================================
		// 供给 / 人口
		// =========================================================

		[ExportGroup("Supply")]
		[Export(PropertyHint.Range, "0,999,1")]
		public int SupplyProvided { get; set; } = 0;

		[Export(PropertyHint.Range, "0,999,1")]
		public int SupplyUsed { get; set; } = 0;

		// =========================================================
		// 资源
		// =========================================================

		[ExportGroup("Resource")]
		[Export] public bool ProvidesResourceIncome { get; set; } = false;

		[Export] public ResourceType IncomeResourceType { get; set; } = ResourceType.Metal;

		[Export(PropertyHint.Range, "0,999999,1")]
		public int IncomeAmountPerCycle { get; set; } = 0;

		[Export(PropertyHint.Range, "1,999999,1")]
		public int IncomeCycleTicks { get; set; } = 20;

		// 中立资源点（矿脉/气泉）：类型与储量
		[Export] public ResourceType ResourceType { get; set; } = ResourceType.Metal;
		[Export(PropertyHint.Range, "0,999999,1")]
		public float ResourceAmount { get; set; } = 0f;

		// =========================================================
		// 光环 / 范围效果预留
		// =========================================================

		[ExportGroup("Aura")]
		[Export] public bool HasAura { get; set; } = false;

		[Export(PropertyHint.Range, "0,999999,1")]
		public int AuraRange { get; set; } = 0;

		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float AuraAttackSpeedBonus { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,999,1")]
		public float AuraHpRegenPerSecond { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,99,1")]
		public int AuraEnemyRangeReductionTiles { get; set; } = 0;

		[Export(PropertyHint.Range, "0,999,0.5")]
		public float AuraBurnDamagePerSecond { get; set; } = 0f;

		// 后续由 BuffConfig 接入。
		[Export] public Godot.Collections.Array<string> AuraBuffIds { get; set; } = new();

		// =========================================================
		// 面板技能数值（轨道控制中心等交互建筑）
		// =========================================================

		[ExportGroup("Panel Skills")]
		[Export(PropertyHint.Range, "1,99,1")]
		public int RadarRadiusTiles { get; set; } = 10;

		[Export(PropertyHint.Range, "0.5,60,0.5")]
		public float RadarDurationSeconds { get; set; } = 5f;

		[Export(PropertyHint.Range, "0,999,1")]
		public float RadarEnergyCost { get; set; } = 20f;

		[Export(PropertyHint.Range, "1,99,1")]
		public int StrikeRadiusTiles { get; set; } = 1;

		[Export(PropertyHint.Range, "0,99999,1")]
		public float StrikeDamage { get; set; } = 200f;

		[Export(PropertyHint.Range, "0,999,1")]
		public float StrikeEnergyCost { get; set; } = 40f;

		[Export(PropertyHint.Range, "0,99999,1")]
		public float ExchangeMetalAmount { get; set; } = 100f;

		[Export(PropertyHint.Range, "0,99999,1")]
		public float ExchangeGasAmount { get; set; } = 150f;

		[Export(PropertyHint.Range, "0,999,1")]
		public float ExchangeEnergyCost { get; set; } = 10f;

		// 面板技能位标记：1=雷达 2=轨道炮 4=资源交换 8=共振波 16=地震波 32=制造虫洞
		[Export(PropertyHint.Flags, "Radar,OrbitalStrike,ResourceExchange,ResonanceWave,SeismicWave,WormholeCreate,OmniLeafDog")]
		public int PanelSkillMode { get; set; } = 0;

		// 洞穴面板技能：波类效果半径（格）/ 持续时间 / 倍率（共振波=受伤倍率，地震波=减速比例）
		[Export(PropertyHint.Range, "1,99,1")]
		public int WaveRadiusTiles { get; set; } = 6;

		[Export(PropertyHint.Range, "0.5,60,0.5")]
		public float WaveDurationSeconds { get; set; } = 10f;

		[Export(PropertyHint.Range, "0.1,9.99,0.01")]
		public float WaveMultiplier { get; set; } = 1.5f;

		// 地壳裂解器：地震波充能（最多 2 次，随时间恢复）
		[Export(PropertyHint.Range, "1,9,1")]
		public int SeismicMaxCharges { get; set; } = 2;

		[Export(PropertyHint.Range, "1,120,1")]
		public float SeismicChargeSeconds { get; set; } = 20f;

		// 虫洞核心：制造虫洞冷却（秒）
		[Export(PropertyHint.Range, "0,120,1")]
		public float WormholeCreateCooldownSeconds { get; set; } = 30f;

		// =========================================================
		// 工具方法
		// =========================================================

		public bool HasWeapon()
		{
			return WeaponIds.Count > 0;
		}

		public bool HasWeaponId(string weaponId)
		{
			if (string.IsNullOrEmpty(weaponId))
				return false;

			foreach (string id in WeaponIds)
			{
				if (id == weaponId)
					return true;
			}

			return false;
		}

		public bool CanTrainUnit(string unitId)
		{
			if (string.IsNullOrEmpty(unitId))
				return false;

			foreach (string id in TrainableUnitIds)
			{
				if (id == unitId)
					return true;
			}

			return false;
		}

		public bool CanResearchTech(string techId)
		{
			if (string.IsNullOrEmpty(techId))
				return false;

			foreach (string id in ResearchableTechIds)
			{
				if (id == techId)
					return true;
			}

			return false;
		}

		public bool ProvidesTech(string techId)
		{
			if (string.IsNullOrEmpty(techId))
				return false;

			foreach (string id in ProvidesTechIds)
			{
				if (id == techId)
					return true;
			}

			return false;
		}

		public bool RequiresTech(string techId)
		{
			if (string.IsNullOrEmpty(techId))
				return false;

			foreach (string id in RequiredTechIds)
			{
				if (id == techId)
					return true;
			}

			return false;
		}

		public override bool IsValidConfig()
		{
			if (!base.IsValidConfig())
				return false;

			if (GridWidth <= 0 || GridHeight <= 0)
				return false;

			if (IsProductionBuilding && ProductionQueueSize <= 0)
				return false;

			if (ProvidesResourceIncome)
			{
				if (IncomeAmountPerCycle <= 0)
					return false;

				if (IncomeCycleTicks <= 0)
					return false;
			}

			if (HasAura && AuraRange <= 0)
				return false;

			return true;
		}
	}
}
