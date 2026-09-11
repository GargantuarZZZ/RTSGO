using Godot;
using System;
using System.Collections.Generic;
using RTS.Core;
using RTS.Simulation;
using RTS.Units;
using RTS.World;
using FP = FixMath.NET.Fix64;

namespace RTS.Test
{
	/// <summary>
	/// 压测场景公共基类：三个 StressTest* 原来各自复制一份
	/// _Ready / AddBattle / EnsureFpsOverlay / TrySetup / _Process 诊断 /
	/// SpawnRandomWalls / IssueChargeCommands（约 120 行 ×3），全部收敛到这里。
	/// 子类只保留默认规模、日志标签、墙开关与各自的兵种生成逻辑。
	/// </summary>
	public abstract partial class StressTestBase : Node
	{
		protected virtual int WallClusters => 80;
		protected int UnitsPerSide;

		private bool _done = false;
		private int _waitFrames = 0;
		private float _diagTimer = 0f;
		private bool _revealApplied = false;

		protected abstract int DefaultUnitsPerSide { get; }
		protected abstract string LogTag { get; }
		protected virtual bool SpawnWallsEnabled => false;
		protected abstract void SpawnAndCharge(SimWorld world);

		protected virtual int ResolveUnitsPerSide(int fallback) =>
			int.TryParse(OS.GetEnvironment("STRESS_UNITS"), out int units)
				? Math.Clamp(units, 1, 1000)
				: fallback;

		public override void _Ready()
		{
			// 压力测试统一跑在空白地图：无起始基地/资源/中立建筑
			RTS.World.Game.SkipInitialSpawns = true;
			UnitsPerSide = ResolveUnitsPerSide(DefaultUnitsPerSide);

			// 自适应垂直同步：不掉帧不撕裂；显示器 240Hz 时正好锁 240
			DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Adaptive);
			Engine.MaxFps = 0;
			OS.LowProcessorUsageMode = false;

			CallDeferred(nameof(AddBattle));
		}

		private void AddBattle()
		{
			var battle = GD.Load<PackedScene>("res://Scenes/Test/main_blank.tscn").Instantiate();
			AddChild(battle);

			// P0-2：环境变量驱动回放录制/验证（RECORD_REPLAY / REPLAY_FILE）
			if (!string.IsNullOrEmpty(OS.GetEnvironment("RECORD_REPLAY")))
				AddChild(new RTS.Tools.ReplayAutoRecord());
			if (!string.IsNullOrEmpty(OS.GetEnvironment("REPLAY_FILE")))
				AddChild(new RTS.Tools.ReplayAutoVerify());
			if (OS.GetEnvironment("STRESS_NO_UI") == "1")
			{
				var userUi = battle.FindChild("UserUI", true, false);
				if (userUi is Godot.CanvasLayer canvasLayer)
					canvasLayer.Visible = false;
			}
			string hideList = OS.GetEnvironment("STRESS_HIDE") ?? "";
			if (hideList.Length > 0)
			{
				foreach (string name in hideList.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
				{
					var target = battle.FindChild(name.Trim(), true, false);
					if (target is Godot.CanvasItem ci)
						ci.Visible = false;
				}
			}

			CallDeferred(nameof(EnsureFpsOverlay));
			CallDeferred(nameof(TrySetup));
		}

		// 帧率小字：不管主场景里的 UserController 是否成功挂载，压测场景自己保证显示
		private void EnsureFpsOverlay()
		{
			if (GetTree().Root.FindChild("FpsOverlay", true, false) == null)
				GetTree().Root.AddChild(new RTS.UI.FpsOverlay { Name = "FpsOverlay" });
		}

		private void TrySetup()
		{
			if (_done)
				return;

			var world = SimManager.Instance?.World;
			if (Main.Instance == null || EntitySpawner.Instance == null ||
				world == null || world.Grid.TerrainCells.Count == 0 || !SimManager.Instance.IsRunning)
			{
				if (++_waitFrames > 180)
				{
					GD.PrintErr($"[{LogTag}] 等待游戏初始化超时，请确认场景能正常启动");
					return;
				}
				CallDeferred(nameof(TrySetup));
				return;
			}

			_done = true;
			Run(world);
		}

		private void Run(SimWorld world)
		{
			world.Grid.RefreshTerrainBounds();
			if (RTS.World.FogOfWar.Instance != null)
				RTS.World.FogOfWar.Instance.RevealAll = true;
			if (SpawnWallsEnabled)
				SpawnRandomWalls(world);
			SpawnAndCharge(world);
		}

		private void SpawnRandomWalls(SimWorld world)
		{
			// 默认无墙（避免阻挡两军接战）；需要寻路压测时用 STRESS_WALLS=1 开启
			if (OS.GetEnvironment("STRESS_WALLS") != "1")
				return;

			var grid = world.Grid;
			var rng = new Random(20260817);
			var cells = new HashSet<SimVector2I>();

			// 随机墙簇集中在两军之间的中央空隙（x 约 ±4 格），不压双方阵型
			for (int k = 0; k < WallClusters; k++)
			{
				int cx = rng.Next(-4, 5);
				int cy = rng.Next(-35, 36);
				bool horizontal = rng.Next(2) == 0;
				int len = rng.Next(3, 8);
				int thick = rng.Next(1, 3);

				for (int d = 0; d < len; d++)
				{
					for (int e = 0; e < thick; e++)
					{
						int x = horizontal ? cx + d : cx + e;
						int y = horizontal ? cy + e : cy + d;
						var cell = new SimVector2I(x, y);
						if (grid.TerrainCells.Contains(cell))
							cells.Add(cell);
					}
				}
			}

			foreach (var cell in cells)
				grid.StaticObstacles.Add(cell);
			grid.SyncStaticBlocking();

			if (Main.Instance.GetNodeOrNull("Ground") is MapGround3D ground)
				ground.RebuildWalls();

			GD.Print($"[{LogTag}] 已生成随机墙体 {cells.Count} 格（{WallClusters} 簇）");
		}

		// 双方朝对方阵型中心攻击移动（镜像），碰撞预算分摊到后续 Tick（本身就是压测的一部分）
		protected void IssueChargeCommands(SimWorld world, List<Unit> friendly, List<Unit> enemy, Vector2 friendlyCenter, Vector2 enemyCenter)
		{
			if (OS.GetEnvironment("STRESS_NO_FIGHT") == "1")
				return;

			var friendlyC = new FPVector2((FP)friendlyCenter.X, (FP)friendlyCenter.Y);
			var enemyC = new FPVector2((FP)enemyCenter.X, (FP)enemyCenter.Y);

			foreach (var unit in friendly)
			{
				if (unit.SimUnitData == null) continue;
				unit.SimUnitData.CommandMove(world, enemyC + (unit.SimUnitData.Position - friendlyC));
			}
			foreach (var unit in enemy)
			{
				if (unit.SimUnitData == null) continue;
				unit.SimUnitData.CommandMove(world, friendlyC + (unit.SimUnitData.Position - enemyC));
			}
		}

		public override void _Process(double delta)
		{
			if (!_done)
				return;

			// 确保全图视野真正生效（FogOfWar 可能晚于生成就绪）
			if (!_revealApplied && RTS.World.FogOfWar.Instance != null)
			{
				RTS.World.FogOfWar.Instance.RevealAll = true;
				_revealApplied = true;
			}

			_diagTimer += (float)delta;
			if (_diagTimer < 1f)
				return;
			_diagTimer = 0f;

			double setup = RenderingServer.GetFrameSetupTimeCpu() * 1000.0;
			ulong draws = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
			double timeProcess = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
			int simUnits = RTS.Core.SimManager.Instance?.World?.Units.Count ?? -1;
			bool reveal = RTS.World.FogOfWar.Instance?.RevealAll ?? false;
			GD.Print($"[{LogTag}] fps={Engine.GetFramesPerSecond():F0} setupCpu={setup:F1}ms process={timeProcess:F1}ms draws={draws} simUnits={simUnits} reveal={reveal}");
		}
	}
}
