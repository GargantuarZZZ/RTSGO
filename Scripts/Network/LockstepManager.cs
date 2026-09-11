using Godot;
using RTS.Core;
using System.Collections.Generic;
using System.Linq;

namespace RTS.Network
{
	public partial class LockstepManager : Node
	{
		public static LockstepManager Instance { get; private set; }

		// =========================================================
		// Lockstep 核心缓冲区
		// =========================================================

		private readonly object _syncRoot = new();
		private readonly System.Collections.Concurrent.ConcurrentQueue<OutPacket> _outbox = new();

		// 待发送包：TargetSteamId=0 表示广播；>0 表示定向补发给指定玩家
		private readonly struct OutPacket
		{
			public readonly byte[] Payload;
			public readonly ulong TargetSteamId;

			public OutPacket(byte[] payload, ulong targetSteamId)
			{
				Payload = payload;
				TargetSteamId = targetSteamId;
			}
		}

		private readonly Dictionary<int, List<NetAction>> _actionBuffer = new();
		private readonly Dictionary<int, HashSet<int>> _tickConfirmations = new();
		// 每个玩家在哪些 tick 上已经收到过战斗包（Sync/指令），系统确认必须以此为前提
		private readonly Dictionary<int, HashSet<int>> _battlePacketsByPlayerTick = new();
		private readonly Dictionary<int, Dictionary<int, long>> _syncHashes = new();
		// 本机发送 HashCheck 时的区段哈希字符串（按 Tick 保存，供房主对比）
		private readonly Dictionary<int, string> _localSectionStrings = new();
		// 每个玩家最后确认的 Tick（掉线判定用）
		private readonly Dictionary<int, int> _lastConfirmTickByPlayer = new();
		// tick 第一次“未就绪”的时间（毫秒），用于延迟误报/补发
		private readonly Dictionary<int, ulong> _tickUnreadySince = new();

		// 本机实际收到每个玩家的“战斗锁步包”数量（只统计去重后的新包），用于定位收包问题
		public readonly Dictionary<int, int> BattlePacketsReceivedByPlayer = new();

		public readonly Dictionary<int, List<NetAction>> HistoryArchive = new();
		// 补发请求时间（毫秒）：同一 tick 每 3 秒最多请求一次，丢包后可持续恢复
		private readonly Dictionary<int, ulong> _resendRequestedTicks = new();
		private const int ResendRetryIntervalMs = 3000;

		// 脱步触发过的 tick（防止重复广播 DESYNC_TRIGGER）
		private readonly HashSet<int> _desyncTriggeredTicks = new();

		// 已判定掉线的玩家（UI 提示用；ResetLockstep 时清空）
		public readonly HashSet<int> DroppedPlayers = new();
		// P1-4：掉线玩家的机器人接管标记（其单位转入自动攻击）
		public readonly HashSet<int> BotTakeoverPlayers = new();
		// 连续等待输入超过 1 秒的 tick（UI 提示用）
		public volatile int LastStallTick = -1;
		// 是否已触发脱步暂停（UI 提示用）
		public volatile bool DesyncTriggered = false;
		// 最近一次脱步报告保存路径
		public string LastDesyncReportPath = "";

		// =========================================================
		// Sequence 去重
		// =========================================================

		private uint _localSequence = 1;

		// 强去重：
		// Key = PlayerID + Sequence。
		// 防止 Steam 回环、重复广播、双入口残留导致同一包重复进入 Tick 缓冲。
		private readonly Dictionary<string, int> _receivedSequenceKeys = new();

		// 兼容旧包：
		// 如果收到 Version 1 或 Sequence = 0 的旧包，使用弱去重。
		private readonly HashSet<string> _receivedLegacyKeys = new();

		private const int MaxHistoryTicks = 2000;
		private const int MaxDedupKeys = 100000;
		private const int MissingTickLogInterval = 60;

		private int _lastMissingLogTick = -999999;

		[Export] public bool DebugLockstepLogs { get; set; } = true;

		// =========================================================
		// 状态变量
		// =========================================================

		public int CurrentTick { get; private set; } = 0;

		// =========================================================
		// P0-2 回放：录制输入 + 重放，重放时逐 tick 记录哈希用于对拍
		// =========================================================
		public bool ReplayMode { get; private set; } = false;
		private List<List<NetAction>> _replayActions = new();
		public readonly List<long> ReplayTickHashes = new();
		// 正常对局中记录的哈希（写入回放文件，供重放后逐 tick 对比）
		public readonly List<long> RecordedTickHashes = new();
		private List<long> _replayHashes = new();

		// 20Hz 逻辑帧时，4 Tick = 约 200ms 输入缓冲。
		// P0-3：动态输入延迟——可随网络质量/人数调节（默认 4，范围建议 3~8）。
		public int TickDelay { get; set; } = 4;
		// P0-3：迟到包容忍窗口（tick），网络抖动大可放宽
		public int LateTickTolerance { get; set; } = 10;
		// 连续卡 tick 计数：用于动态调整输入延迟
		private int _stallStreak = 0;
		private const int StallStreakDecayTicks = 600;
		private int _stallDecayCounter = 0;

		// 掉线判定：超过 300 tick（15 秒）无任何确认/指令，视为掉线并从锁步移除
		private const int PlayerDropTimeoutTicks = 300;

		// 只能代表网络玩家 ID，不能代表 TeamID / 出生点 / 颜色。
		public int LocalPlayerID { get; set; } = 1;

		// 当前参与锁步的玩家 ID。游戏开始后不应重排。
		public List<int> PlayerIDs = new List<int> { 1 };

		public override void _EnterTree()
		{
			Instance = this;
		}

		public override void _Ready()
		{
			// 重要：
			// LockstepManager 不再直接监听 SteamService.packet_received。
			// Steam 原始包只能由 NetworkManager 统一接收。
			GD.Print("[Lockstep] 已初始化。Steam 收包入口由 NetworkManager 统一管理。");
		}

		// =========================================================
		// Tick Ready 检查
		// =========================================================

		public bool IsTickReady(int tick)
		{
			lock (_syncRoot)
			{
				return IsTickReadyInner(tick);
			}
		}

		private bool IsTickReadyInner(int tick)
		{
			// 离线模式（单进程模拟全部玩家/机器人）无需等待确认包
			if (NetworkManager.OfflineMode)
				return true;

			if (PlayerIDs.Count <= 1)
				return true;

			if (!_tickConfirmations.ContainsKey(tick))
			{
				DropTimedOutPlayers(tick);

				if (PlayerIDs.Count <= 1)
					return true;

				HandleUnreadyTick(tick);
				return false;
			}

			foreach (int pid in PlayerIDs)
			{
				if (!_tickConfirmations[tick].Contains(pid))
				{
					DropTimedOutPlayers(tick);

					if (PlayerIDs.Count <= 1)
						return true;

					HandleUnreadyTick(tick);
					return false;
				}
			}

			return true;
		}

		// 确认包通常只比 tick 边界晚几毫秒，立刻补发会造成大量误报和刷包。
		// 只有连续超过 1 秒未就绪才打日志并请求补发。
		private void HandleUnreadyTick(int tick)
		{
			ulong now = Time.GetTicksMsec();

			if (!_tickUnreadySince.TryGetValue(tick, out ulong since))
			{
				_tickUnreadySince[tick] = now;
				return;
			}

			if (now - since < 1000)
				return;

			LastStallTick = tick;
			LogMissingPlayersIfNeeded(tick);
			RequestResendIfNeeded(tick);
			_stallStreak++;
			if (TickDelay < 8)
			{
				TickDelay++;
				GD.Print($"[Lockstep] 动态输入延迟上调: {TickDelay} (stallStreak={_stallStreak})");
			}
		}

		private void DropTimedOutPlayers(int tick)
		{
			foreach (int pid in PlayerIDs.ToList())
			{
				if (_tickConfirmations.TryGetValue(tick, out var confirmed) && confirmed.Contains(pid))
					continue;

				int lastConfirm = _lastConfirmTickByPlayer.GetValueOrDefault(pid, 0);

				if (tick - lastConfirm > PlayerDropTimeoutTicks)
				{
					GD.PrintErr($"[Lockstep] 玩家 {pid} 超过 {PlayerDropTimeoutTicks} tick 无确认，视为掉线移除");
					PlayerIDs.Remove(pid);
					DroppedPlayers.Add(pid);
					BotTakeoverPlayers.Add(pid);
					GD.Print($"[Lockstep] 玩家 {pid} 已由机器人接管");
				}
			}
		}

		public List<int> GetMissingPlayers(int tick)
		{
			if (PlayerIDs.Count <= 1)
				return new List<int>();

			if (!_tickConfirmations.TryGetValue(tick, out HashSet<int> confirmed))
				return new List<int>(PlayerIDs);

			return PlayerIDs
				.Where(pid => !confirmed.Contains(pid))
				.ToList();
		}

		private void LogMissingPlayersIfNeeded(int tick)
		{
			if (!DebugLockstepLogs)
				return;

			if (tick == _lastMissingLogTick)
				return;

			if (CurrentTick - _lastMissingLogTick < MissingTickLogInterval)
				return;

			_lastMissingLogTick = tick;

			List<int> missing = GetMissingPlayers(tick);
			string missingText = missing.Count > 0 ? string.Join(", ", missing) : "unknown";
			var counts = new List<string>();
			foreach (int pid in PlayerIDs)
			{
				int battle = BattlePacketsReceivedByPlayer.GetValueOrDefault(pid, 0);
				int system = NetworkManager.Instance != null
					? NetworkManager.Instance.SystemPacketsReceivedByPlayer.GetValueOrDefault(pid, 0)
					: 0;
				counts.Add($"P{pid}战斗包={battle},系统包={system}");
			}

			GD.PrintErr($"[Lockstep] Tick {tick} 尚未就绪，缺少玩家确认: [{missingText}] ({string.Join(" | ", counts)})");
		}

		// =========================================================
		// 本地发送指令
		// =========================================================

		public void SendAction(NetAction action)
		{
			// 模拟线程在 ExecuteOneTick 全程持有 WorldLock：
			// 主线程发指令也先拿同一把锁，保证 TargetTick 一定落在尚未消费的未来 tick，避免竞态丢指令。
			var worldLock = RTS.Core.SimManager.Instance?.WorldLock;
			if (worldLock != null)
			{
				lock (worldLock)
				{
					SendActionInner(action);
				}
				return;
			}
			SendActionInner(action);
		}

		private void SendActionInner(NetAction action)
		{
			lock (_syncRoot)
			{
				action.TargetTick = CurrentTick + TickDelay;
				action.PlayerID = LocalPlayerID;
				BroadcastAction(action);
			}
		}

		public void SendSyncHeartbeat()
		{
			lock (_syncRoot)
			{
				var syncAction = new NetAction
				{
					PlayerID = LocalPlayerID,
					TargetTick = CurrentTick + TickDelay,
					ActionId = "Sync",
					EntityIDs = new int[0]
				};

				BroadcastAction(syncAction);
			}
		}

		// 等待阶段心跳：卡在某个 tick 时，每一帧都广播“我对当前 tick 已无更多指令”，
		// 让对端立刻能确认这个 tick，而不是靠补发一个 tick 一个 tick 硬爬。
		public void SendCurrentTickConfirm()
		{
			// 不再每帧刷确认包（会造成系统通道刷爆）。
			// 锁步推进由战斗 Sync + 1 秒超时补发保证。
		}

		// 补发响应：把指定 tick 的 Sync 用新序号定向重发，供丢失方恢复确认
		public void ResendSyncForTickTo(int tick, ulong steamId)
		{
			lock (_syncRoot)
			{
				var syncAction = new NetAction
				{
					PlayerID = LocalPlayerID,
					TargetTick = tick,
					ActionId = "Sync",
					EntityIDs = new int[0]
				};

				BroadcastActionTo(syncAction, steamId);
			}
		}

		public void StartInitialBuffer()
		{
			for (int i = 0; i < TickDelay; i++)
			{
				var syncAction = new NetAction
				{
					PlayerID = LocalPlayerID,
					TargetTick = i,
					ActionId = "Sync",
					EntityIDs = new int[0]
				};

				BroadcastAction(syncAction);
				NetworkManager.Instance?.SendTickConfirm(i);
			}

			GD.Print($"[Lockstep] 初始缓冲已填充。LocalPlayerID={LocalPlayerID}, Delay={TickDelay}");
		}

		public void SendStateHash(int tick, long hash)
		{
			if (ReplayMode)
			{
				ReplayTickHashes.Add(hash);
				return;
			}

			RecordedTickHashes.Add(hash);

			lock (_syncRoot)
			{
				string sections = SimManager.Instance != null ? SimManager.Instance.BuildSectionHashString() : "";
				_localSectionStrings[tick] = sections;

				var hashAction = new NetAction
				{
					PlayerID = LocalPlayerID,
					TargetTick = tick,
					ActionId = "HashCheck",
					TargetX = hash,
					ActionIdExtra = sections,
					EntityIDs = new int[0]
				};

				BroadcastAction(hashAction);
			}
		}

		private void BroadcastAction(NetAction action)
		{
			BroadcastActionTo(action, 0);
		}

		private void BroadcastActionTo(NetAction action, ulong targetSteamId)
		{
			PrepareOutgoingAction(ref action);
			byte[] payload = action.Serialize();

			// 网络发送一律走主线程代发（模拟线程只入队），避免跨线程碰 Steam。
			_outbox.Enqueue(new OutPacket(payload, targetSteamId));

			// 本地回环：
			// 本机发出的战斗指令也必须进入自己的锁步缓冲。
			// 如果 SteamService 又把自己的包回发回来，Sequence 去重会挡住重复包。
			ReceiveExternalAction(action);
		}

		/// <summary>主线程每帧调用：把模拟线程/本帧排队的网络包真正发给 Steam。</summary>
		public void FlushOutbox()
		{
			while (_outbox.TryDequeue(out OutPacket packet))
			{
				Node steamService = GetNodeOrNull("/root/SteamService");
				if (steamService != null)
				{
					if (packet.TargetSteamId != 0)
						steamService.Call("send_packet_to", packet.TargetSteamId, packet.Payload);
					else
						steamService.Call("broadcast_packet", packet.Payload);
				}
				else
				{
					GD.PrintErr("[Lockstep] 未找到 /root/SteamService，本地仅回环处理。");
				}
			}
		}

		private void PrepareOutgoingAction(ref NetAction action)
		{
			action.Version = NetAction.ProtocolVersion;

			if (action.PlayerID <= 0)
				action.PlayerID = LocalPlayerID;

			if (action.Sequence == 0)
				action.Sequence = NextSequence();

			if (action.EntityIDs == null)
				action.EntityIDs = new int[0];

			if (action.ActionIdExtra == null)
				action.ActionIdExtra = "";
		}

		private uint NextSequence()
		{
			uint seq = _localSequence;
			_localSequence++;

			if (_localSequence == 0)
				_localSequence = 1;

			return seq;
		}

		// =========================================================
		// 外部接收指令
		// =========================================================

		public void ReceiveExternalAction(NetAction action)
		{
			lock (_syncRoot)
			{
				ReceiveExternalActionInner(action);
			}
		}

		private void ReceiveExternalActionInner(NetAction action)
		{
			if (string.IsNullOrEmpty(action.ActionId))
			{
				GD.PrintErr("[Lockstep] 收到空 ActionId，已丢弃。");
				return;
			}

			if (IsSystemAction(action.ActionId))
			{
				if (DebugLockstepLogs)
					GD.Print($"[Lockstep] 忽略系统包: {action.ActionId}");

				return;
			}

			int tick = action.TargetTick;

			if (tick < CurrentTick - LateTickTolerance && action.ActionId != "DESYNC_TRIGGER" && action.ActionId != "HashCheck")
			{
				GD.PrintErr($"[Lockstep] 收到过旧指令，已丢弃。Action={action.ActionId}, TargetTick={tick}, CurrentTick={CurrentTick}, Player={action.PlayerID}");
				return;
			}

			// 1. 脱步触发包
			if (action.ActionId == "DESYNC_TRIGGER")
			{
				TriggerDesyncDump(tick, action.TargetX, action.TargetY, action.TargetEntityID, action.ActionIdExtra);
				return;
			}

			// 2. 哈希校验包
			if (action.ActionId == "HashCheck")
			{
				if (!TryMarkActionReceived(action))
				{
					if (DebugLockstepLogs)
						GD.Print($"[Lockstep] 重复 HashCheck 已忽略。Tick={action.TargetTick}, Player={action.PlayerID}, Seq={action.Sequence}");

					return;
				}

				HandleHashReceived(tick, action.PlayerID, action.TargetX, action.ActionIdExtra);
				return;
			}

			// 3. 常规战斗指令 / Sync 心跳
			if (!TryMarkActionReceived(action))
			{
				if (DebugLockstepLogs)
				{
					GD.Print($"[Lockstep] 重复包已忽略。Tick={action.TargetTick}, Player={action.PlayerID}, Seq={action.Sequence}, Action={action.ActionId}");
				}

				return;
			}

			BattlePacketsReceivedByPlayer[action.PlayerID] =
				BattlePacketsReceivedByPlayer.GetValueOrDefault(action.PlayerID, 0) + 1;

			if (!_battlePacketsByPlayerTick.ContainsKey(action.PlayerID))
				_battlePacketsByPlayerTick[action.PlayerID] = new HashSet<int>();
			_battlePacketsByPlayerTick[action.PlayerID].Add(tick);

			if (!_actionBuffer.ContainsKey(tick))
				_actionBuffer[tick] = new List<NetAction>();

			if (!_tickConfirmations.ContainsKey(tick))
				_tickConfirmations[tick] = new HashSet<int>();

			if (action.ActionId != "Sync")
			{
				_actionBuffer[tick].Add(action);
			}

			_tickConfirmations[tick].Add(action.PlayerID);
			_lastConfirmTickByPlayer[action.PlayerID] = tick;
		}

		private bool IsSystemAction(string actionId)
		{
			return !string.IsNullOrEmpty(actionId) &&
				   (actionId.StartsWith("LOBBY_") || actionId.StartsWith("GAME_"));
		}

		private bool TryMarkActionReceived(NetAction action)
		{
			if (action.Sequence != 0)
			{
				string key = $"{action.PlayerID}|{action.Sequence}";

				if (_receivedSequenceKeys.ContainsKey(key))
					return false;

				_receivedSequenceKeys[key] = action.TargetTick;
				return true;
			}

			// 兼容旧包。
			string legacyKey = BuildLegacyActionKey(action);
			return _receivedLegacyKeys.Add(legacyKey);
		}

		private string BuildLegacyActionKey(NetAction action)
		{
			string entityIds = action.EntityIDs == null
				? ""
				: string.Join(",", action.EntityIDs);

			return $"{action.PlayerID}|{action.TargetTick}|{action.ActionId}|{action.ActionIdExtra}|{entityIds}|{action.TargetX}|{action.TargetY}|{action.TargetEntityID}|{action.IsQueue}";
		}

		// =========================================================
		// Hash 检查
		// =========================================================

		private void HandleHashReceived(int tick, int playerId, long hash, string sectionString)
		{
			if (!_syncHashes.ContainsKey(tick))
				_syncHashes[tick] = new Dictionary<int, long>();

			_syncHashes[tick][playerId] = hash;

			// 自己的哈希由本地回环写入，不参与对比
			if (playerId == LocalPlayerID)
				return;

			// 只有本机哈希也到达后，才可能做对比
			if (!_syncHashes[tick].TryGetValue(LocalPlayerID, out long localHash) || localHash == 0)
				return;

			if (hash == localHash)
				return;

			if (!_desyncTriggeredTicks.Add(tick))
				return;

			// 任意客户端都可检测并广播脱步，不依赖房主在线（房主掉线后仍能发现）
			string localSections = _localSectionStrings.GetValueOrDefault(tick, "");
			string diffSections = SimManager.Instance != null
				? SimManager.CompareSectionStrings(localSections, sectionString)
				: "unknown";

			var desyncAction = new NetAction
			{
				ActionId = "DESYNC_TRIGGER",
				PlayerID = LocalPlayerID,
				TargetTick = tick,
				TargetX = localHash,
				TargetY = hash,
				TargetEntityID = playerId,
				ActionIdExtra = diffSections,
				EntityIDs = new int[0]
			};

			GD.PrintErr($"[Lockstep] 检测到脱步! Tick={tick}, Player={playerId}, 差异区段: {diffSections}");
			BroadcastAction(desyncAction);
		}

		// =========================================================
		// 消费 Tick 指令
		// =========================================================

		public List<NetAction> ConsumeActions(int tick)
		{
			lock (_syncRoot)
			{
				return ConsumeActionsInner(tick);
			}
		}

		private List<NetAction> ConsumeActionsInner(int tick)
		{
			if (ReplayMode)
			{
				HistoryArchive[tick] = tick < _replayActions.Count
					? new List<NetAction>(_replayActions[tick])
					: new List<NetAction>();
				return HistoryArchive[tick];
			}

			_tickConfirmations.Remove(tick);

			if (_actionBuffer.TryGetValue(tick, out List<NetAction> actions))
			{
				actions.Sort(CompareActionsDeterministically);

				HistoryArchive[tick] = new List<NetAction>(actions);
				_actionBuffer.Remove(tick);

				CleanupOldTicks();

				return actions;
			}

			HistoryArchive[tick] = new List<NetAction>();

			CleanupOldTicks();

			return new List<NetAction>();
		}

		private int CompareActionsDeterministically(NetAction a, NetAction b)
		{
			int cmp1 = a.PlayerID.CompareTo(b.PlayerID);
			if (cmp1 != 0) return cmp1;

			int cmp2 = a.Sequence.CompareTo(b.Sequence);
			if (cmp2 != 0) return cmp2;

			int cmp3 = string.Compare(a.ActionId ?? "", b.ActionId ?? "", System.StringComparison.Ordinal);
			if (cmp3 != 0) return cmp3;

			int idA = a.EntityIDs != null && a.EntityIDs.Length > 0 ? a.EntityIDs[0] : 0;
			int idB = b.EntityIDs != null && b.EntityIDs.Length > 0 ? b.EntityIDs[0] : 0;

			int cmp4 = idA.CompareTo(idB);
			if (cmp4 != 0) return cmp4;

			int cmp5 = a.TargetEntityID.CompareTo(b.TargetEntityID);
			if (cmp5 != 0) return cmp5;

			int cmp6 = a.TargetX.CompareTo(b.TargetX);
			if (cmp6 != 0) return cmp6;

			return a.TargetY.CompareTo(b.TargetY);
		}

		private void CleanupOldTicks()
		{
			int cutoff = CurrentTick - MaxHistoryTicks;

			foreach (int tick in HistoryArchive.Keys.Where(t => t < cutoff).ToList())
				HistoryArchive.Remove(tick);

			foreach (int tick in _syncHashes.Keys.Where(t => t < cutoff).ToList())
				_syncHashes.Remove(tick);

			foreach (int tick in _localSectionStrings.Keys.Where(t => t < cutoff).ToList())
				_localSectionStrings.Remove(tick);

			foreach (int tick in _tickConfirmations.Keys.Where(t => t < cutoff).ToList())
				_tickConfirmations.Remove(tick);

			foreach (int tick in _tickUnreadySince.Keys.Where(t => t < cutoff).ToList())
				_tickUnreadySince.Remove(tick);

			foreach (int pid in _battlePacketsByPlayerTick.Keys.ToList())
			{
				foreach (int tick in _battlePacketsByPlayerTick[pid].Where(t => t < cutoff).ToList())
					_battlePacketsByPlayerTick[pid].Remove(tick);

				if (_battlePacketsByPlayerTick[pid].Count == 0)
					_battlePacketsByPlayerTick.Remove(pid);
			}

			foreach (int pid in _lastConfirmTickByPlayer.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
				_lastConfirmTickByPlayer.Remove(pid);

			foreach (int tick in _resendRequestedTicks.Keys.Where(t => t < cutoff).ToList())
				_resendRequestedTicks.Remove(tick);

			foreach (int tick in _desyncTriggeredTicks.Where(t => t < cutoff).ToList())
				_desyncTriggeredTicks.Remove(tick);

			if (_receivedSequenceKeys.Count > MaxDedupKeys)
			{
				// 不再整体清空（清空后重复包可能再次入队导致指令重复执行），
				// 只淘汰远早于当前 Tick 的旧记录
				foreach (string key in _receivedSequenceKeys.Where(k => k.Value < cutoff).Select(k => k.Key).ToList())
					_receivedSequenceKeys.Remove(key);
			}

			if (_receivedLegacyKeys.Count > MaxDedupKeys)
			{
				GD.Print("[Lockstep] Legacy 去重缓存过大，已清空。");
				_receivedLegacyKeys.Clear();
			}
		}

		// =========================================================
		// 丢包补发：请求方在 Tick 未就绪时广播补发请求，
		// 其他客户端回补 Tick 确认 + 重播该 Tick 的历史指令
		// =========================================================

		private void RequestResendIfNeeded(int tick)
		{
			if (PlayerIDs.Count <= 1)
				return;

			ulong now = Time.GetTicksMsec();

			if (_resendRequestedTicks.TryGetValue(tick, out ulong lastRequest) &&
				now - lastRequest < ResendRetryIntervalMs)
			{
				return;
			}

			_resendRequestedTicks[tick] = now;
			NetworkManager.Instance?.SendResendRequest(tick);
		}

		public void ConfirmTick(int tick, int playerId)
		{
			if (tick < CurrentTick - 10)
				return;

			if (!PlayerIDs.Contains(playerId))
				return;

			// 关键：系统确认不能替代战斗包。
			// 只有已收到该玩家该 tick 的战斗包（Sync/指令），确认才有效，
			// 否则指令包丢失时 tick 会在缺指令的情况下被放行，造成静默脱步。
			if (!_battlePacketsByPlayerTick.TryGetValue(playerId, out var ticks) || !ticks.Contains(tick))
				return;

			if (!_tickConfirmations.ContainsKey(tick))
				_tickConfirmations[tick] = new HashSet<int>();

			_tickConfirmations[tick].Add(playerId);
			_lastConfirmTickByPlayer[playerId] = tick;
		}

		public void RebroadcastHistoricalActionsTo(int tick, ulong steamId)
		{
			if (!HistoryArchive.TryGetValue(tick, out var actions))
				return;

			foreach (var action in actions)
			{
				// 保留原始 PlayerID / Sequence，接收端去重会拦掉自己已收到的包
				BroadcastActionTo(action, steamId);
			}
		}

		// =========================================================
		// 脱步处理
		// =========================================================

		private void TriggerDesyncDump(int tick, long hostHash, long peerHash, int desyncPlayerId, string sectionInfo)
		{
			DesyncTriggered = true;

			if (SimManager.Instance != null)
				SimManager.Instance.IsRunning = false;

			GD.PrintErr($"\n======== 致命脱步异常: TICK {tick} ========");
			GD.PrintErr($"Host Hash: {hostHash} | Player {desyncPlayerId} Hash: {peerHash}");
			GD.PrintErr($"差异区段: {sectionInfo}");

			if (SimManager.Instance != null)
			{
				string report = SimManager.Instance.GetDesyncReport(tick, hostHash, peerHash, desyncPlayerId, sectionInfo);
				string path = $"user://desync_player_{LocalPlayerID}_tick_{tick}.txt";

				using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
				file.StoreString(report);

				GD.PrintErr($"[Desync] 状态快照已保存至: {ProjectSettings.GlobalizePath(path)}");
				LastDesyncReportPath = ProjectSettings.GlobalizePath(path);
			}
		}

		// =========================================================
		// Tick 推进与重置
		// =========================================================

		public void AdvanceTick()
		{
			lock (_syncRoot)
			{
				CurrentTick++;
				if (_stallStreak > 0)
				{
					_stallDecayCounter++;
					if (_stallDecayCounter >= StallStreakDecayTicks)
					{
						_stallDecayCounter = 0;
						_stallStreak--;
						if (_stallStreak == 0 && TickDelay > 3)
						{
							TickDelay--;
							GD.Print($"[Lockstep] 动态输入延迟回落: {TickDelay}");
						}
					}
				}
				else
				{
					_stallDecayCounter = 0;
				}
			}
		}

		public void ResetLockstep()
		{
			lock (_syncRoot)
			{
				ReplayMode = false;
				_replayActions.Clear();
				ReplayTickHashes.Clear();
				RecordedTickHashes.Clear();
				_replayHashes.Clear();
				_actionBuffer.Clear();
				_tickConfirmations.Clear();
				_syncHashes.Clear();
				_localSectionStrings.Clear();
				_lastConfirmTickByPlayer.Clear();
				HistoryArchive.Clear();
				_receivedSequenceKeys.Clear();
				_receivedLegacyKeys.Clear();
				_resendRequestedTicks.Clear();
				_desyncTriggeredTicks.Clear();
				DroppedPlayers.Clear();
				BotTakeoverPlayers.Clear();

				CurrentTick = 0;
				_stallStreak = 0;
				_stallDecayCounter = 0;
				TickDelay = 4;
				_localSequence = 1;
				_lastMissingLogTick = -999999;
				LastStallTick = -1;
				DesyncTriggered = false;
				LastDesyncReportPath = "";
			}

			GD.Print("[Lockstep] 状态已重置。");
		}

		// =========================================================
		// P0-2 回放：录制/加载/启动
		// =========================================================

		/// <summary>把已消费的逐 tick 指令写成回放文件（主线程调用）。</summary>
		public void SaveReplayToFile(string path)
		{
			lock (_syncRoot)
			{
				using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
				if (f == null)
					return;
				f.Store32((uint)HistoryArchive.Count);
				foreach (var kv in HistoryArchive.OrderBy(k => k.Key))
				{
					f.Store32((uint)kv.Key);
					f.Store32((uint)kv.Value.Count);
					foreach (var a in kv.Value)
					{
						byte[] bytes = a.Serialize();
						f.Store32((uint)bytes.Length);
						f.StoreBuffer(bytes);
					}
				}
				f.Store32((uint)RecordedTickHashes.Count);
				foreach (long h in RecordedTickHashes)
					f.Store64((ulong)h);
			}
		}

		/// <summary>从回放文件加载输入并进入回放模式（主线程调用）。</summary>
		public void LoadReplayFromFile(string path)
		{
			lock (_syncRoot)
			{
				using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
				if (f == null)
					return;
				uint count = f.Get32();
				var actions = new List<List<NetAction>>();
				for (uint i = 0; i < count; i++)
				{
					int tick = (int)f.Get32();
					uint n = f.Get32();
					var list = new List<NetAction>();
					for (uint j = 0; j < n; j++)
					{
						uint len = f.Get32();
						byte[] bytes = f.GetBuffer(len);
						if (NetAction.TryDeserialize(bytes, out var a))
							list.Add(a);
					}
					while (actions.Count <= tick)
						actions.Add(new List<NetAction>());
					actions[tick] = list;
				}
				uint hashCount = f.Get32();
				_replayHashes = new List<long>((int)hashCount);
				for (uint i = 0; i < hashCount; i++)
					_replayHashes.Add((long)f.Get64());
				_replayActions = actions;
				ReplayMode = true;
				CurrentTick = 0;
				ReplayTickHashes.Clear();
				GD.Print($"[Replay] 已加载回放：{_replayActions.Count} ticks, {_replayHashes.Count} hashes");
			}
		}

		/// <summary>回放结束后逐 tick 对比哈希。返回空串 = 一致，否则返回首个不一致位置。</summary>
		public string VerifyReplayHashes()
		{
			int n = Mathf.Min(ReplayTickHashes.Count, _replayHashes.Count);
			for (int i = 0; i < n; i++)
			{
				if (ReplayTickHashes[i] != _replayHashes[i])
					return $"mismatch at hash#{i}: replay={ReplayTickHashes[i]} recorded={_replayHashes[i]}";
			}
			if (ReplayTickHashes.Count != _replayHashes.Count)
				return $"hash count mismatch: replay={ReplayTickHashes.Count} recorded={_replayHashes.Count}";
			return "";
		}
	}
}
