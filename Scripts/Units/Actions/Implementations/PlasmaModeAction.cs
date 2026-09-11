using Godot;
using RTS.Simulation;

namespace RTS.Actions.Implementation
{
	// 电浆炮：切换自动/手动释放
	[GlobalClass]
	public partial class PlasmaModeAction : AliveUnitAbilityAction
	{
		[Export] public string DisplayNameText = "切换自动/手动";
		protected override string ActionId => "PlasmaMode";

		public override bool CanExecute()
		{
			return _unit?.LogicEntity is SimUnit su && su.UnitTypeId == "PlasmaCannon";
		}
	}
}
