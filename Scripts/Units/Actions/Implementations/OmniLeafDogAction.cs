using Godot;
using RTS.Units;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// 花田 - 全能性：消耗 10 点花田能量，在任意己方菌毯处生成一只叶犬
	[GlobalClass]
	public partial class OmniLeafDogAction : CompletedStructureAbilityAction
	{
		[Export] public string DisplayNameText = "全能叶犬";
		protected override string ActionId => "PlantOmni";

		public override bool CanExecute()
		{
			if (!base.CanExecute())
				return false;
			if (_unit?.LogicEntity is not SimStructure field || field.FlowerEnergy < (FP)10m)
				return false;
			// 需要先研究“全能性”
			return RTS.World.Game.GetPlayerByTeam(_unit.TeamID)?.PlayerData?
				.HasTech("PlantTech_Omni") == true;
		}
	}
}
