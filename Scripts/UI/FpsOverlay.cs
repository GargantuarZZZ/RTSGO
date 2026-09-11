using Godot;
using RTS.Core;

namespace RTS.UI
{
	// 右下角帧率小字：画面 FPS + 逻辑 TPS（锁步实际执行率）
	public partial class FpsOverlay : CanvasLayer
	{
		private Label _label;
		private double _timer;

		public override void _Ready()
		{
			// 高于所有游戏 UI（UserUI 为 111），保证帧率小字永远显示在最上层
			Layer = 1000;

			_label = new Label
			{
				Name = "FpsLabel",
				MouseFilter = Control.MouseFilterEnum.Ignore,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Bottom
			};
			_label.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
			_label.OffsetLeft = -260;
			_label.OffsetTop = -36;
			_label.OffsetRight = -8;
			_label.OffsetBottom = -8;
			_label.AddThemeFontSizeOverride("font_size", 13);
			_label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.8f));
			_label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
			_label.AddThemeConstantOverride("outline_size", 4);
			AddChild(_label);
		}

		public override void _Process(double delta)
		{
			_timer += delta;
			if (_timer < 0.5)
				return;
			_timer = 0.0;

			float render = (float)Engine.GetFramesPerSecond();
			float logic = SimManager.Instance?.LogicFps ?? 0f;
			_label.Text = RTS.Settings.Localization.Tr("hud.fps_line", $"{render:F0}", $"{logic:F0}");
		}
	}
}
