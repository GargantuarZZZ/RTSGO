using System;
using System.Collections.Generic;
using FixMath.NET;
using RTS.Data;
using RTS.Data.Maps;
using RTS.Simulation.Scripting;
using FP = FixMath.NET.Fix64;

namespace SimulationDeterminismTest
{
	// =========================================================
	// 地图脚本层：表达式引擎 + 触发器调度 的单元测试
	//
	// 覆盖：
	//   1. 词法/语法/优先级/括号
	//   2. Fix64 定点语义（不出现 double 漂移）
	//   3. 字符串拼接与比较（序数比较，不受区域设置影响）
	//   4. 短路求值（&& / || 不应多查世界）
	//   5. 错误处理（除数 0 / 语法错 / 未闭合引号）
	//   6. 触发器调度：相位、冷却、重复、启用开关、变量赋值先于动作
	//   7. 运行时状态哈希对状态变化敏感
	// =========================================================
	internal static partial class Program
	{
		/// <summary>测试用的表达式宿主：变量表 + 记录调用次数的内置函数。</summary>
		private sealed class TestExprHost : ISimExprHost
		{
			public readonly Dictionary<string, FP> Vars = new(StringComparer.Ordinal);
			public int BuiltinCalls;
			/// <summary>当前 tick（tick 变量与 tick() 函数共用同一个值）。</summary>
			public FP Tick = (FP)42;

			public SimExprValue ReadVariable(string name)
			{
				// 与真实 TriggerHost 一致：tick 既是变量也是函数，两者必须同值
				if (name == "tick" || name == "second" || name == "seconds" || name == "elapsed_seconds")
					return SimExprValue.FromNumber(Tick);

				return Vars.TryGetValue(name, out FP v) ? SimExprValue.FromNumber(v) : SimExprValue.Zero;
			}

			public SimExprValue CallBuiltin(string name, SimExprValue[] args)
			{
				BuiltinCalls++;
				FP Arg(int i) => args != null && i >= 0 && i < args.Length ? args[i].AsNumber() : FP.Zero;

				// 这里刻意复刻真实 TriggerHost 的内置函数集合，
				// 这样表达式测试覆盖的就是作者实际能用的函数。
				switch (name)
				{
					case "tick": return SimExprValue.FromNumber(Tick);
					case "resource": return SimExprValue.FromNumber((FP)100);
					case "units_in_region": return SimExprValue.FromNumber((FP)3);
					case "units": return SimExprValue.FromNumber((FP)7);
					case "built":
					case "has_structure": return SimExprValue.FromNumber((FP)8);
					case "count": return SimExprValue.FromNumber((FP)6);
					case "double_it": return SimExprValue.FromNumber(Arg(0) * (FP)2m);
					case "echo": return SimExprValue.FromString(args[0].AsString());
					case "player": return SimExprValue.FromNumber(Arg(0));

					case "min": { FP x = Arg(0), y = Arg(1); return SimExprValue.FromNumber(x < y ? x : y); }
					case "max": { FP x = Arg(0), y = Arg(1); return SimExprValue.FromNumber(x > y ? x : y); }
					case "abs": return SimExprValue.FromNumber(FP.Abs(Arg(0)));
					case "floor": return SimExprValue.FromNumber(FP.Floor(Arg(0)));
					case "ceil": return SimExprValue.FromNumber(FP.Ceiling(Arg(0)));
					case "round": return SimExprValue.FromNumber(FP.Round(Arg(0)));
					case "sqrt": return SimExprValue.FromNumber(FP.Sqrt(Arg(0)));
					case "clamp":
					{
						FP v = Arg(0), lo = Arg(1), hi = Arg(2);
						if (lo > hi) { FP t = lo; lo = hi; hi = t; }
						return SimExprValue.FromNumber(v < lo ? lo : (v > hi ? hi : v));
					}
					case "sign":
					{
						FP v = Arg(0);
						return SimExprValue.FromNumber(v > FP.Zero ? FP.One : (v < FP.Zero ? -FP.One : FP.Zero));
					}
					default: return SimExprValue.Zero;
				}
			}
		}

		private static FP Num(string expr, TestExprHost host = null)
		{
			host ??= new TestExprHost();
			return SimExpr.Evaluate(expr, host).AsNumber();
		}

		private static bool Bool(string expr, TestExprHost host = null)
		{
			host ??= new TestExprHost();
			return SimExpr.Evaluate(expr, host).AsBool();
		}

		private static string Text(string expr, TestExprHost host = null)
		{
			host ??= new TestExprHost();
			return SimExpr.Evaluate(expr, host).AsString();
		}

		private static void RunExpressionTests()
		{
			SimExpr.ClearCache();

			// ---------- 1. 算术与优先级 ----------
			Check(Num("1 + 2 * 3") == (FP)7m, "expression: multiplication binds tighter than addition");
			Check(Num("(1 + 2) * 3") == (FP)9m, "expression: parentheses override precedence");
			Check(Num("10 - 4 - 3") == (FP)3m, "expression: subtraction is left-associative");
			Check(Num("100 / 4 / 5") == (FP)5m, "expression: division is left-associative");
			Check(Num("17 % 5") == (FP)2m, "expression: modulo");
			Check(Num("-3 + 10") == (FP)7m, "expression: unary minus");
			Check(Num("2 * -3") == (FP)(-6), "expression: unary minus after operator");

			// ---------- 2. 定点语义 ----------
			// 0.1 + 0.2 在 double 下是 0.30000000000000004；Fix64 下必须精确等于 0.3
			Check(Num("0.1 + 0.2") == (FP)0.3m, "expression: 0.1 + 0.2 == 0.3 exactly (Fix64, no double drift)");
			// 1/3 在 Fix64 下是有限精度商，乘回 3 不回到 1 —— 这是定点数的**预期**行为，
			// 关键是它必须两端完全一致（可复现），而不是"数学上等于 1"。
			{
				FP a = Num("1 / 3 * 3");
				FP b = Num("1 / 3 * 3");
				Check(a == b, "expression: repeating division is reproducible (Fix64 truncation is deterministic)");
				Check(a > (FP)0.99m && a <= FP.One,
					$"expression: 1/3*3 stays just under 1 as expected (got {SimExprValue.FormatNumber(a)})");
			}

			// ---------- 3. 比较与逻辑 ----------
			Check(Bool("3 > 2 && 1 < 2"), "expression: && of two true comparisons");
			Check(!Bool("3 > 2 && 1 > 2"), "expression: && with one false");
			Check(Bool("3 > 2 || 1 > 2"), "expression: || with one true");
			Check(Bool("not (1 > 2)"), "expression: 'not' keyword");
			Check(Bool("!(1 > 2)"), "expression: '!' operator");
			Check(Bool("2 >= 2 && 2 <= 2"), "expression: >= and <=");
			Check(Bool("1 == 1 && 1 != 2"), "expression: == and !=");
			Check(Bool("true && 1"), "expression: true literal");
			Check(!Bool("false || 0"), "expression: false literal");

			// ---------- 4. 短路求值 ----------
			{
				var host = new TestExprHost();
				Bool("false && units_in_region(1,0,0,5) > 0", host);
				Check(host.BuiltinCalls == 0,
					$"expression: && short-circuits (builtin calls={host.BuiltinCalls})");

				host = new TestExprHost();
				Bool("true || units_in_region(1,0,0,5) > 0", host);
				Check(host.BuiltinCalls == 0,
					$"expression: || short-circuits (builtin calls={host.BuiltinCalls})");

				host = new TestExprHost();
				Bool("units_in_region(1,0,0,5) > 0 && false", host);
				Check(host.BuiltinCalls == 1,
					$"expression: left side still evaluated when needed (builtin calls={host.BuiltinCalls})");
			}

			// ---------- 5. 变量与内置函数 ----------
			{
				var host = new TestExprHost();
				host.Vars["var_gold"] = (FP)250m;
				Check(Num("var_gold", host) == (FP)250m, "expression: reads script variable");
				Check(Num("var_gold * 2", host) == (FP)500m, "expression: variable in arithmetic");
				Check(Num("undefined_var", host) == FP.Zero, "expression: undefined variable reads as 0 (lenient)");
				Check(Num("tick()", host) == (FP)42, "expression: builtin function call");
				Check(Num("double_it(21)", host) == (FP)42m, "expression: builtin with argument");

				// 回归：`tick` 作为变量与作为函数必须**给出同一个值**。
				// 真实 TriggerHost 曾经只实现了变量形式，漏了 tick()，
				// 于是作者写 `tick() >= 40` 时求值落到 default 返回 0，
				// 条件永远为假且不报错 —— 触发器"定义了却从不触发"。
				Check(Num("tick()", host) == Num("tick", host),
					"expression: tick() and variable tick agree (guards against a missing builtin case)");

				// 作者文档里列出的内置函数必须全部可用（未知函数会静默返回 0，
				// 所以这里逐个断言返回值，而不是只断言"不抛异常"）。
				var builtins = new (string Expr, FP Expected)[]
				{
					("player(3)", (FP)3m),
					("min(3,9)", (FP)3m),
					("max(3,9)", (FP)9m),
					("abs(0-7)", (FP)7m),
					("floor(3.7)", (FP)3m),
					("ceil(3.2)", (FP)4m),
					("round(3.5)", (FP)4m),
					("sqrt(16)", (FP)4m),
					("clamp(15,0,10)", (FP)10m),
					("sign(0-4)", (FP)(-1)),
				};
				foreach (var (expr, expected) in builtins)
					Check(Num(expr, host) == expected, $"builtin available: {expr}");
				Check(Num("max(3, 9)", host) == (FP)9m, "expression: max()");
				Check(Num("min(3, 9)", host) == (FP)3m, "expression: min()");
				Check(Num("clamp(15, 0, 10)", host) == (FP)10m, "expression: clamp() upper bound");
				Check(Num("clamp(-5, 0, 10)", host) == FP.Zero, "expression: clamp() lower bound");
				Check(Num("abs(0 - 7)", host) == (FP)7m, "expression: abs()");
				Check(Num("floor(3.7)", host) == (FP)3m, "expression: floor()");
				Check(Num("ceil(3.2)", host) == (FP)4m, "expression: ceil()");
				Check(Num("sign(0 - 4)", host) == (FP)(-1), "expression: sign()");
				// 队伍引用只是可读写法：player(3) 的值就是 3
				Check(Num("player(3)", host) == (FP)3m, "expression: player(n) evaluates to the team number");
				Check(Bool("player(2) == 2", host), "expression: player(n) compares as a plain number");
			}

			// ---------- 6. 字符串 ----------
			{
				var host = new TestExprHost();
				Check(Text("'hello'") == "hello", "expression: single-quoted string literal");
				Check(Text("\"hello\"") == "hello", "expression: double-quoted string literal");
				Check(Text("'a' + 'b'") == "ab", "expression: string concatenation");
				Check(Text("'count=' + 42") == "count=42", "expression: string + number concatenation");
				Check(Bool("'abc' == 'abc'"), "expression: string equality");
				Check(!Bool("'abc' == 'abd'"), "expression: string inequality");
				Check(Text("echo('hi')") == "hi", "expression: builtin returning string");
			}

			// ---------- 7. 错误处理 ----------
			{
				bool threw = false;
				try { Num("1 / 0"); }
				catch (SimExprException) { threw = true; }
				Check(threw, "expression: division by zero raises SimExprException");

				threw = false;
				try { Num("1 + "); }
				catch (SimExprException) { threw = true; }
				Check(threw, "expression: incomplete expression raises");

				threw = false;
				try { Num("(1 + 2"); }
				catch (SimExprException) { threw = true; }
				Check(threw, "expression: unbalanced parenthesis raises");

				threw = false;
				try { Num("'unterminated"); }
				catch (SimExprException) { threw = true; }
				Check(threw, "expression: unterminated string raises");

				threw = false;
				try { Num("1 & 2"); }
				catch (SimExprException) { threw = true; }
				Check(threw, "expression: single '&' is rejected with a helpful message");

				threw = false;
				try { Num("1 $ 2"); }
				catch (SimExprException) { threw = true; }
				Check(threw, "expression: unknown character raises");

				// Validate 返回可读错误而不是抛异常（编辑器保存前批量校验用）
				Check(SimExpr.Validate("1 + 1") == null, "validate: valid expression returns null");
				string err = SimExpr.Validate("1 +");
				Check(!string.IsNullOrEmpty(err), $"validate: invalid expression returns a message ({err})");
				Check(SimExpr.Validate("") == null, "validate: empty expression is treated as always-true");
			}

			// ---------- 8. 求值安全网 ----------
			{
				var host = new TestExprHost();
				// EvaluateBool 出错时返回 fallback，不抛
				bool r = SimExpr.EvaluateBool("1 / 0 > 0", host, fallback: true);
				Check(r, "EvaluateBool: returns fallback on error instead of throwing");
				Check(SimExpr.EvaluateNumber("1 / 0", host) == FP.Zero,
					"EvaluateNumber: returns zero on error");
			}

			SimExpr.ClearCache();
		}

		// =========================================================
		// 触发器调度测试
		// =========================================================

		private sealed class FakeTriggerWorld : ITriggerWorld
		{
			public readonly Dictionary<int, Dictionary<string, FP>> Resources = new();
			public readonly HashSet<string> Techs = new();
			public readonly List<string> Messages = new();
			public readonly List<string> Spawned = new();
			public readonly List<string> Logs = new();
			public int EndMatchWinner = -9999;
			public bool TeamExistsResult = true;
			public int UnitsInRegion;
			public bool PendingEvents;

			public int CurrentTick => 0;
			public bool HasPendingEvents => PendingEvents;
			public bool TeamExists(int teamId) => TeamExistsResult;
			public FP GetResource(int teamId, string resourceName) =>
				Resources.TryGetValue(teamId, out var m) && m.TryGetValue(resourceName, out FP v) ? v : FP.Zero;

			public void AddResource(int teamId, string resourceName, FP amount)
			{
				if (!Resources.TryGetValue(teamId, out var m))
					Resources[teamId] = m = new Dictionary<string, FP>();
				m.TryGetValue(resourceName, out FP cur);
				m[resourceName] = cur + amount;
			}

			public bool HasTech(int teamId, string techId) => Techs.Contains($"{teamId}:{techId}");
			public void GrantTech(int teamId, string techId, bool granted)
			{
				if (granted) Techs.Add($"{teamId}:{techId}");
				else Techs.Remove($"{teamId}:{techId}");
			}
			public int GetUsedSupply(int teamId) => 0;
			public int GetMaxSupply(int teamId) => 100;
			public int CountUnitsInRegion(int teamId, int cx, int cy, int r, bool all) => UnitsInRegion;
			public int CountStructuresInRegion(int teamId, int cx, int cy, int r, bool all) => 0;
			public int CountEntitiesOfTypeNear(string id, int cx, int cy, int r, int team) => 0;
			public int CountEntitiesOfType(string id, int team) => 6;
			public int CountUnitsOfType(string id, int team) => 7;
			public int CountBuiltStructuresOfType(string id, int team) => 8;

			// 玩家指令统计：用可控的字典模拟，方便断言 cmd_used / *_orders 内置函数
			public readonly Dictionary<(int Team, string Kind), int> PlayerCommands = new();

			public bool HasPlayerCommand(int teamId, PlayerCommandKind kind) =>
				PlayerCommands.TryGetValue((teamId, kind.ToString()), out int n) && n > 0;

			public int GetPlayerCommandCount(int teamId, PlayerCommandKind kind) =>
				PlayerCommands.TryGetValue((teamId, kind.ToString()), out int n) ? n : 0;

			public bool IsTeamDefeated(int teamId) => false;
			public int SpawnUnits(string unitId, int teamId, int cx, int cy, int count, int scatter)
			{
				Spawned.Add($"{unitId}:{teamId}:{cx},{cy}:{count}");
				return count;
			}
			public bool AreTeamsHostile(int a, int b) => a != b;
			public void SetDiplomacy(int a, int b, int rel) { }
			public void EndMatch(int winner) => EndMatchWinner = winner;
			public void BroadcastMessage(string text, int teamId) => Messages.Add(text);
			public void PlayFx(string fx, int cx, int cy, int r) { }
			public void Log(string message) => Logs.Add(message);
		}

		private static TriggerDefinition MakeTrigger(
			string id, TriggerPhase phase = TriggerPhase.EveryTick,
			bool repeatable = false, int cooldown = 0, int startTick = 0, int interval = 0,
			bool enabled = true)
		{
			return new TriggerDefinition
			{
				TriggerId = id,
				Phase = phase,
				Repeatable = repeatable,
				CooldownTicks = cooldown,
				StartTick = startTick,
				IntervalTicks = interval,
				EnabledAtStart = enabled,
			};
		}

		private static RtsMapData MakeMap(params TriggerDefinition[] triggers)
		{
			var map = new RtsMapData { MapId = "test", Width = 32, Height = 32 };
			map.Resize(32, 32);
			map.Fill(RtsMapData.SourceGrass);
			foreach (var t in triggers) map.Triggers.Add(t);
			return map;
		}

		/// <summary>把测试用的 RtsMapData 转成运行时地图，走与游戏相同的构建路径。</summary>
		private static MapTriggerRuntime MakeRuntime(RtsMapData map)
		{
			var rt = RTS.World.MapRuntime.FromData(map);
			return new MapTriggerRuntime(rt.Triggers, rt.FindRegion, rt.Width, rt.Height);
		}

		private static void RunTriggerTests()
		{
			// ---------- 1. 无条件 + 定时触发 ----------
			{
				var t = MakeTrigger("t1", TriggerPhase.Scheduled, startTick: 40);
				t.Actions.Add(new TriggerAction { Kind = TriggerActionKind.ShowMessage, Id = "hello" });
				var map = MakeMap(t);
				var runtime = MakeRuntime(map);
				var world = new FakeTriggerWorld();

				MapTriggerSystem.Evaluate(runtime, world, 39);
				Check(world.Messages.Count == 0, "trigger: scheduled trigger does not fire before StartTick");

				MapTriggerSystem.Evaluate(runtime, world, 40);
				Check(world.Messages.Count == 1 && world.Messages[0] == "hello",
					"trigger: scheduled trigger fires at StartTick");

				// 不可重复：再求值也不应再触发
				MapTriggerSystem.Evaluate(runtime, world, 41);
				Check(world.Messages.Count == 1, "trigger: non-repeatable trigger fires only once");
			}

			// ---------- 2. 周期触发 + 冷却 ----------
			{
				var t = MakeTrigger("tick_timer", TriggerPhase.Scheduled, repeatable: true,
					cooldown: 0, startTick: 0, interval: 20);
				t.Actions.Add(new TriggerAction { Kind = TriggerActionKind.ShowMessage, Id = "tick" });
				var runtime = MakeRuntime(MakeMap(t));
				var world = new FakeTriggerWorld();

				for (int tick = 0; tick <= 60; tick++)
					MapTriggerSystem.Evaluate(runtime, world, tick);

				// tick 0/20/40/60 = 4 次
				Check(world.Messages.Count == 4,
					$"trigger: repeating interval fires 4 times over 60 ticks (got {world.Messages.Count})");
			}

			// ---------- 3. 条件门控 ----------
			{
				var t = MakeTrigger("gated", TriggerPhase.EveryTick, repeatable: true, cooldown: 0);
				t.Conditions.Add(new TriggerCondition("units_in_region(1, 5, 5, 3) >= 3"));
				t.Actions.Add(new TriggerAction { Kind = TriggerActionKind.ShowMessage, Id = "breach" });
				var runtime = MakeRuntime(MakeMap(t));
				var world = new FakeTriggerWorld { UnitsInRegion = 1 };

				MapTriggerSystem.Evaluate(runtime, world, 1);
				Check(world.Messages.Count == 0, "trigger: condition false blocks the trigger");

				world.UnitsInRegion = 3;
				MapTriggerSystem.Evaluate(runtime, world, 2);
				Check(world.Messages.Count == 1, "trigger: condition true lets the trigger fire");
			}

			// ---------- 4. 冷却限制重复频率 ----------
			{
				var t = MakeTrigger("cd", TriggerPhase.EveryTick, repeatable: true, cooldown: 50);
				t.Actions.Add(new TriggerAction { Kind = TriggerActionKind.ShowMessage, Id = "x" });
				var runtime = MakeRuntime(MakeMap(t));
				var world = new FakeTriggerWorld();

				for (int tick = 0; tick < 100; tick++)
					MapTriggerSystem.Evaluate(runtime, world, tick);

				// 冷却 50 tick：触发在 0 和 50，共 2 次
				Check(world.Messages.Count == 2,
					$"trigger: cooldown limits repeats to 2 in 100 ticks (got {world.Messages.Count})");
			}

			// ---------- 5. 变量赋值先于动作（作者依赖的先后顺序）----------
			{
				var t = MakeTrigger("assign_first", TriggerPhase.Scheduled, startTick: 0);
				t.Assignments.Add(new TriggerAssignment("reward", "10 * 3"));
				t.Actions.Add(new TriggerAction
				{
					Kind = TriggerActionKind.AddResource,
					TargetExpression = "player(1)",
					Id = "Metal",
					AmountExpression = "reward",
				});
				var runtime = MakeRuntime(MakeMap(t));
				var world = new FakeTriggerWorld();

				MapTriggerSystem.Evaluate(runtime, world, 0);
				Check(world.GetResource(1, "Metal") == (FP)30m,
					$"trigger: assignments run before actions (Metal={world.GetResource(1, "Metal")})");
				Check(runtime.TryGetVariable("reward", out FP rv) && rv == (FP)30m,
					"trigger: variable persisted after firing");
			}

			// ---------- 6. 目标为全体时给所有存在的队伍 ----------
			{
				var t = MakeTrigger("all_players", TriggerPhase.Scheduled, startTick: 0);
				t.Actions.Add(new TriggerAction
				{
					Kind = TriggerActionKind.AddResource,
					TargetExpression = "",     // 空 = 全体
					Id = "Metal",
					AmountExpression = "5",
				});
				var runtime = MakeRuntime(MakeMap(t));
				var world = new FakeTriggerWorld();
				MapTriggerSystem.Evaluate(runtime, world, 0);

				bool everyone = true;
				for (int team = 1; team <= 16; team++)
					if (world.GetResource(team, "Metal") != (FP)5m) everyone = false;
				Check(everyone, "trigger: empty target gives resources to every existing team");
			}

			// ---------- 7. 多阶段：SetTriggerEnabled 串起后续阶段 ----------
			//
			// 语义说明（按地图里的数组顺序在同一趟内求值）：
			//   stage1 在 stage2 **之前**，所以 stage1 本趟启用 stage2 后，
			//   同一趟里 stage2 就会被求值并触发——这正是作者期望的"立刻接上下一阶段"。
			//   若想让 stage2 晚一趟，把 stage2 放在 stage1 前面，或给它加 StartTick 条件。
			{
				var stage1 = MakeTrigger("stage1", TriggerPhase.Scheduled, startTick: 0);
				stage1.Actions.Add(new TriggerAction
				{
					Kind = TriggerActionKind.SetTriggerEnabled,
					Id = "stage2",
					Count = 1,
				});
				var stage2 = MakeTrigger("stage2", TriggerPhase.Scheduled, startTick: 0, enabled: false, repeatable: true);
				stage2.Actions.Add(new TriggerAction { Kind = TriggerActionKind.ShowMessage, Id = "stage2_fired" });

				var runtime = MakeRuntime(MakeMap(stage1, stage2));
				var world = new FakeTriggerWorld();

				Check(!runtime.IsTriggerEnabled("stage2"),
					"multistage: stage2 starts disabled as authored");

				MapTriggerSystem.Evaluate(runtime, world, 0);
				Check(world.Messages.Count == 1 && world.Messages[0] == "stage2_fired",
					"multistage: stage1 enables stage2 and it fires later in the same pass (array order)");
				Check(runtime.IsTriggerEnabled("stage2"),
					"multistage: repeatable stage2 stays enabled after firing");

				// 反向用例：stage2 排在 stage1 前面时，本趟来不及触发
				var early = MakeTrigger("early", TriggerPhase.Scheduled, startTick: 0, enabled: false);
				early.Actions.Add(new TriggerAction { Kind = TriggerActionKind.ShowMessage, Id = "early_fired" });
				var enabler = MakeTrigger("enabler", TriggerPhase.Scheduled, startTick: 0);
				enabler.Actions.Add(new TriggerAction
				{
					Kind = TriggerActionKind.SetTriggerEnabled,
					Id = "early",
					Count = 1,
				});

				var runtime2 = MakeRuntime(MakeMap(early, enabler));
				var world2 = new FakeTriggerWorld();
				MapTriggerSystem.Evaluate(runtime2, world2, 0);
				Check(world2.Messages.Count == 0,
					"multistage: a trigger enabled by a LATER trigger waits for the next pass");
				Check(runtime2.IsTriggerEnabled("early"), "multistage: enabler ran and enabled 'early'");

				MapTriggerSystem.Evaluate(runtime2, world2, 1);
				Check(world2.Messages.Count == 1 && world2.Messages[0] == "early_fired",
					"multistage: 'early' fires on the following pass");
			}

			// ---------- 8. 变量可以引用上一条赋值（链式）----------
			{
				var t = MakeTrigger("chain", TriggerPhase.Scheduled, startTick: 0);
				t.Assignments.Add(new TriggerAssignment("a", "2"));
				t.Assignments.Add(new TriggerAssignment("b", "a * 5"));
				t.Assignments.Add(new TriggerAssignment("c", "a + b"));
				var runtime = MakeRuntime(MakeMap(t));
				var world = new FakeTriggerWorld();

				MapTriggerSystem.Evaluate(runtime, world, 0);
				runtime.TryGetVariable("c", out FP c);
				Check(c == (FP)12m, $"multistage: chained assignments evaluate in order (c={SimExprValue.FormatNumber(c)})");
			}

			// ---------- 9. EventDriven 只在有事件时求值 ----------
			{
				var t = MakeTrigger("on_event", TriggerPhase.EventDriven, repeatable: true, cooldown: 0);
				t.Actions.Add(new TriggerAction { Kind = TriggerActionKind.ShowMessage, Id = "evt" });
				var runtime = MakeRuntime(MakeMap(t));
				var world = new FakeTriggerWorld { PendingEvents = false };

				MapTriggerSystem.Evaluate(runtime, world, 0);
				Check(world.Messages.Count == 0, "eventdriven: no events means no evaluation");

				world.PendingEvents = true;
				MapTriggerSystem.Evaluate(runtime, world, 1);
				Check(world.Messages.Count == 1, "eventdriven: fires once events exist");
			}

			// ---------- 10. 坏条件不会打断模拟 ----------
			{
				var t = MakeTrigger("bad_cond", TriggerPhase.EveryTick);
				t.Conditions.Add(new TriggerCondition("1 / 0 > 0"));
				t.Actions.Add(new TriggerAction { Kind = TriggerActionKind.ShowMessage, Id = "should_not_fire" });
				var runtime = MakeRuntime(MakeMap(t));
				var world = new FakeTriggerWorld();

				int fired = MapTriggerSystem.Evaluate(runtime, world, 0);
				Check(fired == 0 && world.Messages.Count == 0,
					"robustness: malformed condition does not fire and does not throw");
				Check(!string.IsNullOrEmpty(runtime.LastError),
					"robustness: error is recorded for the editor/log");
			}

			// ---------- 11. 运行时哈希对状态变化敏感 ----------
			{
				var t1 = MakeTrigger("h1", TriggerPhase.EveryTick, repeatable: true, cooldown: 0);
				t1.Actions.Add(new TriggerAction { Kind = TriggerActionKind.SetVariable, Id = "count", AmountExpression = "count + 1" });

				var a = MakeRuntime(MakeMap(t1));
				var b = MakeRuntime(MakeMap(t1));

				long hashA0 = a.GetStateHash();
				long hashB0 = b.GetStateHash();
				Check(hashA0 == hashB0, "hash: identical runtimes hash identically");

				MapTriggerSystem.Evaluate(a, new FakeTriggerWorld(), 0);
				long hashA1 = a.GetStateHash();
				Check(hashA1 != hashA0, "hash: variable change alters the runtime hash");
				Check(a.GetStateHash() != b.GetStateHash(), "hash: diverged runtimes hash differently");

				// 两个都推进同样的步数后必须重新一致（确定性）
				MapTriggerSystem.Evaluate(b, new FakeTriggerWorld(), 0);
				Check(a.GetStateHash() == b.GetStateHash(),
					"hash: runtimes converge again when stepped identically (determinism)");
			}

			// ---------- 12. 无触发器地图直接短路 ----------
			{
				var runtime = MakeRuntime(new RtsMapData { MapId = "empty" });
				Check(!runtime.HasTriggers, "runtime: map without triggers reports HasTriggers=false");
				Check(MapTriggerSystem.Evaluate(runtime, new FakeTriggerWorld(), 0) == 0,
					"runtime: evaluating a trigger-less map is a no-op");
			}
		}
	}
}
