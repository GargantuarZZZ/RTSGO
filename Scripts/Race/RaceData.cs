using Godot;
using System.Collections.Generic;

namespace RTS.Data
{
	public abstract partial class RaceData : Node
	{
		public abstract string RaceName { get; }

		public abstract List<ResourceType> GetVisibleResources();

		public abstract Dictionary<ResourceType, float> GetStartingWallet();

		// 统一元组格式：(string EntityName, Vector2 Offset)
		public abstract List<(string EntityName, Vector2 Offset)> GetStartingUnits();
		public abstract List<(string EntityName, Vector2 Offset)> GetStartingStructures();
		public abstract List<(string EntityName, Vector2 Offset)> GetStartingResources();

		public virtual void Initialize(Node worldRoot) { }
	}
}
