using Godot;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// 英雄技能按钮：只负责显示/能量判定，实际效果由 SimManager 确定性处理
	[GlobalClass]
	public partial class HeroSkillAction : AliveUnitAbilityAction
	{
		[Export] public int SkillIndex = 1;
		[Export] public string DisplayNameText = "";
		protected override string ActionId => SkillIndex == 1 ? "HeroSkill1" : "HeroSkill2";

		public override bool CanExecute()
		{
			if (_unit?.LogicEntity is not SimUnit sim)
				return false;

			var cfg = ConfigDatabase.GetUnit(sim.UnitTypeId);
			float cost = SkillIndex == 1 ? (cfg?.Skill1Cost ?? 0f) : (cfg?.Skill2Cost ?? 0f);
			if (cost <= 0f || sim.HeroEnergy < (FP)cost)
				return false;

			// 干扰弹：范围内无法使用技能
			if (sim.Buffs.HasBuff("TerranDisrupt"))
				return false;

			// 干扰弹技能本身需要科技解锁
			if (SkillIndex == 2 && cfg != null && !string.IsNullOrEmpty(cfg.Skill2RequiredTechId))
			{
				var player = RTS.World.Game.GetPlayerByTeam(sim.TeamID);
				if (player?.PlayerData == null || !player.PlayerData.HasTech(cfg.Skill2RequiredTechId))
					return false;
			}

			return true;
		}
	}
}
