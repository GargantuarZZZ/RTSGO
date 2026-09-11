using Godot;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// 泰伦架设：停止 1 秒后进入架设状态（无法移动，获得增益）；再按一次收起
	[GlobalClass]
	public partial class DeployAction : AliveUnitAbilityAction
	{
		[Export] public string DisplayNameText = "架设/收起";
		private RTS.Simulation.FPVector2 _targetPos;
		private bool _hasTargetPos = false;
		protected override string ActionId => "Deploy";
		// 架设会打断移动/攻击移动：否则波次每帧下发的 AttackMove 会在架设
		// 完成前自动收起（CommandMove → PendingUndeployMove），形成架/收循环
		protected override ActionLayer Blocking => ActionLayer.Ability | ActionLayer.Movement;

		public override bool CanExecute()
		{
			if (_unit?.LogicEntity is not SimUnit su)
				return false;

			// 干扰弹：无法使用技能（含架设）
			if (su.Buffs.HasBuff("TerranDisrupt"))
				return false;

			return true;
		}

		public override void Setup(RTS.Simulation.FPVector2 targetPos, IEntity targetObj)
		{
			_targetPos = targetPos;
			_hasTargetPos = true;
		}

		public override void OnEnter()
		{
			base.OnEnter();

			if (_unit?.LogicEntity is SimUnit su)
			{
				var cfg = ConfigDatabase.GetUnit(su.UnitTypeId);
				FP time = (FP)(cfg?.DeployTimeSeconds ?? 1f) * su.DeployTimeMultiplier;

				if (su.IsDeployed)
				{
					su.DeployState = 3;
					su.DeployTimer = time;
				}
				else if (su.DeployState == 0)
				{
					// 解放者：架设前记录指定范围圈（未指定则用自身位置）
					if (su.DeployRequiresTargetCircle)
					{
						su.DeployTargetCenter = _hasTargetPos ? _targetPos : su.Position;
						su.DeployTargetRadius = (FP)((cfg?.DeployTargetCircleTiles ?? 3f) * 64f);
					}

					su.DeployState = 1;
					su.DeployTimer = time;
				}
			}

			Finish();
		}
	}
}
