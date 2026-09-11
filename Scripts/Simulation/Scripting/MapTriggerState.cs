using System;
using System.Collections.Generic;
using FixMath.NET;
using RTS.Data;
using RTS.Data.Maps;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation.Scripting
{
	// =========================================================
	// 触发器运行时状态
	//
	// 分工：
	//   MapTriggerRuntime  —— 可变的执行状态（变量、冷却、已触发位、调度计数）。
	//   MapTriggerSystem   —— 无状态调度器：给定 tick 与上下文，决定触发哪个 trigger。
	//   ITriggerWorld      —— 世界查询/副作用接口，由 Core 层（SimManager）实现，
	//                         让 Simulation 层不依赖 Godot。
	//
	// 确定性红线：本文件所有可变状态都必须进 GetStateHash()。
	// 触发器写资源/刷单位都会改变世界，一旦状态分叉而哈希不覆盖，
	// 就会表现成"两端某处突然不一致"而无法定位。
	// =========================================================

	/// <summary>
	/// 触发器需要向世界读取与写入的全部能力。
	/// 刻意保持窄接口：新增能力必须同步考虑"是否会破坏确定性"。
	/// </summary>
	public interface ITriggerWorld
	{
		/// <summary>当前模拟 tick（20 tick = 1 秒）。</summary>
		int CurrentTick { get; }

		/// <summary>该队伍是否在局内（存在玩家）；用于 player_x 之外的存在性判定。</summary>
		bool TeamExists(int teamId);

		/// <summary>读取资源数量。未知资源类型返回 0。</summary>
		FP GetResource(int teamId, string resourceName);

		/// <summary>增减资源（可为负）。</summary>
		void AddResource(int teamId, string resourceName, FP amount);

		/// <summary>是否已拥有某科技。</summary>
		bool HasTech(int teamId, string techId);

		/// <summary>授予/移除科技。</summary>
		void GrantTech(int teamId, string techId, bool granted);

		/// <summary>占用人口。</summary>
		int GetUsedSupply(int teamId);

		/// <summary>人口上限。</summary>
		int GetMaxSupply(int teamId);

		/// <summary>区域内属于某队伍的存活单位数（ground+air，不含建筑）。</summary>
		int CountUnitsInRegion(int teamId, int centerX, int centerY, int radiusTiles, bool countAllTeams);

		/// <summary>区域内属于某队伍的存活建筑数。</summary>
		int CountStructuresInRegion(int teamId, int centerX, int centerY, int radiusTiles, bool countAllTeams);

		/// <summary>距某点 radiusTiles 内是否存在指定实体 ID（单位或建筑）。</summary>
		int CountEntitiesOfTypeNear(string entityId, int centerX, int centerY, int radiusTiles, int teamId);

		/// <summary>全局指定实体 ID 的存活数量（teamId &lt;= 0 表示不限队伍）。</summary>
		int CountEntitiesOfType(string entityId, int teamId);

		/// <summary>只数单位（不含建筑），用于"造出 N 个兵"这类目标。</summary>
		int CountUnitsOfType(string unitId, int teamId);

		/// <summary>只数**已完工**的建筑（蓝图与施工中不算），用于"造好某建筑"这类目标。</summary>
		int CountBuiltStructuresOfType(string structureId, int teamId);

		/// <summary>
		/// 该队伍是否下达过某类**玩家指令**。
		///
		/// 为什么教程需要它：像"移动单位""建造""研究科技"这种目标，
		/// 光看世界状态判不出来（单位本来就在动、建筑本来就有），
		/// 必须知道"玩家确实按下了这个操作"。
		/// </summary>
		bool HasPlayerCommand(int teamId, PlayerCommandKind kind);

		/// <summary>该队伍某类指令的总次数。</summary>
		int GetPlayerCommandCount(int teamId, PlayerCommandKind kind);

		/// <summary>按队伍号取"该队伍是否已投降/被淘汰"。</summary>
		bool IsTeamDefeated(int teamId);

		/// <summary>在指定格刷若干单位，返回实际生成数量。</summary>
		int SpawnUnits(string unitId, int teamId, int centerX, int centerY, int count, int scatterTiles);

		/// <summary>两队伍是否敌对（复用 SimManager 的唯一权威判定）。</summary>
		bool AreTeamsHostile(int teamA, int teamB);

		/// <summary>设置外交关系。relation: 0=敌对 1=中立 2=盟友。</summary>
		void SetDiplomacy(int teamA, int teamB, int relation);

		/// <summary>结束对局（victoryTeamId 为获胜方；0 = 平局/中止）。</summary>
		void EndMatch(int victoryTeamId);

		/// <summary>广播一条消息给所有玩家（走锁步 Chat 通道，两端一致）。</summary>
		void BroadcastMessage(string text, int teamId);

		/// <summary>表现层效果（镜头/全屏提示），实现方必须走 SimEventQueue 回主线程。</summary>
		void PlayFx(string fxId, int centerX, int centerY, int radiusTiles);

		/// <summary>触发器内部诊断日志（可空实现）。</summary>
		void Log(string message);

		/// <summary>
		/// 本 tick 是否有事件待处理（单位死亡/建筑被毁/单位进入区域…）。
		/// Phase = EventDriven 的触发器靠它决定要不要消耗一次求值。
		/// </summary>
		bool HasPendingEvents { get; }
	}

	/// <summary>单个触发器的执行状态。</summary>
	internal sealed class TriggerRuntimeState
	{
		public string TriggerId;
		public bool Enabled;
		public bool Fired;          // 是否触发过（非 Repeatable 用）
		public int LastFiredTick = int.MinValue;
		public int NextDueTick;     // Scheduled 用

		/// <summary>累计触发次数（诊断 + 表达式 trigger_&lt;id&gt;_count）。
		/// 刻意不参与哈希：它会随触发次数增长，但触发次数已经通过 LastFiredTick 体现，
		/// 重复计入只会让哈希更难对齐排查。诊断信息不应影响确定性判定。</summary>
		public int FireCount;
	}

	/// <summary>
	/// 触发器运行时：持有全部脚本变量与每触发器状态，负责确定性哈希。
	/// 换地图时调用 Reset(...) 重建。
	///
	/// 刻意只依赖**运行时地图的三个只读量**（触发器定义 / 区域查询 / 地图尺寸），
	/// 不直接引用 RtsMapData —— 编辑器数据是 Godot Resource，
	/// 模拟层必须能在无 Godot 环境（对拍工具、无头测试）下工作。
	/// </summary>
	public sealed class MapTriggerRuntime
	{
		private readonly Dictionary<string, TriggerRuntimeState> _states = new(StringComparer.Ordinal);
		private readonly Dictionary<string, FP> _variables = new(StringComparer.Ordinal);
		private readonly Dictionary<string, FP> _constants = new(StringComparer.Ordinal);

		/// <summary>触发器定义（按作者数组顺序 = 执行顺序）。</summary>
		public IReadOnlyList<TriggerDefinition> Triggers { get; private set; } = System.Array.Empty<TriggerDefinition>();

		/// <summary>区域查询：由宿主注入（运行时地图提供）。null = 无区域。</summary>
		public Func<string, MapRegion> RegionLookup { get; private set; }

		public int MapWidth { get; private set; }
		public int MapHeight { get; private set; }

		/// <summary>是否有任何触发器——没有时整条链路直接短路，不影响无触发器地图的性能。</summary>
		public bool HasTriggers => Triggers.Count > 0;

		// 诊断计数（不参与哈希：只用于日志/编辑器回放统计）
		public int TotalFireCount { get; private set; }

		/// <summary>最近一次执行错误（诊断用，不参与哈希）。</summary>
		public string LastError { get; private set; } = "";

		public MapTriggerRuntime() { }

		/// <summary>便捷构造：直接给触发器定义与地图尺寸（无区域）。</summary>
		public MapTriggerRuntime(
			IReadOnlyList<TriggerDefinition> triggers,
			Func<string, MapRegion> regionLookup = null,
			int mapWidth = 0,
			int mapHeight = 0,
			IReadOnlyList<TriggerVariable> variables = null)
			=> Reset(triggers, regionLookup, mapWidth, mapHeight, variables);

		/// <summary>载入运行时地图并重置全部状态。</summary>
		public void Reset(
			IReadOnlyList<TriggerDefinition> triggers,
			Func<string, MapRegion> regionLookup,
			int mapWidth,
			int mapHeight,
			IReadOnlyList<TriggerVariable> variables = null)
		{
			Triggers = triggers ?? System.Array.Empty<TriggerDefinition>();
			RegionLookup = regionLookup;
			MapWidth = mapWidth;
			MapHeight = mapHeight;

			_states.Clear();
			_variables.Clear();
			_constants.Clear();
			TotalFireCount = 0;
			LastError = "";

			// 只读常量：写在变量前面，供变量初始化表达式引用
			_constants["map_width"] = (FP)mapWidth;
			_constants["map_height"] = (FP)mapHeight;
			_constants["tile_size"] = (FP)RtsMapData.TileSize;

			if (variables != null)
			{
				foreach (var v in variables)
				{
					if (v == null || string.IsNullOrEmpty(v.Name)) continue;
					_constants[v.Name] = (FP)v.Value;
					_variables[v.Name] = (FP)v.Value;
				}
			}

			foreach (var t in Triggers)
			{
				if (t == null || string.IsNullOrEmpty(t.TriggerId)) continue;
				_states[t.TriggerId] = new TriggerRuntimeState
				{
					TriggerId = t.TriggerId,
					Enabled = t.EnabledAtStart,
					NextDueTick = t.StartTick,
				};
			}
		}

		// =========================================================
		// 变量访问
		// =========================================================

		public bool TryGetVariable(string name, out FP value) => _variables.TryGetValue(name, out value);

		public void SetVariable(string name, FP value)
		{
			if (string.IsNullOrEmpty(name)) return;
			_variables[name] = value;
		}

		public void AddVariable(string name, FP delta)
		{
			if (string.IsNullOrEmpty(name)) return;
			_variables.TryGetValue(name, out FP current);
			_variables[name] = current + delta;
		}

		/// <summary>只读常量（地图尺寸/玩家数 + 作者声明的初始值）。</summary>
		public bool TryGetConstant(string name, out FP value) => _constants.TryGetValue(name, out value);

		public IReadOnlyDictionary<string, FP> Variables => _variables;

		// =========================================================
		// 触发器状态
		// =========================================================

		internal bool TryGetState(string triggerId, out TriggerRuntimeState state) =>
			_states.TryGetValue(triggerId ?? "", out state);

		internal TriggerRuntimeState GetOrCreateState(string triggerId)
		{
			if (!_states.TryGetValue(triggerId, out var state))
			{
				state = new TriggerRuntimeState { TriggerId = triggerId, Enabled = false, NextDueTick = 0 };
				_states[triggerId] = state;
			}
			return state;
		}

		public bool IsTriggerEnabled(string triggerId) =>
			_states.TryGetValue(triggerId ?? "", out var s) && s.Enabled;

		public void SetTriggerEnabled(string triggerId, bool enabled)
		{
			GetOrCreateState(triggerId).Enabled = enabled;
		}

		internal void NoteFired(TriggerRuntimeState state, int tick, TriggerDefinition def, bool repeatable)
		{
			state.Fired = true;
			state.LastFiredTick = tick;
			state.FireCount++;
			TotalFireCount++;

			if (!repeatable)
			{
				state.Enabled = false;
			}
			else if (def.Phase == TriggerPhase.Scheduled && def.IntervalTicks > 0)
			{
				state.NextDueTick = tick + def.IntervalTicks;
			}
		}

		internal void SetError(string message)
		{
			LastError = message;
		}

		// =========================================================
		// 确定性哈希
		//
		// 用 FNV-1a 链式混合（不是 SimEntity 那种 XOR——XOR 会让两个相同
		// 数值互相抵消，把真实分叉藏起来）。字符串与 key 顺序都参与，
		// 因此变量改名/触发器增删都会改变哈希。
		// =========================================================

		public long GetStateHash()
		{
			long hash = 1469598103934665603L;

			// 变量：按名字排序，保证与字典枚举顺序无关
			var varNames = new List<string>(_variables.Keys);
			varNames.Sort(StringComparer.Ordinal);
			foreach (string name in varNames)
			{
				MixString(ref hash, name);
				Mix(ref hash, (long)(_variables[name] * (FP)1000m));
			}
			Mix(ref hash, -1); // 变量段分隔

			// 触发器状态：按 ID 排序
			var triggerIds = new List<string>(_states.Keys);
			triggerIds.Sort(StringComparer.Ordinal);
			foreach (string id in triggerIds)
			{
				var s = _states[id];
				MixString(ref hash, id);
				Mix(ref hash, s.Enabled ? 1 : 0);
				Mix(ref hash, s.Fired ? 1 : 0);
				Mix(ref hash, s.LastFiredTick);
				Mix(ref hash, s.NextDueTick);
			}
			Mix(ref hash, -2);

			return hash;
		}

		private static void Mix(ref long hash, long value)
		{
			unchecked
			{
				hash ^= value;
				hash *= 1099511628211L;
			}
		}

		private static void MixString(ref long hash, string value)
		{
			if (value == null) { Mix(ref hash, -3); return; }
			foreach (char c in value) Mix(ref hash, c);
			Mix(ref hash, -4); // 终止符，防 "ab"+"c" 与 "a"+"bc" 碰撞
		}
	}
}
