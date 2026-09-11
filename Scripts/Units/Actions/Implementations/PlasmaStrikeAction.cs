using Godot;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// 电浆炮：手动释放按钮（能量改为等离子炮 CD/施法状态）
	[GlobalClass]
	public partial class PlasmaStrikeAction : AliveUnitAbilityAction
	{
		[Export] public string DisplayNameText = "电浆炮";
		protected override string ActionId => "PlasmaStrike";

		public override bool CanExecute()
		{
			if (_unit?.LogicEntity is not SimUnit su || su.UnitTypeId != "PlasmaCannon")
				return false;

			return su.PlasmaCooldown <= FP.Zero && su.PlasmaCastState == 0;
		}
	}
}
