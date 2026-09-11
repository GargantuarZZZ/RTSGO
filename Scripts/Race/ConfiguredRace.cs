using Godot;
using System.Collections.Generic;
using System.Linq;
using RTS.Data.Configs;

namespace RTS.Data
{
	// 配置表驱动的种族占位：所有初始内容/资源/实体都从 RaceConfig 读取
	[GlobalClass]
	public partial class ConfiguredRace : RaceData
	{
		public override string RaceName { get; }

		public ConfiguredRace() { }

		public ConfiguredRace(string raceName)
		{
			RaceName = raceName;
		}

		public override List<ResourceType> GetVisibleResources()
		{
			var cfg = ConfigDatabase.GetRace(RaceName);
			if (cfg == null)
				return new List<ResourceType>();

			if (cfg.VisibleResourceTypes.Count > 0)
				return cfg.VisibleResourceTypes.ToList();

			return cfg.StartingResources
				.Where(r => r != null)
				.Select(r => r.Type)
				.Distinct()
				.ToList();
		}

		public override Dictionary<ResourceType, float> GetStartingWallet()
		{
			// 配置表驱动时由 Game 直接 ResetResources，这里返回空
			return new Dictionary<ResourceType, float>();
		}

		public override List<(string EntityName, Vector2 Offset)> GetStartingUnits() => new();
		public override List<(string EntityName, Vector2 Offset)> GetStartingStructures() => new();
		public override List<(string EntityName, Vector2 Offset)> GetStartingResources() => new();
	}
}
