using Godot;
using RTS.Data;
using RTS.Data.Configs;

namespace RTS.Core
{
// 植物顶部面板：与纳米面板共用 RaceBuildPanelUI 基类，
// 森林蔓延已改为建筑技能（由生命树/分支树/森林节点释放），不再占用面板按钮。
public partial class PlantPanelUI : RaceBuildPanelUI
{
		protected override (string NodeName, string StructId)[] GetBuildBindings() => new (string, string)[]
		{
			("BtnBranch", "PlantBranchTree"),
			("BtnLeafPit", "PlantLeafPit"),
			("BtnSprout", "PlantSproutNest"),
			("BtnFlower", "PlantFlowerField"),
			("BtnNest", "PlantNestFort"),
			("BtnTurret", "PlantSunlightTurret"),
			("BtnSpring", "PlantLifeSpring"),
			("BtnWall", "PlantTreeWall"),
			("BtnEarth", "PlantEarthCore")
		};

		// 森林蔓延已改为建筑技能，面板不再处理地面扩散确认。
		public override bool ConfirmSpreadAt(Vector2 worldPos)
		{
			return false;
		}
	}
}
