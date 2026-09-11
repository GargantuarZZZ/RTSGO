using Godot;
using System.Collections.Generic;
using RTS.Network;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Tools
{
	// 1v1 AI 演示：双方机器人对战，视野按各自队伍正常计算，
	// 观看者（OB）为正交俯视全图视角（FogOfWar.RevealAll 仅影响渲染，不影响机器人视野/行为）。
	// 运行：godot --path . res://Scenes/Test/AIShowcase1v1.tscn
	public partial class AIShowcase1v1 : Node
	{
		[Export] public string Team1Race = "Union";
		[Export] public string Team2Race = "Demon";
		[Export(PropertyHint.Range, "1,3,1")] public int Difficulty = 2;

		/// <summary>演示开关：Game.SetupMatch 生成玩家前会读取并强制应用配置（双保险）。</summary>
		public static bool Enabled = false;
		public static string StaticTeam1Race = "Union";
		public static string StaticTeam2Race = "Demon";
		public static int StaticDifficulty = 2;

		private bool _configured = false;
		private Label _statusLabel;
		private double _reportTimer = 0.0;
		// 每秒心跳：定位“越到后期越多”的卡死发生时间
		private double _heartbeatTimer = 0.0;
		private const double HeartbeatInterval = 1.0;
		private readonly System.Collections.Generic.Dictionary<int, (long X, long Y)> _stuckLastPos = new();
		private readonly System.Collections.Generic.Dictionary<int, int> _stuckCount = new();
		private int _lastShownSpeed = 1;
		// 自动化测试：AI_TEST_SPEED=倍速 AI_TEST_TICKS=退出 tick（0=不自动退出）
		private int _autoQuitTick = 0;
		// 自动化测试：AI_TEST_POP=目标人口（任一机器人队伍达到即退出，0=不启用）
		private int _autoQuitPop = 0;

		private static string EnvOr(string key, string fallback)
		{
			string v = OS.GetEnvironment(key);
			return string.IsNullOrEmpty(v) ? fallback : v;
		}

		public override void _Process(double delta)
		{
			if (_autoQuitTick > 0 &&
				RTS.Network.LockstepManager.Instance is { } lockstep &&
				lockstep.CurrentTick >= _autoQuitTick)
			{
				PrintPopulationReport();
				GD.Print($"[AIShowcase] 自动测试结束 tick={lockstep.CurrentTick}");
				GetTree().Quit();
				return;
			}
			// 人口达标自动退出：AI_TEST_POP=200 时任一队伍 used supply 达标即收尾
			if (_autoQuitPop > 0 &&
				RTS.Core.SimManager.Instance is { } sim &&
				sim.World != null)
			{
				lock (sim.WorldLock)
				{
					foreach (var player in RTS.World.Game.GetAllPlayers())
					{
						if (player?.PlayerData == null)
							continue;
						int used = player.PlayerData.GetUsedSupply();
						if (used >= _autoQuitPop)
						{
							PrintPopulationReport();
							GD.Print($"[AIShowcase] 人口达标结束 team={player.TeamId} 人口={used}/{player.PlayerData.GetMaxSupply()} 目标={_autoQuitPop}");
							GetTree().Quit();
							return;
						}
					}
				}
			}
			// 每 5 秒自检一次，便于观察两队建筑/单位随时间增长
			_reportTimer += delta;
			if (_reportTimer >= 5.0)
			{
				_reportTimer = 0.0;
				ReportTeams();
				PrintPopulationReport();
			}

			// 每秒心跳：输出 tick/移动中/挂起/预算 + 疑似卡住（3 秒位置没变但有目标）单位
			_heartbeatTimer += delta;
			if (_heartbeatTimer >= HeartbeatInterval)
			{
				_heartbeatTimer -= HeartbeatInterval;
				PrintHeartbeat();
			}
			// 倍速热键已移到全局 UserController，这里只负责同步状态栏显示
			int speedNow = RTS.Core.SimManager.Instance?.SimulationSpeed ?? 1;
			if (speedNow != _lastShownSpeed && _statusLabel != null &&
				GodotObject.IsInstanceValid(_statusLabel))
			{
				_lastShownSpeed = speedNow;
				_statusLabel.Text = BuildStatusText();
			}
		}

		public override void _EnterTree()
		{
			if (_configured)
				return;
			_configured = true;
			// 环境变量覆盖种族（自动化矩阵测试用）：AI_TEST_RACE1/AI_TEST_RACE2
			string envRace1 = OS.GetEnvironment("AI_TEST_RACE1");
			if (!string.IsNullOrEmpty(envRace1))
				Team1Race = envRace1;
			string envRace2 = OS.GetEnvironment("AI_TEST_RACE2");
			if (!string.IsNullOrEmpty(envRace2))
				Team2Race = envRace2;
			Enabled = true;
			StaticTeam1Race = Team1Race;
			StaticTeam2Race = Team2Race;
			StaticDifficulty = System.Math.Clamp(Difficulty, 1, 3);

			// 尽量早开全图（FogOfWar._Ready 之前），避免任何一帧把敌方按迷雾隐藏
			if (GetNodeOrNull<RTS.World.FogOfWar>("Main/User/FogOfWar") is { } fogNode)
				fogNode.RevealAll = true;

			// 注意：网络/玩家/机器人配置不在 _EnterTree 应用——
			// autoload 初始化顺序可能晚于本节点，Game.SetupMatch 会在生成前统一应用（见 Game.cs 钩子）。
			GD.Print($"[AIShowcase] 1v1 机器人演示: T1={Team1Race} vs T2={Team2Race} 难度={Difficulty}");
		}

		/// <summary>应用玩家/种族/机器人配置（可重复调用，幂等）。</summary>
		public static void ApplyMatchConfig()
		{
			var network = NetworkManager.Instance;
			var lockstep = LockstepManager.Instance;
			if (network == null || lockstep == null)
			{
				GD.PrintErr("[AIShowcase] NetworkManager/LockstepManager 不可用");
				return;
			}

			network.BeginOfflineHost();

			// 自动化扩展：AI_TEST_TEAMS=1,2,3,4 / AI_TEST_GROUPS=1,1,2,2 /
			// AI_TEST_RACES=Union,Demon,... / AI_TEST_BOTS=2,3,4 /
			// AI_TEST_OBSERVER=1（本地 1 号为旁观者）
			// Godot OS.GetEnvironment 对未设置变量返回空字符串而非 null，
			// 不能只用 ?? 兜底，否则 AI_TEST_TEAMS 未设置时会退化成只生成 1 号玩家。
			string teamsCsv = EnvOr("AI_TEST_TEAMS", "1,2");
			string groupsCsv = EnvOr("AI_TEST_GROUPS", "");
			string racesCsv = EnvOr("AI_TEST_RACES", "");
			string botsCsv = EnvOr("AI_TEST_BOTS", "2");
			bool localObserver = OS.GetEnvironment("AI_TEST_OBSERVER") == "1";
			// AI_TEST_PID_TEAM=101:2 指定机器人玩家号的出生点（pid != team 路径）
			string pidTeamCsv = EnvOr("AI_TEST_PID_TEAM", "");
			var pidTeamMap = new Dictionary<int, int>();
			foreach (string part in pidTeamCsv.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
			{
				string[] kv = part.Split(':');
				if (kv.Length == 2 && int.TryParse(kv[0].Trim(), out int k) && int.TryParse(kv[1].Trim(), out int v))
					pidTeamMap[k] = v;
			}
			string[] teams = teamsCsv.Split(',', System.StringSplitOptions.RemoveEmptyEntries);
			string[] groups = groupsCsv.Split(',', System.StringSplitOptions.RemoveEmptyEntries);
			string[] races = racesCsv.Split(',', System.StringSplitOptions.RemoveEmptyEntries);

			var pids = new List<int>();
			for (int i = 0; i < teams.Length; i++)
			{
				if (int.TryParse(teams[i].Trim(), out int team))
					pids.Add(team);
			}
			if (pids.Count == 0)
				pids.Add(1);

			lockstep.PlayerIDs.Clear();

			// AI_TEST_MAP=<MapId>：让演示/回归跑在指定地图上（例如生成出来的 Gen_Open2）。
			// 没有这个开关时只能跑 Classic，自定义地图与触发器就没法进自动化验证。
			string mapOverride = OS.GetEnvironment("AI_TEST_MAP");
			if (!string.IsNullOrWhiteSpace(mapOverride) && network != null)
			{
				network.SelectedMapId = mapOverride.Trim();
				GD.Print($"[AIShowcase] 地图覆盖为 '{network.SelectedMapId}'");
			}

			for (int i = 0; i < pids.Count; i++)
			{
				int pid = pids[i];
				lockstep.PlayerIDs.Add(pid);
				network.PeerTeamMap[pid] = pidTeamMap.GetValueOrDefault(pid, pid);
				string race = i < races.Length && races[i].Trim().Length > 0
					? races[i].Trim()
					: (pid == 1 ? StaticTeam1Race : StaticTeam2Race);
				network.PeerRaceMap[pid] = race;
				int group = i < groups.Length && int.TryParse(groups[i].Trim(), out int g) && g > 0
					? g
					: pid;
				network.PeerGroupMap[pid] = group;
				network.PeerObserverMap[pid] = localObserver && pid == 1;
				network.PeerReadyMap[pid] = true;
				network.SteamToPlayerId[(ulong)pid] = pid;
				network.PlayerIdToSteam[pid] = (ulong)pid;
			}
			lockstep.PlayerIDs.Sort();

			// 机器人列表（默认 2 号；旁观者局由 AI_TEST_BOTS 指定）
			RTS.Core.SimManager.Instance?.SetBotTeams(botsCsv, StaticDifficulty);
			// 逐机器人难度（默认全部 = StaticDifficulty）
			var botConfigs = new List<RTS.Core.SimManager.BotLobbyConfig>();
			foreach (int pid in pids)
			{
				if (network.PeerObserverMap.GetValueOrDefault(pid, false))
					continue;
				botConfigs.Add(new RTS.Core.SimManager.BotLobbyConfig
				{
					Pid = pid,
					Team = network.PeerTeamMap.GetValueOrDefault(pid, pid),
					Race = network.PeerRaceMap.GetValueOrDefault(pid, "Union"),
					Difficulty = StaticDifficulty,
					Group = network.PeerGroupMap.GetValueOrDefault(pid, pid)
				});
			}
			RTS.Core.SimManager.BotConfigsOverride = botConfigs;
		}

		public override void _Ready()
		{
			// 子场景已就绪后再摆 OB 视角/开全图/提示
			CallDeferred(nameof(SetupObserverView));
			// 自动化测试配置：环境变量驱动，便于无头回归
			if (int.TryParse(OS.GetEnvironment("AI_TEST_SPEED"), out int testSpeed))
				RTS.Core.SimManager.Instance.SimulationSpeed = System.Math.Clamp(testSpeed, 1, 8);
			if (int.TryParse(OS.GetEnvironment("AI_TEST_TICKS"), out int testTicks) && testTicks > 0)
				_autoQuitTick = testTicks;
			if (int.TryParse(OS.GetEnvironment("AI_TEST_POP"), out int testPop) && testPop > 0)
				_autoQuitPop = testPop;
			// 临时回归：AI_TEST_SELECT=1 时 5 秒后自动选中一个己方单位，
			// 验证“选中单位死亡后 UI 不再踩已释放节点”（8 倍速崩溃回归）
			if (OS.GetEnvironment("AI_TEST_SELECT") == "1")
			{
				GetTree().CreateTimer(5.0).Timeout += () =>
				{
					var uc = GetNodeOrNull<RTS.Core.UserController>("Main/User/UserController");
					var sim = RTS.Core.SimManager.Instance;
					if (uc == null || sim?.World == null)
						return;
					foreach (var u in sim.World.Units.Values)
					{
						if (u == null || u.IsDead || u.TeamID != 1)
							continue;
						var node = sim.FindEntityById(u.ID);
						if (node != null)
						{
							uc.ForceSelect(node);
							GD.Print($"[AIShowcase] 自动选中 {u.UnitTypeId} ID{u.ID}（死亡 UI 回归测试）");
							break;
						}
					}
				};
			}
			// 2 秒后自检：两队实体数量打到日志与屏幕，定位“看不见第二队”是生成问题还是渲染问题
			GetTree().CreateTimer(2.0).Timeout += ReportTeams;
		}

		private void SetupObserverView()
		{
			if (!_configured)
				return;

			// OB 全局视角：正交俯视全图（1v1 地图约 ±3400）
			var cam = GetNodeOrNull<Camera3D>("Main/RTSCamera");
			if (cam != null)
			{
				cam.ProcessMode = Node.ProcessModeEnum.Disabled;
				cam.Projection = Camera3D.ProjectionType.Orthogonal;
				// 默认 1v1 小图；大地图空跑用 AI_TEST_CAMERA_SIZE 覆盖（如 13000）
				float camSize = 5200f;
				if (float.TryParse(OS.GetEnvironment("AI_TEST_CAMERA_SIZE"), out float envCam) && envCam > 0f)
					camSize = envCam;
				cam.Size = camSize;
				cam.Near = 100f;
				cam.Far = 30000f;
				cam.Position = new Vector3(0f, 9000f, 0f);
				cam.LookAtFromPosition(cam.Position, Vector3.Zero, Vector3.Forward);
				cam.MakeCurrent();
			}

			// 观看者开全图（机器人视野/行为不受影响）
			if (RTS.World.FogOfWar.Instance != null)
				RTS.World.FogOfWar.Instance.RevealAll = true;

			// 全单位战斗力评分表（数据驱动，供调参/对比）
			GD.Print(RTS.Data.CombatRating.BuildRatingTable());

			var layer = new CanvasLayer { Layer = 90 };
			AddChild(layer);
			_statusLabel = new Label
			{
				Text = BuildStatusText(),
				HorizontalAlignment = HorizontalAlignment.Center
			};
			_statusLabel.AddThemeFontSizeOverride("font_size", 20);
			_statusLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 0.6f));
			_statusLabel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
			layer.AddChild(_statusLabel);
		}

		private string BuildStatusText()
		{
			int speed = RTS.Core.SimManager.Instance?.SimulationSpeed ?? 1;
			return $"AI 演示 — {Team1Race} vs {Team2Race}  (OB 全图 / 机器人视野正常)  倍速 x{speed}";
		}

		private void ReportTeams()
		{
			var sim = RTS.Core.SimManager.Instance;
			if (sim?.World == null)
				return;

			var sb = new System.Text.StringBuilder();
			lock (sim.WorldLock)
			{
				foreach (int team in new[] { 1, 2 })
				{
					int units = 0, structures = 0;
					foreach (var u in sim.World.Units.Values)
						if (u != null && u.TeamID == team && !u.IsDead)
							units++;
					foreach (var s in sim.World.Structures.Values)
						if (s != null && s.TeamID == team && !s.IsDead)
							structures++;
					sb.Append($"Team{team}: {units} 单位 / {structures} 建筑; ");
				}
				// 未完工建筑明细：状态 + 施工进度
				foreach (var s in sim.World.Structures.Values)
				{
					if (s == null || s.IsDead || (s.TeamID != 1 && s.TeamID != 2))
						continue;
					if (s.CurrentState == SimStructure.StructureState.Active)
						continue;
					int pct = (int)((s.ConstructionProgress * (FP)100m));
					sb.Append($"T{s.TeamID}[{s.StructureTypeId}]{s.CurrentState}({pct}%) ");
				}
			}

			if (_statusLabel != null && GodotObject.IsInstanceValid(_statusLabel))
				_statusLabel.Text = BuildStatusText() + "\n" + sb.ToString().TrimEnd();
		}

		/// <summary>自动测试收尾用：把两队人口/资源/建筑数打到日志。</summary>
		private void PrintPopulationReport()
		{
			var sim = RTS.Core.SimManager.Instance;
			if (sim?.World == null)
				return;
			lock (sim.WorldLock)
			{
				foreach (int team in new[] { 1, 2 })
				{
					int units = 0, structures = 0;
					foreach (var u in sim.World.Units.Values)
						if (u != null && u.TeamID == team && !u.IsDead)
							units++;
					var structList = new System.Collections.Generic.List<string>();
					foreach (var s in sim.World.Structures.Values)
						if (s != null && s.TeamID == team && !s.IsDead)
						{
							structures++;
							structList.Add($"{s.StructureTypeId}:{s.CurrentState}");
						}
					var player = RTS.World.Game.GetPlayerByTeam(team);
					string res = "";
					if (player?.PlayerData != null)
					{
						int used = player.PlayerData.GetUsedSupply();
						res = $" 金={player.PlayerData.GetResource(ResourceType.Metal):F0} 木={player.PlayerData.GetResource(ResourceType.Wood):F0} 气={player.PlayerData.GetResource(ResourceType.Gas):F0} 肉={player.PlayerData.GetResource(ResourceType.Biomass):F0}";
						string creep = sim.World.CreepGrid != null
							? $" 毯={sim.World.CreepGrid.PlantCreepCount}"
							: "";
						GD.Print($"[AIShowcase] 人口报告 Team{team} 人口={used}/{player.PlayerData.GetMaxSupply()} 单位={units} 建筑={structures}{res}{creep} [{string.Join(",", structList)}]");
					}
				}
			}
		}

		/// <summary>每秒心跳：tick/移动中/挂起/预算 + 疑似卡住（3 秒位置没变但有目标）单位，定位卡死发生时间。</summary>
		private void PrintHeartbeat()
		{
			var sim = RTS.Core.SimManager.Instance;
			if (sim?.World == null)
				return;
			int tick = RTS.Network.LockstepManager.Instance?.CurrentTick ?? 0;
			ulong sec = Time.GetTicksMsec() / 1000;
			lock (sim.WorldLock)
			{
				int pathPending = 0, moving = 0;
				var stuck = new System.Collections.Generic.List<string>();
				foreach (var u in sim.World.Units.Values)
				{
					if (u == null || u.IsDead)
						continue;
					if (u.PathPending)
						pathPending++;
					if (u.HasTarget)
						moving++;
					long px = (long)(u.Position.X * (FP)1000m);
					long py = (long)(u.Position.Y * (FP)1000m);
					if (_stuckLastPos.TryGetValue(u.ID, out var last) && last.X == px && last.Y == py)
					{
						if (u.HasTarget || u.PathPending)
						{
							int cnt = _stuckCount.GetValueOrDefault(u.ID) + 1;
							_stuckCount[u.ID] = cnt;
							if (cnt == 60)
								stuck.Add($"{u.UnitTypeId}#{u.ID}");
						}
					}
					else
					{
						_stuckCount[u.ID] = 0;
						_stuckLastPos[u.ID] = (px, py);
					}
				}

				string teams = "";
				foreach (int team in new[] { 1, 2 })
				{
					int units = 0, b = 0;
					foreach (var u in sim.World.Units.Values)
						if (u != null && !u.IsDead && u.TeamID == team)
							units++;
					foreach (var s in sim.World.Structures.Values)
						if (s != null && !s.IsDead && s.TeamID == team)
							b++;
					teams += $" T{team}:{units}兵/{b}建";
				}
				string stuckStr = stuck.Count > 0
					? $" 卡住3s[{string.Join(",", stuck.GetRange(0, System.Math.Min(8, stuck.Count)))}]"
					: "";
				GD.Print($"[Debug1s] 秒={sec} tick={tick} 速度={sim.SimulationSpeed} 移动中={moving} 挂起={pathPending} 预算={sim.World.PathBudgetRemaining}/{RTS.Simulation.SimWorld.PathBudgetPerTick}{teams}{stuckStr}");
			}
		}
	}
}
