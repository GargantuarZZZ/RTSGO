using Godot;

namespace RTS.Core
{
	// 种族面板管理器：遍历 user_ui.tscn 中 MainControl 下所有 RacePanel_<种族ID> 节点，
	// 按本地玩家种族显示对应面板、隐藏其他。新种族只需在场景里加面板节点。
	public partial class RacePanelManager : Node
	{
		private string _appliedRace = null;

		public override void _Process(double delta)
		{
			string raceName = RTS.World.Game.GetPlayerByTeam(Main.Instance?.LocalPlayerID ?? 0)?.Race?.RaceName ?? "";
			if (raceName == _appliedRace)
				return;

			var root = GetParent();

			if (root == null)
				return;

			// 兼容面板直接挂在根节点或放在 MainControl 下的两种布局
			ApplyVisibility(root, raceName);

			var mainControl = root.GetNodeOrNull<Control>("MainControl");
			if (mainControl != null)
				ApplyVisibility(mainControl, raceName);

			// 只在种族变化时设置一次；新对局换种族时再触发
			_appliedRace = raceName;
		}

		private void ApplyVisibility(Node container, string raceName)
		{
			foreach (Node child in container.GetChildren())
			{
				if (child is not Control panel)
					continue;

				string nodeName = child.Name.ToString();

				if (!nodeName.StartsWith("RacePanel_"))
					continue;

				string panelRace = nodeName.Substring("RacePanel_".Length);
				panel.Visible = panelRace == raceName;
			}
		}
	}
}
