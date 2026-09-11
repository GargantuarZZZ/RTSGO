using Godot;

namespace RTS.Tools
{
	/// <summary>
	/// P0-2 回放自动录制：设置 RECORD_REPLAY（user:// 路径）与 REPLAY_TICKS 后挂到对局场景，
	/// 跑到目标 tick 后停止模拟并保存回放文件（含逐 tick 哈希）。
	/// </summary>
	public partial class ReplayAutoRecord : Node
	{
		private bool _started = false;
		private int _targetTicks = 600;
		private string _outPath = "";
		private int _autoSurrenderTick = -1;
		private bool _surrenderSent = false;

		public override void _Ready()
		{
			_outPath = OS.GetEnvironment("RECORD_REPLAY");
			if (string.IsNullOrEmpty(_outPath))
			{
				GD.Print("[ReplayRecord] 未设置 RECORD_REPLAY，跳过");
				QueueFree();
				return;
			}
			string ticksStr = OS.GetEnvironment("REPLAY_TICKS");
			if (int.TryParse(ticksStr, out int v) && v > 0)
				_targetTicks = v;
			if (int.TryParse(OS.GetEnvironment("AUTO_SURRENDER_TICK"), out int st) && st > 0)
				_autoSurrenderTick = st;
			_started = true;
			GD.Print($"[ReplayRecord] 录制已启动，目标 tick={_targetTicks} -> {_outPath}");
		}

		public override void _Process(double delta)
		{
			if (!_started)
				return;
			var ls = RTS.Network.LockstepManager.Instance;
			if (ls == null || ls.ReplayMode)
				return;
			if (_autoSurrenderTick > 0 && !_surrenderSent && ls.CurrentTick >= _autoSurrenderTick)
			{
				_surrenderSent = true;
				ls.SendAction(new RTS.Network.NetAction
				{
					ActionId = "Surrender",
					PlayerID = ls.LocalPlayerID,
					EntityIDs = new int[0]
				});
				GD.Print($"[ReplayRecord] 已发送自动投降 @tick {ls.CurrentTick}");
			}

			bool simStopped = RTS.Core.SimManager.Instance != null &&
				!RTS.Core.SimManager.Instance.IsRunning;
			if (ls.CurrentTick < _targetTicks && !simStopped)
				return;

			_started = false;
			RTS.Core.SimManager.Instance?.StopSimThread();
			ls.SaveReplayToFile(_outPath);
			GD.Print($"[ReplayRecord] 已保存回放: {_outPath}（{ls.HistoryArchive.Count} ticks, {ls.RecordedTickHashes.Count} hashes）");
			GetTree().Quit(0);
		}
	}
}
