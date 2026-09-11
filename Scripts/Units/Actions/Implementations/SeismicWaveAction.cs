using Godot;
using RTS.Data.Configs;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	// 地壳裂解器 - 地震波：大范围敌方地面单位减速（可蓄 2 次）
	[GlobalClass]
	public partial class SeismicWaveAction : CompletedStructureAbilityAction
	{
		[Export] public string DisplayNameText = "地震波";
		protected override string ActionId => "SeismicWave";
	}
}
