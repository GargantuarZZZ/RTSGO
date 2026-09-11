using System;
using System.Collections.Generic;

namespace RTS.Simulation.Scripting
{
	// =========================================================
	// 玩家指令统计（教程"你真的下了这个指令"的判定依据）
	//
	// 为什么需要它：
	//   教程里"移动单位"这个目标，光看世界状态是判不出来的——
	//   单位本来就在动、或者玩家根本没动它，两者从世界里看不出区别。
	//   必须记录**玩家确实下达过这类指令**。
	//
	// 确定性红线（重要）：
	//   指令统计直接喂给教程目标条件，而教程进度**要进世界哈希**。
	//   所以这里的数据必须双端一致：
	//     - 只在 DispatchActions 里记录（锁步动作流，双端顺序相同）
	//     - 队伍号直接取自 NetAction.PlayerID（协议字段，双端一致）
	//     - 不含任何浮点、不含时间戳以外的本地信息
	//
	// 设计取舍：
	//   只保留**每类指令"用过没有" + 总次数 + 最近一次 tick**，
	//   不做完整指令日志——教程只需要这几个量，存全量只会让哈希更难对齐。
	// =========================================================

	/// <summary>指令大类。教程条件按"类"判定，作者不用记 ActionId 字符串。</summary>
	public enum PlayerCommandKind
	{
		Move,
		Attack,
		Harvest,
		Repair,
		Build,
		Train,
		Research,
		Garrison,
		Misc,
	}

	/// <summary>某一类指令的使用情况。</summary>
	public struct PlayerCommandSlot
	{
		/// <summary>该类指令总共下过多少次。</summary>
		public int Count;

		/// <summary>最近一次下达的 tick（-1 = 从没下过）。</summary>
		public int LastTick;
	}

	/// <summary>
	/// 按 (队伍, 指令类) 统计。整局替换 CreateNew，不做逐项清零，
	/// 避免"上一局的统计漏清"这种跨局脏状态。
	/// </summary>
	public sealed class PlayerCommandStats
	{
		/// <summary>类别数量：哈希用位掩码，按 Kind 的声明顺序占位。</summary>
		public const int KindCount = 9;

		private readonly Dictionary<int, PlayerCommandSlot[]> _byTeam = new();

		public static PlayerCommandStats CreateNew() => new();

		/// <summary>
		/// 把动作 ID 归到大类。
		///
		/// 这里必须覆盖 UserController.DetermineOrder 产出的全部 ActionId，
		/// 以及建筑面板/技能面板直接发的指令；漏掉的会落到 Misc 而不是丢失。
		/// </summary>
		public static PlayerCommandKind Classify(string actionId)
		{
			if (string.IsNullOrEmpty(actionId)) return PlayerCommandKind.Misc;

			switch (actionId)
			{
				case "Move":
				case "AttackMove":
				case "Stop":
				case "Hold":
				case "Patrol":
					return PlayerCommandKind.Move;

				case "Attack":
					return PlayerCommandKind.Attack;

				case "Harvest":
					return PlayerCommandKind.Harvest;

				case "Repair":
					return PlayerCommandKind.Repair;

				// 建造走 "Build_<StructureName>"；洞穴残骸重建也算建造
				case "RebuildWreckage":
					return PlayerCommandKind.Build;

				// 驻扎：进建筑躲伤害（洞穴/泰伦的核心保命操作）
				case "Garrison":
					return PlayerCommandKind.Garrison;

				// 研究
				case "Research":
					return PlayerCommandKind.Research;
			}

			if (actionId.StartsWith("Build_", StringComparison.Ordinal))
				return PlayerCommandKind.Build;

			// 生产：训练单位 / 自动生产开关
			if (actionId.StartsWith("Train", StringComparison.Ordinal) ||
				actionId.StartsWith("Produce", StringComparison.Ordinal) ||
				actionId == "AutoProduceMode" || actionId == "CancelProduction")
				return PlayerCommandKind.Train;

			// 研究：技能面板发起的科技/变身
			if (actionId.StartsWith("Research", StringComparison.Ordinal)) return PlayerCommandKind.Research;

			return PlayerCommandKind.Misc;
		}

		/// <summary>记录一次指令。锁步派发路径专用（模拟线程）。</summary>
		public void Record(int teamId, string actionId, int tick)
		{
			if (teamId <= 0 || string.IsNullOrEmpty(actionId)) return;

			var kind = Classify(actionId);
			var slots = GetOrCreate(teamId);
			int index = (int)kind;
			slots[index].Count++;
			slots[index].LastTick = tick;
		}

		private PlayerCommandSlot[] GetOrCreate(int teamId)
		{
			if (!_byTeam.TryGetValue(teamId, out var slots))
			{
				slots = new PlayerCommandSlot[KindCount];
				for (int i = 0; i < KindCount; i++) slots[i].LastTick = -1;
				_byTeam[teamId] = slots;
			}
			return slots;
		}

		/// <summary>该队伍是否下达过某类指令。</summary>
		public bool Has(int teamId, PlayerCommandKind kind)
		{
			if (!_byTeam.TryGetValue(teamId, out var slots)) return false;
			int index = (int)kind;
			if (index < 0 || index >= KindCount) return false;
			return slots[index].Count > 0;
		}

		/// <summary>该队伍某类指令的总次数。</summary>
		public int GetCount(int teamId, PlayerCommandKind kind)
		{
			if (!_byTeam.TryGetValue(teamId, out var slots)) return 0;
			int index = (int)kind;
			if (index < 0 || index >= KindCount) return 0;
			return slots[index].Count;
		}

		/// <summary>该队伍某类指令最近一次的 tick（-1 = 从没下过）。</summary>
		public int GetLastTick(int teamId, PlayerCommandKind kind)
		{
			if (!_byTeam.TryGetValue(teamId, out var slots)) return -1;
			int index = (int)kind;
			if (index < 0 || index >= KindCount) return -1;
			return slots[index].LastTick;
		}

		/// <summary>该队伍下过的全部指令类别（位掩码）。诊断用。</summary>
		public int GetUsedMask(int teamId)
		{
			if (!_byTeam.TryGetValue(teamId, out var slots)) return 0;
			int mask = 0;
			for (int i = 0; i < KindCount; i++)
				if (slots[i].Count > 0) mask |= 1 << i;
			return mask;
		}

		/// <summary>
		/// 确定性哈希：按队伍号排序遍历，不依赖字典枚举顺序。
		/// 只混合"用过没有(位掩码) + 总次数"——
		/// LastTick 会随玩家操作时刻变化，不适合作为分叉判据（也不影响教程判定）。
		/// </summary>
		public long GetStateHash()
		{
			long hash = 1469598103934665603L;

			var teams = new List<int>(_byTeam.Keys);
			teams.Sort();
			foreach (int team in teams)
			{
				Mix(ref hash, team);
				Mix(ref hash, GetUsedMask(team));
				for (int i = 0; i < KindCount; i++)
					Mix(ref hash, _byTeam[team][i].Count);
			}
			Mix(ref hash, -7); // 段终止符

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
	}
}
