using Godot;
using RTS.Core;

namespace RTS.UI
{
	// P1-4 协商/投票 HUD：显示进行中的锁步投票，N/M 表决，结果提示 3 秒。
	// 发起入口：Ctrl+H（重开投票，UserController 处理）。
	public partial class VoteUI : CanvasLayer
	{
		private Label _label;
		private double _outcomeRemain = 0.0;
		private string _shownOutcome = "";

		public override void _Ready()
		{
			Layer = 96;

			_label = new Label
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Modulate = new Color(1f, 0.92f, 0.55f)
			};
			_label.AddThemeFontSizeOverride("font_size", 18);

			var margin = new MarginContainer();
			margin.AddThemeConstantOverride("margin_top", 58);
			margin.AddChild(_label);
			AddChild(margin);
			_label.Visible = false;
		}

		public override void _Process(double delta)
		{
			var sim = SimManager.Instance;
			var lockstep = RTS.Network.LockstepManager.Instance;
			var vote = sim?.CurrentVote;

			// 新投票结果提示
			if (sim != null && sim.LastVoteOutcome.Length > 0 && sim.LastVoteOutcome != _shownOutcome)
			{
				_shownOutcome = sim.LastVoteOutcome;
				_outcomeRemain = 3.0;
			}

			if (vote != null && lockstep != null)
			{
				string kindText = RTS.Settings.Localization.Tr(
					vote.Kind == SimManager.VoteKindRematch ? "vote.rematch" : "vote.surrender_team");
				int remainTicks = Mathf.Max(0, vote.TickDeadline - lockstep.CurrentTick);
				_label.Text = RTS.Settings.Localization.Tr("vote.banner", vote.ProposerPid, kindText, remainTicks / 20.0);
				_label.Visible = true;
				return;
			}

			if (_outcomeRemain > 0.0)
			{
				_outcomeRemain -= delta;
				_label.Text = RTS.Settings.Localization.Tr(_shownOutcome);
				_label.Visible = true;
				if (_outcomeRemain <= 0.0)
					_label.Visible = false;
				return;
			}

			_label.Visible = false;
		}

		public override void _UnhandledInput(InputEvent @event)
		{
			var vote = SimManager.Instance?.CurrentVote;
			if (vote == null)
				return;
			if (@event is not InputEventKey key || !key.Pressed || key.Echo)
				return;

			if (key.Keycode == Key.N)
			{
				SendVote(vote.Kind, true);
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.M)
			{
				SendVote(vote.Kind, false);
				GetViewport().SetInputAsHandled();
			}
		}

		public static void SendVote(string kind, bool yes)
		{
			var lockstep = RTS.Network.LockstepManager.Instance;
			if (lockstep == null)
				return;
			lockstep.SendAction(new RTS.Network.NetAction
			{
				ActionId = "Vote",
				ActionIdExtra = kind,
				IsQueue = yes,
				PlayerID = lockstep.LocalPlayerID,
				EntityIDs = System.Array.Empty<int>()
			});
		}
	}
}
