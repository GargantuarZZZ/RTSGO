using Godot;
using RTS.Network;
using RTS.World;

namespace RTS.Core
{
	public partial class Main : Node2D
	{
		public static Main Instance { get; private set; }

		// =========================================================
		// 本地身份字段
		// =========================================================
		//
		// LocalNetworkPlayerID：
		// 	网络锁步玩家编号，例如 P1 / P2 / P3。
		// 	用于 Lockstep、Tick 确认、指令发送、哈希判断。
		//
		// LocalTeamID：
		// 	游戏阵营 / 出生位置 / 颜色归属。
		// 	用于视野、单位归属、出生点、UI表现。
		//
		// 重要：
		// 	这两个值绝对不能混用。
		// 	玩家选择了位置 3，不代表锁步 PlayerID 就变成 3。
		// =========================================================

		public int LocalNetworkPlayerID { get; set; } = 1;
		public int LocalTeamID { get; set; } = 1;

		// 兼容旧代码：
		// 旧代码里大量使用 Main.Instance.LocalPlayerID 来判断本地阵营。
		// 暂时保留这个属性，但它现在明确代表 TeamID，不代表网络 PlayerID。
		public int LocalPlayerID
		{
			get => LocalTeamID;
			set => LocalTeamID = value;
		}

		public override void _EnterTree()
		{
			Instance = this;
		}

		public override void _Ready()
		{
			RTS.Settings.GameSettings.Load();
			ResolveLocalIdentity();

			GD.Print($"[Main] 初始化完成。NetworkPlayerID={LocalNetworkPlayerID}, TeamID={LocalTeamID}");
		}

		// =========================================================
		// 身份初始化
		// =========================================================

		private void ResolveLocalIdentity()
		{
			// 优先从 NetworkManager / LockstepManager 读取已经分配好的身份。
			// 这里不主动重新分配 PlayerID，只同步已有结果。
			if (LockstepManager.Instance != null)
			{
				LocalNetworkPlayerID = LockstepManager.Instance.LocalPlayerID;
			}

			if (NetworkManager.Instance != null)
			{
				// NetworkManager.FinalizeLocalIdentity() 会在切场景前设置 LocalTeamID。
				// 如果当前能读到 TeamID，就同步一次。
				LocalTeamID = NetworkManager.Instance.LocalTeamID > 0
					? NetworkManager.Instance.LocalTeamID
					: LocalNetworkPlayerID;
			}
			else
			{
				LocalTeamID = LocalNetworkPlayerID;
			}

			// 再次强调：不要在这里把 LockstepManager.LocalPlayerID 改成 TeamID。
			if (LockstepManager.Instance != null)
			{
				LockstepManager.Instance.LocalPlayerID = LocalNetworkPlayerID;
			}
		}

		// =========================================================
		// 启动模拟
		// =========================================================

		public void StartSimulation()
		{
			ResolveLocalIdentity();

			if (SimManager.Instance == null)
			{
				GD.PrintErr("[Main] 无法启动模拟：SimManager.Instance 为空。");
				return;
			}

			if (LockstepManager.Instance == null)
			{
				GD.PrintErr("[Main] 无法启动模拟：LockstepManager.Instance 为空。");
				return;
			}

			// 只允许网络 PlayerID 进入 Lockstep。
			LockstepManager.Instance.LocalPlayerID = LocalNetworkPlayerID;

			SimManager.Instance.IsRunning = true;

			// 填充初始同步缓冲帧。
			LockstepManager.Instance.StartInitialBuffer();

			GD.Print($"[Main] Lockstep 同步系统已激活。NetworkPlayerID={LocalNetworkPlayerID}, TeamID={LocalTeamID}");
		}
	}
}
