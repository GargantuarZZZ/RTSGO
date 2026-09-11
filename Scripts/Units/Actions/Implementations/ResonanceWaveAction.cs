using Godot;
using RTS.Data.Configs;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	// 地动仪 - 共振波：选中敌方单位，对所有同类型敌方单位受伤 +50%（持续 10s）
	[GlobalClass]
	public partial class ResonanceWaveAction : CompletedStructureAbilityAction
	{
		[Export] public string DisplayNameText = "共振波";
		protected override string ActionId => "ResonanceWave";
	}
}
