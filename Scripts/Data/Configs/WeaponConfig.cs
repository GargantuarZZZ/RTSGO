using Godot;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Data.Configs
{
	// 弹道行为：决定开火后“伤害如何到达目标”
	public enum ProjectileBehavior
	{
		Instant = 0, // 即时命中：无弹体，开火瞬间结算（光束/近战/扇形）
		Homing = 1,  // 跟踪弹道：弹体持续追击目标当前位置（默认弹道）
		Fixed = 2,   // 延迟直线：朝开火时目标方向直飞，不追踪，超程脱靶
		Lob = 3      // 抛物线抛射：固定飞行时间落到目标点/落点
	}

	/// <summary>
	/// 武器静态配置。
	///
	/// 用途：
	/// - 单位普通攻击
	/// - 建筑防御武器
	/// - 中立敌人攻击
	/// - 后续也可以被技能复用
	///
	/// 注意：
	/// 这里只存武器的基础配置。
	/// 当前冷却、当前目标、当前弹体等运行时状态不要放在这里。
	/// </summary>
	[GlobalClass]
	public partial class WeaponConfig : Resource
	{
		// =========================================================
		// 基础识别
		// =========================================================

		[ExportGroup("Identity")]
		[Export] public string WeaponId { get; set; } = "";
		[Export] public string DisplayName { get; set; } = "";

		// =========================================================
		// 伤害
		// =========================================================

		[ExportGroup("Damage")]
		[Export(PropertyHint.Range, "0,999999,1")]
		public int Damage { get; set; } = 10;

		// 针对特定寿衣（护甲类型）的额外伤害，例如“动能30+40(重甲)”
		[Export] public ArmorType BonusDamageVsArmorType { get; set; } = ArmorType.Light;
		[Export(PropertyHint.Range, "0,999999,1")]
		public int BonusDamage { get; set; } = 0;

		// 命中目标带这些 tag 之一时追加 BonusDamage（如 对工人 / 对空 / 对重甲）
		[Export] public Godot.Collections.Array<string> BonusDamageTags { get; set; } = new();

		[Export] public DamageType DamageType { get; set; } = DamageType.Kinetic;

		[Export] public WeaponRangeType RangeType { get; set; } = WeaponRangeType.Ranged;

		// =========================================================
		// 射程 / 攻速
		// =========================================================

		[ExportGroup("Range & Timing")]
		[Export(PropertyHint.Range, "0,999999,1")]
		public int AttackRange { get; set; } = 300;

		// 攻击间隔，单位：逻辑 Tick。
		// 20Hz 下：20 Tick = 1 秒。
		[Export(PropertyHint.Range, "1,999999,1")]
		public int CooldownTicks { get; set; } = 20;

		// 攻击前摇，单位：逻辑 Tick。
		// 用于以后做“抬手后才造成伤害”。
		[Export(PropertyHint.Range, "0,999999,1")]
		public int WindupTicks { get; set; } = 0;

		// =========================================================
		// 弹体
		// =========================================================

		[ExportGroup("Projectile")]
		[Export] public ProjectileBehavior ProjectileBehavior { get; set; } = ProjectileBehavior.Instant;

		// 扇形攻击（喷火龙）：对射程内扇形范围敌人造成伤害，0 = 不用扇形
		[Export(PropertyHint.Range, "0,120,1")]
		public float ConeAngleDegrees { get; set; } = 0f;

		[Export] public PackedScene ProjectileScene { get; set; }

		[Export(PropertyHint.Range, "0,999999,1")]
		public int ProjectileSpeed { get; set; } = 600;

		[Export(PropertyHint.Range, "0,999999,1")]
		public int HitRadius { get; set; } = 16;

		// 抛物线抛射参数（ProjectileBehavior = Lob 时生效）
		[Export(PropertyHint.Range, "0.1,10,0.05")]
		public float LobDurationSeconds { get; set; } = 1f;

		[Export(PropertyHint.Range, "0,9999,1")]
		public float LobHeight { get; set; } = 0f;

		// =========================================================
		// 命中覆盖（特殊命中结算，如电浆炮“按长宽² 伤害 + 减速”）
		// =========================================================

		[ExportGroup("Impact Override")]
		// 伤害 = ImpactDamagePerFootprintSq × 目标长宽²（单位看 FootprintTiles，建筑看 GridSize）
		[Export] public bool FootprintScaledDamage { get; set; } = false;

		[Export(PropertyHint.Range, "0,99999,1")]
		public int ImpactDamagePerFootprintSq { get; set; } = 50;

		// 命中结算半径（格），0 = 只结算落点
		[Export(PropertyHint.Range, "0,99,1")]
		public int ImpactRadiusTiles { get; set; } = 0;

		// 命中后给范围内敌人上的减速/减攻速（0 = 不上）
		[Export(PropertyHint.Range, "0,0.99,0.01")]
		public float ImpactSlowMoveMultiplier { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,0.99,0.01")]
		public float ImpactSlowAttackMultiplier { get; set; } = 0f;

		[Export(PropertyHint.Range, "0,99,0.5")]
		public float ImpactSlowSeconds { get; set; } = 0f;

		// =========================================================
		// 目标限制
		// =========================================================

		[ExportGroup("Targeting")]
		[Export] public bool CanTargetGround { get; set; } = true;
		[Export] public bool CanTargetAir { get; set; } = false;
		[Export] public bool CanTargetStructure { get; set; } = true;
		[Export] public bool CanTargetNeutral { get; set; } = true;

		// 例如：Infantry, Mechanical, Biological, Structure
		// 为空表示不限制标签。
		[Export] public Godot.Collections.Array<string> RequiredTargetTags { get; set; } = new();

		// 例如：Invulnerable, Untargetable
		[Export] public Godot.Collections.Array<string> ForbiddenTargetTags { get; set; } = new();

		// =========================================================
		// 范围伤害，先预留
		// =========================================================

		[ExportGroup("Area Damage")]
		[Export] public bool HasAreaDamage { get; set; } = false;

		[Export(PropertyHint.Range, "0,999999,1")]
		public int AreaRadius { get; set; } = 0;

		// 0~100，边缘伤害百分比。
		[Export(PropertyHint.Range, "0,100,1")]
		public int AreaEdgeDamagePercent { get; set; } = 100;

		// =========================================================
		// 命中特效（纯表现层，数值也走配置表）
		// =========================================================

		[ExportGroup("Hit FX")]
		[Export(PropertyHint.Range, "0,9999,1")]
		public float HitFxRadius { get; set; } = 36f;

		[Export(PropertyHint.Range, "0.05,5,0.05")]
		public float HitFxDuration { get; set; } = 0.3f;

		[Export]
		public Color HitFxColor { get; set; } = new Color(1f, 0.75f, 0.2f, 1f);

		// 即时命中武器的弹道光束 / 枪口闪光
		[Export(PropertyHint.Range, "0.5,50,0.5")]
		public float TracerThickness { get; set; } = 3.5f;

		[Export(PropertyHint.Range, "0.05,2,0.05")]
		public float TracerDuration { get; set; } = 0.18f;

		[Export(PropertyHint.Range, "0,200,1")]
		public float MuzzleFlashRadius { get; set; } = 18f;

		[Export(PropertyHint.Range, "0.05,2,0.05")]
		public float MuzzleFlashDuration { get; set; } = 0.1f;

		// =========================================================
		// 工具方法
		// =========================================================

		public bool IsProjectile => ProjectileBehavior != ProjectileBehavior.Instant;

		// 映射为纯逻辑弹体规格：模拟层不依赖 Godot 配置资源
		public ProjectileSpec ToProjectileSpec()
		{
			return new ProjectileSpec
			{
				Motion = ProjectileBehavior switch
				{
					ProjectileBehavior.Fixed => ProjectileMotion.Fixed,
					ProjectileBehavior.Lob => ProjectileMotion.Lob,
					_ => ProjectileMotion.Homing
				},
				Speed = (FP)ProjectileSpeed,
				HitRadius = (FP)HitRadius,
				MaxTravel = ProjectileBehavior == ProjectileBehavior.Fixed
					? (FP)AttackRange + (FP)192m
					: FP.Zero,
				LobDuration = (FP)LobDurationSeconds,
				LobHeight = (FP)LobHeight,
				FootprintScaledDamage = FootprintScaledDamage,
				ImpactRadius = (FP)(ImpactRadiusTiles * 64),
				ImpactDamagePerFootprintSq = (FP)ImpactDamagePerFootprintSq,
				ImpactSlowMoveMultiplier = (FP)ImpactSlowMoveMultiplier,
				ImpactSlowAttackMultiplier = (FP)ImpactSlowAttackMultiplier,
				ImpactSlowSeconds = (FP)ImpactSlowSeconds
			};
		}

		public bool IsValidConfig()
		{
			if (string.IsNullOrEmpty(WeaponId))
				return false;

			if (Damage < 0)
				return false;

			if (CooldownTicks <= 0)
				return false;

			if (AttackRange < 0)
				return false;

			if (IsProjectile && ProjectileSpeed <= 0)
				return false;

			return true;
		}

		public bool RequiresTag(string tag)
		{
			if (string.IsNullOrEmpty(tag))
				return false;

			foreach (string item in RequiredTargetTags)
			{
				if (item == tag)
					return true;
			}

			return false;
		}

		public bool ForbidsTag(string tag)
		{
			if (string.IsNullOrEmpty(tag))
				return false;

			foreach (string item in ForbiddenTargetTags)
			{
				if (item == tag)
					return true;
			}

			return false;
		}
	}
}
