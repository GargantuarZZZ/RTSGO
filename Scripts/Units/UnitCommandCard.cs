using Godot;
using Godot.Collections;
using RTS.Actions;
namespace RTS.Data
{
	// 定义单个按钮的配置数据 (仅用于编辑器配置)
	[GlobalClass]
	public partial class ActionSlotData : Resource
	{
		[Export] public int SlotIndex = 0; // 0-14
		[Export] public string ActionId = ""; // 逻辑ID，例如 "Build_Barracks"
		[Export] public string DisplayName = "";
		[Export] public Texture2D Icon;
		[Export] public bool DefaultEnabled = true;
	}

	// 定义整张指令卡
	[GlobalClass]
	public partial class UnitCommandCard : Resource
	{
		// 这是一个数组，你可以拖拽多个 ActionSlotData 进去
		[Export] public Array<ActionSlotData> Slots = new();
	}
}
