// File: res://Scripts/Units/Actions/Implementations/AttackAction.cs
using Godot;
using RTS.Data;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	[GlobalClass]
	public partial class AttackAction : UnitAction
	{
		public IEntity Target { get; private set; }
		private FP _stopRangeBuffer = (FP)10m;
		// 追路节流：目标移动时不再每 Tick 重跑 A*
		private int _chaseCooldownTicks = 0;

		public override void _Ready()
		{
			ActionName = "Attack";
			Layer = ActionLayer.Movement | ActionLayer.Weapon;
			Queueable = true;
		}

		public void SetTarget(IEntity target) => Target = target;
		public override void Setup(RTS.Simulation.FPVector2 targetPos, IEntity targetObj) { if (targetObj != null) SetTarget(targetObj); }
		public override void OnEnter()
		{
			base.OnEnter();

			if (Target == null || (_unit is Structure s && s.CurrentState != Structure.StructureState.Completed))
			{
				Finish();
				return;
			}
		}

		public override void OnUpdate(double delta)
		{
			// SimUnit 与 SimStructure 均可作为攻击目标
			if (Target == null || Target.IsDeadOrNull() || _unit.CombatModule == null || _unit.LogicEntity == null)
			{
				Finish();
				_unit.VisualsModule?.ResetAim();
				return;
			}

			// 武器打不到的目标（例如对地武器点飞行单位）直接放弃，不追到天涯海角
			if (!_unit.CombatModule.CanAnyWeaponTarget(Target))
			{
				Finish();
				_unit.VisualsModule?.ResetAim();
				return;
			}

			// 跑打单位（纳米巨兽）：攻击时不停下，边追边开火
			bool runAndGun = RTS.Data.Configs.ConfigDatabase.GetUnit(_unit.DisplayName)?.AttackMoveFiresWhileMoving == true;

			// 1. 攻击状态：在射程内，直接开火！
			if (_unit.CombatModule.CanAnyWeaponInRange(Target))
			{
				if (_unit.LogicEntity is SimUnit sUnit)
				{
					if (runAndGun)
					{
						// 跑打：保持追击目标，边移动边开火
						sUnit.CommandMove(Target.LogicEntity.Position, Target.LogicEntity.ID);
					}
					else
					{
						// 普通单位：停下脚步开火
						sUnit.HasTarget = false;
						sUnit.Velocity = FPVector2.Zero;
					}
				}

				if (Target?.LogicEntity != null)
					_unit.VisualsModule?.AimAt(new Vector2(
						(float)Target.LogicEntity.Position.X,
						(float)Target.LogicEntity.Position.Y));
				if (_unit is Unit attackUnit)
					attackUnit.PerformMultiShotVolley(Target); // 单发或多段齐射
				else
					_unit.CombatModule?.TryAttack(Target);
			}
			// 2. 追击状态：在射程外
			else
			{
				if (_unit.IsStructure)
				{
					Finish();
					return;
				}

				if (_unit.LogicEntity is SimUnit simUnit)
				{
				_unit.VisualsModule?.ResetAim();
				// 泰伦架设单位：不追敌（移动指令会自动收起）
				if (simUnit.IsDeployed)
					return;
				// 追击目标：以敌人当前坐标寻路
				if (_chaseCooldownTicks > 0)
				{
					_chaseCooldownTicks--;
				}
					else
					{
						// 追路走寻路预算（SimWorld.PathBudgetPerTick），避免大量单位同 Tick 全量 A*
						simUnit.CommandMove(Target.LogicEntity.Position);
						_chaseCooldownTicks = 8; // 0.4 秒
					}
			}
		}
		}

		public override void OnExit()
		{
			base.OnExit();
			if (_unit.LogicEntity is SimUnit simUnit) simUnit.HasTarget = false;
			_unit.VisualsModule?.ResetAim();
		}

		public override long GetDeterministicExtraHash()
		{
			return Target?.LogicEntity?.ID ?? -1;
		}
	}
}
