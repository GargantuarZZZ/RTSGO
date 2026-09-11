using System;
using System.Collections.Generic;
using Godot;
using RTS.Data.Maps;
using RTS.Simulation.Scripting;
using RTS.Tutorial;
using RTS.World;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
	// =========================================================
	// SimManager 的教程接入
	//
	// 职责：
	//   1. 持有 TutorialRuntime（进度状态，纳入世界哈希）
	//   2. 每 tick 在**触发器之后**求值当前目标（触发器可能刷兵/给资源，
	//      教程应当看到本 tick 的最终结果）
	//   3. 提供主线程可读的快照给 UI
	//
	// 线程约定：Evaluate 在模拟线程内跑，只读 World/PlayerData；
	// GetTutorialSnapshot 在主线程调用（只读已算好的字段）。
	// =========================================================

	public partial class SimManager
	{
		/// <summary>当前教程运行时（未开始时 Active=false，整条链路短路）。</summary>
		public TutorialRuntime Tutorial { get; private set; } = new();

		/// <summary>教程开始时额外要刷的实体；由 Game 在生成中立物时取用。</summary>
		public IReadOnlyList<TutorialSpawn> PendingTutorialSpawns { get; private set; } =
			Array.Empty<TutorialSpawn>();

		/// <summary>
		/// 本局要跑的教程（由主菜单在选择教程时设置）。
		/// Game 在载入地图时读取它：非 null 则用教程自带地图并启动教程运行时。
		/// </summary>
		public static TutorialDefinition PendingTutorial { get; set; }

		/// <summary>把教程额外实体交给 Game 生成后清空（避免下一局重复生成）。</summary>
		public IReadOnlyList<TutorialSpawn> ConsumeTutorialSpawns()
		{
			var list = PendingTutorialSpawns;
			PendingTutorialSpawns = Array.Empty<TutorialSpawn>();
			return list;
		}

		/// <summary>开始一套教程。必须在实体生成之前调用（额外实体依赖它）。</summary>
		public void StartTutorial(TutorialDefinition def)
		{
			Tutorial.Start(def);
			PendingTutorialSpawns = def?.ExtraSpawns ?? (IReadOnlyList<TutorialSpawn>)Array.Empty<TutorialSpawn>();

			if (def != null)
				GD.Print($"[Tutorial] 开始教程：{def.DisplayName}（{def.ObjectiveCount} 个目标）");
		}

		public void StopTutorial()
		{
			Tutorial.Stop();
			PendingTutorialSpawns = Array.Empty<TutorialSpawn>();
		}

		/// <summary>取教程快照给 UI（主线程调用）。</summary>
		public TutorialSnapshot GetTutorialSnapshot()
		{
			if (!Tutorial.Active && Tutorial.Definition == null)
				return TutorialSnapshot.Inactive;

			// 宿主只用于算进度表达式里的数值；tick 用当前锁步 tick
			int tick = RTS.Network.LockstepManager.Instance?.CurrentTick ?? 0;
			var host = MapTriggerSystem.CreateHost(TriggerRuntime, this, tick);
			return Tutorial.GetSnapshot(host, tick);
		}

		/// <summary>
		/// 每个 tick 求值教程目标。放在触发器**之后**：
		/// 触发器可能在本 tick 刷兵/给资源，教程应当看到最终结果，
		/// 否则"到达某地"这类目标会晚一 tick 才成立。
		/// </summary>
		private void TickTutorial(int currentTick)
		{
			if (!Tutorial.Active) return;

			// 与触发器共用宿主，保证教程条件能用相同的变量与内置函数
			var host = MapTriggerSystem.CreateHost(TriggerRuntime, this, currentTick);

			int before = Tutorial.ObjectiveIndex;
			Tutorial.Evaluate(this, host, currentTick);

			if (Tutorial.ObjectiveIndex != before)
			{
				var done = Tutorial.Definition?.Objectives;
				string doneTitle = done != null && before >= 0 && before < done.Count ? done[before].Title : "?";
				GD.Print($"[Tutorial] 目标完成：{doneTitle}");

				if (!Tutorial.Active)
					GD.Print("[Tutorial] 全部目标完成，教程结束。");
			}
		}
	}
}
