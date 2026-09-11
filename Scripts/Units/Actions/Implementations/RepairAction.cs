using Godot;
using RTS.Data;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// SCV 维修：右键损坏的友方建筑/机械单位，消耗金属恢复血量（数值来自 SCV 配置表）
	[GlobalClass]
	public partial class RepairAction : UnitAction
	{
		private IEntity _target;
		private float _repairPerSecond = 20f;
		private float _repairCostPerHp = 0.1f;
		private float _repairRange = 80f;

		public override void _Ready()
		{
			ActionName = "Repair";
			Layer = ActionLayer.Movement | ActionLayer.Weapon;
			BlockingLayers = ActionLayer.Movement | ActionLayer.Weapon;
			Queueable = true;
			SlotIndex = -1; // 右键指令，不占按钮格
		}

		public override void Initialize(IEntity unit)
		{
			base.Initialize(unit);

			var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(unit?.DisplayName ?? "");
			if (cfg != null)
			{
				_repairPerSecond = cfg.RepairPerSecond;
				_repairCostPerHp = cfg.RepairCostPerHp;
				_repairRange = cfg.RepairRange;
			}
		}

		public override bool CanExecute()
		{
			if (_unit == null)
				return false;

			return RTS.Data.Configs.ConfigDatabase.GetUnit(_unit.DisplayName)?.CanRepair == true;
		}

		public override void Setup(RTS.Simulation.FPVector2 targetPos, IEntity target)
		{
			_target = target;
		}

		public override void OnEnter()
		{
			base.OnEnter();

			if (_target == null || _target.IsDeadOrNull() || !IsRepairable(_target))
			{
				Finish();
				return;
			}

			if (_unit?.LogicEntity is not SimUnit sim)
			{
				Finish();
				return;
			}

			sim.HasTarget = true;
		}

		public override void OnUpdate(double delta)
		{
			if (_target == null || _target.IsDeadOrNull() || !IsRepairable(_target) || _unit?.LogicEntity is not SimUnit sim)
			{
				Finish();
				return;
			}

			FP radius = (FP)48m;
			FP range = (FP)_repairRange;
			FP safeDist = range + radius;
			FP distSq = FPVector2.DistanceSquared(sim.Position, _target.LogicEntity.Position);

			if (distSq > safeDist * safeDist)
			{
				sim.CommandMove(_target.LogicEntity.Position, _target.LogicEntity.ID);
				return;
			}

			sim.HasTarget = false;
			sim.Velocity = FPVector2.Zero;

			// 每帧修复并扣金属
			FP fixedDelta = RTS.Core.SimManager.Instance.World.FixedDelta;
			FP amount = (FP)_repairPerSecond * fixedDelta;
			FP cost = amount * (FP)_repairCostPerHp;

			var player = RTS.World.Game.GetPlayerByTeam(_unit.TeamID);
			if (player?.PlayerData == null)
			{
				Finish();
				return;
			}

			// 模拟线程每 tick 扣费：不能 new Godot.Collections.Dictionary
			if (!player.PlayerData.TryConsumeResources(ResourceType.Metal, (float)cost))
			{
				Finish(); // 金属耗尽停止维修
				return;
			}

			_target.LifeModule?.Heal((float)amount);
		}

		public override void OnExit()
		{
			if (_unit?.LogicEntity is SimUnit sim)
			{
				sim.HasTarget = false;
				sim.Velocity = FPVector2.Zero;
			}

			base.OnExit();
		}

		public override long GetDeterministicExtraHash()
		{
			return _target?.LogicEntity?.ID ?? -1;
		}

		private bool IsRepairable(IEntity target)
		{
			if (target == null || target.LogicEntity == null || target.LogicEntity.IsDead || target.TeamID != _unit?.TeamID)
				return false;

			if (target.LogicEntity.Hp >= target.LogicEntity.MaxHp)
				return false;
			if (target.LogicEntity.CannotBeHealed)
				return false; // 恶魔禁疗

			if (target is Structure s)
				return s.CurrentState == Structure.StructureState.Completed;

			if (target is Unit u)
			{
				var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitName);
				return cfg != null && cfg.Tags.Contains("Mechanical");
			}

			return false;
		}
	}
}
