using Godot;
using RTS.Core;
using RTS.Data;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions
{
	[GlobalClass]
	public partial class StopAction : UnitAction
	{
		public override void _Ready() {
			ActionName = "Stop";
			Layer = ActionLayer.None;
			BlockingLayers = ActionLayer.Body; // 锁死全身
			Queueable = false;
		}

		public override void OnEnter() {
			base.OnEnter();

			// 直接切断逻辑层的移动意图
			if (_unit.LogicEntity is SimUnit simUnit)
			{
				simUnit.HasTarget = false;
				simUnit.Velocity = FPVector2.Zero;
			}

		}

		public override void OnUpdate(double delta)
		{
			if (_unit?.LogicEntity is not SimUnit simUnit)
			{
				Finish();
				return;
			}

			// 保持停止
			simUnit.HasTarget = false;
			simUnit.Velocity = FPVector2.Zero;

			var world = RTS.Core.SimManager.Instance?.World;
			if (world == null || _unit.CombatModule?.ActiveWeapon == null)
				return;

			// 停止时仍会反击进入射程的敌人（离开停止状态去攻击）
			float searchRadius = Mathf.Max(64f, _unit.CombatModule.GetAttackRange());
			if (_unit.TryAutoAttack(searchRadius))
				return;

			// 没有敌人时：清理附近纳米菌毯，攻速/冷却/特效全部复用武器本身
			ClearNearestCreep(world);
		}

		private void ClearNearestCreep(RTS.Simulation.SimWorld world)
		{
			// 统一走共享入口：武器冷却/射程/特效/溅射与普通攻击一致
			CreepClearHelper.TryFireAtCreep(_unit, world);
		}
	}
}
