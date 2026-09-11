using Godot;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	// 轨道控制中心 - 轨道炮：按钮只负责显示/选目标模式，真正的扣费与轰炸在 SimManager 确定性处理
	[GlobalClass]
	public partial class OrbitalStrikeAction : EnergyCostAbilityAction
	{
		[Export] public string DisplayNameText = "轨道炮";
		protected override string ActionId => "OrbitalStrike";
		protected override float GetEnergyCost(StructureConfig cfg) => cfg.StrikeEnergyCost;
	}
}
