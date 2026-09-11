// res://Scripts/World/Game.cs
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using RTS.Core;
using RTS.Data;
using RTS.Simulation;
using RTS.Network;
using RTS.Units;
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.World
{
	public partial class Game : Node
	{
		// 压力测试专用：空白地图上跳过起始基地/资源，只保留测试自己生成的单位
		public static bool SkipInitialSpawns = false;

		[Export] public NodePath PlayersContainerPath = "../Players";
		[Export] public NodePath SpawnPointsPath = "../SpawnPoints";

		private static readonly Dictionary<int, Player> _playerMap = new();

		public static Player GetPlayerByTeam(int teamId)
		{
			if (_playerMap.TryGetValue(teamId, out var player))
				return player;

			return null;
		}

		public static IEnumerable<Player> GetAllPlayers()
		{
			return _playerMap.Values.OrderBy(p => p.TeamId);
		}

		public override void _Ready()
		{
			// P0-2：真实对局开局同样支持回放录制/校验（RECORD_REPLAY / REPLAY_FILE）
			// 压测场景已自行挂载工具节点，这里只在主场景缺失时补挂，避免重复。
			AttachReplayToolingIfRequested();
			// AUTO_REMATCH=1：45 秒后模拟“打完回大厅再开一局”（第二局回归测试）
			if (OS.GetEnvironment("AUTO_REMATCH") == "1")
			{
				GetTree().CreateTimer(45.0).Timeout += () =>
				{
					GD.Print("[AutoLobby] 模拟回大厅，重开一局…");
					NetworkManager.Instance?.UnlockSessionForLobby();
					GetTree().ChangeSceneToFile("res://Scenes/Menu/UI_MainMenu.tscn");
				};
			}
			// 使用 CallDeferred 确保 Main / NetworkManager / LockstepManager / EntitySpawner 等单例都已就绪。
			CallDeferred(nameof(SetupMatch));
		}

		private void AttachReplayToolingIfRequested()
		{
			if (GetTree().Root.FindChild("ReplayAutoRecord", true, false) != null)
				return;

			if (!string.IsNullOrEmpty(OS.GetEnvironment("RECORD_REPLAY")))
			{
				AddChild(new RTS.Tools.ReplayAutoRecord());
				return;
			}

			if (!string.IsNullOrEmpty(OS.GetEnvironment("REPLAY_FILE")) &&
				GetTree().Root.FindChild("ReplayAutoVerify", true, false) == null)
			{
				AddChild(new RTS.Tools.ReplayAutoVerify());
			}
		}

		private void SetupMatch()
		{
			GD.Print("\n[Game] ======== 开始生成战场 ========");

			// AI_TEST_MAP=<MapId>：把这一局固定到某张地图上。
			// 必须在 LoadActiveMap 之前应用——地图一旦载入地形/出生点就定死了，
			// 之后再改 SelectedMapId 只会出现"日志说用了新图、实际还是旧图"的假象。
			string mapOverride = OS.GetEnvironment("AI_TEST_MAP");
			if (!string.IsNullOrWhiteSpace(mapOverride) && NetworkManager.Instance != null)
			{
				NetworkManager.Instance.SelectedMapId = mapOverride.Trim();
				GD.Print($"[Game] 地图被 AI_TEST_MAP 覆盖为 '{NetworkManager.Instance.SelectedMapId}'");
			}

			// TUTORIAL=<id>：无头/回归用，直接进指定教程（ttutorial_union 等）。
			// 没有这个开关，教程只能靠人手点主菜单按钮才能验证。
			string tutorialOverride = OS.GetEnvironment("TUTORIAL");
			if (!string.IsNullOrWhiteSpace(tutorialOverride))
			{
				var tdef = RTS.Tutorial.TutorialRegistry.Get(tutorialOverride.Trim());
				if (tdef != null)
				{
					SimManager.PendingTutorial = tdef;
					GD.Print($"[Game] TUTORIAL 指定教程：{tdef.DisplayName}");
				}
				else
				{
					GD.PrintErr($"[Game] 找不到教程 '{tutorialOverride}'（可用：" +
						string.Join(", ", System.Linq.Enumerable.Select(RTS.Tutorial.TutorialRegistry.All, x => x.Id)) + "）");
				}
			}

			// 加载配置表（单位/建筑/武器/种族）
			RTS.Data.Configs.ConfigDatabase.LoadAll();

			// 导出包完整性校验：配置表缺失时立即报错停止，避免静默回退 Union 造成脱步
			if (RTS.Data.Configs.ConfigDatabase.GetRace("Union") == null)
			{
				string exePath = OS.GetExecutablePath();
				string exeDir = System.IO.Path.GetDirectoryName(exePath) ?? "";
				string pckPath = System.IO.Path.Combine(exeDir, "RTSAC.pck");
				bool pckBeside = System.IO.File.Exists(pckPath);
				long pckSize = pckBeside ? new System.IO.FileInfo(pckPath).Length : 0;
				bool dirOk = Godot.DirAccess.Open("res://Data/Configs") != null;

				string dirSample = "";
				var dir = Godot.DirAccess.Open("res://Data/Configs");
				if (dir != null)
				{
					dir.ListDirBegin();
					string name;
					var names = new System.Collections.Generic.List<string>();
					while ((name = dir.GetNext()) != "" && names.Count < 10)
					{
						if (name != "." && name != "..")
							names.Add(name);
					}
					dirSample = string.Join(",", names);
				}

				GD.PrintErr(
					$"[Game] 致命错误：配置表未加载（0 种族）。" +
					$"res://Data/Configs 可打开={dirOk}, exe={exePath}, " +
					$"exe旁RTSAC.pck存在={pckBeside}({pckSize}字节), 目录内容=[{dirSample}]。" +
					"请确认 RTSAC.pck 与 RTSAC.exe 在同一目录且约 22MB，并重新导出勾选“导出所有文件”。已停止开局。");
				return;
			}

			// 纳米地毯技能数值从配置表注入纯逻辑世界（跨端一致，必须先于任何 Tick）
			var nanoRace = RTS.Data.Configs.ConfigDatabase.GetRace("Nano");
			if (nanoRace != null && SimManager.Instance?.World != null)
			{
				SimManager.Instance.World.ApplyNanoConfig(
					(FP)nanoRace.CarpetSpreadCost,
					(FP)nanoRace.CarpetSpreadCooldownSeconds,
					(FP)nanoRace.CarpetSpreadRadiusTiles,
					nanoRace.CarpetSpreadMaxCharges,
					(FP)nanoRace.CarpetSpreadDurationSeconds,
					nanoRace.CarpetIncomeCellsPerResource
				);

			}

			_playerMap.Clear();

			Node playersNode = GetOrCreatePlayersNode();

			ClearChildren(playersNode);

			// 先载入地图：出生点/中立物/触发器都依赖它，必须在生成任何实体之前
			LoadActiveMap();

			// 地图中立建筑（圣地/核心/防御塔）：固定顺序生成，保证 ID 分配跨端一致
			SpawnMapNeutrals();

			NetworkManager network = NetworkManager.Instance;
			LockstepManager lockstep = LockstepManager.Instance;

			// =====================================================
			// 1. 读取本局参与锁步的 NetworkPlayerID
			// =====================================================

			// AI 演示钩子：autoload 的 _Ready 可能晚于场景 _EnterTree 执行，
			// NetworkManager 初始化会重置 PlayerIDs —— 演示配置必须在此（一切就绪后）统一应用。
			if (RTS.Tools.AIShowcase1v1.Enabled)
				RTS.Tools.AIShowcase1v1.ApplyMatchConfig();

			// 必须在本钩子之后读取玩家列表（ApplyMatchConfig 会重建 PlayerIDs）
			List<int> playersToSpawn = lockstep != null
				? new List<int>(lockstep.PlayerIDs)
				: new List<int> { 1 };
			GD.Print($"[Game] 本局玩家列表: {string.Join(",", playersToSpawn)} (applyConfig={(RTS.Tools.AIShowcase1v1.Enabled ? "on" : "off")})");

			playersToSpawn.Sort();

			// =====================================================
			// 2. 读取本机身份
			// =====================================================
			//
			// localNetworkPlayerId：
			//	锁步网络玩家编号，不可变成出生点。
			//
			// localTeamId：
			//	出生点 / 阵营 / 颜色 / 视野归属。
			//
			// 重要：
			//	本文件绝对不能执行：
			//	LockstepManager.Instance.LocalPlayerID = localTeamId;
			// =====================================================

			int localNetworkPlayerId = lockstep != null ? lockstep.LocalPlayerID : 1;
			int localTeamId = network != null ? network.GetPlayerTeam(localNetworkPlayerId) : localNetworkPlayerId;

			if (Main.Instance != null)
			{
				Main.Instance.LocalNetworkPlayerID = localNetworkPlayerId;
				Main.Instance.LocalTeamID = localTeamId;
			}

			if (network != null)
			{
				network.FinalizeLocalIdentity();
			}
			// P1-5：队伍分组（2v2 盟友）与旁观者
			bool localObserver = network?.LocalIsObserver == true;
			if (localObserver)
			{
				localTeamId = 0;
				if (Main.Instance != null)
					Main.Instance.LocalTeamID = 0;
			}
			SimManager.Instance?.ConfigureTeamGroups();

			GD.Print($"[Game] 本机身份：NetworkPlayerID={localNetworkPlayerId}, TeamID={localTeamId}");

			// 盟友共享视野：FogOfWar 视角从单队伍扩展为同组队伍集合
			var fog = RTS.World.FogOfWar.Instance;
			if (fog != null)
			{
				fog.CurrentViewerTeam = localTeamId;
				fog.CurrentViewerTeams.Clear();
				if (!localObserver && SimManager.Instance != null)
				{
					int myGroup = SimManager.Instance.GetTeamGroup(localTeamId);
					foreach (var kv in network.PeerTeamMap)
					{
						if (network.IsPlayerObserver(kv.Key))
							continue;
						if (network.PeerGroupMap.GetValueOrDefault(kv.Key, kv.Value) == myGroup)
							fog.CurrentViewerTeams.Add(kv.Value);
					}
				}
				fog.RevealAll = localObserver; // 旁观者默认全图
			}

			// =====================================================
			// 3. 为每个 NetworkPlayer 生成对应 Player 节点和初始实体
			// =====================================================

			foreach (int networkPlayerId in playersToSpawn)
			{
				if (network?.IsPlayerObserver(networkPlayerId) == true)
					continue; // 旁观者不生成玩家实体

				int assignedTeamId = network != null ? network.GetPlayerTeam(networkPlayerId) : networkPlayerId;
				string raceName = ResolveRaceNameForNetworkPlayer(networkPlayerId, network);
				Vector2 spawnPos = ResolveSpawnPosition(assignedTeamId);

				if (raceName != "Union" && RTS.Data.Configs.ConfigDatabase.GetRace(raceName) == null)
				{
					GD.PrintErr($"[Game] 致命错误：找不到种族 {raceName} 的配置（导出包缺少 Data/Configs）。已停止开局，请重新导出并勾选“导出所有文件”。");
					return;
				}

				Player newPlayer = CreatePlayerNode(networkPlayerId, assignedTeamId, spawnPos, raceName, out PlayerController controller, out RaceData raceNode);

				playersNode.AddChild(newPlayer);
				_playerMap[assignedTeamId] = newPlayer;

				if (assignedTeamId == localTeamId)
				{
					BindLocalControl(controller, assignedTeamId, networkPlayerId, spawnPos);
				}

				if (raceNode != null)
				{
					SpawnPlayerInitialContent(newPlayer, raceNode);

					// 调试地图：一次性摆出该种族全部建筑和单位
					if (IsDebugMap())
					{
						SpawnDebugRoster(newPlayer, raceNode);
						UnlockAllDebugTechs(newPlayer, raceName);
					}
				}
				else
				{
					GD.PrintErr($"[Game] 玩家 {networkPlayerId} 的 RaceData 为空，跳过初始实体生成。RaceName={raceName}");
				}
			}

			if (IsDebugMap())
				SpawnDebugTargets();

			// =====================================================
			// 4. 设置本地战争迷雾观察阵营
			// =====================================================

			if (FogOfWar.Instance != null)
			{
				FogOfWar.Instance.CurrentViewerTeam = localTeamId;
				GD.Print($"[Game] 战争迷雾已锁定观测阵营: Team {localTeamId}");
			}

			GD.Print("[Game] ======== 战场生成完毕 ========\n");

			// =====================================================
			// 5. 启动锁步模拟
			// =====================================================
			//
			// Main.StartSimulation() 内部只会把 NetworkPlayerID 写入 Lockstep。
			// 不允许用 TeamID 覆盖 Lockstep.LocalPlayerID。
			// =====================================================

			if (Main.Instance != null)
			{
				Main.Instance.StartSimulation();
			}
			else
			{
				GD.PrintErr("[Game] Main.Instance 为空，无法启动模拟。");
			}
		}

		// =========================================================
		// Player 生成
		// =========================================================

		private Node GetOrCreatePlayersNode()
		{
			Node playersNode = GetNodeOrNull(PlayersContainerPath);

			if (playersNode != null)
				return playersNode;

			Node2D created = new Node2D
			{
				Name = "Players"
			};

			AddChild(created);

			GD.Print("[Game] 未找到 Players 节点，已动态创建。");

			return created;
		}

		private void ClearChildren(Node node)
		{
			foreach (Node child in node.GetChildren())
			{
				child.QueueFree();
			}
		}

		private string ResolveRaceNameForNetworkPlayer(int networkPlayerId, NetworkManager network)
		{
			string raceName = network != null
				? network.PeerRaceMap.GetValueOrDefault(networkPlayerId, "Union")
				: "Union";

			if (string.IsNullOrEmpty(raceName))
				raceName = "Union";

			if (raceName == "未选择")
				raceName = "Union";

			if (raceName == "LOBBY_UPDATE")
				raceName = "Union";

			return raceName;
		}

		/// <summary>本局使用的运行时地图（出生点/中立物/触发器都从这里来）。</summary>
		public static MapRuntime ActiveMap { get; private set; }

		/// <summary>
		/// 载入本局地图：
		///   1. 从注册表取 .tres 地图（res://Maps/），没有则回退到"场景内置地图"；
		///   2. 把地形写进 SimGrid（与编辑器校验用的是同一段代码）；
		///   3. 让"建造禁区"真正生效；
		///   4. 构建运行时地图并交给 SimManager（触发器从此开始每 tick 求值）。
		///
		/// 必须在任何实体生成之前调用，否则出生点/中立物拿不到数据。
		/// </summary>
		private void LoadActiveMap()
		{
			string mapId = RTS.Network.NetworkManager.Instance?.SelectedMapId ?? "Classic";

			// 教程模式：把地图固定成教程自带场地，并在生成实体**之前**启动教程运行时
			// （教程的额外实体来自 StartTutorial 填好的 PendingTutorialSpawns）。
			var pendingTutorial = SimManager.PendingTutorial;
			if (pendingTutorial != null)
			{
				mapId = RTS.Tutorial.TutorialRegistry.TutorialMapId;
				SimManager.Instance.StartTutorial(pendingTutorial);
				AttachTutorialHud();
				GD.Print($"[Game] 教程模式：{pendingTutorial.DisplayName}");
			}
			var data = RTS.World.MapRegistry.Resolve(mapId);

			if (data == null)
			{
				// 老路径：地图是场景里的 TileMapLayer，没有 .tres。
				// 只导出出生点，地形交给 MapGrid.InitSimGrid 从 TileMap 反推。
				var spawnContainer = GetNodeOrNull(SpawnPointsPath);
				data = RTS.World.MapRegistry.ExportSpawnPointsOnly(spawnContainer, mapId);
				GD.Print($"[Game] 地图 '{mapId}' 没有 .tres 数据，使用场景内置地图（导出出生点 {data.SpawnPoints.Count} 个）。");
			}
			else
			{
				// 有地图数据：地形以数据为准。
				// **必须由 Game 显式调用** MapGrid.ApplyDataMap，而不是指望 MapGrid.InitSimGrid：
				// Godot 的 _ready 是子节点先于父节点，MapGrid._Ready（含它的 CallDeferred(InitSimGrid)）
				// 早于 Game._Ready（含 CallDeferred(SetupMatch)）执行 ——
				// 等这里拿到数据时 InitSimGrid 早就跑完并从场景 TileMap 反推过了。
				// 那正是"逻辑换了新地图、屏幕还是旧地图"的根因。
				var mapGrid = RTS.World.MapGrid.Instance;
				if (mapGrid != null && data.HasTerrain)
				{
					mapGrid.ApplyDataMap(data);
					GD.Print($"[Game] 地图 '{mapId}' 地形已从数据载入（{data.Width}x{data.Height}）。");
				}
				else
				{
					// 没有 MapGrid（压测/无地形场景）：至少保证逻辑层正确
					var simGrid = SimManager.Instance?.World?.Grid;
					if (simGrid != null && data.HasTerrain)
						RTS.World.MapLoader.ApplyToSimGrid(data, simGrid);
				}
			}

			// 建造禁区生效（表现层 + 模拟层两条路径都要设）
			RTS.World.MapGrid.SetActiveMap(data);

			ActiveMap = MapRuntime.FromData(data);
			SimManager.Instance?.LoadRuntimeMap(ActiveMap);
		}

		private Vector2 ResolveSpawnPosition(int teamId)
		{
			// 1) 地图数据优先（编辑器产出的地图走这条）
			var spawn = ActiveMap?.GetSpawn(teamId);
			if (spawn != null)
			{
				// 地图数据格坐标 == 世界格坐标（渲染层不做偏移，见 MapGrid.TryRenderActiveMapToTileMap），
				// 因此这里直接换算即可，不需要任何补偿。
				float tile = RTS.Data.Maps.RtsMapData.TileSize;
				return new Vector2(spawn.GridX * tile + tile * 0.5f, spawn.GridY * tile + tile * 0.5f);
			}

			// 2) 回退：场景里的 Spawn_N 标记
			Node spawnContainer = GetNodeOrNull(SpawnPointsPath);

			if (spawnContainer == null)
			{
				GD.PrintErr($"[Game] 未找到 SpawnPoints 容器: {SpawnPointsPath}，Team {teamId} 使用 Vector2.Zero。");
				return Vector2.Zero;
			}

			Node2D marker = spawnContainer.GetNodeOrNull<Node2D>($"Spawn_{teamId}");

			if (marker == null)
			{
				GD.PrintErr($"[Game] 未找到出生点 Spawn_{teamId}，使用 Vector2.Zero。");
				return Vector2.Zero;
			}

			return marker.GlobalPosition;
		}

		private Player CreatePlayerNode(
			int networkPlayerId,
			int teamId,
			Vector2 spawnPos,
			string raceName,
			out PlayerController controller,
			out RaceData raceNode
		)
		{
			Player player = new Player
			{
				Name = $"Player_{networkPlayerId}",
				TeamId = teamId,
				GlobalPosition = spawnPos
			};

			controller = new PlayerController
			{
				Name = "PlayerController"
			};

			PlayerData data = new PlayerData
			{
				Name = "PlayerData"
			};

			player.AddChild(controller);
			player.AddChild(data);

			raceNode = CreateRaceNode(raceName);

			if (raceNode != null)
			{
				raceNode.Name = "Race";
				player.AddChild(raceNode);
			}
			else
			{
				GD.PrintErr($"[Game] 无法创建种族 {raceName}，玩家 {networkPlayerId} 将缺少 Race 节点。");
			}

			GD.Print($"[Game] 生成 Player_{networkPlayerId}: Team={teamId}, Race={raceName}, Spawn={spawnPos}");

			return player;
		}

		private RaceData CreateRaceNode(string raceName)
		{
			if (string.IsNullOrEmpty(raceName))
				raceName = "Union";

			if (raceName == "Union")
				return new RTS.Data.Union();

			// 配置表驱动的种族：无专用 RaceData 类时用占位（内容由 RaceConfig 提供）
			if (RTS.Data.Configs.ConfigDatabase.GetRace(raceName) != null)
				return new RTS.Data.ConfiguredRace(raceName);

			Type raceType =
				Type.GetType($"RTS.Data.{raceName}") ??
				System.Reflection.Assembly.GetExecutingAssembly().GetType($"RTS.Data.{raceName}");

			if (raceType == null)
			{
				GD.PrintErr($"[Game] 找不到种族类型 RTS.Data.{raceName}，回退到 Union。");
				return new RTS.Data.Union();
			}

			object instance = Activator.CreateInstance(raceType);

			if (instance is RaceData race)
				return race;

			GD.PrintErr($"[Game] 类型 RTS.Data.{raceName} 不是 RaceData，回退到 Union。");

			return new RTS.Data.Union();
		}

		private void BindLocalControl(PlayerController controller, int teamId, int networkPlayerId, Vector2 spawnPos)
		{
			UserController user = GetNodeOrNull<UserController>("../User/UserController");

			if (user != null)
			{
				user.TargetController = controller;
			}
			else
			{
				GD.PrintErr("[Game] 未找到 ../User/UserController，无法绑定本地控制器。");
			}

			Camera3D cam = GetViewport().GetCamera3D();

			if (cam is RTSCamera rtsCam)
			{
				// 进入游戏后自动切换到斜上帝视角并聚焦出生点
				rtsCam.SetViewCenter(spawnPos);
			}
			else if (cam != null)
			{
				cam.GlobalPosition = new Vector3(spawnPos.X, cam.GlobalPosition.Y, spawnPos.Y);
			}

			GD.Print($"[Game] 本地控制权已绑定至 Team {teamId} / NetworkPlayer {networkPlayerId}");
		}

		private void SpawnMapNeutrals()
		{
			if (EntitySpawner.Instance == null)
				return;

			// 教程额外实体：与地图实体一起、在同一段确定性顺序里生成。
			// 蓝图（多足）生成后立刻完工，触发 Structure.cs 的
			// "Blueprint_<X> → 单位 <X>" 转换，玩家开局就有可用单位。
			SpawnTutorialExtraEntities();

			// 来自地图数据的中立物/资源（编辑器产出）优先。
			// 这条路径让作者能自由摆放中立塔/圣地/资源，不必改代码。
			if (ActiveMap != null && ActiveMap.Entities.Count > 0)
			{
				SpawnNeutralsFromMap();
				return;
			}

			// 调试地图：跳过中立建筑，只摆玩家全单位/建筑
			if (MapGrid.Instance != null && MapGrid.Instance.MapPresetId == 2)
			{
				GD.Print("[Game] 调试地图：跳过中立建筑。");
				return;
			}

			SpawnLegacyHardcodedNeutrals();
		}

		/// <summary>
		/// 挂上教程 HUD（只有教程局才有这个节点）。
		/// 用代码创建而不是挂进 main.tscn：普通对局不该多出一个空面板节点。
		/// </summary>
		private void AttachTutorialHud()
		{
			if (GetTree().Root.FindChild("TutorialHud", true, false) != null)
				return; // 已挂过（重开一局时可能残留）

			GetTree().Root.AddChild(new RTS.Tutorial.TutorialHud());
		}

		/// <summary>
		/// 生成教程额外实体（教学用初始部队、教学用敌方目标等）。
		///
		/// 多足的初始内容全是 `Blueprint_*`：生成后**立刻完工**，
		/// 让 Structure.cs 走它的正常收尾路径（`PendingSpawnUnitIds.Add(...)`），
		/// 下一个 tick 就会在蓝图位置生成真正的单位。
		/// 这样教程不需要另造一套"直接刷单位"的旁路，走的就是玩家自己拍蓝图的那条路。
		/// </summary>
		private void SpawnTutorialExtraEntities()
		{
			var spawns = SimManager.Instance?.ConsumeTutorialSpawns();
			if (spawns == null || spawns.Count == 0) return;

			int count = 0;
			foreach (var s in spawns)
			{
				if (s == null || string.IsNullOrEmpty(s.EntityId)) continue;

				for (int i = 0; i < Math.Max(1, s.Count); i++)
				{
					var entity = EntitySpawner.Instance.SpawnEntity(
						s.EntityId,
						s.TeamId,
						new FPVector2(
							(FP)(s.GridX * 64 + 32),
							(FP)(s.GridY * 64 + 32)));

					// 蓝图：立即完工（教程要"开局即有兵"，不能等玩家自己施工）。
					// 走 PromoteFromBlueprint → AdvanceProgress(1f)，
					// 这正是正常施工完成的同一条路径（AdvanceProgress 里会把
					// Blueprint_<X> 转成 <X> 并挂起生成），不另造旁路。
					if (entity is RTS.Units.Structure st && st.StructureName.StartsWith("Blueprint_"))
					{
						st.PromoteFromBlueprint();
						st.AdvanceProgress(1f);
					}

					count++;
				}
			}

			GD.Print($"[Game] 教程初始实体：{count} 个");
		}

		/// <summary>
		/// 按地图数据生成中立物与资源点。
		/// 顺序严格按作者列表顺序（MapRuntime 保序），保证两端实体 ID 分配一致。
		/// </summary>
		private void SpawnNeutralsFromMap()
		{
			int spawned = 0;

			foreach (var placement in ActiveMap.Entities)
			{
				if (placement == null || string.IsNullOrEmpty(placement.EntityId)) continue;

				int teamId = placement.ResolveTeamId();
				int count = Math.Max(1, placement.Count);

				for (int i = 0; i < count; i++)
				{
					// count > 1 时按固定偏移排开，避免完全重叠（确定性，不随机）
					int gx = placement.GridX + (i % 2);
					int gy = placement.GridY + (i / 2);

					Vector2 world = new Vector2(
						gx * RTS.Data.Maps.RtsMapData.TileSize + RTS.Data.Maps.RtsMapData.TileSize * 0.5f,
						gy * RTS.Data.Maps.RtsMapData.TileSize + RTS.Data.Maps.RtsMapData.TileSize * 0.5f);

					EntitySpawner.Instance.SpawnEntity(
						placement.EntityId,
						teamId,
						new FPVector2((FP)world.X, (FP)world.Y));
					spawned++;

					// 资源点周围散布（沿用旧版中立塔"2 铁 1 气"的手感）
					if (placement.ScatterResources)
						SpawnScatterAround(world.X, world.Y, placement.ScatterSpec);
				}
			}

			GD.Print($"[Game] 地图 '{ActiveMap.MapId}' 已生成 {spawned} 个中立实体。");
		}

		/// <summary>
		/// 在中立物周围散布资源。spec 形如 "IronOre:2,GasSpring:1"，
		/// 留空用默认的 2 铁 + 1 气（与旧版中立塔一致）。
		/// </summary>
		private void SpawnScatterAround(float x, float y, string spec)
		{
			if (string.IsNullOrWhiteSpace(spec))
			{
				SpawnTowerResources(x, y);
				return;
			}

			foreach (string part in spec.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
			{
				string[] kv = part.Split(':');
				if (kv.Length != 2) continue;
				if (!int.TryParse(kv[1].Trim(), out int n) || n <= 0) continue;

				string entityId = kv[0].Trim();
				for (int i = 0; i < n; i++)
					SpawnRandomResourceNear(entityId, x, y, SimManager.Instance.World.RNG);
			}
		}

		/// <summary>
		/// 旧版硬编码中立物布局（Classic / 1v1 场景内置地图用）。
		///
		/// ---- 坐标系修正（重要）----
		///
		/// 这套坐标是**旧版世界坐标系**（以原点为中心，X/Z 都在 ±5000 左右）。
		/// 而地形现在由地图数据生成，格 (0,0) 对应世界 (32,32)、
		/// 整张 Classic 图占世界 x∈[32,12800] z∈[32,8320]。
		///
		/// 两者坐标系不同 → 塔/矿被放到**地图之外**（实测格坐标 -49 / -53 这种），
		/// 表现就是"小地图上矿的位置和地形对不上，而且空出一大片黑的"。
		///
		/// 修法：把旧布局**等比缩放并平移**到当前地形范围内
		/// （见 MapLegacyLayoutInto），而不是继续把旧坐标原样用。
		/// </summary>
		private void SpawnLegacyHardcodedNeutrals()
		{
			(string Name, float X, float Y)[] decor;

			// 1v1 小图：1 个中央圣地 + 4 座中立塔（无纳米核心）
			if (MapGrid.Instance?.MapPresetId == 1)
			{
				decor = new[]
				{
					("Shrine", 0f, -928f),
					("Shrine", 0f, 928f),
					("Tower", -1056f, -2336f),
					("Tower", 1056f, -2336f),
					("Tower", -1056f, 2336f),
					("Tower", 1056f, 2336f)
				};
			}
			else
			{
				decor = new[]
				{
					("Shrine", -4268f, 852f),
					("Shrine", 4268f, 852f),
					("Shrine", 0f, -346f),
					("Shrine", 0f, 1711f),
					("NanoCore", -5116f, 256f),
					("Tower", -3425f, -849f),
					("Tower", -3646f, 3159f),
					("Tower", -1366f, -2729f),
					("Tower", -1671f, 3732f),
					("Tower", 3425f, -849f),
					("Tower", 3646f, 3159f),
					("Tower", 1366f, -2729f),
					("Tower", 1671f, 3732f),
					("Tower", 0f, 1997f),
					("Tower", 0f, -505f)
				};
			}

			foreach (var item in decor)
			{
				// 旧世界坐标 → 当前地形坐标（详见方法注释）
				var mapped = MapLegacyLayoutInto(item.X, item.Y);

				// 中立防御塔与纳米核心可被攻击：TeamID=-2；圣地等中立物保持 -1
				int teamId = item.Name == "Tower" || item.Name == "NanoCore" ? -2 : -1;
				EntitySpawner.Instance.SpawnEntity(
					item.Name,
					teamId,
					new FPVector2((FP)mapped.X, (FP)mapped.Y)
				);

				// 每个中立塔周围随机刷新 2 金 1 气（确定性随机，双端一致）
				if (item.Name == "Tower")
					SpawnTowerResources(mapped.X, mapped.Y);
			}
		}

		/// <summary>
		/// 旧版世界坐标 → 当前地形世界坐标（**纯平移对齐，不做缩放**）。
		///
		/// ---- 为什么是纯平移 ----
		///
		/// 由 Maps/Classic.rtsmap 反推（Width=200,Height=130,ExportOffset=-100,-65）：
		///   旧地形世界范围 = (格 + ExportOffset) * 64
		///                  = X∈[-6400,6400] Y∈[-4160,4160]
		///                  → 尺寸 12800 × 8320
		/// 而硬编码中立物的范围是 X∈[-5116,4268] Y∈[-2729,3732]
		///                  → 尺寸 10240 × 8320
		///
		/// **两者尺寸同为 8320 高**，说明旧坐标系与旧地形是**同一尺度**，
		/// 只是整体被平移过。所以正确做法是纯平移对齐，**绝不能等比缩放**——
		/// 缩放会把本来正确的相对距离改掉（表现就是"中立单位不在该在的位置、比例错误"）。
		///
		/// 上一版我按"地形半径 / 硬编码 5200"估算了一个缩放比例，那是错的。
		///
		/// 对齐方式：把旧地形的中心对齐到当前地形的中心。
		/// 旧地形中心由 ExportOffset 与地图尺寸算出，因此这是**精确值**，不依赖估算。
		/// </summary>
		private Vector2 MapLegacyLayoutInto(float x, float y)
		{
			const float T = RTS.Data.Maps.RtsMapData.TileSize;

			var sim = SimManager.Instance?.World?.Grid;
			if (sim == null || sim.TerrainCells.Count == 0)
				return new Vector2(x, y);   // 拿不到地形就不做半吊子换算

			// 1) 当前地形中心（世界坐标）
			int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
			foreach (var c in sim.TerrainCells)
			{
				if (c.X < minX) minX = c.X;
				if (c.Y < minY) minY = c.Y;
				if (c.X > maxX) maxX = c.X;
				if (c.Y > maxY) maxY = c.Y;
			}
			float curCenterX = (minX + (maxX - minX + 1) * 0.5f) * T;
			float curCenterY = (minY + (maxY - minY + 1) * 0.5f) * T;

			// 2) 旧地形中心（由地图尺寸与导出偏移精确算出）
			var map = ActiveMap;
			float oldCenterX = (map.Width * 0.5f + map.ExportOffsetX) * T;
			float oldCenterY = (map.Height * 0.5f + map.ExportOffsetY) * T;

			// 3) 纯平移
			return new Vector2(x - oldCenterX + curCenterX, y - oldCenterY + curCenterY);
		}

		// 中立塔周围资源：随机角度 + 2~6 格距离，2 铁矿 + 1 气泉
		// 用世界 RNG（锁步确定性），落点必须在地形上且不压建筑/墙
		private void SpawnTowerResources(float towerX, float towerY)
		{
			if (EntitySpawner.Instance == null || SimManager.Instance?.World == null)
				return;

			SimRandom rng = SimManager.Instance.World.RNG;

			SpawnRandomResourceNear("IronOre", towerX, towerY, rng);
			SpawnRandomResourceNear("IronOre", towerX, towerY, rng);
			SpawnRandomResourceNear("GasSpring", towerX, towerY, rng);
		}

		private void SpawnRandomResourceNear(string name, float towerX, float towerY, SimRandom rng)
		{
			var world = SimManager.Instance.World;

			for (int attempt = 0; attempt < 32; attempt++)
			{
				FP angle = rng.NextFP() * FP.Pi * (FP)2m;
				FP dist = (FP)(2 + (int)(rng.Next() % 5)) * (FP)64m;

				float wx = towerX + (float)(FP.Cos(angle) * dist);
				float wy = towerY + (float)(FP.Sin(angle) * dist);

				Vector2I? grid = MapGrid.Instance?.WorldToGrid(new Vector2(wx, wy));
				if (grid == null)
					continue;

				var cell = new SimVector2I(grid.Value.X, grid.Value.Y);
				if (!world.Grid.TerrainCells.Contains(cell) || world.Grid.StaticObstacles.Contains(cell))
					continue;
				if (MapGrid.Instance != null && !MapGrid.Instance.IsAreaEmpty(grid.Value, 1))
					continue;

				EntitySpawner.Instance.SpawnEntity(name, -1, new FPVector2((FP)wx, (FP)wy));
				return;
			}

			// 兜底：固定偏移，保证资源必然生成
			EntitySpawner.Instance.SpawnEntity(
				name,
				-1,
				new FPVector2((FP)(towerX + 300f), (FP)(towerY + 150f))
			);
		}

		// =========================================================
		// 初始实体生成
		// =========================================================

		private void SpawnPlayerInitialContent(Player player, RaceData race)
		{
			if (player == null || race == null)
				return;

			if (SkipInitialSpawns)
				return;

			// 配置表优先：按 RaceConfig.StartingEntities 生成
			var raceCfg = RTS.Data.Configs.ConfigDatabase.GetRace(race.RaceName);

			if (raceCfg != null && raceCfg.StartingEntities.Count > 0)
			{
				SpawnPlayerFromConfig(player, raceCfg);
				return;
			}

			Vector2 basePos = player.GlobalPosition;

			if (EntitySpawner.Instance == null)
			{
				GD.PrintErr("[Game] EntitySpawner.Instance 为空，无法生成单位。");
				return;
			}

			int count = 0;

			foreach (var item in race.GetStartingStructures())
			{
				var ent = EntitySpawner.Instance.SpawnEntity(
					item.EntityName,
					player.TeamId,
					new FPVector2(
						(FP)basePos.X + (FP)item.Offset.X,
						(FP)basePos.Y + (FP)item.Offset.Y
					)
				);

				if (ent != null)
					count++;
			}

			foreach (var item in race.GetStartingUnits())
			{
				var ent = EntitySpawner.Instance.SpawnEntity(
					item.EntityName,
					player.TeamId,
					new FPVector2(
						(FP)basePos.X + (FP)item.Offset.X,
						(FP)basePos.Y + (FP)item.Offset.Y
					)
				);

				if (ent != null)
					count++;
			}

			foreach (var item in race.GetStartingResources())
			{
				var ent = EntitySpawner.Instance.SpawnEntity(
					item.EntityName,
					-1,
					new FPVector2(
						(FP)basePos.X + (FP)item.Offset.X,
						(FP)basePos.Y + (FP)item.Offset.Y
					)
				);

				if (ent != null)
					count++;
			}

			GD.Print($"[Game] {player.Name} 初始实体生成完成，共 {count} 个。");
		}

		private bool IsDebugMap()
		{
			return NetworkManager.Instance != null && NetworkManager.Instance.SelectedMapId == "Debug";
		}

		// 调试地图：把该种族 Available 列表里的全部建筑和单位一次性摆出来。
		private void SpawnDebugRoster(Player player, RaceData race)
		{
			if (player == null || race == null || EntitySpawner.Instance == null)
				return;

			var raceCfg = RTS.Data.Configs.ConfigDatabase.GetRace(race.RaceName);
			if (raceCfg == null)
				return;

			Vector2 basePos = player.GlobalPosition;
			int count = 0;

			// 单位一行：y = 出生点 + 800，横向排列
			const int unitStep = 160;
			int unitIndex = 0;
			foreach (string id in raceCfg.AvailableUnitIds)
			{
				if (string.IsNullOrEmpty(id))
					continue;

				float x = basePos.X + unitIndex * unitStep;
				float y = basePos.Y + 800;
				var ent = EntitySpawner.Instance.SpawnEntity(id, player.TeamId, new FPVector2((FP)x, (FP)y));
				if (ent != null)
					count++;

				unitIndex++;
			}

			// 建筑一行：y = 出生点 + 1400，横向排列（间距 5 格容纳 4x4 大建筑）
			const int buildingStep = 320;
			int buildingIndex = 0;
			foreach (string id in raceCfg.AvailableStructureIds)
			{
				if (string.IsNullOrEmpty(id))
					continue;

				float x = basePos.X + buildingIndex * buildingStep;
				float y = basePos.Y + 1400;
				var ent = EntitySpawner.Instance.SpawnEntity(id, player.TeamId, new FPVector2((FP)x, (FP)y));
				if (ent != null)
					count++;

				buildingIndex++;
			}

			GD.Print($"[Game] 调试地图：{player.Name} 已一次性摆放全部建筑/单位，共 {count} 个。");
		}

		// 调试地图附加目标：2 座中立塔 + 1 只无武器喷火龙靶子。
		// 调试地图：开局直接解锁该种族全部科技（含毁灭射线等），省去研究步骤。
		private void UnlockAllDebugTechs(Player player, string raceName)
		{
			if (player?.PlayerData == null)
				return;

			var raceCfg = RTS.Data.Configs.ConfigDatabase.GetRace(raceName);
			if (raceCfg == null)
				return;

			foreach (string id in raceCfg.AvailableTechIds)
				player.PlayerData.GrantTech(id);

			RTS.Core.TechEffects.ApplyToPlayer(player);
			GD.Print($"[Game] 调试地图：已解锁 {player.Name} 全部科技（{raceCfg.AvailableTechIds.Count} 项）。");
		}

		// 调试靶子：血量放大（避免测试时一下就死）。
		private static void ScaleDebugTargetHp(IEntity entity, float multiplier)
		{
			if (entity?.LogicEntity == null)
				return;

			var logic = entity.LogicEntity;
			FP newMax = logic.MaxHp * (FP)multiplier;
			logic.MaxHp = newMax;
			logic.Hp = newMax;

			if (entity.LifeModule != null)
				entity.LifeModule.MaxHp = (float)newMax;
		}

		private void SpawnDebugTargets()
		{
			if (EntitySpawner.Instance == null)
				return;

			// 中立塔（Team -2）：放在基地北侧远处，不干扰出生区
			EntitySpawner.Instance.SpawnEntity("Tower", -2, new FPVector2((FP)(-1000), (FP)(-1000)));
			EntitySpawner.Instance.SpawnEntity("Tower", -2, new FPVector2((FP)1000, (FP)(-1000)));

			var enemyLiberator = EntitySpawner.Instance.SpawnEntity("Liberator", 2, new FPVector2((FP)0, (FP)3150));
			if (enemyLiberator is Unit unit && unit.CombatModule != null)
			{
				ScaleDebugTargetHp(enemyLiberator, 200f);
				unit.CombatModule.ClearWeapons();

				var mount = unit.GetNodeOrNull<Node3D>("Visuals/WeaponMount");
				if (mount != null)
				{
					foreach (Node child in new List<Node>(mount.GetChildren()))
					{
						if (child is Weapon)
							child.QueueFree();
					}
				}

				GD.Print("[Game] 调试地图：敌方解放者靶子已生成（无武器，Team 2）。");
			}

			// 无武器塔靶子：放在单位行正下方，保证解放者架设后 14 格射程内就能打到
			var targetTower = EntitySpawner.Instance.SpawnEntity("Tower", 2, new FPVector2((FP)800, (FP)1200));
			if (targetTower is Structure tower && tower.CombatModule != null)
			{
				ScaleDebugTargetHp(targetTower, 200f);
				tower.CombatModule.ClearWeapons();

				var towerMount = tower.GetNodeOrNull<Node3D>("Visuals/WeaponMount");
				if (towerMount != null)
				{
					foreach (Node child in new List<Node>(towerMount.GetChildren()))
					{
						if (child is Weapon)
							child.QueueFree();
					}
				}

				GD.Print("[Game] 调试地图：无武器塔靶子已生成（Team 2）。");
			}
		}

		private void SpawnPlayerFromConfig(Player player, RTS.Data.Configs.RaceConfig raceCfg)
		{
			Vector2 basePos = player.GlobalPosition;

			// 初始资源（整表覆盖）
			var wallet = new Godot.Collections.Dictionary<ResourceType, float>();

			foreach (var rs in raceCfg.StartingResources)
			{
				if (rs == null)
					continue;

				wallet[rs.Type] = wallet.GetValueOrDefault(rs.Type, 0f) + rs.Amount;
			}

			player.PlayerData?.ResetResources(wallet);

			// 纳米虫：开局在出生点铺一块初始地毯（半径 15 格）
			if (raceCfg.RaceId == "Nano")
			{
				int centerX = (int)Mathf.Floor(basePos.X / 64f);
				int centerY = (int)Mathf.Floor(basePos.Y / 64f);

				for (int x = -15; x <= 15; x++)
				{
					for (int y = -15; y <= 15; y++)
					{
						if (x * x + y * y <= 15 * 15)
						{
							RTS.Core.SimManager.Instance?.World?.CreepGrid.AddCreep(
								centerX + x,
								centerY + y,
								RTS.Data.CreepType.NanoCreep,
								player.TeamId
							);
						}
					}
				}
			}

			// 植物：开局在生命树周围铺一块初始菌毯（半径 12 格）
			if (raceCfg.RaceId == "Plant")
			{
				int centerX = (int)Mathf.Floor(basePos.X / 64f);
				int centerY = (int)Mathf.Floor(basePos.Y / 64f);
				for (int x = -12; x <= 12; x++)
				{
					for (int y = -12; y <= 12; y++)
					{
						if (x * x + y * y <= 12 * 12)
						{
							RTS.Core.SimManager.Instance?.World?.CreepGrid.AddCreep(
								centerX + x,
								centerY + y,
								RTS.Data.CreepType.PlantCreep,
								player.TeamId
							);
						}
					}
				}
			}

			int count = 0;

			foreach (var entry in raceCfg.StartingEntities)
			{
				if (entry == null || string.IsNullOrEmpty(entry.EntityId))
					continue;

				int teamId = entry.TeamMode == RTS.Data.Configs.StartingEntityTeamMode.OwnerTeam
					? player.TeamId
					: -1;

				for (int i = 0; i < entry.Count; i++)
				{
					Vector2 offset = entry.Offset + new Vector2(i * entry.Spacing, 0f);
					var ent = EntitySpawner.Instance.SpawnEntity(
						entry.EntityId,
						teamId,
						new FPVector2((FP)(basePos.X + offset.X), (FP)(basePos.Y + offset.Y))
					);

					if (ent != null)
						count++;
				}
			}

			GD.Print($"[Game] {player.Name} 初始实体生成完成（配置表），共 {count} 个。");
		}
	}
}
