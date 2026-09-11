using Godot;
using RTS.Data;
using RTS.Data.Configs;

namespace RTS.Core
{
	// 纳米虫顶部面板：按钮绑定/快捷键/本地化/扩散交互走 RaceBuildPanelUI 基类，
	// 这里只保留纳米专属部分：扩散 CD/充能刷新 + 菌毯合法性校验。
	public partial class NanoPanelUI : RaceBuildPanelUI
	{
		protected override (string NodeName, string StructId)[] GetBuildBindings() => new (string, string)[]
		{
			("BtnTurret", "NanoTurret"),
			("BtnSniper", "NanoSniper"),
			("BtnAA", "NanoAA"),
			("BtnHarvester", "NanoHarvester"),
			("BtnActive", "NanoActiveTower"),
			("BtnSmoke", "NanoSmokeTower")
		};

		public override void _Process(double delta)
		{
			if (!Visible || _spreadButton == null)
				return;

			var world = SimManager.Instance?.World;
			if (world == null)
				return;

			if (_spreadPending)
			{
				_spreadButton.Text = RTS.Settings.Localization.Tr("nano.spread_confirm", "F1");
				_spreadButton.Disabled = false;
			}
			else
			{
				float cd = (float)world.CarpetSpreadCooldown;
				int charges = world.CarpetSpreadCharges;
				_spreadButton.Text = cd > 0f
					? RTS.Settings.Localization.Tr("nano.spread_cd", charges, $"{cd:0.0}")
					: RTS.Settings.Localization.Tr("nano.spread_ready", charges);
				_spreadButton.Disabled = charges <= 0;
			}

			UpdateBuildButtons();
		}

		// 由 UserController 在左键点击地面时调用：释放点必须是已有纳米菌毯的格子
		public override bool ConfirmSpreadAt(Vector2 worldPos)
		{
			if (!_spreadPending)
				return false;

			var world = SimManager.Instance?.World;
			if (world == null)
			{
				_spreadPending = false;
				return true;
			}

			int gx = Mathf.FloorToInt(worldPos.X / world.Grid.TileSize);
			int gy = Mathf.FloorToInt(worldPos.Y / world.Grid.TileSize);

			if (world.CreepGrid.GetActiveCreep(gx, gy) != CreepType.NanoCreep)
			{
				FindUserController();
				_user?.SpawnSpreadClickFeedback(worldPos, Colors.Red);
				return false;
			}

			_spreadPending = false;
			SendSpreadAt(worldPos);
			return true;
		}

		private void SendSpreadAt(Vector2 point)
		{
			if (RTS.World.Game.GetPlayerByTeam(Main.Instance?.LocalPlayerID ?? 0)?.Race?.RaceName != "Nano")
				return;

			var cmd = RTS.Network.NetAction.GroundCommand("NanoSpread", Main.Instance.LocalPlayerID, point);
			RTS.Network.LockstepManager.Instance?.SendAction(cmd);
			_user?.SpawnSpreadClickFeedback(point);
		}
	}
}
