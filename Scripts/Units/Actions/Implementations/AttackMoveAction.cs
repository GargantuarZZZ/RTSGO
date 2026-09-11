using Godot;
using RTS.Core;
using RTS.Simulation;
using RTS.Actions.Implementation;
using FP = FixMath.NET.Fix64;
using RTS.Data;
namespace RTS.Actions
{
	[GlobalClass]
	public partial class AttackMoveAction : UnitAction
	{
		public FPVector2 TargetPosFP;
		[Export] public float SearchRadius = 300f;

		public override void _Ready() {
			ActionName = "AttackMove";
			Layer = ActionLayer.Movement | ActionLayer.Weapon;
			Queueable = true;
		}

		public override void Setup(RTS.Simulation.FPVector2 targetPos, IEntity targetObj) { TargetPosFP = targetPos; }

		public override void OnUpdate(double delta) {
			// 索敌范围不小于自身武器最远射程（配合自动索敌）
			float search = Mathf.Max(SearchRadius, _unit.CombatModule?.GetAttackRange() ?? SearchRadius);
			var enemy = _unit.FindAutoAttackTarget(search);

			bool fireWhileMoving =
				RTS.Data.Configs.ConfigDatabase.GetUnit(_unit.DisplayName)?.AttackMoveFiresWhileMoving == true;

			if (fireWhileMoving)
			{
				bool attacked = false;

				if (enemy != null && enemy.LogicEntity != null && _unit.CombatModule != null)
				{
					// 移动攻击：边朝指令点移动边自动开火，不停止、不追敌
					if (enemy?.LogicEntity != null)
						_unit.VisualsModule?.AimAt(new Vector2(
							(float)enemy.LogicEntity.Position.X,
							(float)enemy.LogicEntity.Position.Y));
					attacked = _unit.CombatModule.TryAttack(enemy);
				}

				// 敌人打不到（超出武器射程/冷却中）但菌毯在射程内：继续清菌毯
				if (!attacked)
				{
					_unit.VisualsModule?.ResetAim();
					CreepClearHelper.TryFireAtCreep(_unit, RTS.Core.SimManager.Instance?.World);
				}

				// 无论是否索敌开火，都继续朝目标点移动
				if (_unit.LogicEntity is SimUnit simUnit)
				{
					simUnit.CommandMove(TargetPosFP);
					// 只有真正到达目标点（1.5 格内）才结束；被 AttackAction 停火清掉 HasTarget 时继续推进
					if (!simUnit.HasTarget && AtWaveTarget(simUnit)) Finish();
				}
				else Finish();
				return;
			}

			// 传统攻击移动：只对射程内的敌人停下开火；射程外不追敌——
			// 追敌会让全军为 300px 外的单兵散开，长距离推进变成“走两步停一下”。
			// 没有敌人或敌人在射程外时，继续朝目标点推进（交火只发生在路过时）。
			if (enemy != null && _unit.CombatModule != null && _unit.CombatModule.CanAnyWeaponInRange(enemy))
			{
				var atk = _unit.Brain.GetAction<AttackAction>("Attack");
				if (atk != null)
				{
					atk.SetTarget(enemy);
					atk.OnUpdate(delta);
				}
			}
			else
			{
				_unit.VisualsModule?.ResetAim();
				if (_unit.LogicEntity is SimUnit simUnit)
				{
					simUnit.CommandMove(TargetPosFP);
					// 只有真正到达目标点才结束；被 AttackAction 停火清掉 HasTarget 时继续推进
					if (!simUnit.HasTarget && AtWaveTarget(simUnit)) Finish();
				}
				else Finish();
			}
		}

		// 是否已到达攻击移动目标点（1.5 格内视为到达）
		private bool AtWaveTarget(SimUnit simUnit)
		{
			return FPVector2.DistanceSquared(simUnit.Position, TargetPosFP) <= (FP)(96 * 96);
		}

		public override long GetDeterministicExtraHash()
		{
			return ((long)(TargetPosFP.X * (FP)1000m) << 32) ^ (long)(TargetPosFP.Y * (FP)1000m);
		}
	}
}
