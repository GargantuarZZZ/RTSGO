using Godot;
using RTS.Data.Configs;

namespace RTS.Actions.Implementation
{
	// =========================================================
	// 巫师族面板技能（8 个）
	//
	// 与其它面板技能一样：**Action 类只负责按钮显示与目标选择模式**，
	// 真正的扣费/伤害/召唤/传送在 SimManager 的确定性处理里做
	// （派发入口见 SimManager 的 HandleNetAction）。
	// 所以这些类都很薄 —— 这是本项目一贯的分层，不要在这里写机制。
	//
	// 能量成本统一读 StructureConfig.SkillEnergyCost（通用字段），
	// 所以新增技能不需要再动基类。
	// =========================================================

	/// <summary>巫师面板技能的公共基类：能量成本取通用字段。</summary>
	public abstract partial class WizardSkillAction : EnergyCostAbilityAction
	{
		protected override float GetEnergyCost(StructureConfig cfg) => cfg.SkillEnergyCost;
	}

	/// <summary>召唤石魔像：视野内选点召唤，存在时间有限（配置里有寿限）。</summary>
	[GlobalClass]
	public partial class SummonStoneGolemAction : WizardSkillAction
	{
		[Export] public string DisplayNameText = "召唤石魔像";
		protected override string ActionId => "SummonStoneGolem";
	}

	/// <summary>召唤土魔像：同石魔像，但更肉且每秒回血。</summary>
	[GlobalClass]
	public partial class SummonEarthGolemAction : WizardSkillAction
	{
		[Export] public string DisplayNameText = "召唤土魔像";
		protected override string ActionId => "SummonEarthGolem";
	}

	/// <summary>
	/// 传送阵：选第一个区域 → 选第二个区域，把第一个区域内的单位传送到第二个区域。
	/// 两段选点由 UserController 的待定目标流程驱动（第二次点击才发指令）。
	/// </summary>
	[GlobalClass]
	public partial class TeleportFieldAction : WizardSkillAction
	{
		[Export] public string DisplayNameText = "传送阵";
		protected override string ActionId => "TeleportField";
	}

	/// <summary>空间冻结：选区域，短暂延迟后冻结其中所有单位（不能行动、不能被攻击）。</summary>
	[GlobalClass]
	public partial class TimeFreezeAction : WizardSkillAction
	{
		[Export] public string DisplayNameText = "空间冻结";
		protected override string ActionId => "TimeFreeze";
	}

	/// <summary>火雨：对区域造成热能范围伤害。可充能（配置 SkillMaxCharges）。</summary>
	[GlobalClass]
	public partial class FireRainAction : WizardSkillAction
	{
		[Export] public string DisplayNameText = "火雨";
		protected override string ActionId => "FireRain";
	}

	/// <summary>水墙：创造一个方形阻挡区，阻挡地面单位，持续一段时间。</summary>
	[GlobalClass]
	public partial class WaterWallAction : WizardSkillAction
	{
		[Export] public string DisplayNameText = "水墙";
		protected override string ActionId => "WaterWall";
	}

	/// <summary>振奋旋律：区域内所有单位移速与攻速提升（不分敌我，按表格定义）。</summary>
	[GlobalClass]
	public partial class InspireMelodyAction : WizardSkillAction
	{
		[Export] public string DisplayNameText = "振奋旋律";
		protected override string ActionId => "InspireMelody";
	}

	/// <summary>庄严旋律：区域内所有单位移速下降、所受伤害减半。</summary>
	[GlobalClass]
	public partial class SolemnMelodyAction : WizardSkillAction
	{
		[Export] public string DisplayNameText = "庄严旋律";
		protected override string ActionId => "SolemnMelody";
	}
}

namespace RTS.Actions.Implementation
{
	/// <summary>
	/// AI 指挥系统 - 基地车坐地/收起。
	///
	/// 与泰伦的 DeployAction 不同：架设是**单位内部改状态**（SimUnit.DeployState），
	/// 这个是**换成另一个实体**（坐地=建筑，收起=基地车），
	/// 真正的转换在 SimManager.HandleAIDeploy 里做。
	/// 放在这个文件是因为同属"面板技能形态"，且都只依赖同一个基类。
	/// </summary>
	[GlobalClass]
	public partial class AIDeployAction : AliveUnitAbilityAction
	{
		[Export] public string DisplayNameText = "坐地/收起";
		protected override string ActionId => "AIDeploy";
		protected override ActionLayer Blocking => ActionLayer.Ability | ActionLayer.Movement;
	}
}