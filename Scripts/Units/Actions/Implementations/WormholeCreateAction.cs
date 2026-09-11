using Godot;
using RTS.Data.Configs;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	// 虫洞核心 - 制造虫洞：在目标位置生成可传送单位的虫洞
	[GlobalClass]
	public partial class WormholeCreateAction : CompletedStructureAbilityAction
	{
		[Export] public string DisplayNameText = "制造虫洞";
		protected override string ActionId => "CaveWormhole";
	}
}
