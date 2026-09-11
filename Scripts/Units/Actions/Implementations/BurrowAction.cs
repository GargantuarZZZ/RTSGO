using Godot;
using RTS.Simulation;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	// 沙虫 - 潜地：切换地下状态（受伤减半、无法攻击）
	[GlobalClass]
	public partial class BurrowAction : AliveUnitAbilityAction
	{
		[Export] public string DisplayNameText = "潜地";
		protected override string ActionId => "Burrow";
	}
}
