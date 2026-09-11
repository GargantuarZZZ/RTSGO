using Godot;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// 兴奋剂：步枪兵 5 秒攻速/移速 +50%，自伤 15（数值全部来自 UnionTech_Stim）
	[GlobalClass]
	public partial class StimAction : AliveUnitAbilityAction
	{
		private const string TechId = "UnionTech_Stim";
		[Export] public string DisplayNameText = "兴奋剂";
		protected override string ActionId => "Stim";

		public override bool CanExecute()
		{
			if (_unit?.LogicEntity is not SimUnit sim)
				return false;

			if (sim.SkillCooldown > FP.Zero)
				return false;

			var pd = RTS.World.Game.GetPlayerByTeam(_unit.TeamID)?.PlayerData;
			return pd != null && pd.HasTech(TechId);
		}

		public override float GetCooldownRemaining()
		{
			if (_unit?.LogicEntity is SimUnit sim)
				return (float)sim.SkillCooldown;

			return 0f;
		}

		public override float GetCooldownMax()
		{
			return ConfigDatabase.GetTech(TechId)?.SkillCooldownSeconds ?? 0f;
		}

		public override void OnEnter()
		{
			base.OnEnter();

			if (_unit?.LogicEntity is not SimUnit sim)
			{
				Finish();
				return;
			}

			var cfg = ConfigDatabase.GetTech(TechId);
			if (cfg == null || cfg.StimDurationSeconds <= 0f)
			{
				Finish();
				return;
			}

			sim.SkillCooldown = (FP)cfg.SkillCooldownSeconds;
			FP duration = (FP)cfg.StimDurationSeconds;

			sim.Buffs.AddStatBuff(
				"UnionStim_Attack", 1, duration,
				(FP)cfg.StimAttackSpeedMultiplier, FP.Zero, FP.Zero, sim.ID);
			sim.Buffs.AddBuff(
				"UnionStim_Move", 1, duration,
				FP.One, FP.One, FP.Zero,
				(FP)cfg.StimMoveSpeedMultiplier, FP.Zero, FP.Zero, sim.ID);

			sim.TakeDamage((FP)cfg.StimSelfDamage, (int)DamageType.Kinetic);

			Finish();
		}
	}
}
