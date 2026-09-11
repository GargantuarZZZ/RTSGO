using Godot;

namespace RTS.Tools
{
	// =========================================================
	// 离屏渲染截图工具（开发期自检用）
	//
	// 为什么需要它：这个项目的美术改动长期只能靠"改完让人肉眼看"，
	// 而对话里的截图反馈有延迟、也容易被漏掉。有了它，模型/材质/光照/UI
	// 的观感问题可以在本地先自查一轮，再按问题去改，而不是盲调参数。
	//
	// 做法：在**真实渲染窗口**里加载目标场景（不能用 --headless，
	// 那是 dummy 渲染器，截出来是空的），等渲染稳定后把视口读成 Image 存盘。
	//
	// 用法：
	//   godot --path . res://Scenes/Tools/ScreenshotTool.tscn -- --shot=<场景> [--wait=<帧>] [--out=<文件>] [--cam=<x,y,z>]
	//
	// 例：
	//   godot --path . res://Scenes/Tools/ScreenshotTool.tscn -- \
	//     --shot=res://Scenes/main.tscn --wait=90 --out=user://shot_ground.png
	// =========================================================

	public partial class ScreenshotTool : Node
	{
		private string _scenePath = "res://Scenes/main.tscn";
		private string _outPath = "user://screenshot.png";
		private int _waitFrames = 90;
		private Vector3? _forcedCamPos;
		private int _frame;
		private bool _captured;

		public override void _Ready()
		{
			ParseArgs();
			GD.Print($"[Shot] 目标={_scenePath} 等待={_waitFrames}帧 输出={_outPath}");

			var packed = GD.Load<PackedScene>(_scenePath);
			if (packed == null)
			{
				GD.PrintErr($"[Shot] 载入失败: {_scenePath}");
				GetTree().Quit(1);
				return;
			}

			var inst = packed.Instantiate();
			AddChild(inst);
			GetTree().CurrentScene = inst as Node;

			// 有些场景自己会跑对局逻辑，给一点时间让地形/单位生成出来
			CallDeferred(nameof(ApplyForcedCamera));
		}

		private void ParseArgs()
		{
			foreach (string a in OS.GetCmdlineUserArgs())
			{
				if (a.StartsWith("--shot=")) _scenePath = a.Substring(7);
				else if (a.StartsWith("--out=")) _outPath = a.Substring(6);
				else if (a.StartsWith("--wait=") && int.TryParse(a.Substring(7), out int w)) _waitFrames = w;
				else if (a.StartsWith("--cam="))
				{
					var parts = a.Substring(6).Split(',');
					if (parts.Length == 3 &&
						float.TryParse(parts[0], out float x) &&
						float.TryParse(parts[1], out float y) &&
						float.TryParse(parts[2], out float z))
						_forcedCamPos = new Vector3(x, y, z);
				}
			}
		}

		private void ApplyForcedCamera()
		{
			if (_forcedCamPos == null) return;
			var cam = GetViewport()?.GetCamera3D();
			if (cam == null) return;
			cam.GlobalPosition = _forcedCamPos.Value;
			GD.Print($"[Shot] 相机位置设为 {_forcedCamPos.Value}");
		}

		public override void _Process(double delta)
		{
			if (_captured) return;

			_frame++;
			if (_forcedCamPos != null && _frame == 1)
			{
				var cam = GetViewport()?.GetCamera3D();
				if (cam != null) cam.GlobalPosition = _forcedCamPos.Value;
			}

			if (_frame < _waitFrames) return;
			_captured = true;
			Capture();
		}

		private void Capture()
		{
			var vp = GetViewport();
			if (vp == null)
			{
				GD.PrintErr("[Shot] 没有视口");
				GetTree().Quit(1);
				return;
			}

			var tex = vp.GetTexture();
			if (tex == null)
			{
				GD.PrintErr("[Shot] 视口纹理为空（是不是用了 --headless？）");
				GetTree().Quit(1);
				return;
			}

			var img = tex.GetImage();
			if (img == null)
			{
				GD.PrintErr("[Shot] 取不到图像数据");
				GetTree().Quit(1);
				return;
			}

			// 注意：不要把 FlipY() 当成必需步骤。
			// Godot 4 的 `Viewport.GetTexture().GetImage()` 已经是**正确朝向**
			// （再翻一次会把 UI 文字倒过来 —— 之前就翻错了，截图里文字是倒的）。
			// 留一个开关是为不同后端/驱动出偏差时能自救。
			if (System.Environment.GetEnvironmentVariable("SHOT_FLIP") == "1")
				img.FlipY();

			var err = img.SavePng(ProjectSettings.GlobalizePath(_outPath));
			if (err != Error.Ok)
			{
				GD.PrintErr($"[Shot] 保存失败 err={err}");
				GetTree().Quit(1);
				return;
			}

			GD.Print($"[Shot] 已保存 {ProjectSettings.GlobalizePath(_outPath)} " +
				$"({img.GetWidth()}x{img.GetHeight()})");
			GetTree().Quit(0);
		}
	}
}
