using Godot;
using RTS.Simulation;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	// 大裂隙：切换自动生产模式（2 恶魔狗 / 1 飞行射手）
	[GlobalClass]
	public partial class AutoProduceModeAction : CompletedStructureAbilityAction
	{
		[Export] public string DisplayNameText = "切换模式";
		protected override string ActionId => "AutoProduceMode";

		public override bool CanExecute()
		{
			if (_unit?.LogicEntity is not SimStructure st)
				return false;

			return st.CurrentState == SimStructure.StructureState.Active;
		}

		public override void OnEnter()
		{
			base.OnEnter();

			if (_unit?.LogicEntity is SimStructure st)
				st.AutoProduceMode = st.AutoProduceMode == 0 ? 1 : 0;

			Finish();
		}
	}
}
