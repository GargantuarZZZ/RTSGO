using Godot;
using RTS.Core;

namespace RTS.Tutorial
{
	// =========================================================
	// 教程 HUD：显示当前目标、进度与完成提示
	//
	// 为什么全部用代码建节点、而不是做成 .tscn：
	//   教程面板**只应该在教程模式下出现**。做成场景就得挂进 main.tscn，
	//   那样普通对局里也会有一个空面板（多一层节点、多一次可见性判断，
	//   还得为每一张地图场景都挂一遍）。运行时代码创建则天然只有教程局才有。
	//
	// 线程：每帧在主线程读 SimManager 的不可变快照（GetTutorialSnapshot），
	//   不直接碰模拟层内部状态。
	// =========================================================

	public partial class TutorialHud : CanvasLayer
	{
		private PanelContainer _panel;
		private Label _nameLabel;
		private Label _objectiveLabel;
		private Label _detailLabel;
		private Label _progressLabel;
		private Label _bannerLabel;
		private PanelContainer _bannerPanel;

		/// <summary>上一次用于刷新的目标标题+进度，避免每帧重排文本。</summary>
		private string _lastKey = "";

		/// <summary>教程完成提示已显示到第几秒（用于自动收起面板）。</summary>
		private double _finishedFor = -1;

		public override void _Ready()
		{
			Layer = 20; // 压在常规 HUD 之上，但低于设置菜单

			BuildUi();
			SetProcess(true);
		}

		private void BuildUi()
		{
			// ---------- 左上：目标面板 ----------
			_panel = new PanelContainer
			{
				Position = new Vector2(16, 16),
				CustomMinimumSize = new Vector2(340, 0),
			};
			AddChild(_panel);

			var box = new VBoxContainer();
			box.AddThemeConstantOverride("separation", 6);
			_panel.AddChild(box);

			_nameLabel = new Label { Text = "教程" };
			_nameLabel.AddThemeFontSizeOverride("font_size", 16);
			_nameLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
			box.AddChild(_nameLabel);

			_objectiveLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
			_objectiveLabel.AddThemeFontSizeOverride("font_size", 15);
			box.AddChild(_objectiveLabel);

			_detailLabel = new Label
			{
				AutowrapMode = TextServer.AutowrapMode.WordSmart,
				CustomMinimumSize = new Vector2(320, 0),
			};
			_detailLabel.AddThemeFontSizeOverride("font_size", 12);
			_detailLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));
			box.AddChild(_detailLabel);

			_progressLabel = new Label { Text = "" };
			_progressLabel.AddThemeFontSizeOverride("font_size", 13);
			_progressLabel.AddThemeColorOverride("font_color", new Color(0.6f, 1f, 0.6f));
			box.AddChild(_progressLabel);

			// ---------- 中上：完成提示横幅 ----------
			_bannerPanel = new PanelContainer
			{
				AnchorLeft = 0.5f,
				AnchorRight = 0.5f,
				OffsetLeft = -220,
				OffsetRight = 220,
				OffsetTop = 90,
				Visible = false,
			};
			AddChild(_bannerPanel);

			_bannerLabel = new Label
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				AutowrapMode = TextServer.AutowrapMode.WordSmart,
			};
			_bannerLabel.AddThemeFontSizeOverride("font_size", 16);
			_bannerLabel.AddThemeColorOverride("font_color", new Color(0.7f, 1f, 0.7f));
			_bannerPanel.AddChild(_bannerLabel);
		}

		public override void _Process(double delta)
		{
			var sim = SimManager.Instance;
			if (sim == null) return;

			var snap = sim.GetTutorialSnapshot();

			// 教程未开始：隐藏面板（普通对局里这个 HUD 不该出现）
			if (!snap.Active && string.IsNullOrEmpty(snap.TutorialName))
			{
				_panel.Visible = false;
				_bannerPanel.Visible = false;
				return;
			}

			_panel.Visible = true;

			// 关键：只在内容变化时重排文本，避免每帧触发布局
			string key = $"{snap.ObjectiveIndex}|{snap.Title}|{snap.ProgressText}|{snap.JustCompletedText}";
			if (key != _lastKey)
			{
				_lastKey = key;

				_nameLabel.Text = snap.Active
					? $"教程 · {snap.TutorialName}   [{snap.ObjectiveIndex + 1}/{snap.ObjectiveCount}]"
					: $"教程 · {snap.TutorialName}   （已完成）";

				_objectiveLabel.Text = snap.Active ? $"目标：{snap.Title}" : "全部目标完成";

				_detailLabel.Text = snap.Active ? snap.Detail : "";
				_detailLabel.Visible = snap.Active && !string.IsNullOrEmpty(snap.Detail);

				// 进度：有进度表达式才显示，避免出现无意义的 "0"
				_progressLabel.Text = string.IsNullOrEmpty(snap.ProgressText)
					? ""
					: $"进度 {snap.ProgressText}";
				_progressLabel.Visible = _progressLabel.Text.Length > 0;
			}

			// 完成提示横幅
			if (!string.IsNullOrEmpty(snap.JustCompletedText))
			{
				_bannerPanel.Visible = true;
				_bannerLabel.Text = snap.JustCompletedText;
			}
			else
			{
				_bannerPanel.Visible = false;
			}

			// 教程结束后再留 6 秒，然后把整个面板收掉，不挡视线
			if (!snap.Active)
			{
				_finishedFor = _finishedFor < 0 ? 0 : _finishedFor + delta;
				if (_finishedFor > 6)
				{
					_panel.Visible = false;
					_bannerPanel.Visible = false;
				}
			}
			else
			{
				_finishedFor = -1;
			}
		}
	}
}
