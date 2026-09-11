using Godot;
using RTS.Simulation;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	// 沙虫 - 吞噬：向目标冲刺，命中后对目标及周围敌人造成伤害，
	// 每击杀一个敌人恢复 100 生命（实际效果由 SimManager 确定性处理）
	[GlobalClass]
	public partial class DevourAction : AliveUnitAbilityAction
	{
		[Export] public string DisplayNameText = "吞噬";
		protected override string ActionId => "Devour";
	}
}
