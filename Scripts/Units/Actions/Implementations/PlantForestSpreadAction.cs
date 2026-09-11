using Godot;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// 森林蔓延（建筑技能）：生命树/分支树/森林节点释放，
	// 以建筑为圆心在范围内生成森林节点（范围由建筑位置决定）
	[GlobalClass]
	public partial class PlantForestSpreadAction : CompletedStructureAbilityAction
	{
		[Export] public string DisplayNameText = "森林蔓延";
		protected override string ActionId => "PlantForestSpread";

		public override bool CanExecute()
		{
			if (!base.CanExecute())
				return false;
			if (_unit?.LogicEntity is not SimStructure tree)
				return false;
			if (tree.ForestSpreadCooldown > FP.Zero)
				return false;
			if (tree.StructureTypeId == "PlantForestNode" && tree.ForestSpreadUsed)
				return false;
			return RTS.World.Game.GetPlayerByTeam(_unit.TeamID)?.PlayerData?
				.GetResource(ResourceType.Wood) >= 50f;
		}

		public override float GetCooldownRemaining()
		{
			if (_unit?.LogicEntity is SimStructure tree)
				return (float)tree.ForestSpreadCooldown;
			return 0f;
		}

		public override float GetCooldownMax()
		{
			// 与 SimManager 施放后设置的冷却一致
			return 20f;
		}
	}
}
