using Godot;
using RTS.Core;
using System.Collections.Generic;
using System.Linq;

namespace RTS.Network
{
	public partial class NetworkManager : Node
	{
		public static NetworkManager Instance { get; private set; }
		// 单机模式：不初始化/不使用 Steam，本机作为唯一玩家和房主。
		public static bool OfflineMode { get; set; } = false;
		// 主机判定缓存（模拟线程只读 C# 状态，不碰 Steam）
		public bool IsHost { get; private set; } = false;

		private Node _steamService;

		// =========================================================
		// Lobby 状态
		// Key = NetworkPlayerID，不是 TeamID
		// =========================================================

		public Dictionary<int, int> PeerTeamMap { get; private set; } = new();
		public Dictionary<int, string> PeerRaceMap { get; private set; } = new();
		// P1-5 队伍分组：同组 = 盟友（2v2 等）。默认组 = 出生点。
		public Dictionary<int, int> PeerGroupMap { get; private set; } = new();
		// 旁观者：不生成玩家实体，全图视野、不可操控
		public Dictionary<int, bool> PeerObserverMap { get; private set; } = new();
		public bool LocalIsObserver { get; private set; } = false;
		public Dictionary<int, bool> PeerReadyMap { get; private set; } = new();
		// 房主选择的地图："Classic" 经典大地图 / "1v1" 断墙小图
		public string SelectedMapId = "Classic";
		// 本机实际收到每个玩家的系统包数量，用于和 Lockstep 战斗包数量对比定位网络问题
		public readonly Dictionary<int, int> SystemPacketsReceivedByPlayer = new();

		// =========================================================
		// 身份映射
		// SteamID <-> NetworkPlayerID
		// =========================================================

		public Dictionary<ulong, int> SteamToPlayerId = new();
		public Dictionary<int, ulong> PlayerIdToSteam = new();

		public int LocalNetworkPlayerID => LockstepManager.Instance != null ? LockstepManager.Instance.LocalPlayerID : 1;
		public int LocalTeamID { get; private set; } = 1;
		public int HostPlayerID { get; private set; } = 1;
		public ulong HostSteamID { get; private set; } = 0;

		private bool _sessionLocked = false;
		private bool _sessionReadyEmitted = false;
		private bool _hostMapApplied = false;

		// =========================================================
		// 系统包 Sequence / 去重
		// =========================================================

		private uint _localSystemSequence = 1;
		private readonly HashSet<string> _receivedSystemPacketKeys = new();
		private const int MaxSystemDedupKeys = 50000;

		[Signal] public delegate void SessionReadyEventHandler();
		[Signal] public delegate void LobbyUpdatedEventHandler();

		public override void _EnterTree()
		{
			Instance = this;
		}

		public override void _Ready()
		{
			_steamService = GetNodeOrNull("/root/SteamService");
			EnsureNetworkNotice();

			if (IsOfflineLaunchRequested())
				OfflineMode = true;

			if (OfflineMode)
			{
				_steamService = null;
				BeginOfflineHost();
				GD.Print("[Network] 单机模式：不连接 Steam，本机为唯一玩家/房主。");
				return;
			}

			if (_steamService == null)
			{
				GD.PrintErr("[Network] 未找到 /root/SteamService。将以本地测试模式运行。");
				return;
			}

			// =====================================================
			// NetworkManager 是唯一 Steam 收包入口。
			// LockstepManager 不再直接连接 packet_received。
			// =====================================================

			Callable packetCallable = Callable.From<ulong, byte[]>(OnSteamPacketReceived);
			if (!_steamService.IsConnected("packet_received", packetCallable))
			{
				_steamService.Connect("packet_received", packetCallable);
			}

			Callable listCallable = Callable.From<Godot.Collections.Array>(RefreshPlayerList);
			if (!_steamService.IsConnected("player_list_updated", listCallable))
			{
				_steamService.Connect("player_list_updated", listCallable);
			}

			GD.Print("[Network] 已初始化。Steam 收包入口由 NetworkManager 统一管理。");
		}

		private void EnsureNetworkNotice()
		{
			if (GetTree().Root.FindChild("NetworkNotice", true, false) != null)
				return;

			// 自动加载 _Ready 阶段根节点仍在构建子节点，必须延迟到主循环再挂 UI
			CallDeferred(nameof(AddNetworkNotice));
		}

		private void AddNetworkNotice()
		{
			if (GetTree().Root.FindChild("NetworkNotice", true, false) != null)
				return;

			GetTree().Root.AddChild(new RTS.UI.NetworkNotice { Name = "NetworkNotice" });
		}

		// =========================================================
		// 玩家身份管理
		// =========================================================

		public void RefreshPlayerList(Godot.Collections.Array members)
		{
			if (_sessionLocked)
			{
				GD.Print("[Network] 会话已锁定，忽略玩家列表重新编号。");
				EmitSignal(SignalName.LobbyUpdated);
				return;
			}

			// 非房主：编号以房主广播的 LOBBY_PLAYER_MAP 为准，避免本机自算不一致
			if (_hostMapApplied && HostPlayerID != LocalNetworkPlayerID)
			{
				EmitSignal(SignalName.LobbyUpdated);
				return;
			}

			if (LockstepManager.Instance == null)
			{
				GD.PrintErr("[Network] LockstepManager.Instance 为空，无法刷新玩家映射。");
				return;
			}

			List<ulong> sortedIds = new();
			ulong hostFromList = 0;

			foreach (Godot.Collections.Dictionary member in members)
			{
				if (member.ContainsKey("id"))
				{
					sortedIds.Add((ulong)member["id"]);
				}

				// 房主标记由 SteamService 从大厅房主查询写入（双端一致）
				if (member.ContainsKey("is_host") && (bool)member["is_host"])
				{
					hostFromList = (ulong)member["id"];
				}
			}

			if (sortedIds.Count == 0)
			{
				GD.PrintErr("[Network] 成员列表为空，无法分配 PlayerID。");
				return;
			}

			ulong localSteamId = GetLocalSteamID();
			HostSteamID = hostFromList != 0 ? hostFromList : ResolveHostSteamID(sortedIds);

			GD.Print($"[Network] 房主判定: SteamID={HostSteamID} (来自玩家列表标记={hostFromList != 0})");

			// 房主永远是 NetworkPlayerID = 1。
			// 其他玩家按 SteamID 升序分配 2,3,4...
			List<ulong> orderedIds = new();

			if (HostSteamID != 0 && sortedIds.Contains(HostSteamID))
			{
				orderedIds.Add(HostSteamID);
			}

			foreach (ulong sid in sortedIds.OrderBy(x => x))
			{
				if (!orderedIds.Contains(sid))
				{
					orderedIds.Add(sid);
				}
			}

			SteamToPlayerId.Clear();
			PlayerIdToSteam.Clear();
			LockstepManager.Instance.PlayerIDs.Clear();

			for (int i = 0; i < orderedIds.Count; i++)
			{
				int playerId = i + 1;
				ulong steamId = orderedIds[i];

				SteamToPlayerId[steamId] = playerId;
				PlayerIdToSteam[playerId] = steamId;
				LockstepManager.Instance.PlayerIDs.Add(playerId);

				if (steamId == localSteamId)
				{
					LockstepManager.Instance.LocalPlayerID = playerId;
				}
			}

			HostPlayerID = SteamToPlayerId.GetValueOrDefault(HostSteamID, 1);

			CleanupInvalidLobbyState();
			RebroadcastLocalLobbyState();

			GD.Print($"[Network] 映射更新：本机 NetworkPlayerID={LockstepManager.Instance.LocalPlayerID}, HostPlayerID={HostPlayerID}");
			EmitSignal(SignalName.LobbyUpdated);

			// 房主把完整映射广播给所有客机，保证多人编号一致
			BroadcastPlayerMap();
		}

		private void BroadcastPlayerMap()
		{
			if (_sessionLocked)
				return;

			if (LockstepManager.Instance == null)
				return;

			// 只有 Steam 大厅房主（lobby creator）才能广播权威映射，
			// 避免编号错误的客机误发自己的表
			bool isHost = false;

			try
			{
				isHost = _steamService != null && (bool)_steamService.Get("is_host");
			}
			catch
			{
				isHost = false;
			}
			IsHost = isHost;

			if (!isHost)
				return;

			var playerIds = new List<int>(LockstepManager.Instance.PlayerIDs);
			var steamIds = new List<string>();

			foreach (int pid in playerIds)
				steamIds.Add(PlayerIdToSteam.GetValueOrDefault(pid, 0UL).ToString());

			var mapAction = new NetAction
			{
				ActionId = "LOBBY_PLAYER_MAP",
				EntityIDs = playerIds.ToArray(),
				ActionIdExtra = string.Join(",", steamIds),
				TargetTick = 0
			};

			BroadcastSystemAction(mapAction);
			GD.Print($"[Network] 房主广播玩家映射: {mapAction.ActionIdExtra}");
		}

		private void CleanupInvalidLobbyState()
		{
			if (LockstepManager.Instance == null)
				return;

			HashSet<int> validPlayers = new(LockstepManager.Instance.PlayerIDs);

			var stalePids = new HashSet<int>(PeerTeamMap.Keys);
			stalePids.UnionWith(PeerRaceMap.Keys);
			stalePids.UnionWith(PeerReadyMap.Keys);
			foreach (int pid in stalePids.Where(pid => !validPlayers.Contains(pid)).ToList())
			{
				PeerTeamMap.Remove(pid);
				PeerRaceMap.Remove(pid);
				PeerReadyMap.Remove(pid);
			}
		}

		private ulong GetLocalSteamID()
		{
			if (_steamService == null)
				return 0;

			try
			{
				return (ulong)_steamService.Call("get_local_steam_id");
			}
			catch
			{
				return 0;
			}
		}

		private ulong ResolveHostSteamID(List<ulong> sortedIds)
		{
			if (_steamService == null || sortedIds.Count == 0)
				return sortedIds.Count > 0 ? sortedIds[0] : 0;

			try
			{
				// 权威判定：Steam 大厅房主（双端查询同一个大厅，结果一致）
				object ownerResult = _steamService.Call("get_lobby_owner_steam_id");
				ulong ownerId = System.Convert.ToUInt64(ownerResult);

				if (ownerId != 0 && sortedIds.Contains(ownerId))
					return ownerId;
			}
			catch
			{
				// ignore
			}

			try
			{
				if ((bool)_steamService.Get("is_host"))
				{
					ulong localId = GetLocalSteamID();
					if (localId != 0)
						return localId;
				}
			}
			catch
			{
				// ignore
			}

			// 最后兜底：仅当大厅房主查询失败时使用（正常情况下不会走到这里）
			return sortedIds.OrderBy(x => x).First();
		}

		// =========================================================
		// 身份锁定
		// =========================================================

		public void FinalizeLocalIdentity()
		{
			if (LockstepManager.Instance == null)
				return;

			int myNetworkPlayerId = LockstepManager.Instance.LocalPlayerID;
			int mySelectedTeam = PeerTeamMap.GetValueOrDefault(myNetworkPlayerId, myNetworkPlayerId);
			LocalIsObserver = PeerObserverMap.GetValueOrDefault(myNetworkPlayerId, false);

			LocalTeamID = mySelectedTeam;

			// 只设置表现层 / 本地视角 TeamID。
			// 绝对不能把 LockstepManager.LocalPlayerID 改成 TeamID。
			if (Main.Instance != null)
			{
				Main.Instance.LocalNetworkPlayerID = myNetworkPlayerId;
				Main.Instance.LocalTeamID = mySelectedTeam;
			}

			GD.Print($"[Network] 身份锁定：NetworkPlayerID={myNetworkPlayerId} | TeamID={mySelectedTeam}");
		}

		public void LockSession()
		{
			_sessionLocked = true;

			if (LockstepManager.Instance != null)
			{
				LockstepManager.Instance.PlayerIDs.Sort();
			}

			GD.Print("[Network] 会话已锁定，玩家映射不再重排。");
		}

		public void UnlockSessionForLobby()
		{
			_sessionLocked = false;
			_sessionReadyEmitted = false;
			_hostMapApplied = false;
			_receivedSystemPacketKeys.Clear();
			_localSystemSequence = 1;

			// 回大厅 = 上一局彻底结束：锁步缓冲和模拟世界必须全量重置，
			// 否则再开一局会带着上一局的单位/指令/波次状态，第二局直接闪退。
			LockstepManager.Instance?.ResetLockstep();
			RTS.Core.SimManager.Instance?.ResetSimulation();

			GD.Print("[Network] 会话锁定已解除，回到大厅状态。");
		}

		// =========================================================
		// Steam 收包：唯一入口
		// =========================================================

		private void OnSteamPacketReceived(ulong senderSteamId, byte[] data)
		{
			if (!NetAction.TryDeserialize(data, out NetAction action))
			{
				GD.PrintErr($"[Network] 收到来自 {senderSteamId} 的坏包，已丢弃。");
				return;
			}

			if (string.IsNullOrEmpty(action.ActionId))
			{
				GD.PrintErr($"[Network] 收到来自 {senderSteamId} 的空 ActionId 包，已丢弃。");
				return;
			}

			BindSenderIfNeeded(senderSteamId, action);

			if (action.IsSystemAction())
			{
				if (!TryMarkSystemPacketReceived(action))
				{
					GD.Print($"[Network] 重复系统包已忽略：{action.BuildDebugString()}");
					return;
				}

				HandleSystemAction(action);
			}
			else
			{
				if (LockstepManager.Instance == null)
				{
					GD.PrintErr($"[Network] 收到战斗包 {action.ActionId}，但 LockstepManager.Instance 为空。");
					return;
				}

				LockstepManager.Instance.ReceiveExternalAction(action);
			}
		}

		private void BindSenderIfNeeded(ulong senderSteamId, NetAction action)
		{
			if (senderSteamId == 0)
				return;

			if (SteamToPlayerId.ContainsKey(senderSteamId))
				return;

			if (action.PlayerID <= 0)
				return;

			SteamToPlayerId[senderSteamId] = action.PlayerID;
			PlayerIdToSteam[action.PlayerID] = senderSteamId;

			GD.Print($"[Network] 补充 Sender 映射：Steam={senderSteamId} -> Player={action.PlayerID}");
		}

		private bool TryMarkSystemPacketReceived(NetAction action)
		{
			string key;

			if (action.Sequence != 0)
			{
				key = $"{action.PlayerID}|{action.Sequence}";
			}
			else
			{
				key = BuildLegacySystemKey(action);
			}

			bool added = _receivedSystemPacketKeys.Add(key);

			if (_receivedSystemPacketKeys.Count > MaxSystemDedupKeys)
			{
				GD.Print("[Network] 系统包去重缓存过大，已清空。");
				_receivedSystemPacketKeys.Clear();
			}

			return added;
		}

		private string BuildLegacySystemKey(NetAction action)
		{
			return $"{action.PlayerID}|{action.ActionId}|{action.ActionIdExtra}|{action.TargetEntityID}|{action.IsQueue}";
		}

		// =========================================================
		// 系统包发送
		// =========================================================

		private void BroadcastSystemAction(NetAction action)
		{
			PrepareOutgoingSystemAction(ref action);

			byte[] payload = action.Serialize();

			if (_steamService != null)
			{
				_steamService.Call("broadcast_packet", payload);
			}
			else
			{
				if (!OfflineMode)
				GD.PrintErr("[Network] 未找到 SteamService，系统包只在本地处理。");
			}

			// 本地回环处理。
			// 如果 SteamService 也把自己的包发回来，Sequence 去重会忽略重复包。
			if (TryMarkSystemPacketReceived(action))
			{
				HandleSystemAction(action);
			}
		}

		private void PrepareOutgoingSystemAction(ref NetAction action)
		{
			action.Version = NetAction.ProtocolVersion;

			if (LockstepManager.Instance != null)
			{
				action.PlayerID = LockstepManager.Instance.LocalPlayerID;
			}

			if (action.PlayerID <= 0)
				action.PlayerID = 1;

			if (action.Sequence == 0)
				action.Sequence = NextSystemSequence();

			if (action.EntityIDs == null)
				action.EntityIDs = new int[0];

			if (action.ActionIdExtra == null)
				action.ActionIdExtra = "";
		}

		private uint NextSystemSequence()
		{
			uint seq = _localSystemSequence;
			_localSystemSequence++;

			if (_localSystemSequence == 0)
				_localSystemSequence = 1;

			return seq;
		}

		// =========================================================
		// Lobby 逻辑
		// =========================================================

		public void SendLobbyUpdate(int teamId, string raceName, bool isReady, int group = 0, bool observer = false)
		{
			// 扩展字段编码进 ActionIdExtra：race|group|obs（旧客户端没有 | 时按纯 race 解析）
			string extra = $"{raceName ?? ""}|{(group > 0 ? group : teamId)}|{(observer ? 1 : 0)}";
			var updateAction = new NetAction
			{
				ActionId = "LOBBY_UPDATE",
				TargetEntityID = teamId,
				ActionIdExtra = extra,
				IsQueue = isReady,
				EntityIDs = new int[0]
			};

			BroadcastSystemAction(updateAction);
		}

		// 玩家列表变化（有人加入/离开）时，把自己当前的大厅状态重新广播一次，
		// 让新加入的玩家立刻看到房间里每个人的种族/位置/准备状态。
		private void RebroadcastLocalLobbyState()
		{
			if (_sessionLocked || LockstepManager.Instance == null)
				return;

			int myPid = LockstepManager.Instance.LocalPlayerID;
			if (myPid <= 0)
				return;

			int team = PeerTeamMap.GetValueOrDefault(myPid, 0);
			string race = PeerRaceMap.GetValueOrDefault(myPid, "");
			bool ready = PeerReadyMap.GetValueOrDefault(myPid, false);
			int group = PeerGroupMap.GetValueOrDefault(myPid, team);
			bool observer = PeerObserverMap.GetValueOrDefault(myPid, false);

			if (team > 0 && !string.IsNullOrEmpty(race))
				SendLobbyUpdate(team, race, ready, group, observer);

			// 房主顺便把当前地图选择广播给新加入的玩家
			if (_steamService != null)
			{
				try
				{
					if ((bool)_steamService.Get("is_host"))
						SendMapSelection(SelectedMapId);
				}
				catch
				{
					// 忽略单机/无大厅状态
				}
			}
		}

		// 房主广播地图选择（客机只读，以房主为准）
		public void SendMapSelection(string mapId)
		{
			if (_sessionLocked)
				return;

			SelectedMapId = string.IsNullOrEmpty(mapId) ? "Classic" : mapId;

			var mapAction = new NetAction
			{
				ActionId = "LOBBY_MAP",
				ActionIdExtra = SelectedMapId,
				EntityIDs = new int[0]
			};

			BroadcastSystemAction(mapAction);
			GD.Print($"[Network] 房主广播地图选择: {SelectedMapId}");
		}

		public void HandleSystemAction(NetAction action)
		{
			if (string.IsNullOrEmpty(action.ActionId))
				return;

			SystemPacketsReceivedByPlayer[action.PlayerID] =
				SystemPacketsReceivedByPlayer.GetValueOrDefault(action.PlayerID, 0) + 1;

			switch (action.ActionId)
			{
				case "LOBBY_UPDATE":
					HandleLobbyUpdate(action);
					break;

				case "LOBBY_MAP":
					HandleMapSelection(action);
					break;

				case "LOBBY_PLAYER_MAP":
					HandlePlayerMap(action);
					break;

				case "GAME_PREPARE":
					HandleGamePrepare(action);
					break;

				case "GAME_RESEND_REQUEST":
					HandleResendRequest(action);
					break;

				case "GAME_TICK_CONFIRM":
					HandleTickConfirm(action);
					break;

				default:
					GD.Print($"[Network] 未处理的系统包: {action.ActionId}");
					break;
			}
		}

		// =========================================================
		// 丢包补发协议
		// =========================================================

		public void SendResendRequest(int tick)
		{
			var request = new NetAction
			{
				ActionId = "GAME_RESEND_REQUEST",
				TargetTick = tick,
				EntityIDs = new int[0]
			};

			BroadcastSystemAction(request);
		}

		public void SendTickConfirm(int tick)
		{
			var confirm = new NetAction
			{
				ActionId = "GAME_TICK_CONFIRM",
				TargetTick = tick,
				EntityIDs = new int[0]
			};

			BroadcastSystemAction(confirm);
		}

		// 定向发送系统包：SteamID 未知时退化为广播，保证本地/旧客户端可用
		private void SendSystemActionTo(NetAction action, int playerId)
		{
			PrepareOutgoingSystemAction(ref action);
			byte[] payload = action.Serialize();
			ulong steamId = PlayerIdToSteam.TryGetValue(playerId, out ulong sid) ? sid : 0UL;

			if (_steamService != null && steamId != 0)
			{
				_steamService.Call("send_packet_to", (long)steamId, payload);
				return;
			}

			if (_steamService != null)
			{
				_steamService.Call("broadcast_packet", payload);
			}
			else if (!OfflineMode)
			{
				GD.PrintErr("[Network] 未找到 SteamService，系统包无法发送。");
			}

			// 本地回环：广播退化路径也走完整处理
			if (TryMarkSystemPacketReceived(action))
				HandleSystemAction(action);
		}

		public void SendTickConfirmTo(int tick, int playerId)
		{
			var confirm = new NetAction
			{
				ActionId = "GAME_TICK_CONFIRM",
				TargetTick = tick,
				EntityIDs = new int[0]
			};

			SendSystemActionTo(confirm, playerId);
		}

		private void HandleResendRequest(NetAction action)
		{
			if (LockstepManager.Instance == null)
				return;

			int requester = action.PlayerID;
			if (requester <= 0)
				return;

			ulong requesterSteam = PlayerIdToSteam.TryGetValue(requester, out ulong rSteam) ? rSteam : 0UL;

			// 定向补发：只回给请求方，避免全体广播造成补发风暴
			// 1. Tick 确认（解决 Sync 心跳丢失导致的确认缺失）
			SendTickConfirmTo(action.TargetTick, requester);
			// 2. 该 tick 的 Sync 用新序号重发（确认必须伴随战斗包，仅系统确认会被拦截）
			LockstepManager.Instance.ResendSyncForTickTo(action.TargetTick, requesterSteam);
			// 3. 若有历史指令，按原始 PlayerID/Sequence 重播
			LockstepManager.Instance.RebroadcastHistoricalActionsTo(action.TargetTick, requesterSteam);
		}

		private void HandleTickConfirm(NetAction action)
		{
			LockstepManager.Instance?.ConfirmTick(action.TargetTick, action.PlayerID);
		}

		private void HandleLobbyUpdate(NetAction action)
		{
			int playerId = action.PlayerID;

			if (playerId <= 0)
			{
				GD.PrintErr("[Lobby] 收到无效 PlayerID 的 LOBBY_UPDATE。");
				return;
			}

			string rawExtra = action.ActionIdExtra ?? "";
			string race = rawExtra;
			int group = action.TargetEntityID;
			bool observer = false;
			string[] parts = rawExtra.Split('|');
			if (parts.Length >= 1 && parts[0].Length > 0)
				race = parts[0];
			if (parts.Length >= 2 && int.TryParse(parts[1], out int parsedGroup) && parsedGroup > 0)
				group = parsedGroup;
			if (parts.Length >= 3 && parts[2] == "1")
				observer = true;

			PeerTeamMap[playerId] = action.TargetEntityID;
			PeerRaceMap[playerId] = race;
			PeerGroupMap[playerId] = group;
			PeerObserverMap[playerId] = observer;
			if (observer)
			{
				// 旁观者不占出生点/种族：权威清空，防止任何残留把旁观者变成玩家
				PeerTeamMap[playerId] = 0;
				PeerRaceMap[playerId] = "";
				PeerGroupMap[playerId] = 0;
			}
			PeerReadyMap[playerId] = action.IsQueue;

			GD.Print($"[Lobby] 玩家 {playerId} | Team={action.TargetEntityID} | Race={race} | Group={group} | Obs={observer} | Ready={action.IsQueue}");

			EmitSignal(SignalName.LobbyUpdated);
		}

		// 地图由房主权威决定：只有 PlayerID 1 的包生效
		private void HandleMapSelection(NetAction action)
		{
			if (_sessionLocked || action.PlayerID != 1)
				return;

			string mapId = action.ActionIdExtra ?? "";
			if (mapId != "Classic" && mapId != "1v1" && mapId != "Debug")
				mapId = "Classic";

			SelectedMapId = mapId;
			GD.Print($"[Network] 已应用房主地图选择: {mapId}");
			EmitSignal(SignalName.LobbyUpdated);
		}

		// 房主广播的权威玩家映射：所有客机直接采用，保证多人编号唯一且一致
		private void HandlePlayerMap(NetAction action)
		{
			if (_sessionLocked || action.PlayerID != 1)
				return;

			if (action.EntityIDs == null || action.EntityIDs.Length == 0)
				return;

			string[] steamParts = (action.ActionIdExtra ?? "").Split(',');
			if (steamParts.Length != action.EntityIDs.Length)
				return;

			var newSteamToPlayer = new Dictionary<ulong, int>();
			var newPlayerToSteam = new Dictionary<int, ulong>();
			var newPlayerIds = new List<int>();

			for (int i = 0; i < action.EntityIDs.Length; i++)
			{
				if (!ulong.TryParse(steamParts[i], out ulong sid) || sid == 0)
					return;

				int pid = action.EntityIDs[i];
				newSteamToPlayer[sid] = pid;
				newPlayerToSteam[pid] = sid;
				newPlayerIds.Add(pid);
			}

			SteamToPlayerId = newSteamToPlayer;
			PlayerIdToSteam = newPlayerToSteam;
			HostSteamID = newPlayerToSteam.GetValueOrDefault(1, 0UL);
			HostPlayerID = 1;

			if (LockstepManager.Instance != null)
			{
				LockstepManager.Instance.PlayerIDs.Clear();
				LockstepManager.Instance.PlayerIDs.AddRange(newPlayerIds);

				ulong localSteam = GetLocalSteamID();
				if (localSteam != 0 && newSteamToPlayer.TryGetValue(localSteam, out int myPid))
					LockstepManager.Instance.LocalPlayerID = myPid;
			}

			_hostMapApplied = true;

			GD.Print($"[Network] 应用房主映射：本机 NetworkPlayerID={LockstepManager.Instance?.LocalPlayerID}, 映射={action.ActionIdExtra}");
			EmitSignal(SignalName.LobbyUpdated);
		}

		private void HandleGamePrepare(NetAction action)
		{
			if (_sessionReadyEmitted)
			{
				GD.Print("[Network] GAME_PREPARE 已处理过，忽略重复包。");
				return;
			}

			GD.Print("[Network] 收到启动游戏指令，正在锁定会话与本地身份。");

			// 房主在 GAME_PREPARE 中带上 pid:team:race 权威出生清单，
			// 两端（包括房主本地回环）都按它覆盖 PeerTeamMap/PeerRaceMap，
			// 防止本地大厅数据在不同客户端上分叉导致初始实体不同步。
			ApplyAuthoritativeSpawnManifest(action);

			LockSession();
			FinalizeLocalIdentity();

			_sessionReadyEmitted = true;
			EmitSignal(SignalName.SessionReady);
		}

		private void ApplyAuthoritativeSpawnManifest(NetAction action)
		{
			if (action.PlayerID != 1 || string.IsNullOrEmpty(action.ActionIdExtra))
				return;

			string[] entries = action.ActionIdExtra.Split(';', System.StringSplitOptions.RemoveEmptyEntries);

			foreach (string entry in entries)
			{
				string[] parts = entry.Split(':');
				if (parts.Length < 3)
					continue;

				if (!int.TryParse(parts[0], out int pid) || pid <= 0)
					continue;

				if (!int.TryParse(parts[1], out int team))
					continue;

				string race = parts[2];
				if (string.IsNullOrEmpty(race) || race == "未选择" || race == "LOBBY_UPDATE")
					race = "Union";

				PeerTeamMap[pid] = team;
				PeerRaceMap[pid] = race;
				PeerGroupMap[pid] = team;
				PeerObserverMap[pid] = false;
				if (parts.Length >= 4 && int.TryParse(parts[3], out int group) && group > 0)
					PeerGroupMap[pid] = group;
				if (parts.Length >= 5 && parts[4] == "1")
					PeerObserverMap[pid] = true;
			}

			GD.Print($"[Network] 已应用权威出生清单: {action.ActionIdExtra}");
		}

		public static bool IsOfflineLaunchRequested()
		{
			var args = new List<string>();
			args.AddRange(OS.GetCmdlineArgs());
			args.AddRange(OS.GetCmdlineUserArgs());

			foreach (string arg in args)
			{
				if (arg == "--offline" || arg == "--single" || arg == "--no-steam")
					return true;
			}
			return false;
		}

		// 单机模式初始化：本机 = 玩家1 / 房主，后续走系统包本地回环即可。
		public void BeginOfflineHost()
		{
			OfflineMode = true;
			_steamService = null;
			HostPlayerID = 1;
			HostSteamID = 1;
			LocalTeamID = 1;
			SteamToPlayerId.Clear();
			SteamToPlayerId[1] = 1;
			PlayerIdToSteam.Clear();
			PlayerIdToSteam[1] = 1;
			_hostMapApplied = true;

			if (LockstepManager.Instance != null)
			{
				LockstepManager.Instance.PlayerIDs.Clear();
				LockstepManager.Instance.PlayerIDs.Add(1);
				LockstepManager.Instance.LocalPlayerID = 1;
			}

			GD.Print("[Network] 单机模式已启动：本机 = 玩家1 / 房主。");
			EmitSignal(SignalName.LobbyUpdated);
		}

		// 命令行直接开局：跳过菜单和准备流程，用默认种族/位置直接进入战场。
		public void StartOfflineGameDirect(string race = "Union", int team = 1, string mapId = "Classic")
		{
			BeginOfflineHost();
			SelectedMapId = mapId;
			PeerTeamMap[1] = team;
			PeerRaceMap[1] = race;
			PeerGroupMap[1] = team;
			PeerObserverMap[1] = false;
			PeerReadyMap[1] = true;
			// P1-1：命令行直达开局同样注入机器人（BOT_TEAMS / SimManager.BotTeamsOverride）
			AddOfflineBots();

			var startAction = new NetAction
			{
				ActionId = "GAME_PREPARE",
				TargetTick = 0,
				EntityIDs = new int[0],
				ActionIdExtra = $"1:{team}:{race}:{team}:0"
			};

			BroadcastSystemAction(startAction);
		}

		// P1-1：把机器人阵营（团队号）注册为离线玩家。单机下网络玩家号 == 队伍号。
		private void AddOfflineBots()
		{
			if (!OfflineMode || LockstepManager.Instance == null)
				return;

			// P1-5：大厅机器人列表（逐机器人种族/难度/组别）优先
			var botConfigs = RTS.Core.SimManager.BotConfigsOverride;
			if (botConfigs != null && botConfigs.Count > 0)
			{
				foreach (var botCfg in botConfigs)
				{
					if (botCfg.Team <= 1)
						continue;

					int pid = botCfg.Pid > 0 ? botCfg.Pid : botCfg.Team;
					if (!LockstepManager.Instance.PlayerIDs.Contains(pid))
						LockstepManager.Instance.PlayerIDs.Add(pid);
					PeerTeamMap[pid] = botCfg.Team;
					PeerRaceMap[pid] = string.IsNullOrEmpty(botCfg.Race) ? "Union" : botCfg.Race;
					PeerGroupMap[pid] = botCfg.Group > 0 ? botCfg.Group : botCfg.Team;
					PeerObserverMap[pid] = false;
					PeerReadyMap[pid] = true;
					SteamToPlayerId[(ulong)pid] = pid;
					PlayerIdToSteam[pid] = (ulong)pid;
					RTS.Core.SimManager.Instance?.SetBotDifficulty(botCfg.Team, botCfg.Difficulty);
				}

			LockstepManager.Instance.PlayerIDs.Sort();
			// SimManager 是常驻单例，开局时必须重新读取机器人配置，
			// 否则菜单里加的机器人永远不会进入 AI 系统（进游戏 AI 失灵）
			RTS.Core.SimManager.Instance?.ConfigureBotsFromOverrides();
			return;
		}

			string cfg = !string.IsNullOrEmpty(RTS.Core.SimManager.BotTeamsOverride)
				? RTS.Core.SimManager.BotTeamsOverride
				: OS.GetEnvironment("BOT_TEAMS") ?? "";

			// BOT_RACES=pid:Race,pid:Race 覆盖机器人种族（无 Steam 多族 AI 回归测试用）
			var raceOverrideMap = new Dictionary<int, string>();
			foreach (string part in (OS.GetEnvironment("BOT_RACES") ?? "")
				.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
			{
				string[] kv = part.Split(':');
				if (kv.Length == 2 && int.TryParse(kv[0].Trim(), out int pid) && pid > 0)
					raceOverrideMap[pid] = kv[1].Trim();
			}

			foreach (string part in cfg.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
			{
				if (!int.TryParse(part.Trim(), out int team) || team <= 1)
					continue;

				int pid = team;
				if (!LockstepManager.Instance.PlayerIDs.Contains(pid))
					LockstepManager.Instance.PlayerIDs.Add(pid);
				PeerTeamMap[pid] = team;
				PeerRaceMap[pid] = PeerRaceMap.GetValueOrDefault(1, "Union");
				if (raceOverrideMap.TryGetValue(pid, out string overrideRace) &&
					overrideRace.Length > 0)
					PeerRaceMap[pid] = overrideRace;
				PeerGroupMap[pid] = team;
				PeerObserverMap[pid] = false;
				PeerReadyMap[pid] = true;
				SteamToPlayerId[(ulong)pid] = pid;
				PlayerIdToSteam[pid] = (ulong)pid;
			}

			LockstepManager.Instance.PlayerIDs.Sort();
			RTS.Core.SimManager.Instance?.ConfigureBotsFromOverrides();
		}

		public void HostStartGame()
		{
			if (!OfflineMode && _steamService == null)
			{
				GD.PrintErr("[Network] 无 SteamService，无法作为房主启动联机游戏。");
				return;
			}

			bool isHost = OfflineMode;

			try
			{
				isHost = OfflineMode || (bool)_steamService.Get("is_host");
			}
			catch
			{
				isHost = false;
			}
			IsHost = isHost;

			if (!isHost)
			{
				GD.PrintErr("[Network] 只有房主可以启动游戏。");
				return;
			}

			if (!ValidateLobbyReady())
			{
				GD.PrintErr("[Network] 无法开始：仍有玩家未选择种族 / 出生点 / 准备。");
				return;
			}

			LockSession();
			FinalizeLocalIdentity();
			// P1-1：单机模式按 UI/环境变量注入机器人玩家（pid==team），保证初始内容生成
			AddOfflineBots();

			var startAction = new NetAction
			{
				ActionId = "GAME_PREPARE",
				TargetTick = 0,
				EntityIDs = new int[0]
			};

			// 权威出生清单：pid:team:race;...
			if (LockstepManager.Instance != null)
			{
				var manifestParts = new List<string>();

				foreach (int pid in LockstepManager.Instance.PlayerIDs)
				{
					int team = PeerTeamMap.GetValueOrDefault(pid, pid);
					string race = PeerRaceMap.GetValueOrDefault(pid, "");
					int group = PeerGroupMap.GetValueOrDefault(pid, team);
					bool observer = PeerObserverMap.GetValueOrDefault(pid, false);

					if (string.IsNullOrEmpty(race) || race == "未选择" || race == "LOBBY_UPDATE")
						race = "Union";

					manifestParts.Add($"{pid}:{team}:{race}:{group}:{(observer ? 1 : 0)}");
				}

				startAction.ActionIdExtra = string.Join(";", manifestParts);
			}

			BroadcastSystemAction(startAction);
		}

		private bool ValidateLobbyReady()
		{
			if (LockstepManager.Instance == null)
				return false;

			var takenTeams = new HashSet<int>();

			foreach (int pid in LockstepManager.Instance.PlayerIDs)
			{
				if (PeerObserverMap.GetValueOrDefault(pid, false))
					continue; // 旁观者无需种族/出生点/准备

				int team = PeerTeamMap.GetValueOrDefault(pid, 0);
				string race = PeerRaceMap.GetValueOrDefault(pid, "");
				bool ready = PeerReadyMap.GetValueOrDefault(pid, false);

				if (team <= 0)
					return false;

				if (string.IsNullOrEmpty(race) || race == "未选择")
					return false;

				if (!ready)
					return false;

				// 出生点唯一：两个玩家不能选同一个位置
				if (!takenTeams.Add(team))
					return false;
			}

			return true;
		}

		public bool GetPlayerReadyStatus(int playerId)
		{
			return PeerReadyMap.GetValueOrDefault(playerId, false);
		}

		public int GetPlayerTeam(int networkPlayerId)
		{
			return PeerTeamMap.GetValueOrDefault(networkPlayerId, networkPlayerId);
		}

		public bool IsPlayerObserver(int networkPlayerId)
		{
			return PeerObserverMap.GetValueOrDefault(networkPlayerId, false);
		}

		public string GetPlayerRace(int networkPlayerId)
		{
			return PeerRaceMap.GetValueOrDefault(networkPlayerId, "");
		}

		public bool IsSessionLocked()
		{
			return _sessionLocked;
		}
	}
}
