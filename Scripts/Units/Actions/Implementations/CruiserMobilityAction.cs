using Godot;
using RTS.Core;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// 战列巡洋舰机动模式：指定一个 30 格内的落点，速度×5 冲向该点，到达后停止。
	// 期间占据 Body 层、不可打断、无法攻击（数值全部来自配置表）。
	[GlobalClass]
	public partial class CruiserMobilityAction : UnitAction
	{
		private FP _timer = FP.Zero;
		private FPVector2 _targetPos;

		[Export] public string DisplayNameText = "机动模式";

		public override bool CanBeInterrupted => false;

		public override void _Ready()
		{
			ActionName = "CruiserMobility";
			Queueable = false;
		}

		public override void Setup(RTS.Simulation.FPVector2 targetPos, IEntity targetObj)
		{
			_targetPos = targetPos;
		}

		public override bool CanExecute()
		{
			if (_unit?.LogicEntity is not SimUnit sim)
				return false;

			if (sim.SkillCooldown > FP.Zero)
				return false;

			var cfg = ConfigDatabase.GetUnit(_unit.DisplayName);
			return cfg != null && cfg.MobilitySpeedMultiplier > 0f && cfg.MobilityRangeTiles > 0;
		}

		public override float GetCooldownRemaining()
		{
			if (_unit?.LogicEntity is SimUnit sim)
				return (float)sim.SkillCooldown;

			return 0f;
		}

		public override float GetCooldownMax()
		{
			var cfg = ConfigDatabase.GetUnit(_unit?.DisplayName ?? "");
			return cfg?.MobilityCooldownSeconds ?? 0f;
		}

		public override long GetDeterministicExtraHash()
		{
			return (long)(_timer * (FP)1000m) ^
				((long)(_targetPos.X * (FP)1000m) << 16) ^
				(long)(_targetPos.Y * (FP)1000m);
		}

		public override void OnEnter()
		{
			base.OnEnter();

			if (_unit?.LogicEntity is not SimUnit sim)
			{
				Finish();
				return;
			}

			var cfg = ConfigDatabase.GetUnit(_unit.DisplayName);
			if (cfg == null || cfg.MobilitySpeedMultiplier <= 0f || cfg.MobilityRangeTiles <= 0 || sim.MaxSpeed <= FP.Zero)
			{
				Finish();
				return;
			}

			// 射程校验：落点必须在 MobilityRangeTiles 格内
			int tileSize = SimManager.Instance?.World?.Grid?.TileSize ?? 64;
			FP range = (FP)(cfg.MobilityRangeTiles * tileSize);
			if (FPVector2.DistanceSquared(sim.Position, _targetPos) > range * range)
			{
				Finish();
				return;
			}

			_timer = FP.Zero;
			sim.SkillCooldown = (FP)cfg.MobilityCooldownSeconds;

			// 速度×MobilitySpeedMultiplier，持续到到达落点（时长作为上限兜底）
			sim.Buffs.AddBuff(
				"CruiserMobility",
				1,
				(FP)cfg.MobilityDurationSeconds,
				FP.One,
				FP.One,
				FP.Zero,
				(FP)cfg.MobilitySpeedMultiplier,
				FP.Zero,
				FP.Zero,
				sim.ID
			);

			// 空中单位直线飞向落点；SimUnit 到达后会清除 HasTarget
			sim.CommandMove(_targetPos);
		}

		public override void OnUpdate(double delta)
		{
			if (_unit?.LogicEntity is not SimUnit sim)
			{
				Finish();
				return;
			}

			var cfg = ConfigDatabase.GetUnit(_unit.DisplayName);
			if (cfg == null)
			{
				Finish();
				return;
			}

			_timer += SimManager.Instance.World.FixedDelta;

			// 到达落点立即停止；超过最长持续时间也强制结束（防卡死兜底）
			if (!sim.HasTarget || _timer >= (FP)cfg.MobilityDurationSeconds)
				Finish();
		}

		public override void OnExit()
		{
			if (_unit?.LogicEntity is SimUnit sim)
			{
				sim.Buffs.RemoveBuff("CruiserMobility");
				sim.HasTarget = false;
				sim.Velocity = FPVector2.Zero;
			}

			base.OnExit();
		}
	}
}
