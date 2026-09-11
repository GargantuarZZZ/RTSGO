// File: res://Scripts/Units/Modules/UnitLife.cs
using Godot;
using RTS.Data;
using RTS.Units;

[GlobalClass]
	public partial class UnitLife : EntityModuleBase
{
	[Signal] public delegate void HealthChangedEventHandler(float current, float max);
	[Signal] public delegate void DiedEventHandler();

	[ExportGroup("Vitals")]
	[Export] public float MaxHp { get; set; } = 100.0f;
	[Export] public float MaxShield { get; set; } = 0.0f;

	[ExportGroup("Defenses")]
	[Export] public ArmorType ArmorType { get; set; } = ArmorType.Light;
	[Export] public float DefKinetic { get; set; } = 0.0f;
	[Export] public float DefThermal { get; set; } = 0.0f;
	[Export] public float DefExplosive { get; set; } = 0.0f;
	[Export] public float DefEM { get; set; } = 0.0f;
	[Export] public float DefBeam { get; set; } = 0.0f;

	public float CurrentHp => _owner?.LogicEntity != null ? (float)_owner.LogicEntity.Hp : MaxHp;
	public float CurrentShield => _owner?.LogicEntity != null ? (float)_owner.LogicEntity.Shield : MaxShield;
	public bool IsDead => _owner?.LogicEntity?.IsDead ?? false;

	private bool _hasEmittedDeath = false;
	private bool _statsInjected = false;

	// 懒加载注入：后台逻辑就绪后立即注入数据
	private void EnsureStatsInjected()
	{
		if (_statsInjected || _owner?.LogicEntity == null) return;

		var logic = _owner.LogicEntity;
		logic.MaxHp = (FixMath.NET.Fix64)MaxHp;
		logic.Hp = (FixMath.NET.Fix64)MaxHp;
		logic.MaxShield = (FixMath.NET.Fix64)MaxShield;
		logic.Shield = (FixMath.NET.Fix64)MaxShield;
		logic.ArmorType = (int)ArmorType;

		logic.Defenses[(int)DamageType.Kinetic] = (FixMath.NET.Fix64)DefKinetic;
		logic.Defenses[(int)DamageType.Thermal] = (FixMath.NET.Fix64)DefThermal;
		logic.Defenses[(int)DamageType.Explosive] = (FixMath.NET.Fix64)DefExplosive;
		logic.Defenses[(int)DamageType.EM] = (FixMath.NET.Fix64)DefEM;
		logic.Defenses[(int)DamageType.Beam] = (FixMath.NET.Fix64)DefBeam;

		_statsInjected = true;
	}

	// 供生成器在入树后立刻注入一次属性（科技加成需要先有基础数值）
	public void ForceInjectStats() => EnsureStatsInjected();

	public override void Tick(double delta)
	{
		if (_owner?.LogicEntity == null) return;

		EnsureStatsInjected(); // 确保注入

		// 同步逻辑层最大生命/护盾（架设翻倍、科技加成等），保证血条比例正确
		MaxHp = (float)_owner.LogicEntity.MaxHp;
		MaxShield = (float)_owner.LogicEntity.MaxShield;

		// 模拟线程不允许碰 Godot 信号：事件入队，主线程统一派发
		RTS.Core.SimEventQueue.Enqueue(new RTS.Core.SimEvent(
			RTS.Core.SimEventType.HealthChanged,
			_owner.LogicEntity.ID,
			CurrentHp,
			(float)_owner.LogicEntity.MaxHp));
		CheckDeath();
	}

	// 轻量死亡检测：只发一次 Died，不触发 HealthChanged。
	// 用于“战斗后补检”，因为即时伤害发生在 LifeModule.Tick 之后、World.Tick 移除实体之前，
	// 若不在移除前再查一次，视觉层永远收不到死亡事件（中立塔不播死亡动画的根因）。
	public void CheckDeath()
	{
		if (_owner?.LogicEntity == null || _hasEmittedDeath)
			return;

		if (_owner.LogicEntity.IsDead)
		{
			_hasEmittedDeath = true;
			RTS.Core.SimEventQueue.Enqueue(new RTS.Core.SimEvent(
				RTS.Core.SimEventType.Died,
				_owner.LogicEntity.ID));
		}
	}

	/// <summary>主线程派发：把队列事件还原成原有信号，订阅方无需改动。</summary>
	public void NotifyHealthChanged(float current, float max)
	{
		EmitSignal(SignalName.HealthChanged, current, max);
	}

	/// <summary>主线程派发：还原 Died 信号。</summary>
	public void NotifyDied()
	{
		EmitSignal(SignalName.Died);
	}

	public void Heal(float amount)
	{
		EnsureStatsInjected();
		if (_owner?.LogicEntity == null || _owner.LogicEntity.IsDead) return;
		if (_owner.LogicEntity.CannotBeHealed) return; // 恶魔禁疗

		FixMath.NET.Fix64 amountFP = (FixMath.NET.Fix64)amount;
		if (amountFP <= FixMath.NET.Fix64.Zero) return;
		_owner.LogicEntity.Hp += amountFP;
		if (_owner.LogicEntity.Hp > _owner.LogicEntity.MaxHp)
			_owner.LogicEntity.Hp = _owner.LogicEntity.MaxHp;
	}

	public void SetHealthRaw(float amount)
	{
		EnsureStatsInjected(); // 蓝图放下时先注满属性，再按蓝图状态调整血量
		if (_owner?.LogicEntity != null)
		{
			FixMath.NET.Fix64 amt = (FixMath.NET.Fix64)amount;
			if (amt < FixMath.NET.Fix64.Zero) amt = FixMath.NET.Fix64.Zero;
			if (amt > _owner.LogicEntity.MaxHp) amt = _owner.LogicEntity.MaxHp;
			_owner.LogicEntity.Hp = amt;
		}
	}

	public void TakeDamage(float rawDamage, DamageType type, float bonusDamage = 0f, ArmorType bonusArmor = ArmorType.Light, RTS.Simulation.SimEntity source = null)
	{
		EnsureStatsInjected();
		_owner?.LogicEntity?.TakeDamage(
			(FixMath.NET.Fix64)rawDamage,
			(int)type,
			(FixMath.NET.Fix64)bonusDamage,
			(int)bonusArmor,
			source
		);
	}
}
