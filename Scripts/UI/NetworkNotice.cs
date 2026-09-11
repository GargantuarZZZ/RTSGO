using Godot;
using RTS.Core;
using RTS.Network;

namespace RTS.UI
{
	// 网络状态提示：等待补包 / 玩家掉线 / 脱步暂停。
	// 只在主线程轮询 LockstepManager 状态，避免跨线程碰 Godot UI。
	public partial class NetworkNotice : CanvasLayer
	{
		public static NetworkNotice Instance { get; private set; }

		private Label _label;
		private string _flashText = "";
		private float _flashRemain = 0f;
		private string _dropText = "";
		private float _dropRemain = 0f;
		private readonly System.Collections.Generic.HashSet<int> _knownDropped = new();

		public override void _Ready()
		{
			Instance = this;
			Layer = 100;

			_label = new Label
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Modulate = new Color(1f, 0.85f, 0.25f)
			};
			_label.AddThemeFontSizeOverride("font_size", 18);

			var margin = new MarginContainer();
			margin.AddThemeConstantOverride("margin_top", 28);
			margin.AddChild(_label);
			AddChild(margin);

			_label.Visible = false;
		}

		public override void _Process(double delta)
		{
			if (LockstepManager.Instance == null || SimManager.Instance == null)
			{
				_label.Visible = false;
				return;
			}

			var lockstep = LockstepManager.Instance;

			// 脱步：持续显示，直到重置
			if (lockstep.DesyncTriggered)
			{
				ShowText(RTS.Settings.Localization.Tr("net.desync", lockstep.LastDesyncReportPath));
				return;
			}

			// 玩家掉线：提示 8 秒
			foreach (int pid in lockstep.DroppedPlayers)
			{
				if (_knownDropped.Add(pid))
				{
					_dropText = RTS.Settings.Localization.Tr("net.player_dropped", pid);
					_dropRemain = 8f;
				}
			}

			if (_dropRemain > 0f)
			{
				_dropRemain -= (float)delta;
				ShowText(_dropText);
				if (_dropRemain <= 0f)
					_label.Visible = false;
				return;
			}

			// 等待补包：连续卡住超过 1 秒才提示
			int tick = lockstep.CurrentTick;
			if (lockstep.LastStallTick == tick)
			{
				var missing = lockstep.GetMissingPlayers(tick);
				if (missing.Count > 0)
				{
					ShowText(RTS.Settings.Localization.Tr("net.waiting_input", string.Join(", ", missing)));
					return;
				}
			}

			if (_flashRemain > 0f)
			{
				_flashRemain -= (float)delta;
				ShowText(_flashText);
				return;
			}

			_label.Visible = false;
		}

		public void ShowMessage(string text, float seconds)
		{
			_flashText = text;
			_flashRemain = seconds;
		}

		private void ShowText(string text)
		{
			_label.Text = text;
			_label.Visible = true;
		}
	}
}
