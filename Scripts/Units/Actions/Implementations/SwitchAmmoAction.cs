using Godot;

namespace RTS.Actions.Implementation
{
	// 泰伦重装步兵：切换弹种（高爆 AOE / 穿甲对重甲）
	[GlobalClass]
	public partial class SwitchAmmoAction : AliveUnitAbilityAction
	{
		[Export] public string DisplayNameText = "切换弹种";
		protected override string ActionId => "SwitchAmmo";

		public override bool CanExecute()
		{
			return _unit?.CombatModule != null && _unit.CombatModule.Weapons.Count > 1;
		}

		public override void OnEnter()
		{
			base.OnEnter();
			_unit?.CombatModule?.CycleActiveWeapon();
			Finish();
		}
	}
}
