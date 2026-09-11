using Godot;

namespace RTS.Tools
{
	/// <summary>
	/// P0-2 回放自动验证：设置 REPLAY_FILE（user:// 路径）后挂到对局场景，
	/// 回放跑到录制 tick 数后停止模拟，逐 tick 对比哈希并打印结果。
	/// </summary>
	public partial class ReplayAutoVerify : Node
	{
		private bool _started = false;
		private int _targetTicks = -1;
		private double _ticksSinceLog = 0.0;

		public override void _Ready()
		{
			string path = OS.GetEnvironment("REPLAY_FILE");
			if (string.IsNullOrEmpty(path))
			{
				GD.Print("[ReplayVerify] 未设置 REPLAY_FILE，跳过");
				QueueFree();
				return;
			}

			CallDeferred(nameof(LoadAndStart), path);
		}

		private void LoadAndStart(string path)
		{
			var ls = RTS.Network.LockstepManager.Instance;
			if (ls == null)
			{
				GD.PrintErr("[ReplayVerify] LockstepManager 不可用");
				GetTree().Quit(1);
				return;
			}

			ls.LoadReplayFromFile(path);
			if (!ls.ReplayMode)
			{
				GD.PrintErr($"[ReplayVerify] 回放加载失败: {path}");
				GetTree().Quit(1);
				return;
			}

			string ticksStr = OS.GetEnvironment("REPLAY_TICKS");
			_targetTicks = int.TryParse(ticksStr, out int v) && v > 0
				? v
				: ls.ReplayTickHashes.Count * 30;
			if (_targetTicks <= 0)
				_targetTicks = 600;
			GD.Print($"[ReplayVerify] 回放已启动，目标 tick={_targetTicks}");
			_started = true;
		}

		public override void _Process(double delta)
		{
			if (!_started)
				return;
			if (OS.GetEnvironment("REPLAY_DEBUG") == "1")
			{
				_ticksSinceLog += delta;
				if (_ticksSinceLog >= 1.0)
				{
					_ticksSinceLog = 0.0;
					GD.Print($"[ReplayVerify] tick={RTS.Network.LockstepManager.Instance?.CurrentTick}/{_targetTicks}");
				}
			}

			var ls = RTS.Network.LockstepManager.Instance;
			if (ls == null || !ls.ReplayMode)
				return;

			bool simStopped = RTS.Core.SimManager.Instance != null &&
				!RTS.Core.SimManager.Instance.IsRunning;
			if (ls.CurrentTick >= _targetTicks || simStopped)
			{
				_started = false;
				RTS.Core.SimManager.Instance?.StopSimThread();
				string result = ls.VerifyReplayHashes();
				if (result.Length == 0)
				{
					GD.Print($"[ReplayVerify] PASS：{ls.ReplayTickHashes.Count} 个哈希逐 tick 一致");
					GetTree().Quit(0);
				}
				else
				{
					GD.PrintErr($"[ReplayVerify] FAIL：{result}");
					GetTree().Quit(1);
				}
			}
		}
	}
}
