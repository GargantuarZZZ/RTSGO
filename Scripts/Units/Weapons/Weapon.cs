// File: res://Scripts/Units/Weapons/Weapon.cs
using Godot;
using RTS.Data;
using RTS.Simulation; // 引入 FPVector2
using FP = FixMath.NET.Fix64;

namespace RTS.Units
{
	[GlobalClass]
	public abstract partial class Weapon : Node3D
	{
		[ExportGroup("Weapon Stats")]
		[Export] public string WeaponName { get; set; } = "Standard Weapon";
		[Export] public DamageType DmgType { get; set; } = DamageType.Kinetic;
		[Export] public WeaponRangeType RangeType { get; set; } = WeaponRangeType.Ranged;

		[Export] public float Damage { get; set; } = 10.0f;
		[Export] public ArmorType BonusDamageVsArmorType { get; set; } = ArmorType.Light;
		[Export] public float BonusDamage { get; set; } = 0f;
		[Export] public Godot.Collections.Array<string> BonusDamageTags { get; set; } = new();
		[Export] public float AttackRange { get; set; } = 200.0f;
		[Export] public float Cooldown { get; set; } = 1.0f;

		[ExportGroup("Fire Point")]
		/// <summary>开火点：在预制体里挂一个节点（Marker3D / Node3D），子弹和特效都从这里出。</summary>
		[Export] public Node3D FirePoint { get; set; }

		/// <summary>开火点世界坐标；没挂节点时退回武器自身位置。</summary>
		public Vector3 GetFirePointWorld()
		{
			// 视觉闭包可能延迟到主线程执行：开火单位可能已死亡释放，必须守卫
			if (!GodotObject.IsInstanceValid(this))
				return Vector3.Zero;
			if (FirePoint != null && GodotObject.IsInstanceValid(FirePoint))
				return FirePoint.GlobalPosition;
			return GlobalPosition;
		}

		[ExportGroup("Targeting")]
		[Export] public bool CanTargetGround { get; set; } = true;
		[Export] public bool CanTargetAir { get; set; } = false;
		[Export] public bool CanTargetStructure { get; set; } = true;
		[Export] public bool CanTargetNeutral { get; set; } = true;
		[Export] public bool HasAreaDamage { get; set; } = false;
		[Export] public float AreaRadius { get; set; } = 0f;
		[Export] public int AreaEdgeDamagePercent { get; set; } = 100;
		[Export] public float ConeAngleDegrees { get; set; } = 0f;

		protected IEntity _owner;
		/// <summary>武器在所属单位武器列表中的索引（P0-1：冷却计时器本体存 SimUnit）。</summary>
		public int WeaponIndex = -1;
		private FP _localCooldown = FP.Zero;
		// P0-1：冷却读写走 SimUnit（确定性/哈希）；无 SimUnit（编辑器/测试）时退回本地字段
		protected FP CurrentCooldown
		{
			get
			{
				var u = OwnerUnit;
				return u != null && WeaponIndex >= 0 ? u.GetWeaponCooldown(WeaponIndex) : _localCooldown;
			}
			set
			{
				var u = OwnerUnit;
				if (u != null && WeaponIndex >= 0)
					u.SetWeaponCooldown(WeaponIndex, value);
				else
					_localCooldown = value;
			}
		}

		public virtual void Initialize(IEntity owner) => _owner = owner;

		// 场景武器节点的名字未必等于配置 ID，这里按 WeaponName（回退到节点名）统一套用配置表数值
		public void ApplyConfig()
		{
			string id = !string.IsNullOrEmpty(WeaponName) && WeaponName != "Standard Weapon"
				? WeaponName
				: Name;

			var cfg = RTS.Data.Configs.ConfigDatabase.GetWeapon(id);
			if (cfg == null)
				return;

			Damage = cfg.Damage;
			AttackRange = cfg.AttackRange;
			Cooldown = cfg.CooldownTicks / 20f;
			DmgType = (DamageType)cfg.DamageType;
			RangeType = cfg.RangeType;
			BonusDamage = cfg.BonusDamage;
			BonusDamageVsArmorType = cfg.BonusDamageVsArmorType;
			BonusDamageTags = cfg.BonusDamageTags;
			CanTargetGround = cfg.CanTargetGround;
			CanTargetAir = cfg.CanTargetAir;
			CanTargetStructure = cfg.CanTargetStructure;
			CanTargetNeutral = cfg.CanTargetNeutral;
			HasAreaDamage = cfg.HasAreaDamage;
			AreaRadius = cfg.AreaRadius;
			AreaEdgeDamagePercent = cfg.AreaEdgeDamagePercent;
			ConeAngleDegrees = cfg.ConeAngleDegrees;
		}

		public virtual void Tick(double delta)
		{
			// 武器冷却必须使用逻辑时间，禁止渲染 delta
			if (CurrentCooldown > FP.Zero)
			{
				// 攻速光环：按倍率加速冷却恢复
				FP speedMult = _owner?.LogicEntity?.Buffs?.GetAttackSpeedMultiplier() ?? FP.One;
				CurrentCooldown -= RTS.Core.SimManager.Instance.World.FixedDelta * speedMult;
			}
		}

		// Fix64 定点误差：0.05f 转定点后与 FixedDelta(0.05m) 差约 1e-9，永远清不到 0。
		// 冷却剩余 ≤ 0.001 就视为就绪，否则 0.05s 的武器会变成每两 tick 才一枪。
		public bool CanFire() => CurrentCooldown <= (FP)0.001m;
		// 供 UI 显示剩余冷却（只读，不影响逻辑）
		public float CooldownRemaining => CurrentCooldown > FP.Zero ? (float)CurrentCooldown : 0f;
		// 确定性镜像：把节点内冷却暴露给模拟层（P0-1：冷却状态纳入 SimUnit 哈希）
		public FP CooldownRemainingFP => CurrentCooldown;

		private RTS.Simulation.SimUnit OwnerUnit => _owner?.LogicEntity as RTS.Simulation.SimUnit;

		// 弹药：每次攻击消耗 1 发；无弹药 / 仅架设可用武器未架设时不能开火
		protected bool TryConsumeAmmo()
		{
			var u = OwnerUnit;
			if (u == null || u.MaxAmmo <= FP.Zero)
				return true;
			if (u.Ammo <= FP.Zero)
				return false;
			u.Ammo -= FP.One;
			return true;
		}

		protected bool CanFireWithDeployAndAmmo()
		{
			var u = OwnerUnit;
			if (u != null && u.DeployOnlyWeapon && !u.IsDeployed)
				return false;
			return TryConsumeAmmo();
		}

		// 强制进入冷却（齐射后所有武器同步冷却，避免多武器轮流开火导致连续齐射）
		public void ForceCooldown() => CurrentCooldown = (FP)Cooldown;

		public bool CanTarget(IEntity target)
		{
			if (target == null || target.LogicEntity == null)
				return false;

			// 解放者范围圈：架设后只能攻击指定圈内的敌人
			var owner = OwnerUnit;
			if (owner != null && owner.DeployRequiresTargetCircle && owner.IsDeployed &&
				owner.DeployTargetRadius > FP.Zero &&
				!FPVector2.IsWithinRangeSq(
					target.LogicEntity.Position,
					owner.DeployTargetCenter,
					owner.DeployTargetRadius * owner.DeployTargetRadius))
				return false;

			bool targetIsStructure = target.IsStructure;
			bool targetIsAir = target.LogicEntity is RTS.Simulation.SimUnit su && su.IsAir;
			if (!targetIsAir && target.LogicEntity is RTS.Simulation.SimStructure ss)
				targetIsAir = ss.IsAir;
			bool targetIsNeutral = target.TeamID <= 0;

			bool structureOk = CanTargetStructure || OwnerUnit?.CanHitStructuresOverride == true;
			bool ok = targetIsStructure
				? (targetIsAir ? CanTargetAir : structureOk)
				: (targetIsAir ? CanTargetAir : CanTargetGround);

			if (targetIsNeutral && !CanTargetNeutral)
				ok = false;

			return ok;
		}

		public bool IsInRange(IEntity target)
		{
			if (target == null || _owner.LogicEntity == null || target.LogicEntity == null) return false;

			// 解放者等架设圈武器：圈内目标无视武器射程（射击圈即有效射程）
			if (_owner.LogicEntity is SimUnit deploySim &&
				deploySim.DeployRequiresTargetCircle && deploySim.IsDeployed &&
				deploySim.DeployTargetRadius > FP.Zero &&
				FPVector2.IsWithinRangeSq(
					target.LogicEntity.Position,
					deploySim.DeployTargetCenter,
					deploySim.DeployTargetRadius * deploySim.DeployTargetRadius))
				return true;

			FP distSq = FPVector2.DistanceSquared(_owner.LogicEntity.Position, target.LogicEntity.Position);

			// 将面板浮点数转成定点数
			FP rangeFp = (FP)Mathf.RoundToInt(AttackRange);
			if (_owner?.LogicEntity?.Buffs != null)
				rangeFp += _owner.LogicEntity.Buffs.GetAttackRangeBonus();

			// 近战：命中距离按“中心距 ≤ 射程 + 双方半径”算，避免大体积互相摸不到
			if (RangeType == WeaponRangeType.Melee && target.LogicEntity != null)
			{
				rangeFp += target.LogicEntity.Radius;
				if (_owner?.LogicEntity != null)
					rangeFp += _owner.LogicEntity.Radius;
			}
			return FPVector2.IsWithinRangeSq(_owner.LogicEntity.Position, target.LogicEntity.Position, rangeFp * rangeFp);
		}

		// 攻击判定统一入口：既能锁定目标类型，又在射程内
		public bool CanHit(IEntity target)
		{
			if (!CanTarget(target) || !IsInRange(target))
				return false;

			// 仅架设可用的武器：未架设时视为打不中（不进入索敌/攻击）
			var u = OwnerUnit;
			if (u != null && u.DeployOnlyWeapon && !u.IsDeployed)
				return false;

			return true;
		}

		public virtual void Fire(IEntity target)
		{
			if (!CanFire()) return;
			if (!CanFireWithDeployAndAmmo()) return;

			CurrentCooldown = (FP)Cooldown;
			OnFireVisuals(target);
			ApplyDamage(target);
			ApplyConeDamage(target);
		}

		// 一轮多段齐射（电浆炮光束线 10×15）：不逐个吃冷却，只在第一发结算冷却
		public void FireVolley(IEntity target, bool consumeCooldown)
		{
			if (target?.LogicEntity == null)
				return;

			if (!CanFireWithDeployAndAmmo())
				return;

			if (consumeCooldown)
				CurrentCooldown = (FP)Cooldown;

			OnFireVisuals(target);
			ApplyDamage(target);
			ApplyConeDamage(target);
		}

		// 扇形攻击（喷火龙龙息）：对射程内扇形范围所有敌人结算伤害
		protected void ApplyConeDamage(IEntity mainTarget)
		{
			if (ConeAngleDegrees <= 0f || _owner?.LogicEntity == null || mainTarget?.LogicEntity == null)
				return;

			var world = RTS.Core.SimManager.Instance?.World;
			if (world == null)
				return;

			FPVector2 origin = _owner.LogicEntity.Position;
			FPVector2 dir = mainTarget.LogicEntity.Position - origin;
			if (dir.X == FP.Zero && dir.Y == FP.Zero)
				return;

			dir = dir.Normalized();
			FP range = (FP)Mathf.RoundToInt(AttackRange);
			FP halfAngle = (FP)(Mathf.DegToRad(ConeAngleDegrees) * 0.5);
			FP cosHalf = FP.Cos(halfAngle);

			foreach (var u in world.Units.Values)
			{
				if (u == null || u.IsDead || u.ID == mainTarget.LogicEntity.ID)
					continue;
				if (u.TeamID == _owner.TeamID || u.TeamID <= 0)
					continue;

				FPVector2 to = u.Position - origin;
				FP dist = FP.Sqrt(to.MagnitudeSquared());
				if (dist > range || dist <= FP.Zero)
					continue;

				FP dot = (to.X * dir.X + to.Y * dir.Y) / dist;
				if (dot < cosHalf)
					continue;

				float bonus = 0f;

				if (BonusDamageTags.Count > 0)
				{
					var tagCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitTypeId);

					if (tagCfg != null)
					{
						foreach (string tag in BonusDamageTags)
						{
							if (tagCfg.Tags.Contains(tag))
							{
								bonus = BonusDamage;
								break;
							}
						}
					}
				}

				u.TakeDamage(
					(FP)Mathf.RoundToInt(Damage),
					(int)DmgType,
					(FP)Mathf.RoundToInt(bonus),
					(int)BonusDamageVsArmorType,
					_owner.LogicEntity as RTS.Simulation.SimEntity);
			}
		}

		// 攻击地面点（清菌毯等）：与普通攻击共用冷却与表现入口。
		// 返回 true 表示这一帧真正开火了（调用方应只在开火时结算命中）。
		public virtual bool FireAtGround(RTS.Simulation.FPVector2 groundPos)
		{
			if (!CanFire())
				return false;
			if (!CanFireWithDeployAndAmmo())
				return false;

			CurrentCooldown = (FP)Cooldown;
			Vector3 point = new Vector3((float)groundPos.X, 12f, (float)groundPos.Y);
			OnFireGroundVisuals(point);
			return true;
		}

		// 对地齐射（电浆炮清菌毯多段）：不逐发吞冷却，第一发结算即可
		public virtual void FireGroundVolley(RTS.Simulation.FPVector2 groundPos, bool consumeCooldown)
		{
			if (!CanFireWithDeployAndAmmo())
				return;

			if (consumeCooldown)
				CurrentCooldown = (FP)Cooldown;

			Vector3 point = new Vector3((float)groundPos.X, 12f, (float)groundPos.Y);
			OnFireGroundVisuals(point);
		}

		protected virtual void OnFireGroundVisuals(Vector3 groundPoint) { }

		protected virtual void ApplyDamage(IEntity target)
		{
			var source = _owner?.LogicEntity as RTS.Simulation.SimEntity;
			float bonus = HasTagBonus(target) ? BonusDamage : 0f;
			// 对特定护甲类型的加成（泰伦重装：20+20(对重甲)），无需依赖单位标签
			if (bonus <= 0f && BonusDamage > 0f &&
				target?.LogicEntity != null &&
				target.LogicEntity.ArmorType == (int)BonusDamageVsArmorType)
				bonus = BonusDamage;
			var u = OwnerUnit;

			// 架设 AOE（自行火炮）：架设后单体攻击变成范围伤害
			if (u != null && u.DeployAoeRadius > FP.Zero && u.IsDeployed && target.LogicEntity != null)
			{
				var world = RTS.Core.SimManager.Instance?.World;
				if (world != null)
				{
					world.ApplyAreaDamage(
						target.LogicEntity.Position,
						u.DeployAoeRadius + u.AoeRadiusBonus,
						100,
						source?.ID ?? -1,
						(FP)Damage,
						(int)DmgType,
						(FP)bonus,
						(int)BonusDamageVsArmorType);
				}
				return;
			}

			float dmg = Damage;
			if (u != null && u.BonusDamageVsStructure > FP.Zero && target.IsStructure)
				dmg += (float)u.BonusDamageVsStructure;

			target.LifeModule?.TakeDamage(dmg, DmgType, bonus, BonusDamageVsArmorType, source);
			ApplyAreaDamageAt(target);

			// 铅弹科技：攻击附带击退 0.5 格（长宽 > 1.5 的单位无效）
			if (u != null && u.HasKnockbackAttacks && u.KnockbackTiles > FP.Zero &&
				target.LogicEntity is RTS.Simulation.SimUnit targetUnit &&
				targetUnit.FootprintTiles <= (FP)1.5m &&
				source != null)
			{
				FPVector2 dir = targetUnit.Position - source.Position;
				if (dir.X != FP.Zero || dir.Y != FP.Zero)
					targetUnit.Position += dir.Normalized() * (u.KnockbackTiles * (FP)64m);
			}
		}

		// 命中目标带 BonusDamageTags 中任一 tag 时，追加 BonusDamage（如 对工人/对空/对重甲）
		protected bool HasTagBonus(IEntity target)
		{
			if (target == null || BonusDamageTags.Count == 0)
				return false;

			string id = target is Unit u ? u.UnitName : target is Structure s ? s.StructureName : "";
			if (string.IsNullOrEmpty(id))
				return false;

			var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(id);
			if (cfg == null)
				return false;

			foreach (string tag in BonusDamageTags)
			{
				if (cfg.Tags.Contains(tag))
					return true;
			}

			return false;
		}

		// 即时武器的溅射：主目标结算后，再对命中点周围敌方结算范围伤害（走同一套攻防/Buff）
		protected void ApplyAreaDamageAt(IEntity target)
		{
			if (!HasAreaDamage || target?.LogicEntity == null)
				return;

			var world = RTS.Core.SimManager.Instance?.World;
			if (world == null)
				return;

			world.ApplyAreaDamage(
				target.LogicEntity.Position,
				(FP)Mathf.RoundToInt(AreaRadius) + (OwnerUnit?.AoeRadiusBonus ?? FP.Zero),
				AreaEdgeDamagePercent,
				_owner?.LogicEntity?.ID ?? -1,
				(FP)Mathf.RoundToInt(Damage),
				(int)DmgType,
				(FP)Mathf.RoundToInt(HasTagBonus(target) ? BonusDamage : 0f),
				(int)BonusDamageVsArmorType);

			// 即时武器的溅射同样清理对应范围菌毯
			var world2 = RTS.Core.SimManager.Instance?.World;
			if (world2?.CreepGrid != null && AreaRadius > 0f)
			{
				var g = world2.Grid.WorldToGrid(target.LogicEntity.Position);
				int radiusTiles = Mathf.Max(1, Mathf.CeilToInt(AreaRadius / world2.Grid.TileSize));
				int sourceTeam = _owner?.LogicEntity?.TeamID ?? -1;
				world2.CreepGrid.DestroyNanoCreepRadius(g.X, g.Y, radiusTiles, sourceTeam,
					owner => sourceTeam <= 0
						? owner == sourceTeam
						: !RTS.Core.SimManager.Instance.AreTeamsHostile(sourceTeam, owner));
			}
		}

		protected virtual void OnFireVisuals(IEntity target) { }
	}
}
