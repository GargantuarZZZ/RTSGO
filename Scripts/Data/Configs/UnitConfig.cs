using Godot;

namespace RTS.Data.Configs
{
	/// <summary>
	/// 单位静态配置。
	///
	/// 适用对象：
	/// - 工人
	/// - 士兵
	/// - 载具
	/// - 英雄 / 指挥官
	/// - 中立野怪单位
	///
	/// 数据驱动原则：
	/// - 本配置只保存静态数据。
	/// - 不直接引用其他 Config Resource，尽量使用字符串 ID。
	/// - 运行时由 ConfigDatabase 通过 ID 查找具体配置。
	///
	/// 不要在这里存：
	/// - 当前血量
	/// - 当前目标
	/// - 当前路径
	/// - 当前 Buff
	/// - 当前攻击冷却
	/// </summary>
	[GlobalClass]
	public partial class UnitConfig : EntityConfig
	{
		// =========================================================
		// 移动
		// =========================================================

		[ExportGroup("Movement")]
		[Export(PropertyHint.Range, "0,999999,1")]
		public int MoveSpeed { get; set; } = 120;

		// 占地面积（格）：影响碰撞半径与挤位，1 格 = 64 世界单位
		[Export(PropertyHint.Range, "0.5,10,0.5")]
		public float FootprintTiles { get; set; } = 1f;

		// 禁疗：恶魔单位无法被任何方式治疗/维修/回血
		[Export] public bool CannotBeHealed { get; set; } = false;

		// 恶魔存续时间（秒）：0 = 无寿命限制；到期自动死亡，离开己方立场时寿命消耗 ×3
		[Export(PropertyHint.Range, "0,9999,1")]
		public float LifespanSeconds { get; set; } = 0f;

		// 自杀冲锋单位（熔岩旗手）：自动冲向最近敌人，碰到即自爆
		[Export] public bool Kamikaze { get; set; } = false;

		// 多足：无需控制（可自动行动/永远可选中操作）
		[Export] public bool NoControlNeeded { get; set; } = false;

		// 多足：控制范围（格），范围内的友军单位可被选中操作
		[Export(PropertyHint.Range, "0,99,1")]
		public int ControlRangeTiles { get; set; } = 0;

		// 多足：采集自动提交（无需交付建筑）
		[Export] public bool AutoSubmitHarvest { get; set; } = false;

		// 多足：范围采矿（半径格，每秒每点产率）
		[Export(PropertyHint.Range, "0,99,1")]
		public int AutoHarvestRadiusTiles { get; set; } = 0;
		[Export(PropertyHint.Range, "0,999,0.5")]
		public float AutoHarvestPerSecondPerNode { get; set; } = 0f;

		// 多足：远程建造射程（格）
		[Export(PropertyHint.Range, "1,99,1")]
		// 建造距离（格）：0 = 近身贴蓝图；>1 表示远程建造单位（多足 Builder / 洞穴工兵）
		public int BuildRangeTiles { get; set; } = 0;

		// 远程采集到达距离（格）：0 = 默认手臂距离（15 世界单位），与建造距离独立配置
		[Export(PropertyHint.Range, "0,99,1")]
		public int HarvestRangeTiles { get; set; } = 0;

		// 多足：该单位可作科技建筑（牧羊人型）
		[Export] public bool IsTechBuilding { get; set; } = false;
		[Export] public Godot.Collections.Array<string> ResearchableTechIds { get; set; } = new();

		// 多足：站在纳米菌毯上获得的全防御加成（战车型）
		[Export(PropertyHint.Range, "0,99,1")]
		public float CreepDefenseBonus { get; set; } = 0f;

		// 闲置时自动清理敌方菌毯（战车型）
		[Export] public bool AutoClearCreep { get; set; } = false;

		// 多足：腾跃（收割型，科技解锁）：自动跳到 2 格内敌人身前
		[Export] public bool HasLeap { get; set; } = false;
		[Export(PropertyHint.Range, "0.1,99,0.1")]
		public float LeapCooldownSeconds { get; set; } = 10f;
		[Export(PropertyHint.Range, "1,9,1")]
		public int LeapRangeTiles { get; set; } = 2;
		[Export] public string LeapTechId { get; set; } = "";

		// 多足：信鸽（游猎型，科技解锁）：死亡时揭示 20 格视野 3 秒
		[Export] public bool HasPigeon { get; set; } = false;
		[Export] public string PigeonTechId { get; set; } = "";

		// 单位自带立场半径（格）：熔岩旗手 / 地狱领主 / 火焰巨魔 / 旗帜
		[Export(PropertyHint.Range, "0,99,1")]
		public int FieldRadius { get; set; } = 0;

		// 立场光环（英雄）：范围内敌人攻速倍率 / 受伤倍率（0 = 无对应光环）
		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float FieldAuraEnemyAttackSpeedMultiplier { get; set; } = 0f;
		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float FieldAuraEnemyIncomingDamageMultiplier { get; set; } = 0f;
		[Export(PropertyHint.Range, "0.05,2,0.05")]
		public float FieldAuraTickSeconds { get; set; } = 0.3f;

		// 一轮攻击多个不同目标（地狱领主 5×3）
		[Export(PropertyHint.Range, "1,9,1")]
		public int MultiShotTargets { get; set; } = 1;

		// 英雄能量
		[Export(PropertyHint.Range, "0,999,1")]
		public float HeroEnergyMax { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,99,0.1")]
		public float HeroEnergyRegenPerSecond { get; set; } = 0f;

		// 技能1/技能2 数值（地狱领主：勾魂 / 狂热武装；火焰巨魔：咆哮 / 冲锋）
		[Export(PropertyHint.Range, "0,999,1")]
		public float Skill1Cost { get; set; } = 0f;
		[Export(PropertyHint.Range, "1,99,1")]
		public int Skill1RangeTiles { get; set; } = 0;
		[Export(PropertyHint.Range, "0,99,0.5")]
		public float Skill1DurationSeconds { get; set; } = 0f;
		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float Skill1MoveSpeedBonus { get; set; } = 0f;
		[Export(PropertyHint.Range, "0,9.99,0.01")]
		public float Skill1EnemyMovePenalty { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,999,1")]
		public float Skill2Cost { get; set; } = 0f;
		[Export(PropertyHint.Range, "1,99,1")]
		public int Skill2RangeTiles { get; set; } = 0;
		[Export(PropertyHint.Range, "0,99,0.5")]
		public float Skill2DurationSeconds { get; set; } = 0f;
		[Export(PropertyHint.Range, "0,99,0.1")]
		public float Skill2Multiplier { get; set; } = 1f;
		[Export(PropertyHint.Range, "0,9999,1")]
		public float Skill2Damage { get; set; } = 0f;

		// 技能目标模式：0=自施 1=敌方单位 2=己方生产建筑 3=地面方向
		[Export(PropertyHint.Range, "0,3,1")]
		public int Skill1TargetMode { get; set; } = 0;
		[Export(PropertyHint.Range, "0,3,1")]
		public int Skill2TargetMode { get; set; } = 0;

		// 技能按钮体制化：Kind 0=无 1=英雄能量技能 2=电浆炮 3=自动/手动切换 4=兴奋剂 5=机动模式
		[Export(PropertyHint.Range, "0,5,1")]
		public int Skill1Kind { get; set; } = 0;
		[Export] public string Skill1Name { get; set; } = "";
		[Export(PropertyHint.Range, "0,5,1")]
		public int Skill2Kind { get; set; } = 0;
		[Export] public string Skill2Name { get; set; } = "";

		// 技能行为 ID：英雄技能的效果实现类（如 DemonCharm / DemonRoar / DemonBoost / DemonDash）
		[Export] public string Skill1BehaviorId { get; set; } = "";
		[Export] public string Skill2BehaviorId { get; set; } = "";

		// 电浆炮状态机参数（Kind=2 时生效）：前摇 / 后摇 / 冷却
		[Export(PropertyHint.Range, "0,60,0.5")]
		public float PlasmaWindupSeconds { get; set; } = 5f;
		[Export(PropertyHint.Range, "0,60,0.5")]
		public float PlasmaRecoverySeconds { get; set; } = 3f;
		[Export(PropertyHint.Range, "0,120,0.5")]
		public float PlasmaCooldownSeconds { get; set; } = 15f;
		[Export(PropertyHint.Range, "0,99,1")]
		public int PlasmaAutoTargetRangeTiles { get; set; } = 20;

		[ExportGroup("Flight")]
		[Export] public bool IsAir { get; set; } = false;

		[Export(PropertyHint.Range, "0,9999,1")]
		public float FlyHeight { get; set; } = 0f;

		// 单位标签（可多个）：Worker / Biological / Mechanical / Air / HeavyArmor 等，
		// 供武器“对某类单位加成”与科技判定使用
		[Export] public Godot.Collections.Array<string> Tags { get; set; } = new();

		// 沙虫节段等“身体附属单位”：不可选中、不响应指令、只跟随队长（供 UI/AI 过滤）
		[Export] public bool IsSegment { get; set; } = false;

		[Export(PropertyHint.Range, "0,999999,1")]
		public int Acceleration { get; set; } = 9999;

		[Export(PropertyHint.Range, "0,999999,1")]
		public int TurnSpeed { get; set; } = 9999;

		[Export] public bool CanMove { get; set; } = true;

		// 是否默认无视单位碰撞。
		// 例如幽灵单位、飞行单位、特殊召唤物。
		[Export] public bool IsGhostByDefault { get; set; } = false;

		// =========================================================
		// 人口
		// =========================================================

		[ExportGroup("Supply")]
		[Export(PropertyHint.Range, "0,999,1")]
		public int SupplyCost { get; set; } = 1;

		[Export(PropertyHint.Range, "0,999,1")]
		public int SupplyProvided { get; set; } = 0;

		// 单位占用人口（牧羊人 -5）
		[Export(PropertyHint.Range, "0,999,1")]
		public int SupplyUsed { get; set; } = 0;

		// =========================================================
		// 武器
		// =========================================================

		[ExportGroup("Combat")]

		// 武器 ID 列表。
		// 例如：
		// - "union_rifle"
		// - "tank_cannon"
		// - "healer_beam"
		//
		// 运行时通过 ConfigDatabase.GetWeapon(id) 获取 WeaponConfig。
		[Export] public Godot.Collections.Array<string> WeaponIds { get; set; } = new();

		// 没有武器时是否仍允许 AttackMove。
		// 例如治疗单位、纯辅助单位可以参与队列但不主动攻击。
		[Export] public bool AllowAttackMoveWithoutWeapon { get; set; } = false;

		// 多武器交替发射的轮换间隔（秒）。0 = 只要冷却好就开火。
		[Export(PropertyHint.Range, "0,5,0.05")]
		public float WeaponRotationInterval { get; set; } = 0.15f;

		// 右键移动默认变成攻击移动（边走边自动索敌），例如纳米巨兽
		[Export] public bool DefaultAttackMove { get; set; } = false;

		// 攻击移动时是否边移动边开火（跑打）；默认 false = 停下开火
		[Export] public bool AttackMoveFiresWhileMoving { get; set; } = false;

		// =========================================================
		// 采集
		// =========================================================

		[ExportGroup("Harvest")]
		[Export] public bool CanHarvest { get; set; } = false;

		[Export(PropertyHint.Range, "0,999999,1")]
		public int HarvestAmountPerCycle { get; set; } = 10;

		// 采集一轮需要多少逻辑 Tick。
		// 20Hz 下：20 Tick = 1 秒。
		[Export(PropertyHint.Range, "1,999999,1")]
		public int HarvestCycleTicks { get; set; } = 20;

		[Export(PropertyHint.Range, "0,999999,1")]
		public int CarryCapacity { get; set; } = 50;

		[Export] public Godot.Collections.Array<ResourceType> HarvestableResources { get; set; } = new();

		// =========================================================
		// 建造
		// =========================================================

		[ExportGroup("Build")]
		[Export] public bool CanBuild { get; set; } = false;

		// 可建造建筑 ID 列表。
		// 例如：
		// - "union_command_center"
		// - "union_barracks"
		// - "union_turret"
		//
		// 不直接引用 StructureConfig，避免 Resource 循环引用。
		[Export] public Godot.Collections.Array<string> BuildableStructureIds { get; set; } = new();

		// 建造效率百分比。
		// 100 = 标准速度，200 = 两倍施工速度。
		[Export(PropertyHint.Range, "1,9999,1")]
		public int BuildPowerPercent { get; set; } = 100;

		// =========================================================
		// 生产 / 解锁
		// =========================================================

		[ExportGroup("Production")]
		[Export] public bool IsWorker { get; set; } = false;
		[Export] public bool IsHero { get; set; } = false;
		[Export] public bool IsSummoned { get; set; } = false;

		// 需要哪些科技 ID 才能生产 / 解锁该单位。
		// 后续 TechConfig 也通过 ID 接入。
		[Export] public Godot.Collections.Array<string> RequiredTechIds { get; set; } = new();

		// =========================================================
		// 维修（SCV）
		// =========================================================

		[ExportGroup("Repair")]
		[Export] public bool CanRepair { get; set; } = false;

		[Export(PropertyHint.Range, "0,9999,1")]
		public float RepairPerSecond { get; set; } = 20f;

		[Export(PropertyHint.Range, "0,99,0.01")]
		public float RepairCostPerHp { get; set; } = 0.1f;

		[Export(PropertyHint.Range, "0,999,1")]
		public float RepairRange { get; set; } = 80f;

		// =========================================================
		// 治疗（医疗兵自动治疗）
		// =========================================================

		[ExportGroup("Heal")]
		[Export] public bool CanHeal { get; set; } = false;

		[Export(PropertyHint.Range, "1,99,1")]
		public int HealRangeTiles { get; set; } = 4;

		[Export(PropertyHint.Range, "0,999,0.5")]
		public float HealPerSecond { get; set; } = 0f;

		// =========================================================
		// 机动模式（战列巡洋舰）：短时加速且暂时无法攻击
		// =========================================================

		[ExportGroup("Mobility Mode")]
		[Export(PropertyHint.Range, "0,999,1")]
		public float MobilitySpeedBonus { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,999,0.5")]
		public float MobilityDurationSeconds { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,999,0.5")]
		public float MobilityCooldownSeconds { get; set; } = 0f;

		[Export(PropertyHint.Range, "1,99,0.5")]
		public float MobilitySpeedMultiplier { get; set; } = 0f;

		[Export(PropertyHint.Range, "1,99,1")]
		public int MobilityRangeTiles { get; set; } = 30;

		// =========================================================
		// 泰伦：弹药 / 架设 / 驻扎
		// =========================================================

		[ExportGroup("Terran")]
		// 弹药上限（0 = 无弹药机制）
		[Export(PropertyHint.Range, "0,9999,1")]
		public int MaxAmmo { get; set; } = 0;

		// 可架设：停止 1 秒后进入架设状态
		[Export] public bool CanDeploy { get; set; } = false;
		[Export(PropertyHint.Range, "0.1,10,0.1")]
		public float DeployTimeSeconds { get; set; } = 1f;
		// 架设后最大生命倍率（大兵/重装 = 2）
		[Export(PropertyHint.Range, "1,5,0.1")]
		public float DeployedMaxHpMultiplier { get; set; } = 1f;
		// 架设后射程加成（格）
		[Export(PropertyHint.Range, "0,20,0.5")]
		public float DeployedAttackRangeBonusTiles { get; set; } = 0f;
		// 架设后攻击变为该半径 AOE（格，0 = 不变）
		[Export(PropertyHint.Range, "0,10,0.25")]
		public float DeployedAoeRadiusTiles { get; set; } = 0f;
		// 只有架设时武器可用（导弹车 / 解放者）
		[Export] public bool DeployOnlyWeapon { get; set; } = false;
		// 架设时需要指定攻击范围圈（解放者；v1 暂按架设点附近自动索敌）
		[Export] public bool DeployRequiresTargetCircle { get; set; } = false;
		[Export(PropertyHint.Range, "1,20,0.5")]
		public float DeployTargetCircleTiles { get; set; } = 3f;

		// 工程兵可驻扎（藻类工厂）
		[Export] public bool CanGarrison { get; set; } = false;

		// 可切换弹种（重装步兵：高爆 / 穿甲）
		[Export] public bool CanSwitchAmmoMode { get; set; } = false;

		// 指挥车：能量在弹药范围内 +1/s，范围外 -1/s
		[Export] public bool EnergyRegenInAmmoRange { get; set; } = false;

		// 技能 2 需要科技（干扰弹）
		[Export] public string Skill2RequiredTechId { get; set; } = "";

		// 烟雾弹等范围技能的射程修正（世界单位，负数为减射程）
		[Export(PropertyHint.Range, "-512,512,8")]
		public float Skill1AttackRangeBonus { get; set; } = 0f;

		// =========================================================
		// 队伍定位
		// =========================================================

		[ExportGroup("Role")]
		[Export] public UnitRoleType Role { get; set; } = UnitRoleType.Basic;

		// 0~5，给 UI、AI、种族说明使用。
		[Export(PropertyHint.Range, "0,5,1")]
		public int OffenseRating { get; set; } = 1;

		[Export(PropertyHint.Range, "0,5,1")]
		public int DefenseRating { get; set; } = 1;

		[Export(PropertyHint.Range, "0,5,1")]
		public int SupportRating { get; set; } = 0;

		[Export(PropertyHint.Range, "0,5,1")]
		public int EconomyRating { get; set; } = 0;

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

		public bool CanHarvestResource(ResourceType type)
		{
			if (!CanHarvest)
				return false;

			foreach (ResourceType item in HarvestableResources)
			{
				if (item == type)
					return true;
			}

			return false;
		}

		public bool CanBuildStructure(string structureId)
		{
			if (!CanBuild || string.IsNullOrEmpty(structureId))
				return false;

			foreach (string id in BuildableStructureIds)
			{
				if (id == structureId)
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

			if (CanMove && MoveSpeed <= 0)
				return false;

			if (SupplyCost < 0)
				return false;

			if (CanHarvest)
			{
				if (HarvestAmountPerCycle <= 0)
					return false;

				if (HarvestCycleTicks <= 0)
					return false;

				if (CarryCapacity <= 0)
					return false;
			}

			if (CanBuild && BuildableStructureIds.Count == 0)
				return false;

			return true;
		}
	}

	public enum UnitRoleType
	{
		Basic,
		Worker,
		Fighter,
		Tank,
		Ranged,
		Siege,
		Scout,
		Support,
		Healer,
		Controller,
		Hero,
		Summon,
		Neutral
	}
}
