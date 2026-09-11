using Godot;
using System.Collections.Generic;

namespace RTS.Data.Configs
{
	// 配置数据库：运行时加载 res://Data/Configs/ 下的 .tres 配置表，
	// 以文件名（不含扩展名）作为 ID，供实体工厂 / 对战初始化查询。
	public static class ConfigDatabase
	{
		private static readonly Dictionary<string, UnitConfig> _units = new();
		private static readonly Dictionary<string, StructureConfig> _structures = new();
		private static readonly Dictionary<string, WeaponConfig> _weapons = new();
		private static readonly Dictionary<string, RaceConfig> _races = new();
		private static readonly Dictionary<string, BuffConfig> _buffs = new();
		private static readonly Dictionary<string, TechConfig> _techs = new();
		private static bool _loaded = false;

		public static bool IsLoaded => _loaded;

		public static void LoadAll(string directory = "res://Data/Configs")
		{
			if (_loaded)
				return;

			try
			{
				LoadFromDir(directory);
				_loaded = true;
			}
			catch
			{
				// Failed validation must not leave a success flag or a partial database.
				_units.Clear();
				_structures.Clear();
				_weapons.Clear();
				_races.Clear();
				_buffs.Clear();
				_techs.Clear();
				throw;
			}
		}

		public static void LoadFromDir(string dir)
		{
			using var d = DirAccess.Open(dir);

			if (d == null)
			{
				throw new System.IO.DirectoryNotFoundException($"[Config] 无法打开配置目录: {dir}");
			}

			d.ListDirBegin();
			string fileName = d.GetNext();

			while (fileName != "")
			{
				// 导出包里资源会带 .remap 后缀（如 Nano.tres.remap），需要先去掉再判断类型
				string cleanName = fileName;
				if (cleanName.EndsWith(".remap"))
					cleanName = cleanName.Substring(0, cleanName.Length - ".remap".Length);

				if (!d.CurrentIsDir() && cleanName.EndsWith(".tres"))
				{
					string id = cleanName.GetBaseName();
					// 用去掉 .remap 的原始路径加载，ResourceLoader 会自动解析导出包里的重映射
					var res = GD.Load<Resource>(dir + "/" + cleanName);
					if (res == null)
						throw new System.InvalidOperationException($"[Config] 无法加载资源: {dir}/{cleanName}");

					switch (res)
					{
						case UnitConfig unit:
							_units[id] = unit;
							break;
						case StructureConfig structure:
							_structures[id] = structure;
							break;
						case WeaponConfig weapon:
							_weapons[id] = weapon;
							break;
						case RaceConfig race:
							_races[id] = race;
							break;
						case BuffConfig buff:
							_buffs[id] = buff;
							break;
						case TechConfig tech:
							_techs[id] = tech;
							break;
					}
				}

				fileName = d.GetNext();
			}

			GD.Print($"[Config] 加载完成: {_units.Count} 单位, {_structures.Count} 建筑, {_weapons.Count} 武器, {_races.Count} 种族, {_buffs.Count} Buff, {_techs.Count} 科技");
			ValidateAll();
		}

		// P2-2：加载期引用校验——缺字段/悬空引用（TechId/UnitTypeId/WeaponId 等）在启动时直接拦截
		private static void ValidateAll()
		{
			var errors = new System.Collections.Generic.List<string>();

			foreach (var (id, unit) in _units)
			{
				CheckEach(errors, $"unit {id}.WeaponIds", unit.WeaponIds, _weapons);
				CheckEach(errors, $"unit {id}.BuildableStructureIds", unit.BuildableStructureIds, _structures);
				CheckEach(errors, $"unit {id}.ResearchableTechIds", unit.ResearchableTechIds, _techs);
				CheckEach(errors, $"unit {id}.RequiredTechIds", unit.RequiredTechIds, _techs);
				CheckOne(errors, $"unit {id}.LeapTechId", unit.LeapTechId, _techs);
				CheckOne(errors, $"unit {id}.PigeonTechId", unit.PigeonTechId, _techs);
				CheckOne(errors, $"unit {id}.Skill2RequiredTechId", unit.Skill2RequiredTechId, _techs);
			}

			foreach (var (id, structure) in _structures)
			{
				CheckEach(errors, $"structure {id}.AutoProduceUnitIds", structure.AutoProduceUnitIds, _units);
				CheckEach(errors, $"structure {id}.AutoProduceModeUnitIds", structure.AutoProduceModeUnitIds, _units);
				CheckEach(errors, $"structure {id}.BonusUnitOnCompleteIds", structure.BonusUnitOnCompleteIds, _units);
				CheckEach(errors, $"structure {id}.TrainableUnitIds", structure.TrainableUnitIds, _units);
				CheckEach(errors, $"structure {id}.ResearchableTechIds", structure.ResearchableTechIds, _techs);
				CheckEach(errors, $"structure {id}.ProvidesTechIds", structure.ProvidesTechIds, _techs);
				CheckEach(errors, $"structure {id}.RequiredTechIds", structure.RequiredTechIds, _techs);
				CheckEach(errors, $"structure {id}.WeaponIds", structure.WeaponIds, _weapons);
				CheckEach(errors, $"structure {id}.AuraBuffIds", structure.AuraBuffIds, _buffs);
			}

			foreach (var (id, tech) in _techs)
			{
				CheckEach(errors, $"tech {id}.RequiredTechIds", tech.RequiredTechIds, _techs);
				CheckEach(errors, $"tech {id}.TargetUnitIds", tech.TargetUnitIds, _units);
				CheckEach(errors, $"tech {id}.UnlockStructureIds", tech.UnlockStructureIds, _structures);
				CheckEach(errors, $"tech {id}.GrantWeaponIds", tech.GrantWeaponIds, _weapons);
			}

			// P2-2：语义校验——类型/数值/缺失关键字段在启动时拦截，
			// 避免“配置能加载但游戏里数值崩坏”的隐性错误。
			foreach (var (id, unit) in _units)
			{
				if (unit.MaxHp <= 0f)
					errors.Add($"unit {id}.MaxHp 必须 > 0（当前 {unit.MaxHp}）");
				if (unit.BuildTime < 0f)
					errors.Add($"unit {id}.BuildTime 不能为负（当前 {unit.BuildTime}）");
				if (unit.CanMove && unit.MoveSpeed <= 0f)
					errors.Add($"unit {id}.MoveSpeed 必须 > 0（当前 {unit.MoveSpeed}）");
				if (unit.CanHarvest && unit.CarryCapacity <= 0f)
					errors.Add($"unit {id}.CarryCapacity 必须 > 0（采集单位）");
				if (unit.CanHarvest && unit.HarvestAmountPerCycle <= 0f)
					errors.Add($"unit {id}.HarvestAmountPerCycle 必须 > 0（采集单位）");
				CheckCosts(errors, $"unit {id}.Costs", unit.Costs);
				if (unit.SupplyCost < 0)
					errors.Add($"unit {id}.SupplyCost 不能为负（当前 {unit.SupplyCost}）");
			}

			foreach (var (id, structure) in _structures)
			{
				if (structure.MaxHp <= 0f)
					errors.Add($"structure {id}.MaxHp 必须 > 0（当前 {structure.MaxHp}）");
				if (structure.BuildTime < 0f)
					errors.Add($"structure {id}.BuildTime 不能为负（当前 {structure.BuildTime}）");
				if (structure.GridWidth <= 0 || structure.GridHeight <= 0)
					errors.Add($"structure {id} 长宽必须 > 0（当前 {structure.GridWidth}x{structure.GridHeight}）");
				if (structure.IsProductionBuilding &&
					structure.TrainableUnitIds.Count > 0 &&
					structure.ProductionQueueSize <= 0)
					errors.Add($"structure {id} 是生产建筑且有训练单位，但 ProductionQueueSize 未配置（默认 0）");
				CheckCosts(errors, $"structure {id}.Costs", structure.Costs);
				if (structure.SupplyProvided < 0 || structure.SupplyUsed < 0)
					errors.Add($"structure {id} SupplyProvided/SupplyUsed 不能为负");
			}

			foreach (var (id, tech) in _techs)
			{
				if (tech.ResearchTimeSeconds <= 0f)
					errors.Add($"tech {id}.ResearchTimeSeconds 必须 > 0（当前 {tech.ResearchTimeSeconds}）");
				CheckCosts(errors, $"tech {id}.Cost", tech.Cost);
			}

			foreach (var (id, race) in _races)
			{
				if (race.AiArmyTarget <= 0)
					errors.Add($"race {id}.AiArmyTarget 必须 > 0（当前 {race.AiArmyTarget}）");
				if (race.AiWorkerTarget < 0)
					errors.Add($"race {id}.AiWorkerTarget 不能为负");
				CheckEach(errors, $"race {id}.AiBuildOrderIds", race.AiBuildOrderIds, _structures);
				CheckEach(errors, $"race {id}.AiUnitMixIds", race.AiUnitMixIds, _units);
				CheckEach(errors, $"race {id}.AiTechOrderIds", race.AiTechOrderIds, _techs);
				CheckEach(errors, $"race {id}.AiExpansionBaseIds", race.AiExpansionBaseIds, _structures);
				if (!string.IsNullOrEmpty(race.AiWorkerUnitId) &&
					!IsTierGate(race.AiWorkerUnitId) &&
					!_units.ContainsKey(race.AiWorkerUnitId))
					errors.Add($"race {id}.AiWorkerUnitId -> 找不到单位 '{race.AiWorkerUnitId}'");
				foreach (var kv in race.AiBuildTargetCounts)
				{
					if (kv.Value <= 0)
						errors.Add($"race {id}.AiBuildTargetCounts[{kv.Key}] 必须 > 0（当前 {kv.Value}）");
					if (!_structures.ContainsKey(kv.Key))
						errors.Add($"race {id}.AiBuildTargetCounts -> 找不到建筑 '{kv.Key}'");
				}
			}

			if (errors.Count > 0)
			{
				throw new System.InvalidOperationException(
					$"[Config] 配置引用校验失败（{errors.Count} 处）:\n" +
					string.Join("\n", errors));
			}
		}

		private static void CheckCosts(
			System.Collections.Generic.List<string> errors,
			string owner,
			Godot.Collections.Dictionary<ResourceType, float> costs)
		{
			if (costs == null)
				return;
			foreach (var kv in costs)
			{
				if (kv.Value < 0f)
					errors.Add($"{owner} -> 资源 {kv.Key} 成本不能为负（当前 {kv.Value}）");
			}
		}

		private static void CheckOne(
			System.Collections.Generic.List<string> errors,
			string owner,
			string id,
			System.Collections.Generic.Dictionary<string, TechConfig> techs)
		{
			if (!string.IsNullOrEmpty(id) && !IsTierGate(id) && !techs.ContainsKey(id))
				errors.Add($"{owner} -> 找不到科技 '{id}'");
		}

		private static void CheckEach<T>(
			System.Collections.Generic.List<string> errors,
			string owner,
			Godot.Collections.Array<string> ids,
			System.Collections.Generic.Dictionary<string, T> table)
			where T : Resource
		{
			if (ids == null)
				return;
			foreach (string id in ids)
			{
				if (!string.IsNullOrEmpty(id) && !IsTierGate(id) && !table.ContainsKey(id))
					errors.Add($"{owner} -> 找不到 '{id}'");
			}
		}

		// 等级门标记（T1~T4 或 TerranT3 等）不是科技/单位 ID，跳过引用校验
		private static bool IsTierGate(string id)
		{
			if (string.IsNullOrEmpty(id))
				return false;
			if (id == "T1" || id == "T2" || id == "T3" || id == "T4")
				return true;
			return id.Length >= 2 &&
				id[^1] >= '1' && id[^1] <= '4' &&
				id[^2] == 'T';
		}

		public static UnitConfig GetUnit(string id) => _units.GetValueOrDefault(id);
		public static StructureConfig GetStructure(string id) => _structures.GetValueOrDefault(id);
		public static WeaponConfig GetWeapon(string id) => _weapons.GetValueOrDefault(id);
		public static RaceConfig GetRace(string id) => _races.GetValueOrDefault(id);
		public static BuffConfig GetBuff(string id) => _buffs.GetValueOrDefault(id);
		public static TechConfig GetTech(string id) => _techs.GetValueOrDefault(id);

		public static IEnumerable<System.Collections.Generic.KeyValuePair<string, UnitConfig>> GetAllUnits() => _units;
		public static IEnumerable<System.Collections.Generic.KeyValuePair<string, StructureConfig>> GetAllStructures() => _structures;
	}
}
