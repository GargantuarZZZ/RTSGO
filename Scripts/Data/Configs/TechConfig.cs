using Godot;

namespace RTS.Data.Configs
{
	public enum TechCategory
	{
		General,
		Combat,
		Defense,
		Mobility,
		Economy
	}

	/// <summary>
	/// 科技静态配置。
	///
	/// 数据驱动原则：
	/// - 所有研究数值（时间/资源/需求）都在配置表里
	/// - 效果也尽量用数值字段描述，运行时不写死
	/// - 主动技能（离子吐息/要塞化等）通过 UnlockSkillIds 预留
	/// </summary>
	[GlobalClass]
	public partial class TechConfig : Resource
	{
		// =========================================================
		// 基础识别
		// =========================================================

		[ExportGroup("Identity")]
		[Export] public string TechId { get; set; } = "";
		[Export] public string DisplayName { get; set; } = "";
		[Export(PropertyHint.MultilineText)] public string Description { get; set; } = "";
		[Export] public Texture2D Icon { get; set; }

		// =========================================================
		// 研究
		// =========================================================

		[ExportGroup("Research")]
		[Export] public TechCategory Category { get; set; } = TechCategory.General;

		// A/B 二选一：相同 ExclusiveGroup 只能研究一个
		[Export] public string ExclusiveGroup { get; set; } = "";

		[Export] public Godot.Collections.Array<string> RequiredTechIds { get; set; } = new();

		// 可循环研究（针对防御）：重复研究次数生效，每次价格翻倍
		[Export] public bool IsRepeatable { get; set; } = false;
		[Export(PropertyHint.Range, "1,99,0.1")]
		public float RepeatCostMultiplier { get; set; } = 2f;

		// 通用科技目标 tag（空 = 全单位）；Air 特判飞行单位
		[Export] public string TargetTag { get; set; } = "";
		// 精确目标单位 ID 列表（泰伦长程飞弹等指定单位的科技）
		[Export] public Godot.Collections.Array<string> TargetUnitIds { get; set; } = new();

		// 采集自动化类：授予无需控制 + 视野加成
		[Export] public bool GrantNoControlNeeded { get; set; } = false;
		[Export(PropertyHint.Range, "0,999,1")]
		public float VisionBonus { get; set; } = 0f;

		// 针对防御：循环研究时按防御类型逐级 +1（0动能 1热能 2爆炸 3电磁 4光束）
		[Export(PropertyHint.Range, "-1,4,1")]
		public int DefenseType { get; set; } = -1;

		[Export] public bool RequiresHarvester { get; set; } = true;

		[Export(PropertyHint.Range, "0.1,9999,0.1")]
		public float ResearchTimeSeconds { get; set; } = 30f;

		[Export]
		public Godot.Collections.Dictionary<ResourceType, float> Cost { get; set; } = new();

		// =========================================================
		// 效果（数值全部可配置）
		// =========================================================

		[ExportGroup("Effects")]
		[Export] public Godot.Collections.Array<string> UnlockStructureIds { get; set; } = new();
		[Export] public Godot.Collections.Array<string> UnlockSkillIds { get; set; } = new();

		// 研究完成后给单位追加的武器（例如要塞化的加强火炮/防空飞弹）
		[Export] public Godot.Collections.Array<string> GrantWeaponIds { get; set; } = new();
		[Export(PropertyHint.Range, "0,99,1")]
		public int GrantWeaponCount { get; set; } = 0;

		// 光环效果（巨兽通用A/B技能，数值全部可配置）
		[Export(PropertyHint.Range, "0,99,1")]
		public int AuraRadiusTiles { get; set; } = 0;

		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float AuraAttackSpeedBonus { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,999,1")]
		public float AuraHpRegenPerSecond { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,99,1")]
		public int AuraEnemyRangeReductionTiles { get; set; } = 0;

		[Export(PropertyHint.Range, "0,999,0.5")]
		public float AuraBurnDamagePerSecond { get; set; } = 0f;

		// 主动技能数值（例如离子吐息）
		[ExportGroup("Active Skill")]
		[Export(PropertyHint.Range, "0,999,0.1")]
		public float SkillWindupSeconds { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,999,0.1")]
		public float SkillDurationSeconds { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,9999,0.5")]
		public float SkillCooldownSeconds { get; set; } = 0f;

		[Export(PropertyHint.Range, "0.05,10,0.05")]
		public float SkillFireIntervalSeconds { get; set; } = 0.1f;

		[Export(PropertyHint.Range, "0,999,0.5")]
		public float SkillProjectileSpeedTiles { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,999,1")]
		public int SkillProjectileRangeTiles { get; set; } = 0;

		[Export(PropertyHint.Range, "0,99,0.5")]
		public float SkillProjectileRadiusTiles { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,99999,1")]
		public float SkillDamage { get; set; } = 0f;

		[Export] public DamageType SkillDamageType { get; set; } = DamageType.Thermal;

		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float CreepIncomeMultiplier { get; set; } = 1f;

		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float SpreadCostMultiplier { get; set; } = 1f;

		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float SpreadCooldownMultiplier { get; set; } = 1f;

		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float HarvesterIncomeMultiplier { get; set; } = 1f;

		// 恶魔：大裂隙自动生产间隔倍率（0.8 = 生产速度 +20%）
		[Export(PropertyHint.Range, "0.01,9.99,0.01")]
		public float AutoProduceIntervalMultiplier { get; set; } = 1f;

		// 恶魔：火焰伤害固定加成（烈火 / 灵魂火）
		[Export(PropertyHint.Range, "0,99,1")]
		public int FireDamageBonus { get; set; } = 0;

		// 洞穴科技：被动攻速倍率（1.5 = +50%），按 TargetUnitIds 精确作用
		[Export(PropertyHint.Range, "0.1,9.99,0.01")]
		public float AttackSpeedMultiplier { get; set; } = 1f;

		// 洞穴科技：固定生命加成（突破者/巨蝎等）
		[Export(PropertyHint.Range, "0,9999,1")]
		public int FlatHpBonus { get; set; } = 0;

		// 洞穴科技：固定攻击力加成（走 BuffContainer 固定伤害）
		[Export(PropertyHint.Range, "0,999,1")]
		public int FlatDamageBonus { get; set; } = 0;

		// 植物：建筑生命加成（纤维装甲）
		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float StructureHpBonusPercent { get; set; } = 0f;

		// 植物：菌毯蔓延半径加成（格）与蔓延时间倍率（菌毯增殖）
		[Export(PropertyHint.Range, "0,99,1")]
		public int CreepSpreadRadiusBonus { get; set; } = 0;

		[Export(PropertyHint.Range, "0.1,9.99,0.01")]
		public float CreepSpreadTimeMultiplier { get; set; } = 1f;

		// 植物：攻击吸血比例（汲取）
		[Export(PropertyHint.Range, "0,0.99,0.01")]
		public float LifestealPercent { get; set; } = 0f;

		// 植物：命中减速（粘液蛛网，命中后目标移速 × 该值）
		[Export(PropertyHint.Range, "0.01,0.99,0.01")]
		public float HitSlowMoveMultiplier { get; set; } = 1f;

		// 恶魔：立场范围加成（地狱扩散）
		[Export(PropertyHint.Range, "0,99,1")]
		public int FieldRadiusBonus { get; set; } = 0;

		// 恶魔：立场上移速加成（急行军）
		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float FieldMoveSpeedBonus { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float HpBonusPercent { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,0.99,0.01")]
		public float KineticResist { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,0.99,0.01")]
		public float ThermalResist { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,0.99,0.01")]
		public float ExplosiveResist { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,0.99,0.01")]
		public float EmResist { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,0.99,0.01")]
		public float BeamResist { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,999,1")]
		public int SelfCreepRadius { get; set; } = 0;

		[Export(PropertyHint.Range, "1,9.99,0.01")]
		public float SpeedOnCreepMultiplier { get; set; } = 1f;

		[Export] public bool BehemothCanHarvest { get; set; } = false;

		// =========================================================
		// 联盟科技效果（数值全部走配置表）
		// =========================================================

		[ExportGroup("Union Effects")]
		// 再生钢：机械单位每秒回血
		[Export] public float UnitHpRegenPerSecond { get; set; } = 0f;
		// 流线型：飞行单位移速倍率（1.2 = +20%）
		[Export] public float MoveSpeedMultiplier { get; set; } = 1f;
		// 光学瞄具：生物单位射程加成（世界单位）
		[Export] public float AttackRangeBonus { get; set; } = 0f;
		// 一体铸造：机械单位生产时间倍率（0.7 = -30%）
		[Export] public float ProductionTimeMultiplier { get; set; } = 1f;

		// 兴奋剂（主动技能，数值预留给动作系统）：5 秒攻速/移速 +50%，自伤
		[Export] public float StimAttackSpeedMultiplier { get; set; } = 1f;
		[Export] public float StimMoveSpeedMultiplier { get; set; } = 1f;
		[Export] public float StimDurationSeconds { get; set; } = 0f;
		[Export] public float StimSelfDamage { get; set; } = 0f;

		// =========================================================
		// 泰伦科技效果
		// =========================================================

		[ExportGroup("Terran Effects")]
		// 铅弹：攻击附带击退（格），对长宽 > 1.5 单位无效
		[Export] public bool GrantKnockbackAttacks { get; set; } = false;
		[Export(PropertyHint.Range, "0,5,0.1")]
		public float KnockbackTiles { get; set; } = 0f;
		// 深埋工事：架设单位受到伤害减少
		[Export(PropertyHint.Range, "0,0.99,0.01")]
		public float DeployDamageReduction { get; set; } = 0f;
		// 快速展开：架设/收起时间倍率
		[Export(PropertyHint.Range, "0.1,2,0.05")]
		public float DeployTimeMultiplier { get; set; } = 1f;
		// 弹药回收：弹药上限倍率 + 击杀回弹
		[Export(PropertyHint.Range, "1,3,0.05")]
		public float AmmoCapMultiplier { get; set; } = 1f;
		[Export] public bool AmmoRefundOnKill { get; set; } = false;
		// 火药除沙：所有 AOE 半径 +N 格
		[Export(PropertyHint.Range, "0,9,0.5")]
		public float AoeRadiusBonusTiles { get; set; } = 0f;
		// 毁灭射线：允许攻击建筑 + 对建筑额外伤害
		[Export] public bool AllowStructureTargeting { get; set; } = false;
		[Export(PropertyHint.Range, "0,999,1")]
		public int BonusDamageVsStructure { get; set; } = 0;

		public bool IsValidConfig()
		{
			return !string.IsNullOrEmpty(TechId) &&
			       !string.IsNullOrEmpty(DisplayName) &&
			       ResearchTimeSeconds > 0f;
		}
	}
}
