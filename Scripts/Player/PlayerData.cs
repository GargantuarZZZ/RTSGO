using Godot;
using System.Collections.Generic;
using System.Linq;
using RTS.Data;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
	public partial class PlayerData : Node
	{
		// 模拟线程写、UI 主线程读，科技集合需要互斥
		private readonly object _syncRoot = new();

		// 资源账本：全部使用定点数
		private Dictionary<ResourceType, FP> _resourcesFP = new();

		// =========================================================
		// 科技研究状态（确定性，纳入同步哈希）
		// =========================================================
		// 必须用有序集合：HashSet<string> 在 .NET 里哈希随机化，
		// 两台机器遍历顺序不同会让科技倍率连乘结果分叉（锁步脱同步根源）
		public SortedSet<string> ResearchedTechs { get; } = new(System.StringComparer.Ordinal);
		// 可循环科技等级（针对防御等），ID -> 已研究次数
		public Dictionary<string, int> TechLevels { get; } = new();
		public string ResearchingTechId { get; private set; } = "";
		public FP ResearchProgress { get; private set; } = FP.Zero;
		public FP ResearchDuration { get; private set; } = FP.Zero;

		[Signal] public delegate void ResourceChangedEventHandler(long type, float amount);

		public bool HasTech(string techId)
		{
			lock (_syncRoot)
				return !string.IsNullOrEmpty(techId) && ResearchedTechs.Contains(techId);
		}

		public int GetTechLevel(string techId)
		{
			if (string.IsNullOrEmpty(techId))
				return 0;

			lock (_syncRoot)
				return TechLevels.GetValueOrDefault(techId);
		}

		public bool IsResearching => !string.IsNullOrEmpty(ResearchingTechId);

		// 建筑提供的时代解锁（如 UnionT2 / UnionT3）：直接记入已研发，幂等
		public void GrantTech(string techId)
		{
			if (!string.IsNullOrEmpty(techId))
			{
			lock (_syncRoot)
				ResearchedTechs.Add(techId);

				var cfg = RTS.Data.Configs.ConfigDatabase.GetTech(techId);
				if (cfg != null && cfg.IsRepeatable)
				{
					TechLevels.TryGetValue(techId, out int level);
			lock (_syncRoot)
				TechLevels[techId] = level + 1;
				}
			}
		}

		public bool StartResearch(string techId, float durationSeconds)
		{
			if (IsResearching || string.IsNullOrEmpty(techId) || durationSeconds <= 0f)
				return false;

			ResearchingTechId = techId;
			ResearchProgress = FP.Zero;
			ResearchDuration = (FP)durationSeconds;
			return true;
		}

		// 每逻辑帧推进研究；完成时返回科技 ID，未完成返回 null
		public string AdvanceResearch(FP delta)
		{
			if (!IsResearching || ResearchDuration <= FP.Zero)
				return null;

			// 洞穴智者驻扎：科技建筑内驻扎时研究加速（确定性）
			ResearchProgress += delta * (FP)TechEffects.GetResearchSpeedMultiplier(this);

			if (ResearchProgress < ResearchDuration)
				return null;

			string completed = ResearchingTechId;
			GrantTech(completed);
			ResearchingTechId = "";
			ResearchProgress = FP.Zero;
			ResearchDuration = FP.Zero;
			return completed;
		}

		public void Initialize(RaceData race)
		{
			_resourcesFP.Clear();
			var wallet = race.GetStartingWallet();
			foreach (var pair in wallet)
			{
				_resourcesFP[pair.Key] = (FP)pair.Value;
				EmitSignal(SignalName.ResourceChanged, (long)pair.Key, pair.Value);
			}
		}

		public void AddResource(ResourceType type, FP amount)
		{
			lock (_syncRoot)
				AddResourceInner(type, amount);
		}

		private void AddResourceInner(ResourceType type, FP amount)
		{
			if (!_resourcesFP.ContainsKey(type)) _resourcesFP[type] = FP.Zero;
			_resourcesFP[type] += amount;

			// 能量上限：从种族配置表读取（0 = 无上限）
			if (type == ResourceType.Energy)
			{
				float max = GetMaxEnergy();
				if (max > 0f && _resourcesFP[type] > (FP)max)
					_resourcesFP[type] = (FP)max;
			}

			float amountAfter = (float)_resourcesFP[type];
			// 模拟线程禁止发 Godot 信号：延迟到主线程（转为 float 只为给 UI 显示）
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (GodotObject.IsInstanceValid(this))
					EmitSignal(SignalName.ResourceChanged, (long)type, amountAfter);
			});
		}

		private float GetMaxEnergy()
		{
			var race = GetParent<Player>()?.Race;
			if (race == null)
				return 0f;

			var cfg = RTS.Data.Configs.ConfigDatabase.GetRace(race.RaceName);
			return cfg?.MaxEnergy ?? 0f;
		}

		// 兼容旧版的 float 增加接口
		public void AddResource(ResourceType type, float amount) => AddResource(type, (FP)amount);

		public float GetResource(ResourceType type)
		{
			lock (_syncRoot)
				return _resourcesFP.ContainsKey(type) ? (float)_resourcesFP[type] : 0f;
		}

		public bool HasResources(Godot.Collections.Dictionary<ResourceType, float> costs)
		{
			if (costs == null || costs.Count == 0) return true;
			lock (_syncRoot)
			{
				foreach (var kvp in costs)
				{
					if (!_resourcesFP.ContainsKey(kvp.Key) || _resourcesFP[kvp.Key] < (FP)kvp.Value)
						return false;
				}
				return true;
			}
		}

		// 模拟线程安全版：纯 C# 集合，不在模拟线程创建 Godot 对象
		// （Godot.Collections.* 在模拟线程 new 会导致 Handle is not initialized 崩溃）
		public bool HasResources(System.Collections.Generic.IReadOnlyDictionary<ResourceType, float> costs)
		{
			if (costs == null || costs.Count == 0) return true;
			lock (_syncRoot)
			{
				foreach (var kvp in costs)
				{
					if (!_resourcesFP.TryGetValue(kvp.Key, out var have) || have < (FP)kvp.Value)
						return false;
				}
				return true;
			}
		}

		public bool HasResources(ResourceType type, float amount)
		{
			lock (_syncRoot)
				return _resourcesFP.TryGetValue(type, out var have) && have >= (FP)amount;
		}

		public bool TryConsumeResources(Godot.Collections.Dictionary<ResourceType, float> costs)
		{
			if (!HasResources(costs)) return false;
			if (costs == null || costs.Count == 0) return true;

			lock (_syncRoot)
			{
				foreach (var kvp in costs)
				{
					_resourcesFP[kvp.Key] -= (FP)kvp.Value;
					float amountAfter = (float)_resourcesFP[kvp.Key];
					// 模拟线程禁止发 Godot 信号：延迟到主线程
					RTS.Core.SimEventQueue.EnqueueMain(() =>
					{
						if (GodotObject.IsInstanceValid(this))
							EmitSignal(SignalName.ResourceChanged, (long)kvp.Key, amountAfter);
					});
				}
			}
			return true;
		}

		public bool TryConsumeResources(System.Collections.Generic.IReadOnlyDictionary<ResourceType, float> costs)
		{
			if (!HasResources(costs)) return false;
			if (costs == null || costs.Count == 0) return true;
			lock (_syncRoot)
			{
				foreach (var kvp in costs)
				{
					ResourceType type = kvp.Key;
					_resourcesFP[type] -= (FP)kvp.Value;
					float amountAfter = (float)_resourcesFP[type];
					RTS.Core.SimEventQueue.EnqueueMain(() =>
					{
						if (GodotObject.IsInstanceValid(this))
							EmitSignal(SignalName.ResourceChanged, (long)type, amountAfter);
					});
				}
			}
			return true;
		}

		public bool TryConsumeResources(ResourceType type, float amount)
		{
			if (!HasResources(type, amount))
				return false;
			lock (_syncRoot)
			{
				_resourcesFP[type] -= (FP)amount;
				float amountAfter = (float)_resourcesFP[type];
				RTS.Core.SimEventQueue.EnqueueMain(() =>
				{
					if (GodotObject.IsInstanceValid(this))
						EmitSignal(SignalName.ResourceChanged, (long)type, amountAfter);
				});
			}
			return true;
		}

		public void AddResources(Godot.Collections.Dictionary<ResourceType, float> resources)
		{
			if (resources == null) return;
			lock (_syncRoot)
			{
				foreach (var kvp in resources)
					AddResourceInner(kvp.Key, (FP)kvp.Value);
			}
		}

		// 配置表驱动：整表覆盖当前资源
		public void ResetResources(Godot.Collections.Dictionary<ResourceType, float> resources)
		{
			_resourcesFP.Clear();
			AddResources(resources);
		}

		// 已占用人口：遍历己方存活单位，按配置表的 SupplyCost 求和（确定性）
		public int GetUsedSupply()
		{
			var lk = RTS.Core.SimManager.Instance?.WorldLock;
			if (lk != null)
			{
				lock (lk)
					return GetUsedSupplyInner();
			}
			return GetUsedSupplyInner();
		}

		private int GetUsedSupplyInner()
		{
			int team = GetParent<Player>()?.TeamId ?? -1;
			var world = RTS.Core.SimManager.Instance?.World;
			if (world == null)
				return 0;

			int used = 0;

			foreach (var unit in world.Units.Values)
			{
				if (unit == null || unit.IsDead || unit.TeamID != team)
					continue;

				var unitCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(unit.UnitTypeId);
				int supply = unitCfg?.SupplyCost ?? 0;

				// 分封制：英雄单位人口 -3
				if (supply > 0 && unitCfg != null && unitCfg.IsHero && HasTech("DemonTech_Feudal"))
					supply = System.Math.Max(0, supply - 3);

				used += supply;
				// 单位自带占用人口（牧羊人 -5 等）
				used += unitCfg?.SupplyUsed ?? 0;
			}

			// 建筑占人口（恶魔负数建筑）
			foreach (var structure in world.Structures.Values)
			{
				if (structure == null || structure.IsDead || structure.TeamID != team)
					continue;

				// 蓝图/施工中/已建成一律占人口：放下蓝图就结算，避免连拍挤爆
				used += RTS.Data.Configs.ConfigDatabase.GetStructure(structure.StructureTypeId)?.SupplyUsed ?? 0;
			}

			return used;
		}

		// 人口上限：遍历己方已建成建筑，按配置表的 SupplyProvided 求和（确定性）
		public int GetMaxSupply()
		{
			var lk = RTS.Core.SimManager.Instance?.WorldLock;
			if (lk != null)
			{
				lock (lk)
					return GetMaxSupplyInner();
			}
			return GetMaxSupplyInner();
		}

		private int GetMaxSupplyInner()
		{
			int team = GetParent<Player>()?.TeamId ?? -1;
			var world = RTS.Core.SimManager.Instance?.World;
			if (world == null)
				return 0;

			var raceCfg = RTS.Data.Configs.ConfigDatabase.GetRace(GetParent<Player>()?.Race?.RaceName ?? "");

			int provided = 0;
			int used = 0;

			foreach (var structure in world.Structures.Values)
			{
				if (structure == null || structure.IsDead || structure.TeamID != team)
					continue;
				if (structure.CurrentState != RTS.Simulation.SimStructure.StructureState.Active)
					continue;

				provided += RTS.Data.Configs.ConfigDatabase.GetStructure(structure.StructureTypeId)?.SupplyProvided ?? 0;
				used += RTS.Data.Configs.ConfigDatabase.GetStructure(structure.StructureTypeId)?.SupplyUsed ?? 0;
			}

			int mode = raceCfg?.SupplyCapMode ?? 0;
			int cap = raceCfg?.MaxSupplyCap ?? 0;

			if (mode == 1)
			{
				// 恶魔：供给封顶
				if (cap > 0 && provided > cap)
					provided = cap;
			}
			else if (mode == 2)
			{
				// 多足：y = 只由牧羊人数量决定（每个提供 25，指挥链路再 +10），封顶 200
				int shepherds = 0;

				foreach (var unit in world.Units.Values)
				{
					if (unit != null && !unit.IsDead && unit.TeamID == team && unit.UnitTypeId == "Shepherd")
						shepherds++;
				}

				int perShepherd = 25 + (HasTech("WandererTech_Link") ? 10 : 0);
				int available = shepherds * perShepherd;

				if (cap > 0 && available > cap)
					available = cap;

				return available;
			}

			return provided;
		}

		// 确定性资源哈希（按资源类型排序，跨端稳定）
		public long GetStateHash()
		{
			long hash = 1469598103934665603L;

			foreach (var kvp in _resourcesFP.OrderBy(k => (int)k.Key))
			{
				unchecked
				{
					hash ^= (int)kvp.Key;
					hash *= 1099511628211L;
					hash ^= (long)(kvp.Value * (FP)100m);
					hash *= 1099511628211L;
				}
			}

			// 科技研究状态：按 ID 排序后混合，跨端稳定
			lock (_syncRoot)
			{
				foreach (string techId in ResearchedTechs.OrderBy(t => t, System.StringComparer.Ordinal))
				{
					unchecked
					{
						foreach (char c in techId)
						{
							hash ^= c;
							hash *= 1099511628211L;
						}
					}
				}

				// 可循环科技等级
				foreach (var kvp in TechLevels.OrderBy(k => k.Key, System.StringComparer.Ordinal))
				{
					unchecked
					{
						foreach (char c in kvp.Key)
						{
							hash ^= c;
							hash *= 1099511628211L;
						}

						hash ^= kvp.Value;
						hash *= 1099511628211L;
					}
				}
			}

			if (!string.IsNullOrEmpty(ResearchingTechId))
			{
				unchecked
				{
					foreach (char c in ResearchingTechId)
					{
						hash ^= c;
						hash *= 1099511628211L;
					}
				}

				hash ^= (long)(ResearchProgress * (FP)1000m);
				hash ^= (long)(ResearchDuration * (FP)1000m);
			}

			return hash;
		}

		// 脱同步报告用：资源明细（按资源类型排序）
		public string GetResourcesDebugString()
		{
			return string.Join(", ", _resourcesFP.OrderBy(k => (int)k.Key).Select(k => $"{k.Key}={k.Value}"));
		}
	}
}
