// File: res://Scripts/Units/Modules/UnitCombat.cs
using Godot;
using System.Collections.Generic;
using RTS.Data;
using RTS.Units;
using FixMath.NET;
using FP = FixMath.NET.Fix64;

[GlobalClass]
	public partial class UnitCombat : EntityModuleBase
{
	[Export] public Node3D WeaponMountPoint;
	public List<Weapon> Weapons = new List<Weapon>();
	public Weapon ActiveWeapon { get; private set; }
	public Weapon LastGroundWeapon { get; private set; }
	private IEntity _localTarget;
	// P0-1 里程碑 3：索敌状态以 SimUnit.CombatTargetId 为准，节点只做 ID→实体解析
	public IEntity CurrentTarget
	{
		get
		{
			if (_simUnit == null)
				return _localTarget;
			if (_simUnit.CombatTargetId < 0)
				return null;
			var e = RTS.Core.SimManager.Instance?.FindEntityById(_simUnit.CombatTargetId);
			if (e == null || e.IsDeadOrNull())
			{
				_simUnit.CombatTargetId = -1;
				return null;
			}
			return e;
		}
		private set
		{
			if (_simUnit != null)
				_simUnit.CombatTargetId = value?.LogicEntity?.ID ?? -1;
			else
				_localTarget = value;
		}
	}
	private int _weaponIndex = 0;
	private int _groundVolleyIndex = 0;
	private FP _rotationInterval = (FP)0.15m;
	private FP _localRotationCooldown = FP.Zero;
	// P0-1：把战斗状态镜像进 SimUnit，纳入确定性哈希
	private RTS.Simulation.SimUnit _simUnit;
	// P0-1：交替发射冷却本体存 SimUnit（无 SimUnit 时退回本地）
	private FP RotationCooldown
	{
		get => _simUnit != null ? _simUnit.WeaponRotationCooldown : _localRotationCooldown;
		set
		{
			if (_simUnit != null)
				_simUnit.WeaponRotationCooldown = value;
			else
				_localRotationCooldown = value;
		}
	}

	// 索敌/追击用最大射程（多武器时以最远那把为准）
	public float GetAttackRange()
	{
		float best = 0f;
		foreach (var w in Weapons)
			best = Mathf.Max(best, w.AttackRange);

		// 射程修正（烟雾塔的敌人射程-1 等），最低 1 格
		FP bonus = _owner?.LogicEntity?.Buffs?.GetAttackRangeBonus() ?? FP.Zero;
		return Mathf.Max(64f, best + (float)bonus);
	}


	public override void Initialize(IEntity owner)
	{
		base.Initialize(owner);
		_simUnit = _owner?.LogicEntity as RTS.Simulation.SimUnit;
		if (WeaponMountPoint != null)
		{
			foreach (var child in WeaponMountPoint.GetChildren())
			{
				if (child is Weapon w)
				{
					w.Initialize(_owner);
					w.ApplyConfig();
					w.WeaponIndex = Weapons.Count;
					Weapons.Add(w);
				}
			}
		}
		if (Weapons.Count > 0) ActiveWeapon = Weapons[0];

		// 交替发射间隔从配置表读取（单位配置）
		var unitCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(owner?.DisplayName ?? "");
		if (unitCfg != null)
			_rotationInterval = (FP)unitCfg.WeaponRotationInterval;
	}

	// 科技追加武器（例如要塞化）时注册进战斗系统
		public void AddWeapon(Weapon weapon)
		{
			if (weapon == null || _owner == null)
				return;

		weapon.Initialize(_owner);
		weapon.ApplyConfig();
		weapon.WeaponIndex = Weapons.Count;
		Weapons.Add(weapon);

			if (ActiveWeapon == null)
				ActiveWeapon = weapon;
		}

		// 移除武器（驻扎民兵离场时清理）：同时修正 ActiveWeapon 引用
		public void RemoveWeapon(Weapon weapon)
		{
			if (weapon == null)
				return;

			Weapons.Remove(weapon);

			if (ActiveWeapon == weapon)
			{
				ActiveWeapon = Weapons.Count > 0 ? Weapons[0] : null;
				if (ActiveWeapon == null)
					LastGroundWeapon = null;
			}

			MirrorStateToSimUnit();
		}

		// 靶子/测试用：移除全部武器，单位不再攻击也不会被当作威胁。
		public void ClearWeapons()
	{
		Weapons.Clear();
		ActiveWeapon = null;
		LastGroundWeapon = null;
		CurrentTarget = null;
		_simUnit?.WeaponCooldowns.Clear();
		MirrorStateToSimUnit();
	}

	public override void Tick(double delta)
	{
		foreach (var w in Weapons) w.Tick(delta);

		if (RotationCooldown > FP.Zero)
		{
			var world = RTS.Core.SimManager.Instance?.World;
			RotationCooldown -= world != null ? world.FixedDelta : (FP)0.05m;
		}

		if (CurrentTarget != null && CurrentTarget.IsDeadOrNull()) CurrentTarget = null;
		MirrorStateToSimUnit();
	}

	// P0-1：把冷却/目标镜像到 SimUnit（模拟线程内只写定点数，不碰 Godot API）
	private void MirrorStateToSimUnit()
	{
		if (_simUnit == null)
			return;
		_simUnit.ActiveWeaponCooldown = ActiveWeapon?.CooldownRemainingFP ?? FP.Zero;
		_simUnit.WeaponRotationCooldown = RotationCooldown;
		_simUnit.CombatTargetId = CurrentTarget?.LogicEntity?.ID ?? -1;
	}

	// 齐射后所有武器同步冷却：一次 15 连发后必须等整轮冷却才能再开火
	public void ConsumeAllWeaponCooldowns()
	{
		foreach (var w in Weapons)
			w.ForceCooldown();
	}

		public void SetTarget(IEntity target)
		{
			// P1-5：同组盟友不是攻击目标（2v2）
			if (target != _owner &&
				RTS.Core.SimManager.Instance != null &&
				RTS.Core.SimManager.Instance.AreTeamsHostile(_owner.TeamID, target.TeamID))
				CurrentTarget = target;
			MirrorStateToSimUnit();
		}

		public bool CanAnyWeaponTarget(IEntity target)
		{
			if (target == null)
				return false;

		foreach (var w in Weapons)
		{
			if (w.CanTarget(target))
				return true;
		}

			return false;
		}

		// 任一武器当前能命中该目标（含近战体积修正）
		public bool CanAnyWeaponInRange(IEntity target)
		{
			if (target == null)
				return false;

			foreach (var w in Weapons)
			{
				if (w.CanHit(target))
					return true;
			}

			return false;
		}

		// 一轮齐射：选一把能命中目标的武器连发（冷却只由第一发结算）
		public bool TryVolley(IEntity target, bool consumeCooldown)
		{
			foreach (var w in Weapons)
			{
				if (w.CanHit(target))
				{
					w.FireVolley(target, consumeCooldown);
					return true;
				}
			}

			return false;
		}

	// 对地面点开火（清菌毯）：与普通攻击一样轮转武器，而不是只用 ActiveWeapon 一门
		public bool FireAtGround(RTS.Simulation.FPVector2 groundPos)
	{
		if (Weapons.Count == 0)
			return false;

		Weapon shooter = PickReadyWeapon(w => w.CanFire() && w.CanTargetGround);

		if (shooter == null)
			return false;

		bool fired = shooter.FireAtGround(groundPos);
		if (fired)
		{
			LastGroundWeapon = shooter;
			RotationCooldown = _rotationInterval;
			CycleWeapon();
		}

			return fired;
		}

		// 对地齐射：第一发走完整开火（含冷却），后续只播特效不重复吞冷却
		public void FireGroundVolley(RTS.Simulation.FPVector2 groundPos, bool consumeCooldown)
		{
			var groundWeapons = new System.Collections.Generic.List<Weapon>();

			foreach (var w in Weapons)
			{
				if (w.CanTargetGround)
					groundWeapons.Add(w);
			}

			if (groundWeapons.Count == 0)
				return;

			// 多枪齐射：按枪口轮转，每发用一把不同的炮
			groundWeapons[_groundVolleyIndex % groundWeapons.Count].FireGroundVolley(groundPos, consumeCooldown);
			_groundVolleyIndex++;

			if (_groundVolleyIndex >= int.MaxValue - 1)
				_groundVolleyIndex = 0;
		}

		public bool TryAttack(IEntity target)
		{
			// 沙虫潜地：地下状态无法攻击（节段跟随头部状态）
			if (_simUnit != null && _simUnit.IsSandwormBody)
			{
				bool burrowed = _simUnit.IsBurrowed;
				if (!burrowed && _simUnit.SegmentGroupId > 0 && _simUnit.SegmentGroupId != _simUnit.ID)
				{
					var wormHead = RTS.Core.SimManager.Instance?.World?
						.FindSimEntity(_simUnit.SegmentGroupId) as RTS.Simulation.SimUnit;
					burrowed = wormHead != null && wormHead.IsBurrowed;
				}
				if (burrowed)
					return false;
			}

			// 沙虫火车式：任意一节死亡 → 整条（含节段）无法攻击
			if (_simUnit != null && _simUnit.IsSandwormBody)
			{
				var wormHead = _simUnit.SegmentGroupId > 0 && _simUnit.SegmentGroupId != _simUnit.ID
					? RTS.Core.SimManager.Instance?.World?
						.FindSimEntity(_simUnit.SegmentGroupId) as RTS.Simulation.SimUnit
					: _simUnit;
				if (wormHead != null && RTS.Core.SimManager.CountAliveSegments(wormHead) < 5)
					return false;
			}

			if (target == null || ActiveWeapon == null)
			{
				return false;
			}

			// 多段齐射（电浆炮 10×15）：所有普通攻击路径统一打满 N 发，优先不同目标
			var unitCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(_owner?.DisplayName ?? "");
			if (unitCfg != null && unitCfg.MultiShotTargets > 1 && _owner is Unit multiUnit)
			{
				// 齐射冷却中如实返回失败，让调用方（跑打等）知道本轮打不出
				if (multiUnit.CombatModule.Weapons.Count == 0 || !multiUnit.CombatModule.Weapons[0].CanFire())
					return false;
				multiUnit.PerformMultiShotVolley(target);
				return true;
			}

			// 建造中/蓝图建筑不能开火
			if (_owner is Structure s && s.CurrentState != Structure.StructureState.Completed)
				return false;

		CurrentTarget = target;

		if (!CanAnyWeaponInRange(target))
			return false;

		// 在射程内就保持锁定
		// 转换间隔只用于多武器交替发射；单武器按武器自身冷却排射，不能被转换间隔限速
		bool multiWeapon = Weapons.Count > 1;
		if (multiWeapon && RotationCooldown > FP.Zero)
			return true;

		// 交替发射：优先当前武器；打不到或没冷却时，换一把能打到且冷却好的
		Weapon shooter = PickReadyWeapon(w => w.CanFire() && w.CanHit(target));

		if (shooter != null)
		{
			shooter.Fire(target);
			if (multiWeapon)
				RotationCooldown = _rotationInterval;
			CycleWeapon();
		}

		return true;
	}

	// 选一把当前可用的武器：优先 ActiveWeapon，否则按挂载顺序找第一把（找到后切为当前武器）
	private Weapon PickReadyWeapon(System.Func<Weapon, bool> ready)
	{
		if (ActiveWeapon != null && ready(ActiveWeapon))
			return ActiveWeapon;

		for (int i = 0; i < Weapons.Count; i++)
		{
			if (ready(Weapons[i]))
			{
				_weaponIndex = i;
				ActiveWeapon = Weapons[i];
				return Weapons[i];
			}
		}

		return null;
	}

	// 交替发射：按挂载顺序轮换下一台武器
	private void CycleWeapon()
	{
		if (Weapons.Count <= 1)
			return;

		_weaponIndex = (_weaponIndex + 1) % Weapons.Count;
		ActiveWeapon = Weapons[_weaponIndex];
	}

	// 泰伦重装：切换弹种（主动切换当前武器，不走攻击轮转）
	public void CycleActiveWeapon()
	{
		if (Weapons.Count <= 1)
			return;

		_weaponIndex = (_weaponIndex + 1) % Weapons.Count;
		ActiveWeapon = Weapons[_weaponIndex];
	}
}
