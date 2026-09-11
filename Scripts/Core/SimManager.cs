using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RTS.Simulation;
using RTS.Network;
using RTS.Data;
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
	public partial class SimManager : Node, ISimulationRules
	{
		public static SimManager Instance { get; private set; }

		public SimWorld World { get; private set; }

		private double _accumulator = 0.0;
		private int _ticksThisWindow = 0;
		private double _fpsWindowTimer = 0.0;

		// 逻辑帧率（TPS）：最近 1 秒实际执行的锁步 Tick 数，供右下角帧率显示
		public float LogicFps { get; private set; } = 0f;

		// 20Hz 逻辑帧
		private readonly double _fixedDeltaFloat = 0.05;

		// 防止网络卡顿后 accumulator 堆积过多，避免恢复后一次性追帧
		private const int MaxTicksPerFrame = 5;
		// 倍速（演示/回放/离线用）：1/2/4/8。联机模式强制 1x，避免本机超前网络缓冲。
		private int _simulationSpeed = 1;
		public int SimulationSpeed
		{
			get
			{
				if (_simulationSpeed > 1 &&
					RTS.Network.NetworkManager.Instance != null &&
					!RTS.Network.NetworkManager.OfflineMode &&
					!(RTS.Network.LockstepManager.Instance?.ReplayMode ?? false))
					return 1;
				return _simulationSpeed;
			}
			set => _simulationSpeed = System.Math.Clamp(value, 1, 8);
		}

		private volatile bool _isRunning = false;
		// P1-4 对局控制：本地暂停（锁步 tick 边界生效，不影响确定性）
		private volatile bool _paused = false;
		public bool IsPaused => _paused;
		public void SetPaused(bool paused) => _paused = paused;
		// P1-1 对战 AI：BOT_TEAMS=1,2 指定机器人阵营（每 30 tick 自动索敌/攻击移动）
		private readonly System.Collections.Generic.HashSet<int> _botTeams = new();
		// P1-1 AI 难度：1=休闲 2=标准 3=疯狂（影响决策频率/出兵节奏）
		private int _botDifficulty = 1;
		// P1-1：难度改为资源倍率（0.7x / 1.0x / 1.5x），决策频率固定
		private const int BotTickInterval = 30;
		/// <summary>UI 入口覆盖环境变量：逗号分隔的机器人阵营（如 "2,3"），null 时回退 BOT_TEAMS。</summary>
		public static string BotTeamsOverride = null;
		/// <summary>UI 入口覆盖环境变量：1/2/3，0 表示未设置（回退 BOT_DIFFICULTY）。</summary>
		public static int BotDifficultyOverride = 0;
		/// <summary>大厅机器人列表（逐机器人种族/难度/组别，优先于 BotTeamsOverride）。</summary>
		public struct BotLobbyConfig
		{
			public int Pid;          // 机器人的网络玩家号（固定 100+，不随位置变化）
			public int Team;
			public string Race;
			public int Difficulty;
			public int Group;
		}
		public static System.Collections.Generic.List<BotLobbyConfig> BotConfigsOverride = new();
		// 逐队伍难度（大厅可为每个机器人单独调难度）
		private readonly System.Collections.Generic.Dictionary<int, int> _botDifficultyByTeam = new();
		// 队伍分组：同组 = 盟友（2v2 等）；由权威出生清单配置
		private readonly System.Collections.Generic.Dictionary<int, int> _teamGroup = new();

		/// <summary>难度=资源倍率：机器人经济/采集/收入按此系数放大或缩小。</summary>
		public FP GetBotResourceMultiplier(int team)
		{
			if (!_botTeams.Contains(team))
				return FP.One;
			if (_botDifficultyByTeam.TryGetValue(team, out int teamDifficulty))
			{
				return teamDifficulty switch
				{
					1 => (FP)0.7m,
					2 => FP.One,
					_ => (FP)1.5m
				};
			}
			return _botDifficulty switch
			{
				1 => (FP)0.7m,
				2 => FP.One,
				_ => (FP)1.5m
			};
		}

		/// <summary>从静态覆盖/环境变量读取机器人配置（Autoload 早于场景，运行期改配置请用 SetBotTeams）。</summary>
		public void ConfigureBotsFromOverrides()
		{
			string botTeamsCfg = !string.IsNullOrEmpty(BotTeamsOverride)
				? BotTeamsOverride
				: OS.GetEnvironment("BOT_TEAMS") ?? "";
			string botDifficultyCfg = BotDifficultyOverride > 0
				? BotDifficultyOverride.ToString()
				: OS.GetEnvironment("BOT_DIFFICULTY");
			int difficulty = 1;
			if (int.TryParse(botDifficultyCfg, out int parsed))
				difficulty = System.Math.Clamp(parsed, 1, 3);
			SetBotTeams(botTeamsCfg, difficulty);
		}

		/// <summary>运行期配置机器人（演示/测试用）：teamsCsv 逗号分隔阵营号，difficulty 1~3（资源倍率）。</summary>
		public void SetBotTeams(string teamsCsv, int difficulty)
		{
			_botTeams.Clear();
			foreach (string part in (teamsCsv ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries))
			{
				if (int.TryParse(part.Trim(), out int team))
					_botTeams.Add(team);
			}
			_botDifficulty = System.Math.Clamp(difficulty, 1, 3);
			if (_botTeams.Count > 0)
				GD.Print($"[BotAI] 机器人阵营: {string.Join(",", _botTeams)} 难度: {_botDifficulty} (资源倍率)");
		}

		/// <summary>为单个机器人队伍设置难度（大厅逐机器人配置）。</summary>
		public void SetBotDifficulty(int team, int difficulty)
		{
			if (!_botTeams.Contains(team))
				_botTeams.Add(team);
			_botDifficultyByTeam[team] = System.Math.Clamp(difficulty, 1, 3);
		}

		/// <summary>从权威出生清单同步队伍分组（开局前调用，双端一致）。</summary>
		public void ConfigureTeamGroups()
		{
			_teamGroup.Clear();
			var network = RTS.Network.NetworkManager.Instance;
			if (network == null)
				return;
			foreach (var kv in network.PeerTeamMap)
			{
				int group = network.PeerGroupMap.GetValueOrDefault(kv.Key, kv.Value);
				_teamGroup[kv.Value] = group;
			}

			// 逻辑层要判定"蓝图是否阻挡我"，但不能依赖 NetworkManager：
			// 注入一个纯委托，并让阻挡指纹失效以重烘焙蓝图覆盖层。
			RTS.Simulation.SimGrid.TeamFriendlyResolver = AreTeamsFriendly;
			RTS.Simulation.SimGrid.BumpTeamRevision();
		}

		/// <summary>同组 = 盟友；中立塔(-2)对所有人敌对；中立资源(-1)不可打。</summary>
		public bool AreTeamsHostile(int teamA, int teamB)
		{
			if (teamA <= 0 || teamB <= 0)
			{
				// 双向对称：中立塔(-2)攻击任何正数队伍，任何正数队伍也可攻击中立塔
				if (teamA == -2 && teamB > 0)
					return true;
				if (teamB == -2 && teamA > 0)
					return true;
				return false;
			}
			return GetTeamGroup(teamA) != GetTeamGroup(teamB);
		}

		public int GetTeamGroup(int team)
		{
			return _teamGroup.TryGetValue(team, out int group) ? group : team;
		}

		/// <summary>
		/// 自己人 = 双方都是真实队伍且同组（2v2 盟友算自己人）。
		/// 中立（负队伍号，如 -1 资源 / -2 中立塔）永远不是任何人的自己人。
		/// 蓝图阻挡、蓝图推出、蓝图可见性都以它为准。
		/// </summary>
		public bool AreTeamsFriendly(int teamA, int teamB)
		{
			if (teamA <= 0 || teamB <= 0)
				return false;
			return GetTeamGroup(teamA) == GetTeamGroup(teamB);
		}

		/// <summary>指定队伍是否为机器人（AI 开全图：索敌/视野检查放行）。</summary>
		public bool IsBotTeam(int team) => _botTeams.Contains(team);
		// P1-4 投降/胜负：已投降队伍与对局结束标记
		private readonly System.Collections.Generic.HashSet<int> _surrenderedTeams = new();
		private bool _matchOver = false;
		public bool MatchOver => _matchOver;
		// P1-4 协商/投票：锁步确定性投票（重新开局 / 队伍投降）
		public const string VoteKindRematch = "VoteRematch";
		public const string VoteKindSurrenderTeam = "VoteSurrenderTeam";
		public const int VoteDurationTicks = 120; // 6 秒 @20Hz

		public sealed class ActiveVoteState
		{
			public string Kind;
			public int ProposerPid;
			public int TickDeadline;
			public readonly HashSet<int> Yes = new();
			public readonly HashSet<int> No = new();
		}

		public ActiveVoteState CurrentVote;
		/// <summary>最近一次投票结果（主线程 UI 读取展示，仅调试/提示用，不参与哈希）。</summary>
		public string LastVoteOutcome = "";
		// P1-5 聊天：锁步 Chat 动作的消息日志（双端一致，UI 后续接入）
		// P1-5 聊天/地图信号：模拟线程写入，UI 主线程只读快照（锁保护）
		public readonly object ChatLock = new();
		public readonly object PingLock = new();
		public readonly System.Collections.Generic.List<string> ChatMessages = new();
		public readonly System.Collections.Generic.List<MapPing> MapPings = new();

		/// <summary>地图信号：锁步数据，渲染层按 ExpireTick 过期消失。</summary>
		public readonly struct MapPing
		{
			public readonly FPVector2 Position;
			public readonly int PlayerID;
			public readonly int ExpireTick;
			public MapPing(FPVector2 position, int playerId, int expireTick)
			{
				Position = position;
				PlayerID = playerId;
				ExpireTick = expireTick;
			}
		}

		public System.Collections.Generic.List<string> GetChatSnapshot()
		{
			lock (ChatLock)
				return new System.Collections.Generic.List<string>(ChatMessages);
		}

		public System.Collections.Generic.List<MapPing> GetPingSnapshot(int nowTick)
		{
			lock (PingLock)
			{
				MapPings.RemoveAll(p => p.ExpireTick < nowTick);
				return new System.Collections.Generic.List<MapPing>(MapPings);
			}
		}
		public bool IsRunning
		{
			get => _isRunning;
			set => _isRunning = value;
		}

		// =========================================================
		// 方案 B：模拟独占单线程（锁步确定性不变，渲染主线程解放）
		// =========================================================
		private System.Threading.Thread _simThread;
		private readonly object _simSync = new();
		private volatile bool _simThreadRunning = false;
		private volatile bool _tickReadyToDrain = false;
		private volatile bool _drainDone = true;
		// 世界锁：模拟线程 tick 期间持有；主线程直接生成实体（非延迟路径）也需持有，避免并发改 World
		public readonly object WorldLock = new();

		// 每 tick 发布的单位位置/速度快照（主线程视觉读取，避免与模拟线程竞争）
		private const int SnapshotMaxId = 16384;
		private readonly float[] _snapA = new float[SnapshotMaxId * 4];
		private readonly float[] _snapB = new float[SnapshotMaxId * 4];
		private float[] _snapRead = new float[SnapshotMaxId * 4];
		private readonly object _snapLock = new();

		public bool TryGetUnitVisualState(int id, out float x, out float y, out float vx, out float vy)
		{
			x = 0f; y = 0f; vx = 0f; vy = 0f;
			int idx = (id - 1000) * 4;
			var snap = System.Threading.Volatile.Read(ref _snapRead);
			if (id < 1000 || idx < 0 || idx + 3 >= snap.Length)
				return false;
			x = snap[idx];
			y = snap[idx + 1];
			vx = snap[idx + 2];
			vy = snap[idx + 3];
			return true;
		}

		private void PublishSnapshot()
		{
			lock (_snapLock)
			{
				// 交替双缓冲：写入当前不被读取的那块，绝不清空正在被主线程读的数组
				var write = ReferenceEquals(_snapRead, _snapA) ? _snapB : _snapA;
				Array.Clear(write, 0, write.Length);
				foreach (var u in World.Units.Values)
				{
					int idx = (u.ID - 1000) * 4;
					if (u.ID < 1000 || idx < 0 || idx + 3 >= write.Length)
						continue;
					write[idx] = (float)u.Position.X;
					write[idx + 1] = (float)u.Position.Y;
					write[idx + 2] = (float)u.Velocity.X;
					write[idx + 3] = (float)u.Velocity.Y;
				}
				System.Threading.Volatile.Write(ref _snapRead, write);
			}
		}

		// 调试作弊：每次 F11 给发送者刷的资源数量
		public const int CheatResourceAmount = 5000;

		// 调试作弊：秒建建筑 / 秒点科技（通过锁步指令切换，双端一致）
		public static bool CheatInstantBuild = false;
		public static bool CheatInstantResearch = false;

		// 逻辑实体 ID -> Godot 表现节点 / 控制模块桥接
		// 视觉节点注册表：主线程维护；模拟线程不直接访问
		public System.Collections.Concurrent.ConcurrentDictionary<int, IEntity> EntityNodes = new();
		// 泰伦驻扎：本 Tick 待移除的 (单位ID, 建筑ID)，在单位遍历结束后处理
		private readonly List<(int UnitId, int StructId)> _pendingGarrison = new();

		public void QueueGarrison(int unitId, int structId)
		{
			_pendingGarrison.Add((unitId, structId));
		}

		public void RegisterEntityNode(IEntity entity)
		{
			if (entity?.LogicEntity == null)
				return;

			EntityNodes[entity.LogicEntity.ID] = entity;
		}

		public void UnregisterEntityNode(int id)
		{
			EntityNodes.TryRemove(id, out _);
		}

		public IEntity FindEntityById(int id)
		{
			return EntityNodes.TryGetValue(id, out var e) ? e : null;
		}

		public override void _EnterTree()
		{
			Instance = this;
			ConfigureBotsFromOverrides();
			StartSimThread();

			// SIM_SPEED=1..8：无头/离线测试用倍速（等价于 AIShowcase 的 AI_TEST_SPEED）
			if (int.TryParse(OS.GetEnvironment("SIM_SPEED"), out int simSpeed) &&
				simSpeed >= 1 && simSpeed <= 8)
				SimulationSpeed = simSpeed;

			if (World == null)
			{
				World = new SimWorld(WorldLock, this);
			}
			ConfigureWorldDiagnostics();
		}

		FP ISimulationRules.GetLifestealFraction(int teamId)
		{
			var playerData = RTS.World.Game.GetPlayerByTeam(teamId)?.PlayerData;
			return playerData == null ? FP.Zero : (FP)TechEffects.GetLifestealPercent(playerData);
		}

		private void ConfigureWorldDiagnostics()
		{
			_profileEnabled = OS.GetEnvironment("SIM_PROFILE") == "1";
			World.ProfileEnabled = _profileEnabled;
			_profileMs.Clear();
			World.DiagnosticSink = OS.GetEnvironment("SIM_PATH_LOG") == "1"
				? message => RTS.Core.SimEventQueue.EnqueueMain(() => GD.Print(message))
				: null;
		}

		public override void _ExitTree()
		{
			StopSimThread();
			base._ExitTree();
		}

		public override void _Process(double delta)
		{
			_fpsWindowTimer += delta;
			if (_fpsWindowTimer >= 1.0)
			{
				LogicFps = (float)(_ticksThisWindow / _fpsWindowTimer);
				_ticksThisWindow = 0;
				_fpsWindowTimer = 0.0;
			}

			if (_simThreadRunning)
			{
				// 模拟线程已完成一次 tick：主线程只派发事件/网络包，然后放行下一 tick。
				// 派发期间模拟线程等待（屏障），World 不会并发修改。
				if (_tickReadyToDrain)
				{
					DrainSimEvents();
					LockstepManager.Instance?.FlushOutbox();
					lock (_simSync)
					{
						_drainDone = true;
						_tickReadyToDrain = false;
						System.Threading.Monitor.PulseAll(_simSync);
					}
				}
				// 血条实例数据每帧一次上传（与 tick 无关）
				RTS.Core.HealthBarBatchRenderer.Flush();
				return;
			}

			if (World == null || LockstepManager.Instance == null || !IsRunning)
				return;

			_accumulator += delta;

			int processedTicks = 0;

			while (_accumulator >= _fixedDeltaFloat && processedTicks < MaxTicksPerFrame)
			{
				int currentTick = LockstepManager.Instance.CurrentTick;

				if (!LockstepManager.Instance.IsTickReady(currentTick))
				{
					// 网络没准备好时，不继续追帧。
					// 同时限制 accumulator，避免等包期间无限累积。
					double maxAccum = _fixedDeltaFloat * MaxTicksPerFrame;
					if (_accumulator > maxAccum)
						_accumulator = maxAccum;

					break;
				}

				ExecuteOneTick(currentTick);

				LockstepManager.Instance.AdvanceTick();

				_accumulator -= _fixedDeltaFloat;
				processedTicks++;
			}

			// 防止长时间卡顿后一次性模拟过多 Tick。
			double cap = _fixedDeltaFloat * MaxTicksPerFrame;
			if (_accumulator > cap)
				_accumulator = cap;

			// 模拟线程产生的事件统一在主线程派发，避免跨线程碰 Godot 信号。
			DrainSimEvents();

			// 网络发送统一由主线程代发（模拟线程只入队）。
			LockstepManager.Instance?.FlushOutbox();
			RTS.Core.HealthBarBatchRenderer.Flush();
		}

		public void StartSimThread()
		{
			if (_simThreadRunning)
				return;
			_simThreadRunning = true;
			_tickReadyToDrain = false;
			_drainDone = true;
			_simThread = new System.Threading.Thread(SimThreadLoop)
			{
				IsBackground = true,
				Name = "LockstepSim"
			};
			_simThread.Start();
		}

		public void StopSimThread()
		{
			_simThreadRunning = false;
			lock (_simSync)
				System.Threading.Monitor.PulseAll(_simSync);
			_simThread?.Join(2000);
			_simThread = null;
		}

		private void SimThreadLoop()
		{
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			double last = 0.0;

			while (_simThreadRunning)
			{
				if (_paused)
				{
					System.Threading.Thread.Sleep(2);
					continue;
				}

				// 屏障：上一 tick 的延迟动作还没被主线程派发完时，不能动 World。
				lock (_simSync)
				{
					while (_simThreadRunning && _tickReadyToDrain && !_drainDone)
						System.Threading.Monitor.Wait(_simSync, 5);
				}
				if (!_simThreadRunning)
					break;

				if (World == null || LockstepManager.Instance == null || !IsRunning)
				{
					System.Threading.Thread.Sleep(2);
					continue;
				}

				double now = stopwatch.Elapsed.TotalSeconds;
				double delta = now - last;
				last = now;
				if (delta <= 0.0)
					delta = 0.0;
				if (delta > 0.25)
					delta = 0.25;
				// 倍速：放大模拟时间流速（屏障协议不变，主线程每轮派发一次）
				delta *= SimulationSpeed;
				_accumulator += delta;

				int processed = 0;
				int maxTicksThisRound = MaxTicksPerFrame * SimulationSpeed;
				lock (WorldLock)
				{
					while (_accumulator >= _fixedDeltaFloat && processed < maxTicksThisRound)
					{
						int currentTick = LockstepManager.Instance.CurrentTick;
						if (!LockstepManager.Instance.IsTickReady(currentTick))
						{
							double maxAccum = _fixedDeltaFloat * maxTicksThisRound;
							if (_accumulator > maxAccum)
								_accumulator = maxAccum;
							break;
						}

						ExecuteOneTick(currentTick);
						LockstepManager.Instance.AdvanceTick();
						_accumulator -= _fixedDeltaFloat;
						processed++;
					}
				}

				double cap = _fixedDeltaFloat * maxTicksThisRound;
				if (_accumulator > cap)
					_accumulator = cap;

				PublishSnapshot();

				lock (_simSync)
				{
					_tickReadyToDrain = true;
					_drainDone = false;
					System.Threading.Monitor.PulseAll(_simSync);
				}

				System.Threading.Thread.Sleep(1);
			}
		}

		private void DrainSimEvents()
		{
			// 先执行延迟到主线程的视觉/节点操作，再派发数据事件
			while (RTS.Core.SimEventQueue.TryDequeueMain(out var action))
			{
				try
				{
					action();
				}
				catch (System.Exception ex)
				{
					GD.PrintErr($"[SimEvent] 主线程延迟动作异常: {ex}");
				}
			}

			while (RTS.Core.SimEventQueue.TryDequeue(out var e))
			{
				var node = FindEntityById(e.EntityId);
				if (node?.LifeModule is UnitLife life)
				{
					if (e.Type == RTS.Core.SimEventType.HealthChanged)
						life.NotifyHealthChanged(e.A, e.B);
					else if (e.Type == RTS.Core.SimEventType.Died)
						life.NotifyDied();
				}
				// Resource nodes created by the factory have no UnitLife module.
				if (node is RTS.Units.ResourceStructure resource && resource.ResModule is { } res &&
					GodotObject.IsInstanceValid(resource) && GodotObject.IsInstanceValid(res))
				{
					if (e.Type == RTS.Core.SimEventType.ResourceAmountChanged) res.NotifyAmountChanged(e.A, e.B);
					else if (e.Type == RTS.Core.SimEventType.ResourceDepleted) res.NotifyDepleted();
				}
			}
		}

		private void ExecuteOneTick(int currentTick)
		{
			_ticksThisWindow++;

#if DEBUG
			// 每秒时间标记：20Hz 下 20 tick = 1 秒，便于按日志定位时刻
			if (currentTick % 20 == 0)
			{
				int pendingPaths = 0;
				foreach (var u in World.Units.Values)
					if (u != null && !u.IsDead && u.PathPending)
						pendingPaths++;
				GD.Print($"[Time] 秒={currentTick / 20} tick={currentTick} 挂起={pendingPaths}");
			}
#endif

			List<NetAction> actionsToExecute = LockstepManager.Instance.ConsumeActions(currentTick);

			DispatchActions(actionsToExecute);

			ProfileTick("StructureModules", TickStructureModules);
			ProfileTick("UnitModules", TickUnitModules);
			ProfileTick("BotTakeover", () => TickBotTakeover(currentTick));
			ProfileTick("BotAI", () => TickBotAI(currentTick));
			ProfileTick("Garrisons", TickPendingGarrisons);
			ProfileTick("DemonFields", TickDemonFields);
			ProfileTick("DemonAuras", TickDemonAuras);
			ProfileTick("HeroCharm", TickHeroCharm);
			ProfileTick("AutoProduce", TickAutoProduce);
			ProfileTick("DeathEffects", TickDeathEffects);
			ProfileTick("Wanderer", TickWandererSystems);
			ProfileTick("PendingSpawns", TickPendingSpawns);
			ProfileTick("DeathChecks", TickDeathChecks);
			ProfileTick("CaveWreckages", TickCaveWreckages);
			ProfileTick("WorldTick", () => World.Tick());
			// 蓝图让路对账：放在 World.Tick 之后，用本 tick 的最终位置判定，
			// 这样"自己人站在蓝图里"的移出指令下一 tick 就能生效
			ProfileTick("BlueprintEvict", () => World.TickBlueprintEvictions());
			ProfileTick("Dash", TickDash);
			ProfileTick("Devour", TickDevour);
			ProfileTick("Research", TickResearch);
			ProfileTick("NanoEconomy", TickNanoEconomy);
			ProfileTick("PlantFields", TickPlantFields);
			ProfileTick("CaveFields", TickCaveFields);
			ProfileTick("Harvester", TickHarvesterCollection);
			ProfileTick("Algae", TickAlgaeGarrison);
			ProfileTick("NanoAuras", TickNanoAuras);
			// 触发器放最后：此时本 tick 的移动/战斗/死亡都已结算，
			// 触发器看到的是稳定的本 tick 结果，不会读到半更新的世界。
			ProfileTick("Triggers", () => TickTriggers(currentTick));
			// 教程放在触发器之后：触发器可能本 tick 刷兵/给资源，
			// 教程目标应当看到最终结果，否则"到达某地"会晚一 tick 才成立。
			ProfileTick("Tutorial", () => TickTutorial(currentTick));

			if (_profileEnabled && currentTick % 60 == 0)
				PrintProfile(currentTick);


			// 发送未来 Tick 的心跳，保证无操作时锁步仍可继续。
			LockstepManager.Instance.SendSyncHeartbeat();

			// 每 30 Tick 发送一次状态哈希（1.5 秒），更快发现脱步。
			if (currentTick > 0 && currentTick % 30 == 0)
			{
				LockstepManager.Instance.SendStateHash(currentTick, GetFullWorldHash());
			}

		}

		// =========================================================
		// P1-2 性能探针：SIM_PROFILE=1 时按系统累计每 tick 耗时，每 60 tick 打印
		// =========================================================
		private readonly System.Collections.Generic.Dictionary<string, double> _profileMs = new();
		private bool _profileEnabled = false;

		private void ProfileTick(string name, System.Action tickMethod)
		{
			if (!_profileEnabled)
			{
				tickMethod();
				return;
			}
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			tickMethod();
			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 /
				System.Diagnostics.Stopwatch.Frequency;
			_profileMs[name] = _profileMs.GetValueOrDefault(name) + ms;
		}

		private void PrintProfile(int currentTick)
		{
			var sb = new System.Text.StringBuilder();
			sb.Append($"[Profile] tick={currentTick}");
			foreach (var kv in _profileMs.OrderByDescending(k => k.Value))
				sb.Append($" {kv.Key}={kv.Value:F1}ms");
			if (World != null)
			{
				foreach (var kv in World.ProfileMs.OrderByDescending(k => k.Value))
					sb.Append($" W.{kv.Key}={kv.Value:F1}ms");
				World.ProfileMs.Clear();
			}
			GD.Print(sb.ToString());
			_profileMs.Clear();
		}

		private void TickStructureModules()
		{
			// 注意：
			// 这里遍历 World.Structures.Values。
			// 如果 World.Structures 是 SortedDictionary，则顺序稳定。
			// 如果以后改成 Dictionary，必须改回按 ID 排序。
			foreach (var struc in World.Structures.Values)
			{
				IEntity node = struc.ID >= 1000 ? FindEntityById(struc.ID) : null;

				if (node == null)
					continue;

				node.LifeModule?.Tick(_fixedDeltaFloat);
				node.CombatModule?.Tick(_fixedDeltaFloat);

				if (node is RTS.Units.Structure s)
					s.CreepModule?.Tick(_fixedDeltaFloat);

				if (node is RTS.Units.ResourceStructure res)
					res.ResModule?.Tick(_fixedDeltaFloat);

				if (node is RTS.Units.ShrineStructure shrine)
					shrine.LogicTick(_fixedDeltaFloat);

				// 建筑被动回血（配置驱动）：永久 Buff，逻辑层确定性挂载
				if (node is RTS.Units.Structure passiveStruct &&
					passiveStruct.CurrentState == RTS.Units.Structure.StructureState.Completed &&
					passiveStruct.LogicEntity != null &&
					!passiveStruct.LogicEntity.Buffs.HasBuff("PassiveRegen"))
				{
					var passiveCfg = RTS.Data.Configs.ConfigDatabase.GetStructure(passiveStruct.StructureName);

					if (passiveCfg != null && passiveCfg.PassiveHpRegenPerSecond > 0f)
					{
						passiveStruct.LogicEntity.Buffs.AddBuff(
							"PassiveRegen", 1,
							FP.Zero,
							FP.One, FP.One, FP.Zero, FP.One,
							(FP)passiveCfg.PassiveHpRegenPerSecond, FP.Zero,
							-1
						);
					}
				}

				// 自动施工（纳米建筑/恶魔献祭建筑）：施工推进交给建筑上的行为模块
				if (node is RTS.Units.Structure autoBuilding &&
					autoBuilding.IsUnderConstruction &&
					autoBuilding.LogicEntity != null)
				{
					if (autoBuilding.AutoBuildCache is { } ab)
						ab.Tick(autoBuilding, (float)_fixedDeltaFloat);
				}

				// 交互建筑被动产出（轨道控制中心能量等）：数值来自配置表
				if (node is RTS.Units.Structure incomeBuilding &&
					incomeBuilding.CurrentState == RTS.Units.Structure.StructureState.Completed &&
					incomeBuilding.LogicEntity != null)
				{
					var incomeCfg = RTS.Data.Configs.ConfigDatabase.GetStructure(incomeBuilding.StructureName);
					if (incomeCfg != null && incomeCfg.ProvidesResourceIncome && incomeCfg.IncomeCycleTicks > 0)
					{
						// 每 tick = 每周期量 / 周期tick；20Hz 下 1/20 周期 = 每秒 1 份
						FP income = (FP)incomeCfg.IncomeAmountPerCycle / (FP)incomeCfg.IncomeCycleTicks;

						if (income != FP.Zero)
						{
				var incomePlayer = RTS.World.Game.GetPlayerByTeam(incomeBuilding.TeamID);
				incomePlayer?.PlayerData?.AddResource(
					incomeCfg.IncomeResourceType,
					income * GetBotResourceMultiplier(incomeBuilding.TeamID));
						}
					}
				}

				node.Brain?.LogicTick(_fixedDeltaFloat);
			}
		}

		// =========================================================
		// 恶魔：立场 / 自动生产 / 死亡特效
		// =========================================================

		private void TickDemonFields()
		{
			// 恶魔立场内持续扣血已移除：不再对站在立场内的敌方单位自动扣血
		}

		private void TickAutoProduce()
		{
			foreach (var sim in World.Structures.Values)
			{
				if (sim == null || sim.IsDead || sim.CurrentState != RTS.Simulation.SimStructure.StructureState.Active)
					continue;

				var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(sim.StructureTypeId);
				if (cfg == null || cfg.AutoProduceUnitIds.Count == 0 || cfg.AutoProduceIntervalSeconds <= 0f)
					continue;

				// 狂热武装加速计时
				if (sim.AutoProduceBoostTimer > FP.Zero)
				{
					sim.AutoProduceBoostTimer -= World.FixedDelta;

					if (sim.AutoProduceBoostTimer <= FP.Zero)
					{
						sim.AutoProduceBoostTimer = FP.Zero;
						sim.AutoProduceBoostMultiplier = FP.One;
					}
				}

				var player = RTS.World.Game.GetPlayerByTeam(sim.TeamID);
				float interval = cfg.AutoProduceIntervalSeconds;

				if (cfg.AutoProduceAffectedByRiftTechs)
					interval *= TechEffects.GetAutoProduceIntervalMultiplier(player?.PlayerData);

				if (sim.AutoProduceBoostMultiplier > FP.One)
					interval /= (float)sim.AutoProduceBoostMultiplier;

				if (interval <= 0f)
					continue;

				sim.AutoProduceTimer += World.FixedDelta;
				FP intervalFP = (FP)interval;
				sim.AutoProduceIntervalCurrent = intervalFP;
				int safety = 0;

				while (sim.AutoProduceTimer >= intervalFP && safety++ < 8)
				{
					sim.AutoProduceTimer -= intervalFP;
					SpawnAutoProducedUnits(sim, cfg);
				}
			}
		}

		private void SpawnAutoProducedUnits(SimStructure sim, RTS.Data.Configs.StructureConfig cfg)
		{
			// 地狱城绑定上限：只统计该城还活着的怨灵
			if (cfg.AutoProduceMaxBound > 0)
			{
				int boundAlive = 0;

				foreach (var u in World.Units.Values)
				{
					if (!u.IsDead && u.BoundStructureId == sim.ID)
						boundAlive++;
				}

				if (boundAlive >= cfg.AutoProduceMaxBound)
					return;
			}

			var ids = sim.AutoProduceMode == 1 && cfg.AutoProduceModeUnitIds.Count > 0
				? cfg.AutoProduceModeUnitIds
				: cfg.AutoProduceUnitIds;

			foreach (string unitId in ids)
			{
				FP angle = World.RNG.NextFP() * FP.Pi * (FP)2m;
				FP dist = (FP)((sim.GridSize * World.Grid.TileSize) / 2) + (FP)100m;
				FPVector2 pos = sim.Position + new FPVector2(-dist * FP.Sin(angle), dist * FP.Cos(angle));

				// 生成（含视觉节点）延迟到主线程；输入全部为确定性数据
				string spawnUnitId = unitId;
				int spawnTeam = sim.TeamID;
				int spawnStructId = sim.ID;
				float spawnX = (float)pos.X;
				float spawnY = (float)pos.Y;
				RTS.Core.SimEventQueue.EnqueueMain(() =>
				{
					var ent = EntitySpawner.Instance?.SpawnEntity(spawnUnitId, spawnTeam,
						new RTS.Simulation.FPVector2((FP)spawnX, (FP)spawnY));

					if (ent is RTS.Units.Unit newUnit && newUnit.LogicEntity is SimUnit newSim)
					{
						newSim.BoundStructureId = spawnStructId;

						if (FindEntityById(spawnStructId) is RTS.Units.Structure node)
						{
							var queue = node.RallyQueue;

							for (int i = 0; i < queue.Count; i++)
								newUnit.Brain.StartAction(queue[i].ActionName, queue[i].TargetPos, queue[i].TargetObj, false, i > 0);
						}
					}
				});
			}
		}

		private void TickDeathEffects()
		{
			foreach (var unit in World.Units.Values)
			{
				if (unit == null || unit.DeathProcessed)
					continue;

				unit.DeathProcessed = true;

				if (!unit.IsDead)
					continue;

				// 多足数据：击杀敌人 / 被敌人击杀，双方各得 1 数据
				if (unit.TeamID > 0)
				{
					var victimPlayer = RTS.World.Game.GetPlayerByTeam(unit.TeamID);
			victimPlayer?.PlayerData?.AddResource(ResourceType.Data, FP.One * GetBotResourceMultiplier(unit.TeamID));

					if (unit.LastDamageSourceId != -1)
					{
						var killer = World.FindSimEntity(unit.LastDamageSourceId);
						if (killer != null && killer.TeamID > 0 && killer.TeamID != unit.TeamID)
			RTS.World.Game.GetPlayerByTeam(killer.TeamID)?.PlayerData?.AddResource(
				ResourceType.Data,
				FP.One * GetBotResourceMultiplier(killer.TeamID));
					}

					// 信鸽：死亡时揭示 20 格视野 3 秒（仅本地方）
					var deathCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(unit.UnitTypeId);
					if (deathCfg != null && deathCfg.HasPigeon &&
						victimPlayer?.PlayerData != null &&
						victimPlayer.PlayerData.HasTech(deathCfg.PigeonTechId) &&
						Main.Instance?.LocalPlayerID == unit.TeamID &&
						RTS.World.FogOfWar.Instance != null)
					{
						float revealX = (float)unit.Position.X;
						float revealY = (float)unit.Position.Y;
						float revealRadius = 20f * World.Grid.TileSize;
						RTS.Core.SimEventQueue.EnqueueMain(() =>
						{
							RTS.World.FogOfWar.Instance?.AddTemporaryReveal(
								new Vector2(revealX, revealY),
								revealRadius,
								3f);
						});
					}
				}

				// 熔岩旗手：自爆 100 伤害半径 1 格 + 原地留旗帜
				if (unit.UnitTypeId == "LavaBanner")
				{
					World.ApplyAreaDamage(
						unit.Position,
						(FP)64m,
						100,
						unit.ID,
						(FP)100m,
						(int)DamageType.Thermal,
						FP.Zero,
						0
					);

					float bannerX = (float)unit.Position.X;
					float bannerY = (float)unit.Position.Y;
					int bannerTeam = unit.TeamID;
					RTS.Core.SimEventQueue.EnqueueMain(() =>
					{
						EntitySpawner.Instance?.SpawnEntity("Banner", bannerTeam,
							new RTS.Simulation.FPVector2((FP)bannerX, (FP)bannerY));
					});
				}
			}
		}

		// 英雄立场光环：按单位配置的 FieldAura* 字段驱动（地狱领主减速攻速 / 火焰巨魔增伤）
		private void TickDemonAuras()
		{
			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead || u.FieldRadius <= 0)
					continue;

				var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitTypeId);
				if (cfg == null ||
					(cfg.FieldAuraEnemyAttackSpeedMultiplier <= 0f &&
					 cfg.FieldAuraEnemyIncomingDamageMultiplier <= 0f))
					continue;

				FP radius = (FP)(u.FieldRadius * World.Grid.TileSize);
				FP radiusSq = radius * radius;
				FP dur = (FP)cfg.FieldAuraTickSeconds;

				foreach (var e in World.Units.Values)
				{
					if (e == null || e.IsDead || e.TeamID == u.TeamID || e.TeamID <= 0)
						continue;

					if (FPVector2.DistanceSquared(e.Position, u.Position) > radiusSq)
						continue;

					if (cfg.FieldAuraEnemyAttackSpeedMultiplier > 0f)
						e.Buffs.AddStatBuff(
							"HellLordFieldDebuff", 1, dur,
							(FP)cfg.FieldAuraEnemyAttackSpeedMultiplier, FP.Zero, FP.Zero, u.ID);

					if (cfg.FieldAuraEnemyIncomingDamageMultiplier > 0f)
						e.Buffs.AddBuff(
							"FireTitanFieldDebuff", 1, dur,
							FP.One, (FP)cfg.FieldAuraEnemyIncomingDamageMultiplier,
							FP.Zero, FP.One, FP.Zero, FP.Zero, u.ID);
				}
			}
		}

		// 勾魂：被魅惑的敌人不受控制地向施法者移动
		private void TickHeroCharm()
		{
			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead || !u.Buffs.HasBuff("DemonCharm"))
					continue;

				var buff = u.Buffs.GetBuff("DemonCharm");
				if (buff == null)
					continue;

				var src = World.FindSimEntity(buff.SourceEntityId);
				if (src == null || src.IsDead)
					continue;

				u.CommandMove(src.Position, src.ID);
			}
		}

		// 指令执行者：EntityIDs[0] 为空返回 null（Handle* 系列统一入口，去掉 12 处重复样板）
		private IEntity GetExecutor(NetAction netAct)
		{
			if (netAct.EntityIDs == null || netAct.EntityIDs.Length == 0)
				return null;
			return FindEntityById(netAct.EntityIDs[0]);
		}

		private void HandleHeroSkill(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;
			if (executor?.LogicEntity is not SimUnit hero || !hero.IsDemonUnit)
				return;

			int skillIndex = netAct.ActionId == "HeroSkill1" ? 1 : 2;
			var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(hero.UnitTypeId);
			if (cfg == null)
				return;

			// 泰伦干扰弹技能需要科技解锁
			if (skillIndex == 2 && !string.IsNullOrEmpty(cfg.Skill2RequiredTechId))
			{
				var playerCheck = RTS.World.Game.GetPlayerByTeam(hero.TeamID);
				if (playerCheck?.PlayerData == null || !playerCheck.PlayerData.HasTech(cfg.Skill2RequiredTechId))
					return;
			}

			// 技能效果按 SkillBehaviorId 查注册表执行，不再按单位名 if/else
			string behaviorId = skillIndex == 1 ? cfg.Skill1BehaviorId : cfg.Skill2BehaviorId;
			var behavior = RTS.Simulation.HeroSkillBehaviors.Get(behaviorId);
			if (behavior == null)
				return;

			float cost = skillIndex == 1 ? cfg.Skill1Cost : cfg.Skill2Cost;
			if (cost <= 0f || hero.HeroEnergy < (FP)cost)
				return;

			IEntity target = netAct.TargetEntityID != -1 ? FindEntityById(netAct.TargetEntityID) : null;
			FPVector2 targetPos = netAct.DecodeTargetPos();

			var args = new RTS.Simulation.HeroSkillArgs
			{
				Cost = (FP)cost,
				DurationSeconds = (FP)(skillIndex == 1 ? cfg.Skill1DurationSeconds : cfg.Skill2DurationSeconds),
				RangeTiles = (FP)(skillIndex == 1 ? cfg.Skill1RangeTiles : cfg.Skill2RangeTiles),
				MoveSpeedBonus = (FP)(skillIndex == 1 ? cfg.Skill1MoveSpeedBonus : 0f),
				EnemyMovePenalty = (FP)(skillIndex == 1 ? cfg.Skill1EnemyMovePenalty : 0f),
				Multiplier = (FP)(skillIndex == 1 ? 1f : cfg.Skill2Multiplier),
				Damage = (FP)(skillIndex == 1 ? 0f : cfg.Skill2Damage),
				AttackRangeBonus = (FP)(skillIndex == 1 ? cfg.Skill1AttackRangeBonus : 0f)
			};

			if (behavior.Execute(World, hero, args, target?.LogicEntity, targetPos))
				hero.HeroEnergy -= (FP)cost;
		}

		// 泰伦藻类工厂：2 格内每名工程兵每秒 +5 肉（上限 = 驻扎容量）
		private void TickAlgaeGarrison()
		{
			foreach (var sim in World.Structures.Values)
			{
				if (sim == null || sim.IsDead || sim.CurrentState != RTS.Simulation.SimStructure.StructureState.Active)
					continue;

				var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(sim.StructureTypeId);
				if (cfg == null || cfg.GarrisonCapacity <= 0 || cfg.GarrisonIncomePerSecondPerUnit <= 0f)
					continue;

				int count = sim.GarrisonedCount;

				if (count <= 0)
					continue;

				var player = RTS.World.Game.GetPlayerByTeam(sim.TeamID);
				player?.PlayerData?.AddResource(
					ResourceType.Biomass,
					(FP)(count * cfg.GarrisonIncomePerSecondPerUnit) * World.FixedDelta *
					GetBotResourceMultiplier(sim.TeamID));
			}
		}

		// 泰伦驻扎结算：工程兵从世界移除并进入建筑（在单位遍历之后，避免修改集合）
		private void TickPendingGarrisons()
		{
			foreach (var (unitId, structId) in _pendingGarrison)
			{
				IEntity node = FindEntityById(unitId);
				var simUnit = World.FindSimEntity(unitId) as SimUnit;
				var simStruct = World.FindSimEntity(structId) as SimStructure;

				if (simUnit == null || simStruct == null || simStruct.IsDead)
					continue;

				var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(simStruct.StructureTypeId);
				if (cfg == null || simStruct.GarrisonedCount >= cfg.GarrisonCapacity)
					continue;

				World.Units.Remove(unitId);
				UnregisterEntityNode(unitId);
				simStruct.GarrisonedCount++;
				simStruct.MilitiaGarrisonDamage = TeamHasTech(simStruct.TeamID, "CaveTech_Militia")
					? simStruct.GarrisonedCount * 5
					: 0;

				if (node is Godot.Node n && GodotObject.IsInstanceValid(n))
				{
					// 视觉节点释放延迟到主线程（模拟线程禁止碰 Godot）
					RTS.Core.SimEventQueue.EnqueueMain(() =>
					{
						if (GodotObject.IsInstanceValid(n))
							n.QueueFree();
					});
				}
			}

			_pendingGarrison.Clear();
		}

		private static bool TeamHasTech(int teamId, string techId)
		{
			return RTS.World.Game.GetPlayerByTeam(teamId)?.PlayerData?.HasTech(techId) == true;
		}

		// 冲锋：朝指定点高速冲刺（最多 5 格），落地对周围敌人造成伤害
		private void TickDash()
		{
			FP dashSpeed = (FP)(World.Grid.TileSize * 10m); // 10 格/秒

			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead || u.DashTimer <= FP.Zero)
					continue;

				u.DashTimer -= World.FixedDelta;
				u.HasTarget = false;
				u.Velocity = FPVector2.Zero;

				FP dist = FP.Sqrt(FPVector2.DistanceSquared(u.Position, u.DashTarget));
				FP step = dashSpeed * World.FixedDelta;
				bool arrived = false;

				if (dist <= step)
				{
					u.Position = u.DashTarget;
					arrived = true;
				}
				else
				{
					FPVector2 dir = (u.DashTarget - u.Position).Normalized();
					u.Position += dir * step;
				}

				// 沿途碰撞：碰到敌人造成伤害并击退一点距离
				if (u.DashDamage > FP.Zero)
				{
					foreach (var e in World.Units.Values)
					{
						if (e == null || e.IsDead || e.TeamID == u.TeamID || e.TeamID <= 0)
							continue;
						if (u.DashHitIds.Contains(e.ID))
							continue;

						FP hitDist = u.Radius + e.Radius + (FP)24m;
						if (FPVector2.DistanceSquared(u.Position, e.Position) > hitDist * hitDist)
							continue;

						u.DashHitIds.Add(e.ID);
						e.TakeDamage(u.DashDamage, (int)DamageType.Thermal, FP.Zero, 0, u);

						FPVector2 away = (e.Position - u.Position).Normalized();
						if (away.X == FP.Zero && away.Y == FP.Zero)
							away = (u.DashTarget - u.Position).Normalized();
						e.Position += away * (FP)48m;
					}
				}

				if (arrived)
				{
					u.DashTimer = FP.Zero;
					u.DashHitIds.Clear();
				}
			}
		}

		// 多足：腾跃 / 菌毯防御 / 等离子炮自动索敌与施法
		private void TickWandererSystems()
		{
			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead)
					continue;

				var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitTypeId);
				if (cfg == null)
					continue;

				var player = RTS.World.Game.GetPlayerByTeam(u.TeamID);

				// 腾跃：自动跳到 2 格内敌人身前
				if (cfg.HasLeap && player?.PlayerData != null &&
					!string.IsNullOrEmpty(cfg.LeapTechId) &&
					player.PlayerData.HasTech(cfg.LeapTechId))
				{
					if (u.LeapCooldown > FP.Zero)
						u.LeapCooldown -= World.FixedDelta;

					if (u.LeapCooldown <= FP.Zero)
					{
						FP leapRange = (FP)(cfg.LeapRangeTiles * World.Grid.TileSize);
						FP leapRangeSq = leapRange * leapRange;
						SimUnit leapTarget = null;
						FP bestDist = FP.MaxValue;

						foreach (var e in World.Units.Values)
						{
							if (e == null || e.IsDead || e.TeamID == u.TeamID || e.TeamID <= 0)
								continue;

							FP d2 = FPVector2.DistanceSquared(u.Position, e.Position);
							if (d2 > leapRangeSq)
								continue;
							if (d2 < bestDist)
							{
								bestDist = d2;
								leapTarget = e;
							}
						}

						if (leapTarget != null)
						{
							u.LeapCooldown = (FP)cfg.LeapCooldownSeconds;
							FPVector2 to = (u.Position - leapTarget.Position).Normalized();
							FP dist = u.Radius + leapTarget.Radius + (FP)12m;
							u.Position = leapTarget.Position + to * dist;
						}
					}
				}

				// 战车型：纳米菌毯上全防御加强
				if (cfg.CreepDefenseBonus > 0f)
				{
					var g = World.Grid.WorldToGrid(u.Position);

					if (World.CreepGrid.GetActiveCreep(g.X, g.Y) == CreepType.NanoCreep)
					{
						u.Buffs.AddBuff(
							"WandererCreepDef", 1, (FP)0.5m,
							FP.One, FP.One, (FP)cfg.CreepDefenseBonus, FP.One,
							FP.Zero, FP.Zero, -1);
					}
				}

				// 等离子炮
				if (u.UnitTypeId == "PlasmaCannon")
				{
					TickPlasmaUnit(u, cfg);
				}
			}
		}

		private void TickPlasmaUnit(SimUnit u, RTS.Data.Configs.UnitConfig cfg)
		{
			if (u.PlasmaCooldown > FP.Zero)
				u.PlasmaCooldown -= World.FixedDelta;

			// 前摇/后摇期间停止移动
			if (u.PlasmaCastState != 0)
			{
				u.HasTarget = false;
				u.Velocity = FPVector2.Zero;
			}

			if (u.PlasmaCastState == 1)
			{
				u.PlasmaCastTimer += World.FixedDelta;

				if (u.PlasmaCastTimer >= (FP)cfg.PlasmaWindupSeconds)
				{
					SpawnPlasmaShell(u);
					u.PlasmaCastState = 2;
					u.PlasmaCastTimer = FP.Zero;
				}
			}
			else if (u.PlasmaCastState == 2)
			{
				u.PlasmaCastTimer += World.FixedDelta;

				if (u.PlasmaCastTimer >= (FP)cfg.PlasmaRecoverySeconds)
				{
					u.PlasmaCastState = 0;
					u.PlasmaCastTimer = FP.Zero;
					u.PlasmaCooldown = (FP)cfg.PlasmaCooldownSeconds;
				}
			}
			else if (u.PlasmaAutoFire && u.PlasmaCooldown <= FP.Zero)
			{
				// 自动模式：锁定长宽最大的敌人（20 格内）
				SimEntity best = null;
				FP bestFootprint = FP.Zero;
				FP range = (FP)((cfg.PlasmaAutoTargetRangeTiles > 0 ? cfg.PlasmaAutoTargetRangeTiles : 20) * World.Grid.TileSize);
				FP rangeSq = range * range;

				void Consider(SimEntity e, FP footprint)
				{
					if (e == null || e.IsDead || !AreTeamsHostile(u.TeamID, e.TeamID) ||
						FPVector2.DistanceSquared(u.Position, e.Position) > rangeSq)
						return;

					if (best == null || footprint > bestFootprint || footprint == bestFootprint && e.ID < best.ID)
					{
						bestFootprint = footprint;
						best = e;
					}
				}
				foreach (var e in World.Units.Values)
					Consider(e, e.FootprintTiles * e.FootprintTiles);
				foreach (var e in World.Structures.Values)
					Consider(e, (FP)e.GridWidth * (FP)e.GridHeight);

				if (best != null)
				{
					u.PlasmaCastState = 1;
					u.PlasmaCastTimer = FP.Zero;
					u.PlasmaTarget = best.Position;
					u.PlasmaTargetEntityId = best.ID;
				}
			}
		}

		private void SpawnPlasmaShell(SimUnit u)
		{
			// 弹道/命中行为全部来自 PlasmaArtillery 配置表（抛射 + 长宽²伤害 + 减速）
			var cfg = RTS.Data.Configs.ConfigDatabase.GetWeapon("PlasmaArtillery");
			if (cfg == null)
				return;

			var target = u.PlasmaTargetEntityId != -1 ? World.FindSimEntity(u.PlasmaTargetEntityId) : null;

			var shell = RTS.Simulation.SimProjectileFactory.Create(
				cfg.ToProjectileSpec(),
				u,
				target,
				u.PlasmaTarget,
				true,
				(FP)cfg.Damage,
				(int)cfg.DamageType,
				FP.Zero,
				0,
				FP.Zero,
				100);

			if (shell == null)
				return;

			World.AddProjectile(shell);

			float startX = (float)u.Position.X;
			float startY = (float)u.Position.Y;
			// 视觉弹体延迟到主线程创建（模拟线程禁止碰 Godot 节点）
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				var visual = new RTS.Units.HomingProjectile
				{
					Name = "PlasmaShell",
					Speed = 480f,
					HitRadius = 24f,
					VisualSize = 70f,
					BallTint = new Color(0.25f, 0.75f, 1f),
					GlowBall = true,
					TopLevel = true
				};
				GetTree().Root.AddChild(visual);
				visual.GlobalPosition = new Vector3(startX, 60f, startY);
				visual.Setup(shell, null, 0f, DamageType.EM, 80f, 0.5f, new Color(0.25f, 0.75f, 1f), 128f);
			});
		}

		private void HandlePlasmaStrike(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;

			if (executor?.LogicEntity is not SimUnit u || u.UnitTypeId != "PlasmaCannon")
				return;

			if (u.PlasmaCooldown > FP.Zero || u.PlasmaCastState != 0)
				return;

			u.PlasmaCastState = 1;
			u.PlasmaCastTimer = FP.Zero;
			u.PlasmaTarget = netAct.DecodeTargetPos();
			u.PlasmaTargetEntityId = -1; // 手动 = 固定点
		}

		private void HandlePlasmaMode(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;

			if (executor?.LogicEntity is SimUnit u && u.UnitTypeId == "PlasmaCannon")
				u.PlasmaAutoFire = !u.PlasmaAutoFire;
		}

		// 多足控制范围：单位是否在友方控制单位（牧羊人/电浆炮）的范围内
		public static bool IsInControlRange(SimUnit unit)
		{
			var world = SimManager.Instance?.World;
			if (world == null)
				return true;

			foreach (var c in world.Units.Values)
			{
				if (c == null || c.IsDead || c.TeamID != unit.TeamID || c.ID == unit.ID)
					continue;

				var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(c.UnitTypeId);
				if (cfg == null || cfg.ControlRangeTiles <= 0)
					continue;

				FP r = (FP)(cfg.ControlRangeTiles * world.Grid.TileSize);
				if (FPVector2.DistanceSquared(c.Position, unit.Position) <= r * r)
					return true;
			}

			return false;
		}

		// 友军视野判定：目标是否被己方视野照亮（索敌用，与攻击射程共同约束）
		public static bool IsTargetVisibleToTeam(int team, IEntity target)
		{
			var world = SimManager.Instance?.World;
			if (world == null || target?.LogicEntity == null)
				return true;

			// P1-1：机器人开全图（不模拟信息差，成熟 RTS bot 测试常用做法）
			if (SimManager.Instance?.IsBotTeam(team) == true)
				return true;

			return world.IsInTeamVision(team, target.LogicEntity.Position, target.LogicEntity.Radius);
		}

		private void TickUnitModules()
		{
			foreach (var unit in World.Units.Values)
			{
				IEntity node = FindEntityById(unit.ID);

				if (node == null)
					continue;

				node.LifeModule?.Tick(_fixedDeltaFloat);
				node.CombatModule?.Tick(_fixedDeltaFloat);
				node.HarvestModule?.Tick(_fixedDeltaFloat);
				node.Brain?.LogicTick(_fixedDeltaFloat);
			}
		}

		// P1-4 掉线机器人接管：掉线玩家的单位在无目标时自动攻击最近敌人
		private void TickBotTakeover(int currentTick)
		{
			var lockstep = RTS.Network.LockstepManager.Instance;
			if (lockstep == null || lockstep.BotTakeoverPlayers.Count == 0)
				return;
			if (currentTick % 10 != 0)
				return;

			foreach (var player in RTS.World.Game.GetAllPlayers())
			{
				if (player == null ||
					!int.TryParse(player.Name.ToString().Replace("Player_", ""), out int networkPlayerId))
					continue;
				if (!lockstep.BotTakeoverPlayers.Contains(networkPlayerId))
					continue;

				foreach (var simUnit in World.Units.Values)
				{
					if (simUnit == null || simUnit.IsDead || simUnit.TeamID != player.TeamId ||
						simUnit.CombatTargetId >= 0)
						continue;
					var node = FindEntityById(simUnit.ID);
					if (node == null || node.CombatModule == null)
						continue;
					float range = node.CombatModule.GetAttackRange();
					RTS.Core.EntityExtensions.TryAutoAttack(node, Mathf.Max(320f, range));
				}
			}
		}

		// P1-1 分族对战 AI：按种族配置表（AiBuildOrderIds/AiTechOrderIds/AiUnitMixIds/资源倍率）执行宏运营。
		// 具体实现见 SimManager.BotAI.cs（纯逻辑输入，可进回放对拍验证确定性）。
		private void TickBotAI(int currentTick)
		{
			if (_botTeams.Count == 0)
				return;
			TickBotAIAll(currentTick);
		}

		private void TickDeathChecks()
		{
			foreach (var unit in World.Units.Values)
			{
				if (unit == null || unit.ID < 1000)
					continue;
				FindEntityById(unit.ID)?.LifeModule?.CheckDeath();
			}

			foreach (var struc in World.Structures.Values)
			{
				if (struc == null || struc.ID < 1000)
					continue;
				FindEntityById(struc.ID)?.LifeModule?.CheckDeath();
			}
		}

		// 安全生成挂起的单位（蓝图结果/建成赠送）：不在单位/建筑遍历中直接插入集合
		private void TickPendingSpawns()
		{
			foreach (var sim in World.Structures.Values)
			{
				if (sim == null || sim.PendingSpawnUnitIds.Count == 0)
					continue;

				var pos = sim.Position;
				int index = 0;
				bool isBlueprint = sim.StructureTypeId.StartsWith("Blueprint_");
				int blueprintSimId = sim.ID;

				foreach (string unitId in sim.PendingSpawnUnitIds)
				{
					FP offset = isBlueprint ? FP.Zero : (FP)(140 + index * 80);
					string spawnUnitId = unitId;
					int spawnTeam = sim.TeamID;
					float spawnX = (float)(pos.X + offset);
					float spawnY = (float)pos.Y;
					RTS.Core.SimEventQueue.EnqueueMain(() =>
					{
						var spawned = EntitySpawner.Instance?.SpawnEntity(spawnUnitId, spawnTeam,
							new RTS.Simulation.FPVector2((FP)spawnX, (FP)spawnY));
						// 多足：单位在“建造完成”这一刻就按蓝图位置预指派编队
						// （采集队/战斗队），不再等生成后靠距离收编
						if (spawned != null)
							WandererAssignSpawnedUnit(spawnTeam, blueprintSimId, spawned);
					});
					index++;
				}

				sim.PendingSpawnUnitIds.Clear();
			}
		}

		// 纳米地毯扩散技能：释放点必须在已有菌毯的格子上，
		// 以该格为圆心在配置时长内向四周蔓延，耗资源，CD 恢复充能（满层为止）
		private void HandleNanoSpread(NetAction netAct)
		{
			// 只要有充能就能扩散；CD 只是充能恢复计时
			if (World.CarpetSpreadCharges <= 0)
				return;

			var decodedPos = netAct.DecodeTargetPos();
			FP centerX = decodedPos.X;
			FP centerY = decodedPos.Y;
			int gridX = (int)FP.Floor(centerX / (FP)World.Grid.TileSize);
			int gridY = (int)FP.Floor(centerY / (FP)World.Grid.TileSize);

			var player = RTS.World.Game.GetPlayerByTeam(ResolveTeamIdFromNetworkPlayer(netAct.PlayerID));
			if (player?.PlayerData == null)
				return;

			// 释放点必须是己方纳米菌毯的格子
			if (World.CreepGrid.GetActiveCreep(gridX, gridY) != CreepType.NanoCreep ||
				World.CreepGrid.GetOwner(gridX, gridY) != player.TeamId)
				return;

			// 模拟线程：不能 new Godot.Collections.Dictionary
			float nanoCost = (float)(World.CarpetSpreadCost * (FP)TechEffects.GetSpreadCostMultiplier(player.PlayerData));
			if (!player.PlayerData.TryConsumeResources(ResourceType.NanoBots, nanoCost))
				return;

			if (!World.StartCarpetSpread(gridX, gridY, player.TeamId))
				return;

			World.CarpetSpreadCharges--;
			World.CarpetSpreadCooldown = World.CarpetSpreadCooldownTime * (FP)TechEffects.GetSpreadCooldownMultiplier(player.PlayerData);
		}

		// 科技研究：消耗资源开始研究（一次只研究一项）
		private void HandleNanoResearch(NetAction netAct)
		{
			string techId = netAct.ActionId.Substring("Research_".Length);
			var cfg = RTS.Data.Configs.ConfigDatabase.GetTech(techId);
			if (cfg == null)
				return;

			var player = RTS.World.Game.GetPlayerByTeam(ResolveTeamIdFromNetworkPlayer(netAct.PlayerID));
			if (player?.PlayerData == null)
				return;

			// 只有已完成的研究建筑才能发起研究
			var researchExec = GetExecutor(netAct);
			if (researchExec == null)
				return;

			if (researchExec is RTS.Units.Structure st)
			{
				if (st.CurrentState != RTS.Units.Structure.StructureState.Completed)
					return;
			}
			else if (researchExec is RTS.Units.Unit researchUnit)
			{
				var unitCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(researchUnit.UnitName);
				if (unitCfg == null || !unitCfg.IsTechBuilding)
					return;
			}
			else
			{
				return;
			}

			var pd = player.PlayerData;
			if (pd.IsResearching)
				return;

			if (pd.HasTech(techId) && !cfg.IsRepeatable)
				return;

			if (cfg.RequiresHarvester && !HasCompletedNanoHarvester(player.TeamId))
				return;

			if (!string.IsNullOrEmpty(cfg.ExclusiveGroup))
			{
				foreach (string owned in pd.ResearchedTechs)
				{
					var ownedCfg = RTS.Data.Configs.ConfigDatabase.GetTech(owned);
					if (ownedCfg != null && ownedCfg.ExclusiveGroup == cfg.ExclusiveGroup)
						return;
				}
			}

			var scaledCost = RTS.Actions.Implementation.ResearchAction.GetScaledCost(cfg, pd);

			if (!pd.TryConsumeResources(scaledCost))
				return;

			if (CheatInstantResearch)
			{
				pd.GrantTech(techId);
				TechEffects.ApplyToPlayer(player);
				GD.Print($"[Cheat] 秒点科技完成: {techId}");
				return;
			}

			pd.StartResearch(techId, cfg.ResearchTimeSeconds);
		}

		private bool HasCompletedNanoHarvester(int teamId)
		{
			foreach (var sim in World.Structures.Values)
			{
				if (sim.TeamID != teamId || sim.IsDead || sim.CurrentState != RTS.Simulation.SimStructure.StructureState.Active)
					continue;

				if (FindEntityById(sim.ID) is RTS.Units.Structure st && st.StructureName == "NanoHarvester")
					return true;
			}

			return false;
		}

		private void TickResearch()
		{
			foreach (var player in RTS.World.Game.GetAllPlayers())
			{
				if (player?.PlayerData == null)
					continue;

				string completed = player.PlayerData.AdvanceResearch((FP)_fixedDeltaFloat);

				if (completed != null)
					TechEffects.ApplyToPlayer(player);
			}
		}

		// 自动采集驱动：遍历挂载了 HarvestBehavior 的实体，把采集逻辑交给行为模块执行
		private void TickHarvesterCollection()
		{
			foreach (var sim in World.Structures.Values)
			{
				if (sim == null || sim.IsDead ||
					sim.CurrentState != RTS.Simulation.SimStructure.StructureState.Active)
					continue;

				if (FindEntityById(sim.ID) is not RTS.Units.Structure st)
					continue;

				if (st.AutoHarvestCache is { } bh)
				{
					var owner = RTS.World.Game.GetPlayerByTeam(sim.TeamID)?.PlayerData;
					if (owner != null)
						bh.Tick(World, sim, owner);
				}
			}

			foreach (var sim in World.Units.Values)
			{
				if (sim == null || sim.IsDead)
					continue;

				if (FindEntityById(sim.ID) is not RTS.Units.Unit u)
					continue;

				if (u.AutoHarvestCache is { } bh)
				{
					var owner = RTS.World.Game.GetPlayerByTeam(sim.TeamID)?.PlayerData;
					if (owner != null)
						bh.Tick(World, sim, owner);
				}
			}
		}

		// =========================================================
		// 纳米光环（数值全部来自配置表）
		// =========================================================

		private static readonly string[] NanoTurretNames =
		{
			"NanoTurret", "NanoSniper", "NanoAA", "NanoActiveTower", "NanoSmokeTower"
		};

		private void TickNanoAuras()
		{
			// 1. 活性化塔 / 烟雾塔（已完成才生效）
			foreach (var sim in World.Structures.Values)
			{
				if (sim.IsDead || sim.CurrentState != RTS.Simulation.SimStructure.StructureState.Active)
					continue;

				if (FindEntityById(sim.ID) is not RTS.Units.Structure st)
					continue;

				var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(st.StructureName);
				if (cfg == null || cfg.AuraRange <= 0)
					continue;

				ApplyAura(
					sim.Position,
					sim.TeamID,
					cfg.AuraRange,
					cfg.AuraAttackSpeedBonus,
					cfg.AuraHpRegenPerSecond,
					cfg.AuraEnemyRangeReductionTiles,
					cfg.AuraBurnDamagePerSecond
				);
			}

			// 2. 纳米巨兽：通用A（活性化）/ 通用B（电子云）永久光环
			foreach (var sim in World.Units.Values)
			{
				if (sim.IsDead)
					continue;

				if (FindEntityById(sim.ID) is not RTS.Units.Unit u || u.UnitName != "NanoBehemoth")
					continue;

				var pd = RTS.World.Game.GetPlayerByTeam(sim.TeamID)?.PlayerData;
				if (pd == null)
					continue;

				var auraA = RTS.Data.Configs.ConfigDatabase.GetTech("NanoTech_GeneralA");
				if (pd.HasTech("NanoTech_GeneralA") && auraA != null && auraA.AuraRadiusTiles > 0)
				{
					ApplyAura(
						sim.Position,
						sim.TeamID,
						auraA.AuraRadiusTiles,
						auraA.AuraAttackSpeedBonus,
						auraA.AuraHpRegenPerSecond,
						auraA.AuraEnemyRangeReductionTiles,
						auraA.AuraBurnDamagePerSecond
					);
				}

				var auraB = RTS.Data.Configs.ConfigDatabase.GetTech("NanoTech_GeneralB");
				if (pd.HasTech("NanoTech_GeneralB") && auraB != null && auraB.AuraRadiusTiles > 0)
				{
					ApplyAura(
						sim.Position,
						sim.TeamID,
						auraB.AuraRadiusTiles,
						auraB.AuraAttackSpeedBonus,
						auraB.AuraHpRegenPerSecond,
						auraB.AuraEnemyRangeReductionTiles,
						auraB.AuraBurnDamagePerSecond
					);
				}
			}
		}

		private void ApplyAura(
			FPVector2 sourcePos,
			int sourceTeam,
			int radiusTiles,
			float attackSpeedBonus,
			float hpRegenPerSecond,
			int enemyRangeReductionTiles,
			float burnDamagePerSecond)
		{
			if (radiusTiles <= 0)
				return;

			FP radiusSq = (FP)(radiusTiles * World.Grid.TileSize);
			radiusSq *= radiusSq;
			FP buffDuration = World.FixedDelta * (FP)5m; // 短暂持续，离开光环自动过期
			FP burnDmg = (FP)burnDamagePerSecond * World.FixedDelta;

			// 友方炮台：攻速加成 / 回血
			if (attackSpeedBonus > 0f || hpRegenPerSecond > 0f)
			{
				foreach (var sim in World.Structures.Values)
				{
					if (sim.IsDead || sim.TeamID != sourceTeam)
						continue;

					if (FPVector2.DistanceSquared(sourcePos, sim.Position) > radiusSq)
						continue;

					if (FindEntityById(sim.ID) is not RTS.Units.Structure st || !IsNanoTurretName(st.StructureName))
						continue;

					if (attackSpeedBonus > 0f)
						sim.Buffs.AddStatBuff("NanoActiveAura", 1, buffDuration, (FP)(1f + attackSpeedBonus), FP.Zero, FP.Zero, -1);

					if (hpRegenPerSecond > 0f)
						sim.Buffs.AddStatBuff("NanoSmokeAura", 1, buffDuration, FP.One, FP.Zero, (FP)hpRegenPerSecond, -1);
				}
			}

			// 敌方：射程-1（最低 1 格由射程查询统一限制）
			if (enemyRangeReductionTiles > 0)
			{
				FP rangePenalty = (FP)(-enemyRangeReductionTiles * World.Grid.TileSize);

				foreach (var sim in World.Units.Values)
				{
					if (sim.IsDead || sim.TeamID == sourceTeam || sim.TeamID == -1)
						continue;

					if (FPVector2.DistanceSquared(sourcePos, sim.Position) > radiusSq)
						continue;

					sim.Buffs.AddStatBuff("NanoSmokeDebuff", 1, buffDuration, FP.One, rangePenalty, FP.Zero, -1);
				}

				foreach (var sim in World.Structures.Values)
				{
					if (sim.IsDead || sim.TeamID == sourceTeam || sim.TeamID == -1)
						continue;

					if (FPVector2.DistanceSquared(sourcePos, sim.Position) > radiusSq)
						continue;

					sim.Buffs.AddStatBuff("NanoSmokeDebuff", 1, buffDuration, FP.One, rangePenalty, FP.Zero, -1);
				}
			}

			// 地毯灼烧：站在纳米地毯上的敌人
			if (burnDamagePerSecond > 0f && burnDmg > FP.Zero)
			{
				foreach (var sim in World.Units.Values)
				{
					if (sim.IsDead || sim.TeamID == sourceTeam || sim.TeamID == -1)
						continue;

					if (FPVector2.DistanceSquared(sourcePos, sim.Position) > radiusSq)
						continue;

					var g = World.Grid.WorldToGrid(sim.Position);
					if (World.CreepGrid.GetActiveCreep(g.X, g.Y) != CreepType.NanoCreep)
						continue;

					sim.TakeDamage(burnDmg, (int)DamageType.Thermal);
				}

				foreach (var sim in World.Structures.Values)
				{
					if (sim.IsDead || sim.TeamID == sourceTeam || sim.TeamID == -1)
						continue;

					if (FPVector2.DistanceSquared(sourcePos, sim.Position) > radiusSq)
						continue;

					var g = World.Grid.WorldToGrid(sim.Position);
					if (World.CreepGrid.GetActiveCreep(g.X, g.Y) != CreepType.NanoCreep)
						continue;

					sim.TakeDamage(burnDmg, (int)DamageType.Thermal);
				}
			}
		}

		private static bool IsNanoTurretName(string structureName)
		{
			foreach (string name in NanoTurretNames)
			{
				if (name == structureName)
					return true;
			}

			return false;
		}

		// 地毯经济：配置表定义每多少格/秒产 1 纳米机器人（虚空资源转化）
		private void TickNanoEconomy()
		{
			foreach (var player in RTS.World.Game.GetAllPlayers())
			{
				if (player?.Race?.RaceName != "Nano")
					continue;

				// 直接用缓存计数：原实现每 tick 拷贝整张菌毯表（大后期数千格），
				// 单 tick 可飙到 30~50ms，是 4AI 画面周期性卡顿的主要来源之一。
				int carpetCells = World.CreepGrid.NanoCreepCount;

				// count / cellsPerResource 每秒 → 每 tick 再 /20（20Hz）
				FP perSecond = (FP)carpetCells / (FP)World.CarpetIncomeCellsPerResource *
					(FP)TechEffects.GetCreepIncomeMultiplier(player.PlayerData);
				FP income = perSecond / (FP)20m;

				if (income > FP.Zero)
					player.PlayerData.AddResource(
						ResourceType.NanoBots,
						income * GetBotResourceMultiplier(player.TeamId));
			}
		}

		// 植物：菌毯产出木材（每 CarpetIncomeCellsPerResource 格/秒 1 木）+ 菌毯回血/防御 + 花田能量 + 森林蔓延冷却 + 大地核心
		private void TickPlantFields()
		{
			// 菌毯经济：植物菌毯每 N 格每秒产 1 木材（N = RaceConfig.CarpetIncomeCellsPerResource，当前 Plant.tres = 300）
			foreach (var player in RTS.World.Game.GetAllPlayers())
			{
				if (player?.Race?.RaceName != "Plant" || player.PlayerData == null)
					continue;
				// 植物菌毯经济：读种族配置（Plant.tres 当前为 300 格/秒 1 木），
				// 不复用 SimWorld 全局的纳米 20 格/资源
				var plantRaceCfg = player?.Race != null
					? RTS.Data.Configs.ConfigDatabase.GetRace(player.Race.RaceName)
					: null;
				int plantIncomeCellsPerResource =
					plantRaceCfg != null && plantRaceCfg.CarpetIncomeCellsPerResource > 0
						? plantRaceCfg.CarpetIncomeCellsPerResource
						: 400;
				FP perSecond = (FP)World.CreepGrid.PlantCreepCount /
					(FP)plantIncomeCellsPerResource *
					(FP)TechEffects.GetCreepIncomeMultiplier(player.PlayerData);
				FP income = perSecond / (FP)20m;
				if (income > FP.Zero)
					player.PlayerData.AddResource(ResourceType.Wood,
						income * GetBotResourceMultiplier(player.TeamId));
			}

			// 植物单位：站在己方菌毯上每秒回 2 血、护甲+2、受伤 -20%（铁木甲壳升级到 -40%/-60%）
			foreach (var unit in World.Units.Values)
			{
				if (unit == null || unit.IsDead || !unit.IsPlantUnit)
					continue;
				var g = World.Grid.WorldToGrid(unit.Position);
				bool onCreep = World.CreepGrid.GetActiveCreep(g.X, g.Y) == CreepType.PlantCreep &&
					World.CreepGrid.GetOwner(g.X, g.Y) == unit.TeamID;
				if (onCreep)
				{
					if (!unit.Buffs.HasBuff("PlantCreepDefense"))
					{
						var player = RTS.World.Game.GetPlayerByTeam(unit.TeamID);
						FP reduction = (FP)0.8m;
						if (player?.PlayerData != null)
						{
							if (player.PlayerData.HasTech("PlantTech_Ironwood2"))
								reduction = (FP)0.4m;
							else if (player.PlayerData.HasTech("PlantTech_Ironwood1"))
								reduction = (FP)0.6m;
						}
						unit.Buffs.AddBuff("PlantCreepDefense", 1, FP.Zero, FP.One, reduction,
							(FP)2, FP.One, (FP)2m, FP.Zero, -1);
					}
				}
				else
				{
					unit.Buffs.RemoveBuff("PlantCreepDefense");
				}
			}

			foreach (var s in World.Structures.Values)
			{
				if (s == null || s.IsDead)
					continue;
				if (s.StructureTypeId == "PlantFlowerField" && s.TeamID > 0)
				{
					var player = RTS.World.Game.GetPlayerByTeam(s.TeamID);
					FP regen = FP.One;
					FP max = (FP)100m;
					if (player?.PlayerData != null && player.PlayerData.HasTech("PlantTech_Sunflower"))
					{
						max = (FP)200m;
						regen = player.PlayerData.HasTech("PlantTech_Fission") ? (FP)2m : (FP)1.3m;
					}
					s.FlowerEnergy += regen * World.FixedDelta;
					if (s.FlowerEnergy > max)
						s.FlowerEnergy = max;
					s.FlowerEnergyMax = max;
				}
				if (s.ForestSpreadCooldown > FP.Zero)
					s.ForestSpreadCooldown -= World.FixedDelta;

				// 大地核心：完工后为周围 6 格内资源点总量 +30%（每个资源点只增幅一次）
				if (s.StructureTypeId == "PlantEarthCore" && s.TeamID > 0 &&
					s.CurrentState == SimStructure.StructureState.Active &&
					!World.AppliedEarthCores.Contains(s.ID))
				{
					World.AppliedEarthCores.Add(s.ID);
					foreach (var node in World.Structures.Values)
					{
						if (node == null || node.IsDead || node.TeamID > 0 || node.ResourceAmount <= FP.Zero)
							continue;
						if (!SimWorld.IsResourceNodeType(node.StructureTypeId))
							continue;
						if (FPVector2.DistanceSquared(node.Position, s.Position) > (FP)(6 * 64) * (FP)(6 * 64))
							continue;
						if (World.EarthCoreBoostedNodes.Contains(node.ID))
							continue;
						World.EarthCoreBoostedNodes.Add(node.ID);
						node.ResourceAmount = node.ResourceAmount * (FP)1.3m;
					}
				}
			}
		}

		// 植物：森林蔓延面板技能入口（模拟线程）
		private void HandlePlantForestNode(NetAction netAct)
		{
			int team = ResolveTeamIdFromNetworkPlayer(netAct.PlayerID);
			var player = RTS.World.Game.GetPlayerByTeam(team);
			if (player?.PlayerData == null || player.Race?.RaceName != "Plant")
				return;
			var target = netAct.DecodeTargetPos();
			TrySpawnForestNode(team, target);
		}

		// 森林蔓延（建筑技能）：由生命树/分支树/森林节点释放，
		// 施法范围以建筑位置为圆心（12 格），消耗 50 木生成森林节点。
		private void HandlePlantForestSpread(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor?.LogicEntity is not SimStructure tree ||
				tree.CurrentState != SimStructure.StructureState.Active)
				return;
			if (tree.StructureTypeId != "PlantLifeTree" &&
				tree.StructureTypeId != "PlantBranchTree" &&
				tree.StructureTypeId != "PlantForestNode")
				return;
			if (tree.ForestSpreadCooldown > FP.Zero)
				return;
			if (tree.StructureTypeId == "PlantForestNode" && tree.ForestSpreadUsed)
				return;

			var player = RTS.World.Game.GetPlayerByTeam(tree.TeamID);
			if (player?.PlayerData == null || !player.PlayerData.TryConsumeResources(ResourceType.Wood, 50f))
				return;

			var target = netAct.DecodeTargetPos();
			// 范围由建筑所在位置与建筑类型共同决定（生命树/分支树/森林节点各自不同）
			int spreadRange = RTS.Data.Configs.ConfigDatabase.GetStructure(tree.StructureTypeId)?.ForestSpreadRangeTiles ?? 12;
			FP spreadRangeFp = (FP)(spreadRange * 64);
			if (FPVector2.DistanceSquared(tree.Position, target) > spreadRangeFp * spreadRangeFp)
			{
				player.PlayerData.AddResource(ResourceType.Wood, 50f);
				return;
			}

			var grid = RTS.World.MapGrid.Instance;
			if (grid == null)
			{
				player.PlayerData.AddResource(ResourceType.Wood, 50f);
				return;
			}
			Vector2I center = grid.WorldToGrid(new Godot.Vector2((float)target.X, (float)target.Y));
			Vector2I topLeft = grid.GetTopLeftFromCenter(center, 1);
			if (!grid.IsPositionAvailableForBlueprint(topLeft, 1, CreepType.Any, 0))
			{
				player.PlayerData.AddResource(ResourceType.Wood, 50f);
				return;
			}
			Vector2 aligned = grid.GetAlignedWorldPos(topLeft, 1);

			string spawnName = "PlantForestNode";
			int spawnTeam = tree.TeamID;
			float spawnX = aligned.X;
			float spawnY = aligned.Y;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				var ent = EntitySpawner.Instance?.SpawnEntity(
					spawnName, spawnTeam, new FPVector2((FP)spawnX, (FP)spawnY));
				if (ent is RTS.Units.Structure structure)
				{
					structure.InitAsBlueprint(new Godot.Collections.Dictionary<ResourceType, float>());
					structure.PromoteFromBlueprint();
					// 森林节点出生自带 20 秒技能 CD，不能落地立刻连锁蔓延
					if (structure.SimStructureData != null)
						structure.SimStructureData.ForestSpreadCooldown = (FP)20m;
				}
			});
			tree.ForestSpreadCooldown = (FP)20m;
			if (tree.StructureTypeId == "PlantForestNode")
				tree.ForestSpreadUsed = true;
		}

			// 找范围内可用的森林来源（生命树/分支/森林节点冷却就绪；节点未用过），
			// 扣 50 木并在目标点生成森林节点（玩家与 AI 共用，确定性）
		private bool TrySpawnForestNode(int team, FPVector2 target)
		{
			var player = RTS.World.Game.GetPlayerByTeam(team);
			if (player?.PlayerData == null)
				return false;

			SimStructure source = null;
			foreach (var s in World.Structures.Values)
			{
				if (s == null || s.IsDead || s.TeamID != team ||
					s.CurrentState != SimStructure.StructureState.Active)
					continue;
				if (s.StructureTypeId != "PlantLifeTree" && s.StructureTypeId != "PlantBranchTree" &&
					s.StructureTypeId != "PlantForestNode")
					continue;
				if (s.StructureTypeId == "PlantForestNode" && s.ForestSpreadUsed)
					continue;
				if (s.ForestSpreadCooldown > FP.Zero)
					continue;
				// 范围按建筑类型各自配置（生命树 12 / 分支树 8 / 森林节点 5）
				int srcRange = RTS.Data.Configs.ConfigDatabase.GetStructure(s.StructureTypeId)?.ForestSpreadRangeTiles ?? 12;
				FP srcRangeFp = (FP)(srcRange * 64);
				if (FPVector2.DistanceSquared(s.Position, target) > srcRangeFp * srcRangeFp)
					continue;
				source = s;
				break;
			}
			if (source == null)
				return false;

			if (!player.PlayerData.TryConsumeResources(ResourceType.Wood, 50f))
				return false;

			var grid = RTS.World.MapGrid.Instance;
			if (grid == null)
				return false;
			Vector2I center = grid.WorldToGrid(new Godot.Vector2((float)target.X, (float)target.Y));
			Vector2I topLeft = grid.GetTopLeftFromCenter(center, 1);
			if (!grid.IsPositionAvailableForBlueprint(topLeft, 1, CreepType.Any, 0))
			{
				player.PlayerData.AddResource(ResourceType.Wood, 50f);
				return false;
			}
			Vector2 aligned = grid.GetAlignedWorldPos(topLeft, 1);

			string spawnName = "PlantForestNode";
			int spawnTeam = team;
			float spawnX = aligned.X;
			float spawnY = aligned.Y;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				var ent = EntitySpawner.Instance?.SpawnEntity(
					spawnName, spawnTeam, new FPVector2((FP)spawnX, (FP)spawnY));
				if (ent is RTS.Units.Structure structure)
				{
					structure.InitAsBlueprint(new Godot.Collections.Dictionary<ResourceType, float>());
					structure.PromoteFromBlueprint();
					// 森林节点出生自带 20 秒技能 CD
					if (structure.SimStructureData != null)
						structure.SimStructureData.ForestSpreadCooldown = (FP)20m;
				}
			});
			source.ForestSpreadCooldown = (FP)20m;
			if (source.StructureTypeId == "PlantForestNode")
				source.ForestSpreadUsed = true;
			return true;
		}

		// 洞穴族：虫洞传送——站在虫洞内的单位传送到同队另一个虫洞
		private void TickCaveFields()
		{
			foreach (var unit in World.Units.Values)
			{
				if (unit == null || unit.IsDead)
					continue;
				var g = World.Grid.WorldToGrid(unit.Position);
				SimStructure hole = null;
				foreach (var s in World.Structures.Values)
				{
					if (s == null || s.IsDead || s.TeamID != unit.TeamID ||
						s.StructureTypeId != "CaveWormhole" ||
						s.CurrentState != SimStructure.StructureState.Active)
						continue;
					if (g.X >= s.GridPosition.X && g.X < s.GridPosition.X + s.GridWidth &&
						g.Y >= s.GridPosition.Y && g.Y < s.GridPosition.Y + s.GridHeight)
					{
						hole = s;
						break;
					}
				}
				if (hole == null)
					continue;
				SimStructure dest = null;
				foreach (var s in World.Structures.Values)
				{
					if (s == null || s.IsDead || s.TeamID != unit.TeamID ||
						s.StructureTypeId != "CaveWormhole" || s.ID == hole.ID ||
						s.CurrentState != SimStructure.StructureState.Active)
						continue;
					if (dest == null || s.ID < dest.ID)
						dest = s;
				}
				if (dest == null)
					continue;
				unit.Position = new FPVector2(
					(FP)(dest.GridPosition.X * World.Grid.TileSize + dest.GridWidth * 32),
					(FP)(dest.GridPosition.Y * World.Grid.TileSize + dest.GridHeight * 32));
				unit.HasTarget = false;
				unit.Velocity = FPVector2.Zero;
				unit.Path?.Clear();
				unit.PathPending = false;
			}

			// 地动仪被动雷达（每 2 秒点亮自身半径视野，纯表现层）
			// 地壳裂解器地震波充能恢复 / 虫洞核心制造虫洞冷却
			foreach (var s in World.Structures.Values)
			{
				if (s == null || s.IsDead || s.CurrentState != SimStructure.StructureState.Active)
					continue;
				var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(s.StructureTypeId);
				if (cfg == null)
					continue;

				if (s.StructureTypeId == "CaveSeismograph")
				{
					s.RadarPulseTimer -= World.FixedDelta;
					if (s.RadarPulseTimer <= FP.Zero)
					{
						s.RadarPulseTimer = (FP)2m;
						if (Main.Instance?.LocalPlayerID == s.TeamID && RTS.World.FogOfWar.Instance != null)
						{
							float rx = (float)s.Position.X;
							float ry = (float)s.Position.Y;
							float rr = cfg.WaveRadiusTiles * World.Grid.TileSize;
							RTS.Core.SimEventQueue.EnqueueMain(() =>
								RTS.World.FogOfWar.Instance?.AddTemporaryReveal(new Vector2(rx, ry), rr, 1.5f));
						}
					}
				}

				if (s.StructureTypeId == "CaveCrustCracker" && s.SeismicCharges < cfg.SeismicMaxCharges)
				{
					s.SeismicChargeTimer += World.FixedDelta;
					if (s.SeismicChargeTimer >= (FP)cfg.SeismicChargeSeconds)
					{
						s.SeismicChargeTimer = FP.Zero;
						s.SeismicCharges++;
					}
				}

				if (s.StructureTypeId == "CaveWormholeCore" && s.WormholeCooldown > FP.Zero)
					s.WormholeCooldown -= World.FixedDelta;
			}

			// 沙虫：一次遍历处理火车式跟随 + 节段减速（头按存活节数 -15%/节）
			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead)
					continue;

				if (u.UnitTypeId == "CaveSandworm")
				{
					int dead = 5 - CountAliveSegments(u);
					if (dead <= 0)
					{
						u.Buffs.RemoveBuff("CaveSandwormCripple");
					}
					else if (u.Buffs.HasBuff("CaveSandwormCripple"))
					{
						var buff = u.Buffs.GetBuff("CaveSandwormCripple");
						if (buff != null && buff.Stacks != dead)
							buff.Stacks = dead;
					}
					else
					{
						u.Buffs.AddBuff("CaveSandwormCripple", dead, FP.Zero,
							FP.One, FP.One, FP.Zero, (FP)0.85m, FP.Zero, FP.Zero, -1);
					}
				}

				if (u.SegmentLeaderId >= 0)
				{
					var leader = World.FindSimEntity(u.SegmentLeaderId);
					if (leader == null || leader.IsDead)
					{
						// 头部死亡：身体瘫痪留在原地（仍可被攻击，全部击杀才算整条死亡）
						u.Velocity = FPVector2.Zero;
						u.HasTarget = false;
						u.Path?.Clear();
						u.PathPending = false;
						continue;
					}

					FP spacing = (FP)96m;
					FP dist = FP.Sqrt(FPVector2.DistanceSquared(u.Position, leader.Position));
					if (dist <= spacing)
					{
						u.Velocity = FPVector2.Zero;
						u.HasTarget = false;
						continue;
					}

					FPVector2 dir = (leader.Position - u.Position) / dist;
					FP step = u.MaxSpeed * World.FixedDelta;
					FP remain = dist - spacing;
					if (step > remain)
						step = remain;
					u.Position += dir * step;
					u.Velocity = FPVector2.Zero;
					u.HasTarget = false;
					u.Path?.Clear();
					u.PathPending = false;
				}
			}
		}

		// 沙虫整条存活节数（含头；group = 头部实体 ID）
		public static int CountAliveSegments(SimUnit head)
		{
			if (head == null || head.World == null || head.ID <= 0)
				return 0;
			int alive = 0;
			foreach (var u in head.World.Units.Values)
			{
				if (u == null || u.IsDead || u.SegmentGroupId != head.ID)
					continue;
				alive++;
			}
			return alive;
		}

		// 洞穴：建筑被摧毁后留下地下残骸（血量 = 原建筑 10 倍，可快速重建）
		private void TickCaveWreckages()
		{
			foreach (var s in World.Structures.Values)
			{
				if (s == null || !s.IsDead || s.CurrentState != SimStructure.StructureState.Active ||
					s.StructureTypeId == "CaveWreckage")
					continue;

				var player = RTS.World.Game.GetPlayerByTeam(s.TeamID);
				if (player?.Race?.RaceName != "Cave")
					continue;

				string origId = s.StructureTypeId;
				int origW = s.GridWidth;
				int origH = s.GridHeight;
				int origTopLeftX = s.GridPosition.X;
				int origTopLeftY = s.GridPosition.Y;
				var origCfg = RTS.Data.Configs.ConfigDatabase.GetStructure(origId);
				int team = s.TeamID;
				float x = (float)s.Position.X;
				float y = (float)s.Position.Y;
				RTS.Core.SimEventQueue.EnqueueMain(() =>
				{
					var ent = EntitySpawner.Instance?.SpawnEntity(
						"CaveWreckage", team, new FPVector2((FP)x, (FP)y));
					if (ent is not RTS.Units.Structure wreck)
						return;

					var sim = wreck.SimStructureData;
					float hp = (origCfg?.MaxHp ?? 100f) * 10f;
					if (sim != null)
					{
						// 残骸中心对齐原建筑中心（覆盖 1x1 对齐产生的半格偏移）
						sim.Position = new FPVector2((FP)x, (FP)y);
						// 占位对齐原建筑：残骸按原长宽在正确区域阻挡，避免偏移错位
						sim.GridPosition = new RTS.Simulation.SimVector2I(origTopLeftX, origTopLeftY);
						sim.GridWidth = origW;
						sim.GridHeight = origH;
						sim.GridSize = origW;
						sim.RebuildTargetId = origId;
						sim.MaxHp = (FP)hp;
						sim.Hp = sim.MaxHp;
					}
					wreck.GlobalPosition = new Vector3(x, 0f, y);
					wreck.GridSize = origW;
					if (wreck.LifeModule != null)
						wreck.LifeModule.MaxHp = hp;

					// 残骸不阻挡也不占建筑位：释放原建筑区域的地图占用（幂等，原址可直接重建）
					RTS.World.MapGrid.Instance?.UnregisterStructure(
						new Vector2I(origTopLeftX, origTopLeftY), origW);
				});
			}
		}

		// 地动仪 - 共振波：对选中敌方单位同类型全体受伤 +50%（持续 10s）
		private void HandleResonanceWave(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;
			if (executor?.LogicEntity is not SimStructure st ||
				st.StructureTypeId != "CaveSeismograph" ||
				st.CurrentState != SimStructure.StructureState.Active)
				return;
			if (FindEntityById(netAct.TargetEntityID)?.LogicEntity is not SimUnit victim ||
				victim.TeamID <= 0 || victim.TeamID == st.TeamID)
				return;

			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure("CaveSeismograph");
			if (cfg == null)
				return;

			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead || u.TeamID != victim.TeamID || u.UnitTypeId != victim.UnitTypeId)
					continue;
				u.Buffs.AddBuff("CaveResonance", 1, (FP)cfg.WaveDurationSeconds,
					FP.One, (FP)cfg.WaveMultiplier, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);
			}
		}

		// 地壳裂解器 - 地震波：目标点大范围敌方地面单位减速（消耗 1 次充能）
		private void HandleSeismicWave(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;
			if (executor?.LogicEntity is not SimStructure st ||
				st.StructureTypeId != "CaveCrustCracker" ||
				st.CurrentState != SimStructure.StructureState.Active ||
				st.SeismicCharges <= 0)
				return;

			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure("CaveCrustCracker");
			if (cfg == null)
				return;

			var center = netAct.DecodeTargetPos();
			FP radius = (FP)(cfg.WaveRadiusTiles * 64);
			FP radiusSq = radius * radius;
			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead || u.TeamID == st.TeamID || u.TeamID <= 0 || u.IsAir)
					continue;
				if (!FPVector2.IsWithinRangeSq(u.Position, center, radiusSq))
					continue;
				u.Buffs.AddBuff("CaveSeismicSlow", 1, (FP)cfg.WaveDurationSeconds,
					FP.One, FP.One, FP.Zero, FP.One - (FP)cfg.WaveMultiplier, FP.Zero, FP.Zero, -1);
			}
			st.SeismicCharges--;
		}

		// 虫洞核心 - 制造虫洞：目标点生成虫洞（扣费 + 30s 冷却）
		private void HandleCaveWormhole(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;
			if (executor?.LogicEntity is not SimStructure core ||
				core.StructureTypeId != "CaveWormholeCore" ||
				core.CurrentState != SimStructure.StructureState.Active ||
				core.WormholeCooldown > FP.Zero)
				return;

			var player = RTS.World.Game.GetPlayerByTeam(core.TeamID);
			var holeCfg = RTS.Data.Configs.ConfigDatabase.GetStructure("CaveWormhole");
			if (player?.PlayerData == null || holeCfg == null || RTS.World.MapGrid.Instance == null)
				return;

			var target = netAct.DecodeTargetPos();
			var grid = RTS.World.MapGrid.Instance;
			Vector2I center = grid.WorldToGrid(new Godot.Vector2((float)target.X, (float)target.Y));
			Vector2I topLeft = grid.GetTopLeftFromCenter(center, holeCfg.GridWidth);
			if (!grid.IsPositionAvailableForBlueprint(topLeft, holeCfg.GridWidth, holeCfg.RequiredCreepType, core.TeamID))
				return;
			if (!player.PlayerData.TryConsumeResources(holeCfg.Costs))
				return;

			Vector2 aligned = grid.GetAlignedWorldPos(topLeft, holeCfg.GridWidth);
			int spawnTeam = core.TeamID;
			float spawnX = aligned.X;
			float spawnY = aligned.Y;
			var costs = holeCfg.Costs;
			core.WormholeCooldown = (FP)holeCfg.WormholeCreateCooldownSeconds;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				var ent = EntitySpawner.Instance?.SpawnEntity(
					"CaveWormhole", spawnTeam, new FPVector2((FP)spawnX, (FP)spawnY));
				if (ent is RTS.Units.Structure structure)
				{
					structure.InitAsBlueprint(costs);
					structure.PromoteFromBlueprint();
					structure.AdvanceProgress(1f);
					structure.LifeModule?.SetHealthRaw(structure.LifeModule.MaxHp);
				}
			});
		}

		// 花田 - 全能性：消耗 10 点花田能量，在任意己方菌毯处建造叶犬
		//（需要科技 PlantTech_Omni；蓝图缓慢生长，生命树范围内建造速度翻倍）
		private void HandlePlantOmni(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;
			if (executor?.LogicEntity is not SimStructure field ||
				field.StructureTypeId != "PlantFlowerField" ||
				field.CurrentState != SimStructure.StructureState.Active ||
				field.FlowerEnergy < (FP)10m ||
				RTS.World.MapGrid.Instance == null)
				return;

			var player = RTS.World.Game.GetPlayerByTeam(field.TeamID);
			if (player?.PlayerData == null || !player.PlayerData.HasTech("PlantTech_Omni"))
				return;

			var target = netAct.DecodeTargetPos();
			var g = World.Grid.WorldToGrid(target);
			if (World.CreepGrid.GetActiveCreep(g.X, g.Y) != CreepType.PlantCreep ||
				World.CreepGrid.GetOwner(g.X, g.Y) != field.TeamID)
				return;

			var grid = RTS.World.MapGrid.Instance;
			Vector2I topLeft = grid.WorldToGrid(new Godot.Vector2((float)target.X, (float)target.Y));
			if (!grid.IsPositionAvailableForBlueprint(topLeft, 1, CreepType.PlantCreep, field.TeamID))
				return;

			Vector2 aligned = grid.GetAlignedWorldPos(topLeft, 1);
			field.FlowerEnergy -= (FP)10m;
			int spawnTeam = field.TeamID;
			float spawnX = aligned.X;
			float spawnY = aligned.Y;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				var ent = EntitySpawner.Instance?.SpawnEntity(
					"Blueprint_PlantLeafDog", spawnTeam, new FPVector2((FP)spawnX, (FP)spawnY));
				if (ent is RTS.Units.Structure structure)
				{
					structure.InitAsBlueprint(new Godot.Collections.Dictionary<ResourceType, float>());
					structure.PromoteFromBlueprint();
				}
			});
		}

		// 沙虫 - 潜地：切换地下状态（受伤减半、无法攻击）
		private void HandleBurrow(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;
			if (executor?.LogicEntity is SimUnit u &&
				u.UnitTypeId == "CaveSandworm" && !u.IsDead)
				u.IsBurrowed = !u.IsBurrowed;
		}

		// 沙虫 - 吞噬：锁定目标冲刺，命中后对目标及周围敌人造成伤害，
		// 每击杀一个敌人恢复 100 生命
		private void HandleDevour(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;
			if (executor?.LogicEntity is not SimUnit worm ||
				worm.UnitTypeId != "CaveSandworm" || worm.IsDead || worm.DevourTimer > FP.Zero)
				return;
			if (FindEntityById(netAct.TargetEntityID)?.LogicEntity is not SimUnit victim ||
				victim.TeamID == worm.TeamID || victim.TeamID <= 0)
				return;
			// 吞噬范围：以沙虫为圆心 12 格（与技能范围圈一致，冲刺 10 格/秒 × 1.2 秒）
			if (FPVector2.DistanceSquared(worm.Position, victim.Position) >
				(FP)(12 * 64) * (FP)(12 * 64))
				return;

			worm.DevourTargetId = victim.ID;
			worm.DevourTimer = (FP)1.2m;
			worm.HasTarget = false;
			worm.Velocity = FPVector2.Zero;
		}

		// 沙虫吞噬冲刺：冲向目标，命中结算范围伤害 + 击杀回血
		private void TickDevour()
		{
			FP dashSpeed = (FP)(World.Grid.TileSize * 10m);
			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead || u.DevourTimer <= FP.Zero)
					continue;

				var target = u.DevourTargetId >= 0 ? World.FindSimEntity(u.DevourTargetId) : null;
				if (target == null || target.IsDead)
				{
					u.DevourTimer = FP.Zero;
					u.DevourTargetId = -1;
					continue;
				}

				u.DevourTimer -= World.FixedDelta;
				u.HasTarget = false;
				u.Velocity = FPVector2.Zero;

				FP dist = FP.Sqrt(FPVector2.DistanceSquared(u.Position, target.Position));
				FP step = dashSpeed * World.FixedDelta;
				if (dist > step)
				{
					FPVector2 dir = (target.Position - u.Position).Normalized();
					u.Position += dir * step;
					continue;
				}

				// 命中：目标点周围 3 格内所有敌人受到 300 伤害
				u.Position = target.Position;
				FP radius = (FP)192m;
				FP radiusSq = radius * radius;
				int kills = 0;
				foreach (var e in World.Units.Values)
				{
					if (e == null || e.IsDead || e.TeamID == u.TeamID || e.TeamID <= 0)
						continue;
					if (!FPVector2.IsWithinRangeSq(e.Position, target.Position, radiusSq))
						continue;
					e.TakeDamage((FP)300m, (int)DamageType.Kinetic, FP.Zero, 0, u);
					if (e.IsDead)
						kills++;
				}
				if (kills > 0)
				{
					// 每消灭一个敌人，所有身体（五节）各恢复 100 生命
					FP heal = (FP)(100 * kills);
					foreach (var seg in World.Units.Values)
					{
						if (seg == null || seg.IsDead || seg.SegmentGroupId != u.ID)
							continue;
						seg.Hp += heal;
						if (seg.Hp > seg.MaxHp)
							seg.Hp = seg.MaxHp;
					}
				}
				u.DevourTimer = FP.Zero;
				u.DevourTargetId = -1;
			}
		}

		// 虫洞核心：已建成则启用“视野内自由建造”（同一时间只能建造一个洞穴建筑）
		private bool HasActiveWormholeCore(int teamId)
		{
			foreach (var s in World.Structures.Values)
			{
				if (s == null || s.IsDead || s.TeamID != teamId ||
					s.StructureTypeId != "CaveWormholeCore" ||
					s.CurrentState != SimStructure.StructureState.Active)
					continue;
				return true;
			}
			return false;
		}

		private bool HasActiveCaveConstruction(int teamId)
		{
			foreach (var s in World.Structures.Values)
			{
				if (s == null || s.IsDead || s.TeamID != teamId ||
					s.StructureTypeId == "CaveWreckage")
					continue;
				if (s.CurrentState == SimStructure.StructureState.Constructing)
					return true;
			}
			return false;
		}

		// 面板凭空建造公共内核（纳米/植物各自有独立指令前缀与入口，
		// 这里只共享确定性校验/生成逻辑，不共享种族命名）
		private void HandlePanelBuildCore(string structName, string logTag, NetAction netAct, string expectedRace)
		{
			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(structName);

			if (cfg == null)
			{
				GD.PrintErr($"[{logTag}] 失败:配置缺失 {structName}");
				return;
			}

			var player = RTS.World.Game.GetPlayerByTeam(ResolveTeamIdFromNetworkPlayer(netAct.PlayerID));
			if (player?.PlayerData == null || RTS.World.MapGrid.Instance == null)
			{
				GD.PrintErr($"[{logTag}] 失败:玩家/地图不可用 {structName}");
				return;
			}

			// 跨族指令防护：纳米只能用 NanoBuild_，植物只能用 PlantBuild_
			if (player.Race?.RaceName != expectedRace)
			{
				GD.PrintErr($"[{logTag}] 失败:种族不符 {structName} race={player.Race?.RaceName} need={expectedRace}");
				return;
			}

			// 唯一建筑：同一玩家只能建一座
			if (cfg.IsUnique && PlayerOwnsStructure(player.TeamId, structName))
			{
				GD.PrintErr($"[{logTag}] 失败:唯一建筑已存在 {structName}");
				return;
			}

			// 科技门槛：未研究的建筑不允许凭空建造
			foreach (string req in cfg.RequiredTechIds)
			{
				if (!player.PlayerData.HasTech(req))
				{
					GD.PrintErr($"[{logTag}] 失败:缺科技 {req} ({structName})");
					return;
				}
			}

			if (!player.PlayerData.TryConsumeResources(cfg.Costs))
			{
				GD.PrintErr($"[{logTag}] 失败:资源不足 {structName}");
				return;
			}

			var decodedPos = netAct.DecodeTargetPos();
			FP xFP = decodedPos.X;
			FP yFP = decodedPos.Y;
			var grid = RTS.World.MapGrid.Instance;
			Vector2I center = grid.WorldToGrid(new Godot.Vector2((float)xFP, (float)yFP));
			Vector2I topLeft = grid.GetTopLeftFromCenter(center, cfg.GridWidth);

			if (!grid.IsPositionAvailableForBlueprint(topLeft, cfg.GridWidth, cfg.RequiredCreepType, player.TeamId))
			{
				// 放置失败：退还资源
				player.PlayerData.AddResources(cfg.Costs);
				GD.PrintErr($"[{logTag}] 失败:放置校验 {structName} @({topLeft.X},{topLeft.Y}) 毯格={RTS.Core.SimManager.Instance?.World?.CreepGrid?.GetActiveCreep(topLeft.X, topLeft.Y)} 属主={RTS.Core.SimManager.Instance?.World?.CreepGrid?.GetOwner(topLeft.X, topLeft.Y)} 需属主={player.TeamId}");
				return;
			}

			Vector2 aligned = grid.GetAlignedWorldPos(topLeft, cfg.GridWidth);
			// 生成（含视觉节点）延迟到主线程；资源/落点判定已在上面用确定性数据完成
			string spawnStruct = structName;
			int spawnTeam = player.TeamId;
			float spawnX = aligned.X;
			float spawnY = aligned.Y;
			var spawnCosts = cfg.Costs;
			bool instantBuild = CheatInstantBuild;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				var ent = EntitySpawner.Instance?.SpawnEntity(
					spawnStruct,
					spawnTeam,
					new RTS.Simulation.FPVector2((FP)spawnX, (FP)spawnY)
				);

				if (ent is RTS.Units.Structure structure)
				{
					structure.InitAsBlueprint(spawnCosts);
					structure.PromoteFromBlueprint();

					if (instantBuild)
					{
						structure.AdvanceProgress(1f);
						structure.LifeModule?.SetHealthRaw(structure.LifeModule.MaxHp);
					}
				}
			});
		}

		// 纳米虫凭空建造：NanoBuild_<结构名>
		private void HandleNanoBuild(NetAction netAct)
		{
			HandlePanelBuildCore(netAct.ActionId.Substring("NanoBuild_".Length), "NanoBuild", netAct, "Nano");
		}

		// 植物面板建造：PlantBuild_<结构名>（与纳米完全独立，只共用确定性内核）
		private void HandlePlantBuild(NetAction netAct)
		{
			HandlePanelBuildCore(netAct.ActionId.Substring("PlantBuild_".Length), "PlantBuild", netAct, "Plant");
		}

		private void DispatchActions(List<NetAction> actions)
		{
			int dispatchTick = LockstepManager.Instance?.CurrentTick ?? 0;

			foreach (var netAct in actions)
			{
				if (string.IsNullOrEmpty(netAct.ActionId))
					continue;

				// 玩家指令统计：在这里统一记录，而不是散落在每个 handle 里。
				// 好处是"教程能判定的操作"永远等于"玩家能发的指令"，
				// 新增一个指令不需要再去教程侧补一次记录。
				// 注意：只记录真实玩家（PlayerID > 0）；系统包（HashCheck/Sync 等）
				// 不会走到这里，它们在 LockstepManager 就已被分流。
				CommandStats.Record(netAct.PlayerID, netAct.ActionId, dispatchTick);

				// 调试作弊：给自己刷资源（确定性处理，双端一致）
			if (netAct.ActionId == "CheatAddResources")
			{
				HandleCheatAddResources(netAct);
				continue;
			}

			// P1-4 投降：投降方所有单位/建筑立即确定性清除；只剩一队时结束
			if (netAct.ActionId == "Surrender")
			{
				HandleSurrender(netAct.PlayerID);
				continue;
			}

			// P1-4 协商/投票：全票同意才生效，超时自动作废
			if (netAct.ActionId == "Vote")
			{
				HandleVoteAction(netAct);
				continue;
			}

			// P1-5 聊天（锁步广播，双端收到同样的消息）
			if (netAct.ActionId == "Chat")
			{
				string text = netAct.ActionIdExtra ?? "";
				string line = $"[P{netAct.PlayerID}] {text}";
				lock (ChatLock)
				{
					ChatMessages.Add(line);
					if (ChatMessages.Count > 200)
						ChatMessages.RemoveAt(0);
				}
				GD.Print($"[Chat] {line}");
				continue;
			}

			// P1-5 地图信号：TargetX/TargetY = 世界坐标 ×1000（与 Move 指令同编码）
			if (netAct.ActionId == "Ping")
			{
				var pingPos = new FPVector2(
					(FP)netAct.TargetX / (FP)1000,
					(FP)netAct.TargetY / (FP)1000);
				int expire = (LockstepManager.Instance?.CurrentTick ?? 0) + 60;
				lock (PingLock)
				{
					MapPings.Add(new MapPing(pingPos, netAct.PlayerID, expire));
					if (MapPings.Count > 64)
						MapPings.RemoveAt(0);
				}
				continue;
			}

				if (netAct.ActionId == "CheatToggleBuild")
				{
					CheatInstantBuild = !CheatInstantBuild;
					GD.Print($"[Cheat] 秒建建筑: {CheatInstantBuild}");
					continue;
				}

				if (netAct.ActionId == "CheatToggleResearch")
				{
					CheatInstantResearch = !CheatInstantResearch;
					GD.Print($"[Cheat] 秒点科技: {CheatInstantResearch}");
					continue;
				}

				// 轨道控制中心：轨道炮轰炸（目标点选择后确定性处理）
				if (netAct.ActionId == "Radar")
				{
					HandleRadar(netAct);
					continue;
				}

				if (netAct.ActionId == "OrbitalStrike")
				{
					HandleOrbitalStrike(netAct);
					continue;
				}

				// 洞穴面板技能：共振波 / 地震波 / 制造虫洞
				if (netAct.ActionId == "ResonanceWave")
				{
					HandleResonanceWave(netAct);
					continue;
				}

				if (netAct.ActionId == "SeismicWave")
				{
					HandleSeismicWave(netAct);
					continue;
				}

				if (netAct.ActionId == "CaveWormhole")
				{
					HandleCaveWormhole(netAct);
					continue;
				}

				// 花田 - 全能性：菌毯上生成叶犬
				if (netAct.ActionId == "PlantOmni")
				{
					HandlePlantOmni(netAct);
					continue;
				}

				// 沙虫技能：潜地切换 / 吞噬冲刺
				if (netAct.ActionId == "Burrow")
				{
					HandleBurrow(netAct);
					continue;
				}

				if (netAct.ActionId == "Devour")
				{
					HandleDevour(netAct);
					continue;
				}

				if (netAct.ActionId == "HeroSkill1" || netAct.ActionId == "HeroSkill2")
				{
					HandleHeroSkill(netAct);
					continue;
				}

				if (netAct.ActionId == "PlasmaStrike")
				{
					HandlePlasmaStrike(netAct);
					continue;
				}

				if (netAct.ActionId == "PlasmaMode")
				{
					HandlePlasmaMode(netAct);
					continue;
				}

				// 纳米虫面板技能：地毯扩散（确定性处理）
				if (netAct.ActionId == "NanoSpread")
				{
					HandleNanoSpread(netAct);
					continue;
				}

				// 植物面板技能：森林蔓延（在生命树/节点 12 格内生成森林节点）
				if (netAct.ActionId == "PlantForestNode")
				{
					HandlePlantForestNode(netAct);
					continue;
				}

				// 植物建筑技能：森林蔓延（由生命树/分支树/森林节点释放）
				if (netAct.ActionId == "PlantForestSpread")
				{
					HandlePlantForestSpread(netAct);
					continue;
				}

				// 纳米虫科技研究
				if (netAct.ActionId.StartsWith("Research_"))
				{
					HandleNanoResearch(netAct);
					continue;
				}

				// 纳米虫凭空建造：NanoBuild_<结构名>
				if (netAct.ActionId.StartsWith("NanoBuild_"))
				{
					HandleNanoBuild(netAct);
					continue;
				}

				// 植物面板建造：PlantBuild_<结构名>（独立于纳米）
				if (netAct.ActionId.StartsWith("PlantBuild_"))
				{
					HandlePlantBuild(netAct);
					continue;
				}

				// 纯逻辑指令：取消生产队列
			if (netAct.ActionId == "CancelProduction")
			{
				foreach (int entityId in netAct.EntityIDs)
				{
					IEntity executor = FindEntityById(entityId);
					executor?.Brain?.CancelProduction(netAct.TargetEntityID);
				}

				continue;
			}

			// 自杀：单位和建筑直接进入既有死亡流程（标记死亡/清理占用/死亡动画），确定性双端一致
			if (netAct.ActionId == "SelfDestruct")
			{
				if (netAct.EntityIDs != null)
				{
					foreach (int entityId in netAct.EntityIDs)
					{
						IEntity executor = FindEntityById(entityId);
						if (executor is RTS.Units.Unit selfDestructUnit)
							selfDestructUnit.StartDeathVisual();
						else if (executor is RTS.Units.Structure selfDestructStructure)
							selfDestructStructure.OnDestroyed();
					}
				}

				continue;
			}

			IEntity targetObj = null;

				if (netAct.TargetEntityID != -1)
				{
					targetObj = FindEntityById(netAct.TargetEntityID);
				}

				// 坐标解析：保持定点数，不转 float。
				var decodedPos = netAct.DecodeTargetPos();
				FP xFP = decodedPos.X;
				FP yFP = decodedPos.Y;
				var targetPosFP = new FPVector2(xFP, yFP);

				// 集结点：给生产建筑设置/追加集结点指令（新建单位会按队列执行）
				if (netAct.ActionId.StartsWith("Rally_"))
				{
					if (netAct.EntityIDs != null)
					{
						// Rally_Move / Rally_Attack / Rally_Harvest
						string rallyAction = netAct.ActionId.Substring("Rally_".Length);

						foreach (int entityId in netAct.EntityIDs)
						{
							IEntity executor = FindEntityById(entityId);
							if (executor is RTS.Units.Structure rallyStructure)
							{
								rallyStructure.AddRallyCommand(
									new RTS.Actions.OrderData
									{
										ActionName = rallyAction,
										TargetPos = targetPosFP,
										TargetObj = targetObj
									},
									netAct.IsQueue);
							}
						}
					}

					continue;
				}

				// 建筑放置：
				// 所有客户端在同一 Tick 确定性生成蓝图和扣资源。
				if (netAct.ActionId.StartsWith("Build_") && targetObj == null)
				{
					string structName = netAct.ActionId.Substring(6);

					if (netAct.EntityIDs != null && netAct.EntityIDs.Length > 0)
					{
						IEntity executor = FindEntityById(netAct.EntityIDs[0]);
						var buildAction = executor?.Brain?.GetAction<RTS.Actions.Implementation.BuildAction>(netAct.ActionId);

						// 注意：
						// PlayerID 是网络玩家编号。
						// 建筑归属应该使用玩家实际 TeamID。
						// 目前 netAct.PlayerID 是否等于 TeamID，取决于你发包处。
						// 后续最好给 NetAction 增加 TeamID 字段，或者在这里通过 NetworkManager 映射。
						int ownerTeamId = ResolveTeamIdFromNetworkPlayer(netAct.PlayerID);

						var player = RTS.World.Game.GetPlayerByTeam(ownerTeamId);

						// 科技门：建筑配置里的 RequiredTechIds 未满足不能放蓝图
						var buildCfg = RTS.Data.Configs.ConfigDatabase.GetStructure(structName);
						bool techOk = buildCfg == null;

						if (buildCfg != null)
						{
							techOk = true;

							foreach (string req in buildCfg.RequiredTechIds)
							{
								if (player?.PlayerData == null || !player.PlayerData.HasTech(req))
								{
									techOk = false;
									break;
								}
							}
						}

						bool uniqueOk = buildCfg == null || !buildCfg.IsUnique ||
							!PlayerOwnsStructure(ownerTeamId, structName);

				bool popOk = true;

				// 蓝图建筑豁免人口检查（多足牧羊人蓝图：满人口时允许先建后扩）
				if (buildCfg != null && buildCfg.SupplyUsed > 0 &&
					!structName.StartsWith("Blueprint_") && player?.PlayerData != null)
				{
					popOk = player.PlayerData.GetUsedSupply() + buildCfg.SupplyUsed <=
						player.PlayerData.GetMaxSupply();
				}

				// 落点校验：不能压墙/压空地，也不能和现存建筑（含蓝图）重叠
				int placeSize = buildCfg != null
					? Math.Max(buildCfg.GridWidth, buildCfg.GridHeight)
					: (buildAction?.GridSize ?? 1);
				bool placeOk = false;

				if (RTS.World.MapGrid.Instance != null)
				{
					var placeCenter = RTS.World.MapGrid.Instance.WorldToGrid(new Godot.Vector2((float)targetPosFP.X, (float)targetPosFP.Y));
					var placeTopLeft = RTS.World.MapGrid.Instance.GetTopLeftFromCenter(placeCenter, placeSize);
					var simTopLeft = new RTS.Simulation.SimVector2I(placeTopLeft.X, placeTopLeft.Y);
					placeOk = World.Grid.IsAreaPlaceable(simTopLeft, placeSize) &&
							  !World.IsAreaOccupiedByStructure(simTopLeft, placeSize);
				}

				// 虫洞核心：视野内自由建造（同一时间只能建造一个洞穴建筑）
				if (buildCfg != null && player != null && HasActiveWormholeCore(ownerTeamId))
				{
					bool visible = World.IsInTeamVision(ownerTeamId, targetPosFP, (FP)(placeSize * 32));
					if (!visible || HasActiveCaveConstruction(ownerTeamId))
						placeOk = false;
				}

				if (buildAction != null && player != null && techOk && uniqueOk && popOk && placeOk && player.PlayerData.TryConsumeResources(buildAction.Costs))
				{
					// 生成 + 集结点指令延迟到主线程，FIFO 保持同 tick 内多个建造的顺序；
					// 资源扣费/落点/科技/唯一判定已在上面用确定性数据完成。
					string buildStructName = structName;
					int buildOwnerTeam = ownerTeamId;
					float buildX = (float)targetPosFP.X;
					float buildY = (float)targetPosFP.Y;
					var buildActionRef = buildAction;
					var buildNetAct = netAct;
					bool instantBuild = CheatInstantBuild;
					RTS.Core.SimEventQueue.EnqueueMain(() =>
					{
						targetObj = EntitySpawner.Instance?.SpawnEntity(
							buildStructName,
							buildOwnerTeam,
							new RTS.Simulation.FPVector2((FP)buildX, (FP)buildY));

						if (targetObj is RTS.Units.Structure s)
						{
							s.InitAsBlueprint(buildActionRef.Costs);

							if (instantBuild)
							{
								s.PromoteFromBlueprint();
								s.AdvanceProgress(1f);
								s.LifeModule?.SetHealthRaw(s.LifeModule.MaxHp);
							}
						}

						if (buildNetAct.EntityIDs == null)
							return;

						foreach (int entityId in buildNetAct.EntityIDs)
						{
							IEntity executor = FindEntityById(entityId);
							if (executor == null || executor.Brain == null)
								continue;

							executor.Brain.StartAction(
								buildNetAct.ActionId,
								new RTS.Simulation.FPVector2((FP)buildX, (FP)buildY),
								targetObj,
								false,
								buildNetAct.IsQueue
							);
						}
					});
				}
				else
				{
					continue;
				}
			}
		}
		else
		{
			// 通用指令（非建筑放置）：Move/Attack/Harvest/Stop 等
			if (netAct.EntityIDs == null)
				continue;

			// 群体移动/攻击移动：按当前相对阵型分配目标点，避免所有单位挤向同一点
			bool groupMove = (netAct.ActionId == "Move" || netAct.ActionId == "AttackMove") &&
				targetObj == null && netAct.EntityIDs.Length > 1;
			var formationTargets = groupMove ? ComputeFormationTargets(netAct, targetPosFP) : null;

			for (int i = 0; i < netAct.EntityIDs.Length; i++)
			{
				IEntity executor = FindEntityById(netAct.EntityIDs[i]);
				if (executor == null || executor.Brain == null)
					continue;

				FPVector2 targetForUnit = formationTargets != null && i < formationTargets.Count
					? formationTargets[i]
					: targetPosFP;

				executor.Brain.StartAction(
					netAct.ActionId,
					targetForUnit,
					targetObj,
					false,
					netAct.IsQueue
				);
			}
		}
	}
	}

		// 群体移动编队：以队伍平均位置为基准，把目标点展开成行列阵型。
		// 单位按 EntityIDs 顺序（包内固定，双端一致）领取槽位，保证确定性。
		private List<FPVector2> ComputeFormationTargets(NetAction netAct, FPVector2 targetPosFP)
		{
			var validPositions = new List<FPVector2>();

			foreach (int entityId in netAct.EntityIDs)
			{
				IEntity e = FindEntityById(entityId);
				if (e?.LogicEntity != null && !e.LogicEntity.IsDead)
					validPositions.Add(e.LogicEntity.Position);
			}

			int count = validPositions.Count;
			if (count < 2)
				return null;

			FPVector2 avg = FPVector2.Zero;
			foreach (var p in validPositions)
				avg += p;
			avg /= (FP)count;

			// 阵型间距 1.5 格（96 世界单位），兼容 2×2 占地面积单位
			var slots = FormationSolver.SolveFP(count, targetPosFP, (FP)96m, avg);

			var results = new List<FPVector2>();
			int slotIndex = 0;

			foreach (int entityId in netAct.EntityIDs)
			{
				IEntity e = FindEntityById(entityId);
				if (e?.LogicEntity == null || e.LogicEntity.IsDead)
				{
					results.Add(targetPosFP);
					continue;
				}

				results.Add(slotIndex < slots.Count ? slots[slotIndex] : targetPosFP);
				slotIndex++;
			}

			return results;
		}

		private bool PlayerOwnsStructure(int teamId, string structName)
		{
			foreach (var sim in World.Structures.Values)
			{
				if (sim.IsDead || sim.TeamID != teamId)
					continue;

				if (sim.StructureTypeId == structName)
					return true;
			}

			return false;
		}

		private void HandleRadar(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;
			if (executor is not RTS.Units.Structure structure || structure.StructureName != "OrbitalControl")
				return;

			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure("OrbitalControl");
			if (cfg == null || structure.LogicEntity == null)
				return;

			var player = RTS.World.Game.GetPlayerByTeam(structure.TeamID);
			if (player?.PlayerData == null)
				return;

			// 模拟线程：不能 new Godot.Collections.Dictionary
			if (!player.PlayerData.TryConsumeResources(ResourceType.Energy, cfg.RadarEnergyCost))
				return;

			var decodedPos = netAct.DecodeTargetPos();
			FP xFP = decodedPos.X;
			FP yFP = decodedPos.Y;

			// 表现层：本地玩家在落点点亮视野（纯视觉，不参与同步）
			if (Main.Instance?.LocalPlayerID == structure.TeamID &&
				RTS.World.FogOfWar.Instance != null && cfg.RadarRadiusTiles > 0)
			{
				float rx = (float)xFP;
				float ry = (float)yFP;
				float rRadius = cfg.RadarRadiusTiles * World.Grid.TileSize;
				float rDuration = cfg.RadarDurationSeconds;
				RTS.Core.SimEventQueue.EnqueueMain(() =>
				{
					RTS.World.FogOfWar.Instance?.AddTemporaryReveal(
						new Vector2(rx, ry),
						rRadius,
						rDuration);
				});
			}
		}

		private void HandleCheatAddResources(NetAction netAct)
		{
			var player = RTS.World.Game.GetPlayerByTeam(ResolveTeamIdFromNetworkPlayer(netAct.PlayerID));
			if (player?.PlayerData == null)
				return;

			var pd = player.PlayerData;
			FP amount = (FP)CheatResourceAmount;

			pd.AddResource(ResourceType.Metal, amount);
			pd.AddResource(ResourceType.Wood, amount);
			pd.AddResource(ResourceType.Food, amount);
			pd.AddResource(ResourceType.Supply, amount);
			pd.AddResource(ResourceType.Gas, amount);
			pd.AddResource(ResourceType.Biomass, amount);
			pd.AddResource(ResourceType.Energy, amount);
			pd.AddResource(ResourceType.NanoBots, amount);
			pd.AddResource(ResourceType.Money, amount);
			pd.AddResource(ResourceType.Data, amount);

			GD.Print($"[Cheat] 玩家 {netAct.PlayerID} (Team {player.TeamId}) 已刷资源 x{CheatResourceAmount}");
		}

		private void HandleSurrender(int networkPlayerId)
		{
			int team = -1;
			foreach (var player in RTS.World.Game.GetAllPlayers())
			{
				if (player == null ||
					!int.TryParse(player.Name.ToString().Replace("Player_", ""), out int pid) ||
					pid != networkPlayerId)
					continue;
				team = player.TeamId;
				break;
			}
			HandleSurrenderByTeam(team);
		}

		private void HandleSurrenderByTeam(int team)
		{
			if (team <= 0 || !_surrenderedTeams.Add(team))
				return;

			foreach (var simUnit in World.Units.Values)
			{
				if (simUnit != null && simUnit.TeamID == team && !simUnit.IsDead)
				{
					simUnit.IsDead = true;
					simUnit.DeathProcessed = true;
				}
			}
			foreach (var simStructure in World.Structures.Values)
			{
				if (simStructure != null && simStructure.TeamID == team && !simStructure.IsDead)
					simStructure.IsDead = true;
			}

			GD.Print($"[Surrender] 队伍 {team} 已投降");
			CheckMatchOver();
		}

		// =========================================================
		// P1-4 协商/投票：全部在模拟线程确定性处理，状态纳入哈希
		// =========================================================

		private void HandleVoteAction(NetAction netAct)
		{
			string kind = netAct.ActionIdExtra ?? "";
			if (kind != VoteKindRematch && kind != VoteKindSurrenderTeam)
				return;

			int pid = netAct.PlayerID;
			int tick = LockstepManager.Instance?.CurrentTick ?? 0;
			bool yes = netAct.IsQueue;

			if (CurrentVote == null)
			{
				// 只能以“同意”发起；反对票不发起
				if (!yes)
					return;
				int eligible = GetVoteEligibleCount(kind, pid);
				if (eligible <= 0)
					return;
				var v = new ActiveVoteState
				{
					Kind = kind,
					ProposerPid = pid,
					TickDeadline = tick + VoteDurationTicks
				};
				v.Yes.Add(pid);
				CurrentVote = v;
				LastVoteOutcome = "";
				GD.Print($"[Vote] P{pid} 发起 {kind}，截止 tick {v.TickDeadline}");
				TryResolveVote(tick);
				return;
			}

			var vote = CurrentVote;
			if (vote.Kind != kind)
				return;
			if (vote.Yes.Contains(pid) || vote.No.Contains(pid))
				return;
			if (yes)
				vote.Yes.Add(pid);
			else
				vote.No.Add(pid);
			TryResolveVote(tick);
		}

		private int GetVoteEligibleCount(string kind, int proposerPid)
		{
			var lockstep = LockstepManager.Instance;
			if (lockstep == null)
				return 1;

			int count = 0;
			foreach (int pid in lockstep.PlayerIDs)
			{
				if (kind == VoteKindSurrenderTeam)
				{
					int proposerTeam = ResolveTeamIdFromNetworkPlayer(proposerPid);
					int pidTeam = ResolveTeamIdFromNetworkPlayer(pid);
					if (pidTeam != proposerTeam)
						continue;
				}
				count++;
			}
			return Mathf.Max(1, count);
		}

		private void TryResolveVote(int tick)
		{
			var v = CurrentVote;
			if (v == null)
				return;

			int total = GetVoteEligibleCount(v.Kind, v.ProposerPid);
			bool allVoted = (v.Yes.Count + v.No.Count) >= total;
			bool deadlinePassed = tick >= v.TickDeadline;

			if (!deadlinePassed && !allVoted)
				return;

			bool pass = v.Yes.Count >= total; // 全票同意才通过
			string kind = v.Kind;
			int proposer = v.ProposerPid;
			CurrentVote = null;

			if (!pass)
			{
				LastVoteOutcome = kind == VoteKindRematch ? "vote.failed_rematch" : "vote.failed_surrender";
				GD.Print($"[Vote] {kind} 未通过（同意 {v.Yes.Count}/{total}）");
				return;
			}

			LastVoteOutcome = kind == VoteKindRematch ? "vote.passed_rematch" : "vote.passed_surrender";
			GD.Print($"[Vote] {kind} 通过（{v.Yes.Count}/{total} 同意）");
			if (kind == VoteKindSurrenderTeam)
				HandleSurrenderByTeam(ResolveTeamIdFromNetworkPlayer(proposer));
			else if (kind == VoteKindRematch)
				HandleRematchVotePassed();
		}

		// 重开投票通过：所有存活队伍视为投降 → 无存活方 → 平局收尾（不判胜者）
		private void HandleRematchVotePassed()
		{
			var teams = CollectAliveTeams(byGroup: false);

			if (teams.Count == 0)
			{
				_matchOver = true;
				IsRunning = false;
				GD.Print("[Match] 重开投票通过（无存活队伍，平局）");
				return;
			}

			foreach (int team in teams)
				HandleSurrenderByTeam(team);
			CheckMatchOver();
		}

		private void CheckMatchOver()
		{
			if (_matchOver)
				return;
			// 2v2 队伍分组：按“阵营组”判胜，同组盟友死光才淘汰
			var alive = CollectAliveTeams(byGroup: true);
			if (alive.Count > 1)
				return;

			_matchOver = true;
			GD.Print(alive.Count == 0
				? "[Match] 平局"
				: $"[Match] 阵营组 {string.Join(",", alive)} 获胜");
			IsRunning = false;
		}

		// 收集存活队伍（byGroup=true 时按阵营组合并，用于 2v2 判胜）
		private HashSet<int> CollectAliveTeams(bool byGroup)
		{
			var set = new HashSet<int>();
			foreach (var u in World.Units.Values)
				if (u != null && !u.IsDead && u.TeamID > 0)
					set.Add(byGroup ? GetTeamGroup(u.TeamID) : u.TeamID);
			foreach (var s in World.Structures.Values)
				if (s != null && !s.IsDead && s.TeamID > 0)
					set.Add(byGroup ? GetTeamGroup(s.TeamID) : s.TeamID);
			return set;
		}

		private void HandleOrbitalStrike(NetAction netAct)
		{
			IEntity executor = GetExecutor(netAct);
			if (executor == null)
				return;
			if (executor is not RTS.Units.Structure structure || structure.StructureName != "OrbitalControl")
				return;

			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure("OrbitalControl");
			if (cfg == null || structure.LogicEntity == null)
				return;

			var player = RTS.World.Game.GetPlayerByTeam(structure.TeamID);
			if (player?.PlayerData == null)
				return;

			// 模拟线程：不能 new Godot.Collections.Dictionary
			if (!player.PlayerData.TryConsumeResources(ResourceType.Energy, cfg.StrikeEnergyCost))
				return;

			var decodedPos = netAct.DecodeTargetPos();
			FP xFP = decodedPos.X;
			FP yFP = decodedPos.Y;
			FP radius = (FP)(cfg.StrikeRadiusTiles * World.Grid.TileSize);

			World.ApplyAreaDamage(
				new FPVector2(xFP, yFP),
				radius,
				100,
				structure.LogicEntity.ID,
				(FP)cfg.StrikeDamage,
				(int)DamageType.Explosive,
				FP.Zero,
				0
			);

			// 表现层：本地玩家看到落点特效（纯视觉，不参与同步）
			if (Main.Instance?.LocalPlayerID == structure.TeamID)
			{
				float fxX = (float)xFP;
				float fxY = (float)yFP;
				float fxRadius = (float)radius;
				RTS.Core.SimEventQueue.EnqueueMain(() =>
				{
					RTS.Core.HitFx.Spawn(
						GetTree().Root,
						new Vector3(fxX, 30f, fxY),
						Mathf.Max(40f, fxRadius),
						0.5f,
						new Color(1f, 0.5f, 0.15f, 1f),
						fxRadius
					);
				});
			}
		}

		private int ResolveTeamIdFromNetworkPlayer(int networkPlayerId)
		{
			if (NetworkManager.Instance != null)
			{
				return NetworkManager.Instance.GetPlayerTeam(networkPlayerId);
			}

			return networkPlayerId;
		}

		// =========================================================
		// 完整确定性状态哈希：逻辑世界 + 玩家资源 + 单位/建筑动作队列
		// =========================================================

		public long GetFullWorldHash()
		{
			long hash = 1469598103934665603L;

			foreach (var section in GetFullWorldSectionHashes())
			{
				hash = Mix(hash, section.Value);
			}

			return hash;
		}

		// 区段哈希列表：顺序固定，用于定位脱同步差异来自哪个子系统
		public List<KeyValuePair<string, long>> GetFullWorldSectionHashes()
		{
			return new List<KeyValuePair<string, long>>
			{
				new KeyValuePair<string, long>("Counters", World.GetCountersHash()),
				new KeyValuePair<string, long>("Units", World.GetUnitsHash()),
				new KeyValuePair<string, long>("Structures", World.GetStructuresHash()),
				new KeyValuePair<string, long>("Projectiles", World.GetProjectilesHash()),
				new KeyValuePair<string, long>("Creep", World.GetCreepHash()),
				new KeyValuePair<string, long>("CarpetSpread", World.GetCarpetSpreadHash()),
				new KeyValuePair<string, long>("RNG", World.GetRngHash()),
				new KeyValuePair<string, long>("Resources", GetPlayerResourcesHash()),
				new KeyValuePair<string, long>("Brains", GetBrainsHash()),
				new KeyValuePair<string, long>("Votes", GetVoteHash()),
				new KeyValuePair<string, long>("BotAI", GetBotStateHash()),
				// 触发器状态（变量 + 每触发器启用/已触发/冷却）必须纳入哈希：
				// 触发器会改资源与刷单位，一旦状态分叉而哈希不覆盖，
				// 表现就是"某处突然不一致"却定位不到来源。
				new KeyValuePair<string, long>("Triggers", TriggerRuntime.GetStateHash()),
				// 教程进度（当前目标下标 / 已完成数）同样必须纳入哈希：
				// 否则"一端已进入下一目标、另一端还在上一目标"的分叉不会被发现。
				new KeyValuePair<string, long>("Tutorial", Tutorial.GetStateHash()),
				// 玩家指令统计也进哈希：教程目标直接依赖它，
				// 一旦两端对"玩家下过哪些指令"理解不同，教程进度就会分叉。
				new KeyValuePair<string, long>("CommandStats", CommandStats.GetStateHash())
			};
		}

		private long GetVoteHash()
		{
			long hash = 1469598103934665603L;

			var v = CurrentVote;
			if (v != null)
			{
				if (v.Kind != null)
					foreach (char c in v.Kind)
						hash = Mix(hash, c);
				hash = Mix(hash, v.ProposerPid);
				hash = Mix(hash, v.TickDeadline);
				foreach (int pid in v.Yes.OrderBy(p => p))
					hash = Mix(hash, pid * 2L);
				foreach (int pid in v.No.OrderBy(p => p))
					hash = Mix(hash, pid * 2L + 1L);
			}

			// 已投降队伍也纳入，保证全队投降的确定性状态可追踪
			foreach (int team in _surrenderedTeams.OrderBy(t => t))
				hash = Mix(hash, team + 1000L);

			return hash;
		}

		private long GetPlayerResourcesHash()
		{
			long hash = 1469598103934665603L;

			foreach (var player in RTS.World.Game.GetAllPlayers())
			{
				hash = Mix(hash, player?.TeamId ?? -1);
				hash = Mix(hash, player?.PlayerData?.GetStateHash() ?? 0);
			}

			return hash;
		}

		private long GetBrainsHash()
		{
			long hash = 1469598103934665603L;

			// Dictionary 枚举顺序不确定，按实体 ID 排序后混合
			foreach (int id in EntityNodes.Keys.OrderBy(k => k))
			{
				EntityNodes.TryGetValue(id, out var entityNode);
				hash = Mix(hash, id);
				hash = Mix(hash, entityNode?.Brain?.GetDeterministicStateHash() ?? 0);
			}

			return hash;
		}

		// 区段哈希字符串：随 HashCheck 包一起发送，房主可直接对比定位差异区段
		public string BuildSectionHashString()
		{
			var sb = new StringBuilder();

			foreach (var section in GetFullWorldSectionHashes())
			{
				if (sb.Length > 0)
					sb.Append(';');
				sb.Append(section.Key).Append('=').Append(section.Value.ToString("X"));
			}

			return sb.ToString();
		}

		public static Dictionary<string, long> ParseSectionHashString(string value)
		{
			var result = new Dictionary<string, long>();

			if (string.IsNullOrEmpty(value))
				return result;

			foreach (string part in value.Split(';'))
			{
				int idx = part.IndexOf('=');
				if (idx <= 0)
					continue;

				string name = part.Substring(0, idx);
				string hex = part.Substring(idx + 1);

				if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long hash))
					result[name] = hash;
			}

			return result;
		}

		// 对比两份区段哈希字符串，返回差异区段名（无差异返回 "unknown"）
		public static string CompareSectionStrings(string a, string b)
		{
			var dictA = ParseSectionHashString(a);
			var dictB = ParseSectionHashString(b);
			var names = dictA.Keys.Union(dictB.Keys).OrderBy(k => k, System.StringComparer.Ordinal).ToList();
			var diff = new List<string>();

			foreach (string name in names)
			{
				long va = dictA.GetValueOrDefault(name, 0);
				long vb = dictB.GetValueOrDefault(name, 0);

				if (va != vb)
					diff.Add(name);
			}

			return diff.Count == 0 ? "unknown" : string.Join(",", diff);
		}

		// =========================================================
		// 脱同步报告：区段哈希 + 玩家资源 + 世界状态 + 动作队列 + 指令历史
		// 供两端的报告文件做 diff，快速定位第一个出错点
		// =========================================================

		public string GetDesyncReport(int tick, long localHash, long peerHash, int peerPlayerId, string sectionInfo)
		{
			var sb = new StringBuilder();

			sb.AppendLine("============ DESYNC REPORT ============");
			sb.AppendLine($"LocalPlayerID: {Main.Instance?.LocalNetworkPlayerID ?? 1}");
			sb.AppendLine($"DesyncTick: {tick}");
			sb.AppendLine($"LocalTickNow: {LockstepManager.Instance?.CurrentTick ?? -1}");
			sb.AppendLine($"LocalFullHash: {localHash:X}");
			sb.AppendLine($"PeerFullHash: {peerHash:X}");
			sb.AppendLine($"PeerPlayerID: {peerPlayerId}");
			sb.AppendLine($"DifferingSections: {sectionInfo}");
			sb.AppendLine($"FullHashNow: {GetFullWorldHash():X}");

			sb.AppendLine();
			sb.AppendLine("[SECTION_HASHES]");

			foreach (var section in GetFullWorldSectionHashes())
			{
				sb.AppendLine($"{section.Key}={section.Value:X}");
			}

			sb.AppendLine();
			sb.AppendLine("[PLAYER_RESOURCES]");

			foreach (var player in RTS.World.Game.GetAllPlayers())
			{
				string res = player?.PlayerData?.GetResourcesDebugString() ?? "(null)";
				sb.AppendLine($"Team {player?.TeamId ?? -1}: {res}");
			}

			sb.AppendLine();
			sb.AppendLine("[WORLD]");
			sb.AppendLine(World.GetWorldDebugReport());

			sb.AppendLine();
			sb.AppendLine("[ENTITY_STATES]");

			foreach (int id in EntityNodes.Keys.OrderBy(k => k))
			{
				EntityNodes.TryGetValue(id, out var entity);
				string brainState = entity?.Brain?.GetDebugSnapshot() ?? "(no brain)";
				string display = entity?.DisplayName ?? "(null)";
				sb.AppendLine($"ID {id} [{display}] {brainState}");
			}

			sb.AppendLine();
			sb.AppendLine("[TICK_HISTORY]");

			if (LockstepManager.Instance != null)
			{
				int from = Math.Max(0, tick - 20);

				for (int t = from; t <= tick; t++)
				{
					if (!LockstepManager.Instance.HistoryArchive.TryGetValue(t, out var actions))
					{
						sb.AppendLine($"Tick {t}: (no history)");
						continue;
					}

					if (actions.Count == 0)
					{
						sb.AppendLine($"Tick {t}: (no actions)");
						continue;
					}

					foreach (var action in actions)
					{
						sb.AppendLine($"Tick {t}: {action.BuildDebugString()}");
					}
				}
			}
			else
			{
				sb.AppendLine("(no lockstep manager)");
			}

			return sb.ToString();
		}

		private static long Mix(long hash, long value)
		{
			unchecked
			{
				hash ^= value;
				hash *= 1099511628211L;
				return hash;
			}
		}

		public void ResetSimulation()
		{
			// 第二局必须全量重置：模拟线程/世界/实体注册表/对局状态/机器人决策状态
			// 全部清空，否则上一局的单位、路径、波次、拆塔目标会残留到新对局
			// （“打完一局再开一局直接闪退/错乱”的根因）。
			StopSimThread();
			lock (WorldLock)
			{
				_accumulator = 0.0;
				IsRunning = false;
				EntityNodes.Clear();
				World = new SimWorld(WorldLock, this);
				_pendingGarrison.Clear();
				ConfigureWorldDiagnostics();
				_surrenderedTeams.Clear();
				_matchOver = false;
				CurrentVote = null;
				LastVoteOutcome = "";
				lock (ChatLock)
					ChatMessages.Clear();
				lock (PingLock)
					MapPings.Clear();
				ResetBotState();

				GD.Print("[Sim] 模拟世界已重置（含机器人状态）。");
			}
			StartSimThread();
		}
	}
}
