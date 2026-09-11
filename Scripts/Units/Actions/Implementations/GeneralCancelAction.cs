using Godot;
using RTS.Core;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	[GlobalClass]
	public partial class GeneralCancelAction : UnitAction
	{
		public override void _Ready()
		{
			ActionName = "Cancel";
			SlotIndex = 14;
			Layer = ActionLayer.None;
			Queueable = false;
			// 图标在编辑器里配置
		}

		public override void OnEnter()
		{
			// 蓝图态与工地态统一按 Structure 处理
			// P0-1：模拟线程禁止场景树遍历，直接用 Initialize 注入的 _unit
			if (_unit is Structure struc)
			{
				// 只要没完成，就可以取消
				if (struc.CurrentState != Structure.StructureState.Completed)
				{
					struc.CancelConstruction();
				}
			}

			Finish();
		}
	}
}
