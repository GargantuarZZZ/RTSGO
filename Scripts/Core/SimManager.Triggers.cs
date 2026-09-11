using System;
using System.Collections.Generic;
using Godot;
using RTS.Data;
using RTS.Data.Maps;
using RTS.Simulation;
using RTS.Simulation.Scripting;
using RTS.World;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
	// =========================================================
	// SimManager 的触发器宿主实现（ITriggerWorld）
	//
	// 为什么放在 Core 而不是 Simulation：
	//   ITriggerWorld 需要读玩家资源/科技/人口（PlayerData，表现层对象），
	//   以及刷单位/结束对局这类"改世界"的操作。把这些放在 Core 层，
	//   Simulation 层就还能保持"无 Godot 依赖"（check_sim_thread.py 会拦）。
	//
	// 线程约定：本文件的回调全部在**模拟线程**内被调用（`ExecuteOneTick` 里），
	//   因此禁止直接碰 Godot 节点：刷单位走 `SimEventQueue.EnqueueMain`，
	//   消息/特效同理。PlayerData 是纯 C# 数据，可以直接读。
	// =========================================================

	public partial class SimManager : ITriggerWorld
	{
		/// <summary>当前地图的触发器运行时（无触发器地图时 HasTriggers=false，整条链路短路）。</summary>
		public MapTriggerRuntime TriggerRuntime { get; private set; } = new();

		/// <summary>当前运行时地图（出生点/中立物/触发器都从这里来）。</summary>
		public MapRuntime CurrentMap { get; private set; }

		/// <summary>
		/// 玩家指令统计（教程判定"玩家确实下过这个指令"）。
		/// 整局替换新建实例，避免跨局脏状态。
		/// </summary>
		public PlayerCommandStats CommandStats { get; private set; } = PlayerCommandStats.CreateNew();

		// 本 tick 的事件集合（供 EventDriven 触发器判断"有没有事件"）。
		// 用集合去重，避免每 tick 分配大量字符串。
		private readonly HashSet<string> _pendingTriggerEvents = new(StringComparer.Ordinal);

		/// <summary>
		/// 载入一张运行时地图：重建触发器状态、登记出生点与中立物。
		/// 必须在新对局开始、第一个 tick 之前调用（ResetSimulation 之后）。
		/// </summary>
		public void LoadRuntimeMap(MapRuntime map)
		{
			CurrentMap = map;

			// 方法组不能直接参与 ?. 传播（CS8978），先落成局部委托
			Func<string, MapRegion> regionLookup = map != null ? map.FindRegion : (Func<string, MapRegion>)null;

			TriggerRuntime.Reset(
				map?.Triggers,
				regionLookup,
				map?.Width ?? 0,
				map?.Height ?? 0,
				null);

			TriggerRuntimeInitialized = true;

			// 新一局：指令统计必须从零开始。
			// 它是**按局**的状态（"这一局玩家学会移动了吗"），
			// 沿用上一局的统计会让教程目标一开局就自动完成。
			CommandStats = PlayerCommandStats.CreateNew();

			if (map != null)
				GD.Print($"[Map] 载入运行时地图：{map.Describe()}");
		}

		/// <summary>是否已经装过地图（防止无地图时触发器乱跑）。</summary>
		public bool TriggerRuntimeInitialized { get; private set; }

		// =========================================================
		// 每 tick 调度
		// =========================================================

		/// <summary>
		/// 在 tick 末尾求值所有触发器。
		///
		/// 放在 tick 末尾是刻意的：此时本 tick 的移动/战斗/死亡都已结算，
		/// 触发器看到的是"稳定的本 tick 结果"，不会读到半更新的世界。
		/// </summary>
		private void TickTriggers(int currentTick)
		{
			if (!TriggerRuntimeInitialized || !TriggerRuntime.HasTriggers)
			{
				_pendingTriggerEvents.Clear();
				return;
			}

			int fired = MapTriggerSystem.Evaluate(TriggerRuntime, this, currentTick);

			// 诊断：TRIGGER_DEBUG=1 时每 20 tick 打印一次触发器状态，
			// 用于排查"触发器明明定义了却不触发"（条件写错、相位不对、被禁用等）。
			if (System.Environment.GetEnvironmentVariable("TRIGGER_DEBUG") == "1" && currentTick % 20 == 0)
			{
				GD.Print($"[TriggerDbg] tick={currentTick} triggers={TriggerRuntime.Triggers.Count} " +
						 $"fired={fired} total={TriggerRuntime.TotalFireCount} " +
						 $"lastError='{TriggerRuntime.LastError}'");
			}

			// 事件只在被消费的这一个 tick 内有效：触发器已经看过了，清空。
			_pendingTriggerEvents.Clear();

			if (fired > 0 && TriggerRuntime.LastError.Length > 0)
				GD.PrintErr($"[Trigger] {TriggerRuntime.LastError}");
		}

		/// <summary>记录一个本 tick 发生的事件（供 EventDriven 触发器判定）。</summary>
		private void NoteTriggerEvent(string kind, int teamId)
		{
			if (!TriggerRuntimeInitialized || !TriggerRuntime.HasTriggers) return;
			_pendingTriggerEvents.Add($"{kind}:{teamId}");
		}

		/// <summary>当前 tick 是否有事件待处理。</summary>
		private bool HasPendingTriggerEvents => _pendingTriggerEvents.Count > 0;

		// =========================================================
		// ITriggerWorld
		// =========================================================

		int ITriggerWorld.CurrentTick => RTS.Network.LockstepManager.Instance?.CurrentTick ?? 0;

		bool ITriggerWorld.HasPendingEvents => HasPendingTriggerEvents;

		void ITriggerWorld.Log(string message) => GD.Print($"[Trigger] {message}");

		bool ITriggerWorld.TeamExists(int teamId) =>
			RTS.World.Game.GetPlayerByTeam(teamId) != null;

		FP ITriggerWorld.GetResource(int teamId, string resourceName)
		{
			var pd = RTS.World.Game.GetPlayerByTeam(teamId)?.PlayerData;
			if (pd == null) return FP.Zero;
			if (!TryParseResource(resourceName, out var type)) return FP.Zero;
			return (FP)pd.GetResource(type);
		}

		void ITriggerWorld.AddResource(int teamId, string resourceName, FP amount)
		{
			var pd = RTS.World.Game.GetPlayerByTeam(teamId)?.PlayerData;
			if (pd == null) return;
			if (!TryParseResource(resourceName, out var type)) return;
			pd.AddResource(type, amount);
		}

		bool ITriggerWorld.HasTech(int teamId, string techId)
		{
			var pd = RTS.World.Game.GetPlayerByTeam(teamId)?.PlayerData;
			return pd != null && pd.HasTech(techId);
		}

		void ITriggerWorld.GrantTech(int teamId, string techId, bool granted)
		{
			var pd = RTS.World.Game.GetPlayerByTeam(teamId)?.PlayerData;
			if (pd == null || !granted) return;
			pd.GrantTech(techId);
		}

		int ITriggerWorld.GetUsedSupply(int teamId) =>
			RTS.World.Game.GetPlayerByTeam(teamId)?.PlayerData?.GetUsedSupply() ?? 0;

		int ITriggerWorld.GetMaxSupply(int teamId) =>
			RTS.World.Game.GetPlayerByTeam(teamId)?.PlayerData?.GetMaxSupply() ?? 0;

		bool ITriggerWorld.AreTeamsHostile(int teamA, int teamB) => AreTeamsHostile(teamA, teamB);

		bool ITriggerWorld.IsTeamDefeated(int teamId) => _surrenderedTeams.Contains(teamId);

		/// <summary>
		/// 区域内单位/建筑统计。判定用格子坐标（纯整数），与区域定义一致。
		/// 模拟线程内调用，只读 World，不碰 Godot 节点。
		/// </summary>
		int ITriggerWorld.CountUnitsInRegion(int teamId, int centerX, int centerY, int radiusTiles, bool countAllTeams)
		{
			int count = 0;
			int r2 = radiusTiles * radiusTiles;
			FP tile = (FP)64m;

			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead) continue;
				if (!countAllTeams && u.TeamID != teamId) continue;
				if (countAllTeams && u.TeamID <= 0) continue;   // 中立不算

				int gx = (int)(u.Position.X / tile);
				int gy = (int)(u.Position.Y / tile);
				int dx = gx - centerX;
				int dy = gy - centerY;
				if (dx * dx + dy * dy <= r2) count++;
			}
			return count;
		}

		int ITriggerWorld.CountStructuresInRegion(int teamId, int centerX, int centerY, int radiusTiles, bool countAllTeams)
		{
			int count = 0;
			int r2 = radiusTiles * radiusTiles;

			foreach (var s in World.Structures.Values)
			{
				if (s == null || s.IsDead) continue;
				if (s.CurrentState == SimStructure.StructureState.Blueprint) continue;
				if (!countAllTeams && s.TeamID != teamId) continue;
				if (countAllTeams && s.TeamID <= 0) continue;

				// 用建筑中心格判定，避免大建筑"边缘在圈内"的歧义
				int cx = s.GridPosition.X + s.GridWidth / 2;
				int cy = s.GridPosition.Y + s.GridHeight / 2;
				int dx = cx - centerX;
				int dy = cy - centerY;
				if (dx * dx + dy * dy <= r2) count++;
			}
			return count;
		}

		int ITriggerWorld.CountEntitiesOfTypeNear(string entityId, int centerX, int centerY, int radiusTiles, int teamId)
		{
			if (string.IsNullOrEmpty(entityId)) return 0;

			int count = 0;
			int r2 = radiusTiles * radiusTiles;
			FP tile = (FP)64m;

			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead || u.UnitTypeId != entityId) continue;
				if (teamId > 0 && u.TeamID != teamId) continue;

				int gx = (int)(u.Position.X / tile);
				int gy = (int)(u.Position.Y / tile);
				int dx = gx - centerX;
				int dy = gy - centerY;
				if (dx * dx + dy * dy <= r2) count++;
			}

			foreach (var s in World.Structures.Values)
			{
				if (s == null || s.IsDead || s.StructureTypeId != entityId) continue;
				if (teamId > 0 && s.TeamID != teamId) continue;

				int cx = s.GridPosition.X + s.GridWidth / 2;
				int cy = s.GridPosition.Y + s.GridHeight / 2;
				int dx = cx - centerX;
				int dy = cy - centerY;
				if (dx * dx + dy * dy <= r2) count++;
			}

			return count;
		}

		int ITriggerWorld.CountEntitiesOfType(string entityId, int teamId)
		{
			if (string.IsNullOrEmpty(entityId)) return 0;

			int count = 0;
			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead || u.UnitTypeId != entityId) continue;
				if (teamId > 0 && u.TeamID != teamId) continue;
				count++;
			}
			foreach (var s in World.Structures.Values)
			{
				if (s == null || s.IsDead || s.StructureTypeId != entityId) continue;
				// **蓝图不算**：正在放置的蓝图 Factory/Barracks 还没建成，
				// 如果算进 count()，教程里"造好兵营"的条件会在玩家刚拍下蓝图时
				// 就立刻成立——玩家还没看到建筑动工，提示就已经跳过了。
				if (s.CurrentState == SimStructure.StructureState.Blueprint) continue;
				if (teamId > 0 && s.TeamID != teamId) continue;
				count++;
			}
			return count;
		}

		int ITriggerWorld.CountUnitsOfType(string unitId, int teamId)
		{
			if (string.IsNullOrEmpty(unitId)) return 0;

			int count = 0;
			foreach (var u in World.Units.Values)
			{
				if (u == null || u.IsDead || u.UnitTypeId != unitId) continue;
				if (teamId > 0 && u.TeamID != teamId) continue;
				count++;
			}
			return count;
		}

		int ITriggerWorld.CountBuiltStructuresOfType(string structureId, int teamId)
		{
			if (string.IsNullOrEmpty(structureId)) return 0;

			int count = 0;
			foreach (var s in World.Structures.Values)
			{
				if (s == null || s.IsDead || s.StructureTypeId != structureId) continue;
				// 只数真正完工的建筑：蓝图与施工中都不算"造好了"
				if (s.CurrentState != SimStructure.StructureState.Active) continue;
				if (teamId > 0 && s.TeamID != teamId) continue;
				count++;
			}
			return count;
		}

		// ---- 玩家指令统计（教程用：判定"玩家真的按了这个操作"）----

		bool ITriggerWorld.HasPlayerCommand(int teamId, PlayerCommandKind kind) =>
			CommandStats.Has(teamId, kind);

		int ITriggerWorld.GetPlayerCommandCount(int teamId, PlayerCommandKind kind) =>
			CommandStats.GetCount(teamId, kind);

		/// <summary>
		/// 刷单位。实际生成必须在主线程做（碰节点树），所以走 EnqueueMain。
		///
		/// 注意：返回值是"请求生成的数量"。真正的实体在下一个主线程派发点才出现，
		/// 因此触发器不能用它做"生成后立刻清点"的判断——需要的话请用
		/// count("UnitId") 在后续 tick 再查。
		/// </summary>
		int ITriggerWorld.SpawnUnits(string unitId, int teamId, int centerX, int centerY, int count, int scatterTiles)
		{
			if (string.IsNullOrEmpty(unitId) || count <= 0) return 0;

			// 在模拟线程先确定落点（纯整数运算，确定性），再交给主线程实例化
			var positions = new List<FPVector2>(count);
			FP tile = (FP)64m;

			for (int i = 0; i < count; i++)
			{
				int ox = 0, oy = 0;
				if (scatterTiles > 0)
				{
					// 确定性散布：用世界 RNG（纳入哈希），不要用 Random
					int rx = (int)(World.RNG.Next() % (uint)(scatterTiles * 2 + 1)) - scatterTiles;
					int ry = (int)(World.RNG.Next() % (uint)(scatterTiles * 2 + 1)) - scatterTiles;
					ox = rx;
					oy = ry;
				}

				positions.Add(new FPVector2(
					((FP)(centerX + ox)) * tile + tile / (FP)2m,
					((FP)(centerY + oy)) * tile + tile / (FP)2m));
			}

			// 主线程实例化；捕获值类型/纯数据，符合 EnqueueMain 的约定
			SimEventQueue.EnqueueMain(() =>
			{
				var spawner = RTS.Core.EntitySpawner.Instance;
				if (spawner == null) return;
				foreach (var pos in positions)
					spawner.SpawnEntity(unitId, teamId, pos);
			});

			return count;
		}

		void ITriggerWorld.SetDiplomacy(int teamA, int teamB, int relation)
		{
			if (teamA <= 0 || teamB <= 0 || teamA == teamB) return;

			int group = relation switch
			{
				2 => GetTeamGroup(teamA),   // 盟友：并入自己的组
				1 => teamA,                 // 中立：各自独立（仍敌对，但可用于后续扩展）
				_ => teamA,
			};

			_teamGroup[teamB] = group;
			RTS.Simulation.SimGrid.BumpTeamRevision();
		}

		void ITriggerWorld.EndMatch(int victoryTeamId)
		{
			if (_matchOver) return;
			_matchOver = true;

			string who = victoryTeamId > 0 ? $"Team {victoryTeamId} 胜利" : "对局结束";
			GD.Print($"[Trigger] EndMatch: {who}");

			// 结束对局牵涉 UI，回主线程
			SimEventQueue.EnqueueMain(() =>
			{
				BroadcastTriggerMessage($"【{who}】", 0);
			});
		}

		void ITriggerWorld.BroadcastMessage(string text, int teamId)
		{
			if (string.IsNullOrEmpty(text)) return;

			// 走与聊天相同的锁步通道，保证两端看到同一条消息
			lock (ChatLock)
			{
				ChatMessages.Add(text);
				if (ChatMessages.Count > 200) ChatMessages.RemoveAt(0);
			}
			GD.Print($"[Trigger] {text}");
		}

		/// <summary>主线程可调的消息广播（EndMatch 等回主线程后用）。</summary>
		private void BroadcastTriggerMessage(string text, int teamId) => ((ITriggerWorld)this).BroadcastMessage(text, teamId);

		void ITriggerWorld.PlayFx(string fxId, int centerX, int centerY, int radiusTiles)
		{
			if (string.IsNullOrEmpty(fxId)) return;

			// 表现层效果一律回主线程。
			// 目前只做日志：具体特效（镜头移动/全屏提示）等编辑器 UI 落地后
			// 再在这里接 GameFX3D / RTSCamera —— 现在硬接会在无头测试下炸。
			SimEventQueue.EnqueueMain(() =>
				GD.Print($"[Trigger] FX {fxId} @({centerX},{centerY}) r={radiusTiles}"));
		}

		// =========================================================
		// 辅助
		// =========================================================

		private static bool TryParseResource(string name, out ResourceType type)
		{
			type = ResourceType.Metal;
			if (string.IsNullOrEmpty(name)) return false;
			return Enum.TryParse(name, ignoreCase: true, out type);
		}
	}
}
