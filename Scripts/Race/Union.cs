using Godot;
using System.Collections.Generic;

namespace RTS.Data
{
	[GlobalClass]
	public partial class Union : RaceData
	{
		public override string RaceName => "Union";

		public override List<ResourceType> GetVisibleResources() => new()
		{
			ResourceType.Metal,
			ResourceType.Gas
		};

		public override Dictionary<ResourceType, float> GetStartingWallet() => new()
		{
			{ ResourceType.Metal, 2000f }
		};

		// 必须保持 (string EntityName, Vector2 Offset)
		public override List<(string EntityName, Vector2 Offset)> GetStartingUnits() => new()
		{
			("SCV", new Vector2(-150, -150)),
			("SCV", new Vector2(150, -150)),
			("SCV", new Vector2(-150, 150)),
			("SCV", new Vector2(150, 150))
		};

		public override List<(string EntityName, Vector2 Offset)> GetStartingStructures() => new()
		{
			("CommandCenter", Vector2.Zero)
		};

		public override List<(string EntityName, Vector2 Offset)> GetStartingResources() => new()
		{
			("IronOre", new Vector2(400, -300)),
			("IronOre", new Vector2(400, 0)),
			("GasSpring", new Vector2(400, 300))
		};
	}
}
