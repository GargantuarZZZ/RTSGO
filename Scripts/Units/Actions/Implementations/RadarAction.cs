using Godot;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	// 轨道控制中心 - 雷达：点按钮后点地面指定落点，短暂点亮该处大片视野（数值来自建筑配置表）
	[GlobalClass]
	public partial class RadarAction : EnergyCostAbilityAction
	{
		[Export] public string DisplayNameText = "雷达";
		protected override string ActionId => "Radar";
		protected override float GetEnergyCost(StructureConfig cfg) => cfg.RadarEnergyCost;
	}
}
