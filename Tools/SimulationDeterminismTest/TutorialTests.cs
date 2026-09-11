using System;
using RTS.Data.Maps;
using RTS.Simulation.Scripting;
using RTS.Tutorial;
using FP = FixMath.NET.Fix64;

namespace SimulationDeterminismTest
{
	// =========================================================
	// 教程系统测试
	//
	// 两类断言：
	//   A. **内容正确性**：三套教程的每个条件/进度表达式都必须能解析；
	//      每个目标都必须有标题与完成条件；教程地图必须合法。
	//      —— 教程最容易坏的方式是"文案写了但条件永远不成立"，
	//      那种情况下玩家会卡在某个目标上，而程序不会报任何错。
	//   B. **推进逻辑**：条件成立 → 目标前进；顺序推进；全部完成 → 结束；
	//      进度状态哈希对进度变化敏感（双端教程进度分叉要能被脱卡检测发现）。
	// =========================================================
	internal static partial class Program
	{
		private static void RunTutorialTests()
		{
			// ---------- A1. 全部教程都存在且结构完整 ----------
			var all = TutorialRegistry.All;
			Check(all.Count >= 8, $"tutorial: at least 8 built-in tutorials (got {all.Count})");

			// 基础教程必须有且只有一套，并且排在最前面（面板默认选中它）
			Check(all[0].Tier == TutorialTier.Basics,
				$"tutorial: the first entry is the basics tutorial (got {all[0].Tier})");
			int basicsCount = 0;
			foreach (var t in all)
				if (t.Tier == TutorialTier.Basics) basicsCount++;
			Check(basicsCount == 1, $"tutorial: exactly one basics tutorial (got {basicsCount})");

			// 七个可玩阵营都必须有进阶教程
			string[] requiredRaces = { "Union", "Terran", "Demon", "Nano", "Plant", "Cave", "Wanderer" };
			foreach (string race in requiredRaces)
			{
				TutorialDefinition found = null;
				foreach (var t in all)
					if (t.RaceId == race && t.Tier == TutorialTier.Advanced) { found = t; break; }

				Check(found != null, $"tutorial: an advanced tutorial exists for race {race}");
				if (found == null) continue;

				Check(!string.IsNullOrWhiteSpace(found.DisplayName),
					$"tutorial[{race}]: has a display name");
				// 上限放到 10：基础教程要覆盖移动/采集/建造/生产/科技/攻击等
				// 多个通用操作，本来就需要更多步；进阶教程仍应保持精简。
				Check(found.ObjectiveCount >= 3 && found.ObjectiveCount <= 10,
					$"tutorial[{race}]: objective count is short and focused (got {found.ObjectiveCount})");
				Check(found.ExtraSpawns.Count > 0,
					$"tutorial[{race}]: provides starting content");
			}

			// ---------- A2. 每个目标都必须可解析、可判定 ----------
			foreach (var t in all)
			{
				for (int i = 0; i < t.Objectives.Count; i++)
				{
					var o = t.Objectives[i];
					string label = $"{t.RaceId}#{i + 1}({o.Id})";

					Check(!string.IsNullOrWhiteSpace(o.Id), $"tutorial[{label}]: has an id");
					Check(!string.IsNullOrWhiteSpace(o.Title), $"tutorial[{label}]: has a title");
					Check(!string.IsNullOrWhiteSpace(o.Detail), $"tutorial[{label}]: has detail text");

					// 完成条件可以为空（靠代码推进），但写了就必须能解析
					if (!string.IsNullOrWhiteSpace(o.CompleteWhen))
					{
						string err = SimExpr.Validate(o.CompleteWhen);
						Check(err == null, $"tutorial[{label}]: completion condition parses ({err})");
					}
					else
					{
						Check(false, $"tutorial[{label}]: has a completion condition");
					}

					if (!string.IsNullOrWhiteSpace(o.ProgressExpression))
					{
						string err = SimExpr.Validate(o.ProgressExpression);
						Check(err == null, $"tutorial[{label}]: progress expression parses ({err})");
					}
				}
			}

			// ---------- A4. 分层与资料页的数据模型 ----------
			{
				// 基础教程必须带理论页（资源/操作/建造规则），否则新手看完目标还是不会玩
				var basics = all[0];
				Check(basics.PageCount >= 3, $"basics: has reference pages (got {basics.PageCount})");
				foreach (var p in basics.Pages)
				{
					Check(!string.IsNullOrWhiteSpace(p.Title), "basics page: has a title");
					Check(!string.IsNullOrWhiteSpace(p.Section), $"basics page[{p.Title}]: has a section");
					Check(p.Lines.Count > 0, $"basics page[{p.Title}]: has content lines");
				}

				// PageSections / PagesIn 必须自洽：分组里的页数加起来 = 总页数
				foreach (var t in all)
				{
					int counted = 0;
					foreach (string section in t.PageSections())
						counted += t.PagesIn(section).Count;
					Check(counted == t.PageCount,
						$"tutorial[{t.Id}]: page sections cover every page ({counted}/{t.PageCount})");
				}

				// 进阶教程的图鉴依赖 ConfigDatabase，测试工程没有配置层，
				// 所以这里只断言"分离设计成立"：定义本身不依赖配置库也能构建。
				// 真正的图鉴内容由无头 Godot 侧的 DUMP_TUTORIALS=1 验证。
				Check(TutorialRegistry.Advanced().Count == 7,
					$"tutorial: seven advanced tutorials (got {TutorialRegistry.Advanced().Count})");
			}

			// ---------- A5. 教程条件用到的指令内置函数必须可解析 ----------
			{
				// 这些是基础/进阶教程里实际写的判定，解析失败会让目标永远无法完成
				string[] exprs =
				{
					"move_orders(player(1)) >= 1",
					"harvest_orders(player(1)) >= 1",
					"build_orders(player(1)) >= 1",
					"train_orders(player(1)) >= 1",
					"research_orders(player(1)) >= 1",
					"garrison_orders(player(1)) >= 1",
					"any_orders(player(1)) >= 1",
					"cmd_used(player(1), 'build')",
					"cmd_count(player(1), 'move') >= 2",
				};
				foreach (string e in exprs)
				{
					string err = SimExpr.Validate(e);
					Check(err == null, $"tutorial expr parses: {e} ({err})");
				}
			}

			// ---------- A6. 玩家指令统计（教程判定的基础） ----------
			{
				var stats = PlayerCommandStats.CreateNew();

				// 分类：教程依赖这些归类，归错了目标就会判不出来
				Check(PlayerCommandStats.Classify("Move") == PlayerCommandKind.Move, "cmd: Move → Move");
				Check(PlayerCommandStats.Classify("AttackMove") == PlayerCommandKind.Move, "cmd: AttackMove → Move");
				Check(PlayerCommandStats.Classify("Stop") == PlayerCommandKind.Move, "cmd: Stop → Move");
				Check(PlayerCommandStats.Classify("Attack") == PlayerCommandKind.Attack, "cmd: Attack → Attack");
				Check(PlayerCommandStats.Classify("Harvest") == PlayerCommandKind.Harvest, "cmd: Harvest → Harvest");
				Check(PlayerCommandStats.Classify("Repair") == PlayerCommandKind.Repair, "cmd: Repair → Repair");
				Check(PlayerCommandStats.Classify("Garrison") == PlayerCommandKind.Garrison, "cmd: Garrison → Garrison");
				Check(PlayerCommandStats.Classify("Build_BB") == PlayerCommandKind.Build,
					"cmd: Build_<Structure> → Build");
				Check(PlayerCommandStats.Classify("RebuildWreckage") == PlayerCommandKind.Build,
					"cmd: RebuildWreckage → Build");
				Check(PlayerCommandStats.Classify("TrainUnit") == PlayerCommandKind.Train,
					"cmd: Train* → Train");
				Check(PlayerCommandStats.Classify("SomethingUnknown") == PlayerCommandKind.Misc,
					"cmd: unknown → Misc（不能丢）");

				// 记录与查询
				Check(!stats.Has(1, PlayerCommandKind.Move), "cmd: starts empty");
				stats.Record(1, "Move", 10);
				stats.Record(1, "AttackMove", 20);
				Check(stats.Has(1, PlayerCommandKind.Move), "cmd: move recorded");
				Check(stats.GetCount(1, PlayerCommandKind.Move) == 2, "cmd: move count is 2");
				Check(stats.GetLastTick(1, PlayerCommandKind.Move) == 20, "cmd: last tick updates");
				Check(!stats.Has(2, PlayerCommandKind.Move), "cmd: per-team isolation");

				// 队伍号非法（0/负数）不该被记录：系统包 PlayerID 可能是 0
				stats.Record(0, "Move", 30);
				Check(!stats.Has(0, PlayerCommandKind.Move), "cmd: team 0 is ignored");

				// 哈希：状态不同必须不同哈希（否则教程进度分叉检测不到）
				var a = PlayerCommandStats.CreateNew();
				var b = PlayerCommandStats.CreateNew();
				Check(a.GetStateHash() == b.GetStateHash(), "cmd hash: identical states hash identically");

				a.Record(1, "Move", 5);
				Check(a.GetStateHash() != b.GetStateHash(), "cmd hash: recorded command changes hash");

				b.Record(1, "Move", 999);
				Check(a.GetStateHash() == b.GetStateHash(),
					"cmd hash: tick 不影响哈希（只比类别与次数）");
			}

			// ---------- A3. 教程自带地图合法 ----------
			{
				var map = TutorialRegistry.BuildTutorialMap();
				Check(map.HasTerrain, "tutorial map: has terrain");
				Check(map.Width == TutorialRegistry.MapSize && map.Height == TutorialRegistry.MapSize,
					$"tutorial map: is {TutorialRegistry.MapSize}x{TutorialRegistry.MapSize}");
				Check(map.SpawnPoints.Count >= 1, "tutorial map: has a player spawn");

				// 出生点必须在可走地面上（否则玩家开局就卡在墙里）
				var sp = map.SpawnPoints[0];
				Check(map.IsWalkableCell(sp.GridX, sp.GridY),
					"tutorial map: player spawn is on walkable ground");

				// 地图必须整体合法（复用真实校验器）
				var report = RTS.MapEditing.MapValidator.Validate(map);
				bool noErrors = report.ErrorCount == 0;
				string firstErr = "";
				foreach (var issue in report.Issues)
					if (issue.Severity == RTS.MapEditing.MapIssueSeverity.Error) { firstErr = issue.Message; break; }
				Check(noErrors, $"tutorial map: passes the real validator (errors={report.ErrorCount}: {firstErr})");

				// 幂等：两次构造必须完全一致（教程布局不能随机）
				var map2 = TutorialRegistry.BuildTutorialMap();
				bool identical = map.Terrain.Length == map2.Terrain.Length;
				for (int i = 0; identical && i < map.Terrain.Length; i++)
					if (map.Terrain[i] != map2.Terrain[i]) identical = false;
				Check(identical, "tutorial map: construction is deterministic");
			}

			// ---------- B1. 条件成立时目标推进 ----------
			{
				// 用测试用宿主/世界驱动：把资源查询做成"可调值"
				var tdef = new TutorialDefinition { Id = "t_test", DisplayName = "T", RaceId = "Union" };
				tdef.Objectives.Add(new TutorialObjective
				{
					Id = "o1", Title = "第一步", Detail = "d",
					CompleteWhen = "var_step >= 1",
				});
				tdef.Objectives.Add(new TutorialObjective
				{
					Id = "o2", Title = "第二步", Detail = "d",
					CompleteWhen = "var_step >= 2",
					CompleteText = "第二步完成",
				});

				var rt = new TutorialRuntime();
				rt.Start(tdef);
				Check(rt.Active, "runtime: becomes active after Start");
				Check(rt.ObjectiveIndex == 0, "runtime: starts at the first objective");

				var host = new TestExprHost();
				var world = new FakeTriggerWorld();

				host.Vars["var_step"] = FP.Zero;
				rt.Evaluate(world, host, 0);
				Check(rt.ObjectiveIndex == 0, "runtime: does not advance while the condition is false");

				host.Vars["var_step"] = FP.One;
				rt.Evaluate(world, host, 1);
				Check(rt.ObjectiveIndex == 1, "runtime: advances when the condition becomes true");
				Check(rt.CompletedCount == 1, "runtime: completion count increments");

				// 第二个目标还没达成
				rt.Evaluate(world, host, 2);
				Check(rt.ObjectiveIndex == 1, "runtime: waits on the second objective");

				host.Vars["var_step"] = (FP)2m;
				rt.Evaluate(world, host, 3);
				Check(!rt.Active, "runtime: becomes inactive when all objectives are done");
				Check(rt.CompletedCount == 2, "runtime: all objectives counted");
			}

			// ---------- B2. 空条件目标不会被自动推进 ----------
			{
				var tdef = new TutorialDefinition { Id = "t_manual", DisplayName = "M" };
				tdef.Objectives.Add(new TutorialObjective { Id = "m1", Title = "手动", Detail = "d" });

				var rt = new TutorialRuntime();
				rt.Start(tdef);

				var host = new TestExprHost();
				rt.Evaluate(new FakeTriggerWorld(), host, 0);
				Check(rt.ObjectiveIndex == 0, "runtime: objective without a condition is not auto-advanced");

				rt.AdvanceObjective(0);
				Check(!rt.Active, "runtime: manual advance completes the tutorial");
			}

			// ---------- B3. 坏条件不会打断、也不会推进 ----------
			{
				var tdef = new TutorialDefinition { Id = "t_bad", DisplayName = "B" };
				tdef.Objectives.Add(new TutorialObjective
				{
					Id = "bad", Title = "坏条件", Detail = "d",
					CompleteWhen = "1 / 0 > 0",
				});
				tdef.Objectives.Add(new TutorialObjective { Id = "after", Title = "后续", Detail = "d", CompleteWhen = "1 == 1" });

				var rt = new TutorialRuntime();
				rt.Start(tdef);

				var world = new FakeTriggerWorld();
				rt.Evaluate(world, host: new TestExprHost(), tick: 0);

				Check(rt.ObjectiveIndex == 0, "runtime: a throwing condition does not advance the tutorial");
				Check(rt.Active, "runtime: a throwing condition does not crash the tutorial");
				Check(world.Logs.Count > 0, "runtime: the failure is reported to the log");
			}

			// ---------- B4. 进度状态哈希对进度敏感 ----------
			{
				var tdef = new TutorialDefinition { Id = "t_hash", DisplayName = "H" };
				tdef.Objectives.Add(new TutorialObjective { Id = "h1", Title = "1", Detail = "d", CompleteWhen = "1 == 1" });
				tdef.Objectives.Add(new TutorialObjective { Id = "h2", Title = "2", Detail = "d", CompleteWhen = "1 == 1" });

				var a = new TutorialRuntime();
				var b = new TutorialRuntime();
				a.Start(tdef);
				b.Start(tdef);
				Check(a.GetStateHash() == b.GetStateHash(), "hash: identical tutorial states hash identically");

				a.Evaluate(new FakeTriggerWorld(), new TestExprHost(), 0);
				Check(a.GetStateHash() != b.GetStateHash(),
					"hash: diverged tutorial progress hashes differently (desync would be detected)");

				// 起步状态也参与哈希：未开始的教程与已开始的教程必须不同
				var idle = new TutorialRuntime();
				Check(idle.GetStateHash() != a.GetStateHash(), "hash: inactive vs active tutorial differ");
			}

			// ---------- B5. 快照内容合理（UI 依赖它）----------
			{
				var tdef = TutorialRegistry.Get("tutorial_union");
				Check(tdef != null, "registry: tutorial_union is resolvable by id");

				var rt = new TutorialRuntime();
				rt.Start(tdef);

				var snap = rt.GetSnapshot(new TestExprHost(), 0);
				Check(snap.Active, "snapshot: active tutorial reports Active");
				Check(snap.ObjectiveCount == tdef.ObjectiveCount, "snapshot: objective count matches");
				Check(!string.IsNullOrWhiteSpace(snap.Title), "snapshot: current objective has a title");
			}
		}
	}
}
