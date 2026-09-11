using System;
using System.Collections.Generic;
using FixMath.NET;
using RTS.Data;
using RTS.Data.Maps;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation.Scripting
{
	// =========================================================
	// 触发器调度器（无状态）
	//
	// 每个 tick 由 SimManager 调用 Evaluate(runtime, world, tick)。
	// 执行顺序严格固定：
	//    1. 按地图里 Triggers 的**数组顺序**遍历（作者可控的确定性顺序，不做 ID 排序）
	//    2. 跳过未启用 / 已触发且不可重复 / 仍在冷却 / 相位不匹配的
	//    3. Conditions 全部为真（AND）
	//    4. Assignments（变量赋值）→ 再跑 Actions（动作可以引用刚写入的变量）
	//    5. 记冷却与触发位
	//
	// 为什么按数组顺序而不是 ID 排序：
	//   作者需要"先扣钱再提示"这类顺序控制；数组顺序是地图数据的一部分，
	//   两端加载同一份地图必然一致，比隐式按 ID 排序更符合作者直觉。
	// =========================================================

	public static class MapTriggerSystem
	{
		/// <summary>
		/// 造一个表达式宿主给教程等外部系统复用。
		/// 教程的完成条件与触发器条件**共用同一套变量与内置函数**，
		/// 避免"教程里能判定的东西触发器里判不了"这种割裂。
		/// </summary>
		public static ISimExprHost CreateHost(MapTriggerRuntime runtime, ITriggerWorld world, int tick) =>
			new TriggerHost(runtime, world, tick);

		/// <summary>
		/// 求值并触发所有满足条件的触发器。
		/// 返回本 tick 实际触发的触发器数量（诊断用，不参与哈希）。
		/// </summary>
		public static int Evaluate(MapTriggerRuntime runtime, ITriggerWorld world, int tick)
		{
			if (runtime == null || world == null || !runtime.HasTriggers)
				return 0;

			var triggers = runtime.Triggers;
			TriggerHost host = null;
			int fired = 0;

			foreach (var def in triggers)
			{
				if (def == null || string.IsNullOrEmpty(def.TriggerId))
					continue;

				var state = runtime.GetOrCreateState(def.TriggerId);
				if (!state.Enabled)
					continue;

				// 非重复触发器触发过一次就不再求值
				if (state.Fired && !def.Repeatable)
					continue;

				// 相位检查
				if (def.Phase == TriggerPhase.Scheduled)
				{
					if (tick < state.NextDueTick)
						continue;
				}
				else if (def.Phase == TriggerPhase.EventDriven)
				{
					// 事件驱动：本帧没有事件入队时不消耗求值。
					// 事件由 SimManager 侧采集（单位死亡/建筑被毁等），
					// 存成"本 tick 发生过的 (event, teamId)"列表供条件查询。
					if (!world.HasPendingEvents)
						continue;
				}

				// 冷却（Repeatable 才需要；不可重复的已经在上面挡掉）
				if (def.Repeatable && def.CooldownTicks > 0 &&
					state.LastFiredTick != int.MinValue &&
					tick - state.LastFiredTick < def.CooldownTicks)
					continue;

				host ??= new TriggerHost(runtime, world, tick);
				host.SetCurrent(def);

				if (!EvaluateConditions(def, host, runtime))
					continue;

				ApplyAssignments(def, host, runtime);
				ApplyActions(def, host, runtime, world);

				runtime.NoteFired(state, tick, def, def.Repeatable);
				fired++;
			}

			return fired;
		}

		private static bool EvaluateConditions(TriggerDefinition def, TriggerHost host, MapTriggerRuntime runtime)
		{
			if (def.Conditions == null || def.Conditions.Count == 0)
				return true; // 无条件 = 时间到就触发

			bool strict = def.EvalMode == TriggerEvalMode.Strict;
			foreach (var cond in def.Conditions)
			{
				if (cond == null) continue;
				if (string.IsNullOrWhiteSpace(cond.Expression))
					continue;

				bool result;
				try
				{
					result = SimExpr.Evaluate(cond.Expression, host).AsBool();
				}
				catch (SimExprException ex)
				{
					// 条件写错时：严格模式记错并跳过，宽松模式当作不满足
					runtime.SetError($"[Trigger:{def.TriggerId}] 条件求值失败: {ex.Message}");
					if (strict)
					{
						host.World.Log(runtime.LastError);
						return false;
					}
					return false;
				}

				if (!result) return false;
			}
			return true;
		}

		private static void ApplyAssignments(TriggerDefinition def, TriggerHost host, MapTriggerRuntime runtime)
		{
			if (def.Assignments == null) return;

			foreach (var assign in def.Assignments)
			{
				if (assign == null || string.IsNullOrEmpty(assign.Variable)) continue;

				FP value;
				if (string.IsNullOrWhiteSpace(assign.ValueExpression))
				{
					value = (FP)assign.Value;
				}
				else
				{
					try
					{
						value = SimExpr.Evaluate(assign.ValueExpression, host).AsNumber();
					}
					catch (SimExprException ex)
					{
						runtime.SetError($"[Trigger:{def.TriggerId}] 赋值 '{assign.Variable}' 失败: {ex.Message}");
						continue;
					}
				}

				runtime.SetVariable(assign.Variable, value);
			}
		}

		private static void ApplyActions(TriggerDefinition def, TriggerHost host, MapTriggerRuntime runtime, ITriggerWorld world)
		{
			if (def.Actions == null) return;

			foreach (var action in def.Actions)
			{
				if (action == null) continue;
				try
				{
					ExecuteAction(action, host, runtime, world, def);
				}
				catch (SimExprException ex)
				{
					runtime.SetError($"[Trigger:{def.TriggerId}] 动作 {action.Kind} 求值失败: {ex.Message}");
				}
				catch (Exception ex)
				{
					// 动作执行绝不能打断模拟：记录后继续
					runtime.SetError($"[Trigger:{def.TriggerId}] 动作 {action.Kind} 执行异常: {ex.Message}");
				}
			}
		}

		private static void ExecuteAction(
			TriggerAction action, TriggerHost host, MapTriggerRuntime runtime,
			ITriggerWorld world, TriggerDefinition def)
		{
			// 目标队伍：未填时默认全场（0 表示"对所有存在的队伍"）
			int teamId = ResolveTeam(action.TargetExpression, host, world, 0);

			switch (action.Kind)
			{
				case TriggerActionKind.SpawnUnit:
				{
					if (string.IsNullOrEmpty(action.Id)) break;
					int count = Math.Max(1, action.Count);
					if (!string.IsNullOrWhiteSpace(action.AmountExpression))
						count = Math.Max(1, (int)SimExpr.Evaluate(action.AmountExpression, host).AsNumber());

					// teamId <= 0 时用中立（-1）
					int spawnTeam = teamId > 0 ? teamId : -1;
					world.SpawnUnits(action.Id, spawnTeam, action.GridX, action.GridY, count, action.RadiusTiles);
					break;
				}

				case TriggerActionKind.AddResource:
				{
					if (string.IsNullOrEmpty(action.Id)) break;
					FP amount = string.IsNullOrWhiteSpace(action.AmountExpression)
						? (FP)action.Count
						: SimExpr.Evaluate(action.AmountExpression, host).AsNumber();

					if (teamId > 0)
					{
						world.AddResource(teamId, action.Id, amount);
					}
					else
					{
						// 未指定目标 = 全体玩家
						for (int t = 1; t <= 16; t++)
							if (world.TeamExists(t))
								world.AddResource(t, action.Id, amount);
					}
					break;
				}

				case TriggerActionKind.GrantTech:
				{
					if (string.IsNullOrEmpty(action.Id) || teamId <= 0) break;
					world.GrantTech(teamId, action.Id, true);
					break;
				}

				case TriggerActionKind.ShowMessage:
				{
					// Id 是本地化 key 或字面文本；AmountExpression 可做参数（暂不支持格式化，保留接口）
					string key = action.Id ?? "";
					if (string.IsNullOrEmpty(key) && !string.IsNullOrWhiteSpace(action.TargetExpression))
						key = SimExpr.Evaluate(action.TargetExpression, host).AsString();
					if (!string.IsNullOrEmpty(key))
						world.BroadcastMessage(key, teamId);
					break;
				}

				case TriggerActionKind.SetVariable:
				{
					if (string.IsNullOrEmpty(action.Id)) break;
					FP value = string.IsNullOrWhiteSpace(action.AmountExpression)
						? (FP)action.Count
						: SimExpr.Evaluate(action.AmountExpression, host).AsNumber();
					runtime.SetVariable(action.Id, value);
					break;
				}

				case TriggerActionKind.SetTriggerEnabled:
				{
					if (string.IsNullOrEmpty(action.Id)) break;
					bool enable = action.Count != 0;
					if (!string.IsNullOrWhiteSpace(action.TargetExpression))
						enable = SimExpr.Evaluate(action.TargetExpression, host).AsBool();
					runtime.SetTriggerEnabled(action.Id, enable);
					break;
				}

				case TriggerActionKind.SetDiplomacy:
				{
					int other = ResolveTeam(action.AmountExpression, host, world, 0);
					if (teamId > 0 && other > 0)
						world.SetDiplomacy(teamId, other, action.Count);
					break;
				}

				case TriggerActionKind.PlayFx:
				{
					world.PlayFx(action.Id ?? "", action.GridX, action.GridY, action.RadiusTiles);
					break;
				}

				case TriggerActionKind.EndMatch:
				{
					world.EndMatch(teamId);
					break;
				}
			}
		}

		/// <summary>
		/// 把"目标表达式"解析成队伍号。
		/// 空表达式 → fallback；"player(3)"/"3"/变量都走同一套求值。
		/// </summary>
		private static int ResolveTeam(string expression, TriggerHost host, ITriggerWorld world, int fallback)
		{
			if (string.IsNullOrWhiteSpace(expression))
				return fallback;

			try
			{
				FP v = SimExpr.Evaluate(expression, host).AsNumber();
				return (int)v;
			}
			catch (SimExprException)
			{
				return fallback;
			}
		}
	}

	// =========================================================
	// 表达式宿主实现：把 runtime 的变量 + world 的查询暴露给表达式
	// =========================================================

	internal sealed class TriggerHost : ISimExprHost
	{
		private readonly MapTriggerRuntime _runtime;
		private readonly int _tick;
		private TriggerDefinition _current;

		public ITriggerWorld World { get; }

		/// <summary>触发器自身状态变量前缀：trigger_&lt;id&gt;_fired / _count / _cooldown_until。</summary>
		public const string TriggerVarPrefix = "trigger_";

		public TriggerHost(MapTriggerRuntime runtime, ITriggerWorld world, int tick)
		{
			_runtime = runtime;
			World = world;
			_tick = tick;
		}

		public void SetCurrent(TriggerDefinition def) => _current = def;

		public SimExprValue ReadVariable(string name)
		{
			if (string.IsNullOrEmpty(name)) return SimExprValue.Zero;

			// 1) 内置：当前 tick 与秒数
			switch (name)
			{
				case "tick": return SimExprValue.FromNumber((FP)_tick);
				case "second":
				case "seconds":
				case "elapsed_seconds": return SimExprValue.FromNumber((FP)_tick / (FP)20m);
				case "map_width": return SimExprValue.FromNumber((FP)_runtime.MapWidth);
				case "map_height": return SimExprValue.FromNumber((FP)_runtime.MapHeight);
			}

			// 2) 触发器自身状态：trigger_<id>_fired / trigger_<id>_count
			if (name.StartsWith(TriggerVarPrefix, StringComparison.Ordinal))
			{
				if (TryReadTriggerSelfVar(name, out FP selfValue))
					return SimExprValue.FromNumber(selfValue);
			}

			// 3) 脚本变量
			if (_runtime.TryGetVariable(name, out FP value))
				return SimExprValue.FromNumber(value);

			// 4) 只读常量
			if (_runtime.TryGetConstant(name, out FP constant))
				return SimExprValue.FromNumber(constant);

			// 未定义：宽松模式当 0（并保证确定性——两端都当 0）
			return SimExprValue.Zero;
		}

		/// <summary>解析 trigger_&lt;id&gt;_fired / trigger_&lt;id&gt;_count。</summary>
		private bool TryReadTriggerSelfVar(string name, out FP value)
		{
			value = FP.Zero;
			string body = name.Substring(TriggerVarPrefix.Length);

			if (body.EndsWith("_fired", StringComparison.Ordinal))
			{
				string id = body.Substring(0, body.Length - "_fired".Length);
				value = _runtime.TryGetState(id, out var s) && s.Fired ? FP.One : FP.Zero;
				return true;
			}
			if (body.EndsWith("_count", StringComparison.Ordinal))
			{
				string id = body.Substring(0, body.Length - "_count".Length);
				value = _runtime.TryGetState(id, out var s)
					? (FP)Math.Max(0, s.FireCount)
					: FP.Zero;
				return true;
			}
			if (body.EndsWith("_enabled", StringComparison.Ordinal))
			{
				string id = body.Substring(0, body.Length - "_enabled".Length);
				value = _runtime.TryGetState(id, out var s) && s.Enabled ? FP.One : FP.Zero;
				return true;
			}
			return false;
		}

		public SimExprValue CallBuiltin(string name, SimExprValue[] args)
		{
			switch (name)
			{
				// --- 时间 ---
				// 注意：`tick` 作为**变量**和作为**函数**都要能用。
				// 曾经漏了函数形式 `tick()`，导致作者写 tick() >= 40 时
				// CallBuiltin 落到 default 返回 0，条件永远为假且不报错——
				// 触发器"定义了却从不触发"就是这么来的。
				case "tick": return SimExprValue.FromNumber((FP)_tick);
				case "second":
				case "seconds":
				case "elapsed_seconds": return SimExprValue.FromNumber((FP)_tick / (FP)20m);
				case "map_width": return SimExprValue.FromNumber((FP)_runtime.MapWidth);
				case "map_height": return SimExprValue.FromNumber((FP)_runtime.MapHeight);

				// --- 队伍引用（只是可读写法，值就是队伍号）---
				case "player":
					return SimExprValue.FromNumber(arg(args, 0));

				// --- 基础数学（FixMath.NET 没有 Min/Max/Clamp，这里就地实现）---
				case "min":
				{
					FP x = arg(args, 0), y = arg(args, 1);
					return SimExprValue.FromNumber(x < y ? x : y);
				}
				case "max":
				{
					FP x = arg(args, 0), y = arg(args, 1);
					return SimExprValue.FromNumber(x > y ? x : y);
				}
				case "abs": return SimExprValue.FromNumber(FP.Abs(arg(args, 0)));
				case "floor": return SimExprValue.FromNumber(FP.Floor(arg(args, 0)));
				case "ceil": return SimExprValue.FromNumber(FP.Ceiling(arg(args, 0)));
				case "round": return SimExprValue.FromNumber(FP.Round(arg(args, 0)));
				case "sqrt": return SimExprValue.FromNumber(FP.Sqrt(arg(args, 0)));
				case "clamp":
				{
					FP v = arg(args, 0), lo = arg(args, 1), hi = arg(args, 2);
					if (lo > hi) { FP t = lo; lo = hi; hi = t; }
					return SimExprValue.FromNumber(v < lo ? lo : (v > hi ? hi : v));
				}
				case "sign":
				{
					FP v = arg(args, 0);
					return SimExprValue.FromNumber(v > FP.Zero ? FP.One : (v < FP.Zero ? -FP.One : FP.Zero));
				}

				// --- 资源/科技/人口 ---
				case "resource":
				{
					int team = (int)arg(args, 0);
					string res = args.Length > 1 ? args[1].AsString() : "";
					return SimExprValue.FromNumber(World.GetResource(team, res));
				}
				case "has_tech":
				{
					int team = (int)arg(args, 0);
					string tech = args.Length > 1 ? args[1].AsString() : "";
					return SimExprValue.FromBool(World.HasTech(team, tech));
				}
				case "supply_used": return SimExprValue.FromNumber((FP)World.GetUsedSupply((int)arg(args, 0)));
				case "supply_max": return SimExprValue.FromNumber((FP)World.GetMaxSupply((int)arg(args, 0)));

				// --- 世界查询 ---
				case "units_in_region":
				{
					// units_in_region(team, cx, cy, r)  或 units_in_region("all", cx, cy, r)
					bool all = args.Length > 0 && args[0].Type == SimExprType.String;
					int team = all ? 0 : (int)arg(args, 0);
					int cx = (int)arg(args, 1);
					int cy = (int)arg(args, 2);
					int r = (int)arg(args, 3);
					return SimExprValue.FromNumber((FP)World.CountUnitsInRegion(team, cx, cy, r, all));
				}
				case "units_in":
				{
					// units_in("region_id", team) —— 用命名区域，作者改区域不用改触发器
					string regionId = args.Length > 0 ? args[0].AsString() : "";
					int team = args.Length > 1 ? (int)arg(args, 1) : 0;
					var region = _runtime.RegionLookup?.Invoke(regionId);
					if (region == null) return SimExprValue.Zero;
					return SimExprValue.FromNumber((FP)World.CountUnitsInRegion(
						team, region.GridX, region.GridY, RegionRadius(region), team <= 0));
				}
				case "structures_in":
				{
					string regionId = args.Length > 0 ? args[0].AsString() : "";
					int team = args.Length > 1 ? (int)arg(args, 1) : 0;
					var region = _runtime.RegionLookup?.Invoke(regionId);
					if (region == null) return SimExprValue.Zero;
					return SimExprValue.FromNumber((FP)World.CountStructuresInRegion(
						team, region.GridX, region.GridY, RegionRadius(region), team <= 0));
				}
				case "count_near":
				{
					// count_near("Tower", cx, cy, r, team)
					string id = args.Length > 0 ? args[0].AsString() : "";
					int cx = (int)arg(args, 1);
					int cy = (int)arg(args, 2);
					int r = (int)arg(args, 3);
					int team = args.Length > 4 ? (int)arg(args, 4) : 0;
					return SimExprValue.FromNumber((FP)World.CountEntitiesOfTypeNear(id, cx, cy, r, team));
				}
				case "count":
				{
					// count("Tower", team)  单位 + 已完工建筑
					string id = args.Length > 0 ? args[0].AsString() : "";
					int team = args.Length > 1 ? (int)arg(args, 1) : 0;
					return SimExprValue.FromNumber((FP)World.CountEntitiesOfType(id, team));
				}

				// --- 教程/目标常用：区分"造出单位"与"造好建筑" ---
				case "units":
				{
					// units("RifleMan", team)  只数单位
					string id = args.Length > 0 ? args[0].AsString() : "";
					int team = args.Length > 1 ? (int)arg(args, 1) : 0;
					return SimExprValue.FromNumber((FP)World.CountUnitsOfType(id, team));
				}
				case "built":
				case "has_structure":
				{
					// built("BB", team)  只数已完工建筑（蓝图/施工中不算）
					string id = args.Length > 0 ? args[0].AsString() : "";
					int team = args.Length > 1 ? (int)arg(args, 1) : 0;
					return SimExprValue.FromNumber((FP)World.CountBuiltStructuresOfType(id, team));
				}
				case "defeated":
					return SimExprValue.FromBool(World.IsTeamDefeated((int)arg(args, 0)));
				case "hostile":
					return SimExprValue.FromBool(World.AreTeamsHostile((int)arg(args, 0), (int)arg(args, 1)));

				// --- 玩家指令（教程：判定"玩家真的按了这个操作"）---
				//
				// 教程里"移动单位/建造/研究"这类目标，光看世界状态判不出来：
				// 单位本来就在动、建筑本来就有。必须问"这个玩家下过这类指令吗"。
				//
				// 两个入口：
				//   cmd_used(team, "kind")  → 0/1，按**类**判定（推荐，作者不用记 ActionId）
				//   cmd_count(team, "kind") → 次数
				// kind 取值：move / attack / harvest / repair / build / train / research / misc
				case "cmd_used":
					return SimExprValue.FromBool(
						World.HasPlayerCommand((int)arg(args, 0), ParseCommandKind(args, 1)));
				case "cmd_count":
					return SimExprValue.FromNumber((FP)World.GetPlayerCommandCount(
						(int)arg(args, 0), ParseCommandKind(args, 1)));

				// 语义化别名：教程文本读起来更直白
				case "move_orders": return SimExprValue.FromNumber(CommandCountOf(args, PlayerCommandKind.Move));
				case "attack_orders": return SimExprValue.FromNumber(CommandCountOf(args, PlayerCommandKind.Attack));
				case "harvest_orders": return SimExprValue.FromNumber(CommandCountOf(args, PlayerCommandKind.Harvest));
				case "build_orders": return SimExprValue.FromNumber(CommandCountOf(args, PlayerCommandKind.Build));
				case "train_orders": return SimExprValue.FromNumber(CommandCountOf(args, PlayerCommandKind.Train));
				case "research_orders": return SimExprValue.FromNumber(CommandCountOf(args, PlayerCommandKind.Research));
				case "garrison_orders": return SimExprValue.FromNumber(CommandCountOf(args, PlayerCommandKind.Garrison));
				// 任意指令总数：用于"玩家已经开始操作了"这类宽松判定
				case "any_orders":
				{
					int team = (int)arg(args, 0);
					int total = 0;
					for (int k = 0; k < PlayerCommandStats.KindCount; k++)
						total += World.GetPlayerCommandCount(team, (PlayerCommandKind)k);
					return SimExprValue.FromNumber((FP)total);
				}

				// --- 几何 ---
				case "distance":
				{
					FP dx = arg(args, 2) - arg(args, 0);
					FP dy = arg(args, 3) - arg(args, 1);
					return SimExprValue.FromNumber(FP.Sqrt(dx * dx + dy * dy));
				}
				case "tile_distance":
				{
					FP dx = arg(args, 2) - arg(args, 0);
					FP dy = arg(args, 3) - arg(args, 1);
					return SimExprValue.FromNumber(FP.Sqrt(dx * dx + dy * dy) / (FP)RtsMapData.TileSize);
				}

				default:
					// 未知函数：宽松返回 0，保证一帧里的拼写错误不会中断模拟
					return SimExprValue.Zero;
			}
		}

		/// <summary>矩形区域取"半宽/半高里的大者"作为等效半径，供圆形统计复用。</summary>
		private static int RegionRadius(MapRegion region)
		{
			if (region.Shape == MapRegionShape.Circle) return region.Radius;
			return Math.Max(region.HalfWidth, region.HalfHeight);
		}

		/// <summary>语义化别名的公共实现（args[0] = 队伍号）。</summary>
		private FP CommandCountOf(SimExprValue[] args, PlayerCommandKind kind) =>
			(FP)World.GetPlayerCommandCount((int)arg(args, 0), kind);

		/// <summary>把 "move"/"Move"/"1" 解析成指令大类；无法识别时返回 Misc。</summary>
		private static PlayerCommandKind ParseCommandKind(SimExprValue[] args, int index)
		{
			if (args == null || index < 0 || index >= args.Length) return PlayerCommandKind.Misc;

			// 也允许直接写数字（枚举值），方便作者用变量驱动
			if (args[index].Type != SimExprType.String)
				return (PlayerCommandKind)Math.Clamp((int)args[index].AsNumber(), 0, PlayerCommandStats.KindCount - 1);

			string name = args[index].AsString();
			if (string.IsNullOrEmpty(name)) return PlayerCommandKind.Misc;

			return name.Trim().ToLowerInvariant() switch
			{
				"move" => PlayerCommandKind.Move,
				"attack" => PlayerCommandKind.Attack,
				"harvest" => PlayerCommandKind.Harvest,
				"repair" => PlayerCommandKind.Repair,
				"build" => PlayerCommandKind.Build,
				"train" or "produce" => PlayerCommandKind.Train,
				"research" or "tech" => PlayerCommandKind.Research,
				"garrison" => PlayerCommandKind.Garrison,
				_ => PlayerCommandKind.Misc,
			};
		}

		private static FP arg(SimExprValue[] args, int i) =>
			args != null && i >= 0 && i < args.Length ? args[i].AsNumber() : FP.Zero;
	}
}
