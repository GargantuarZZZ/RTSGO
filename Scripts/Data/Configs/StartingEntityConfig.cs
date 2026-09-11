using Godot;

namespace RTS.Data.Configs
{
	/// <summary>
	/// 种族开局生成配置。
	///
	/// 用途：
	/// - 开局主基地
	/// - 开局工人
	/// - 开局资源点
	/// - 开局防御物
	///
	/// 注意：
	/// EntityId 是字符串 ID，不直接引用 UnitConfig / StructureConfig / NeutralConfig。
	/// 运行时由 ConfigDatabase 根据 EntityKind + EntityId 查找具体配置。
	/// </summary>
	[GlobalClass]
	public partial class StartingEntityConfig : Resource
	{
		[ExportGroup("Entity")]
		[Export] public StartingEntityKind Kind { get; set; } = StartingEntityKind.Unit;

		// 例如：
		// - "union_worker"
		// - "union_command_center"
		// - "metal_node_small"
		[Export] public string EntityId { get; set; } = "";

		// TeamMode 决定生成时归属谁。
		[Export] public StartingEntityTeamMode TeamMode { get; set; } = StartingEntityTeamMode.OwnerTeam;

		// 相对出生点偏移。
		[ExportGroup("Transform")]
		[Export] public Vector2 Offset { get; set; } = Vector2.Zero;

		// 预留：建筑朝向、单位初始朝向等。
		[Export(PropertyHint.Range, "0,359,1")]
		public int RotationDegrees { get; set; } = 0;

		// 数量。适合一次生成多个工人。
		[ExportGroup("Spawn")]
		[Export(PropertyHint.Range, "1,999,1")]
		public int Count { get; set; } = 1;

		// 多个单位之间的间距。
		[Export(PropertyHint.Range, "0,9999,1")]
		public int Spacing { get; set; } = 64;

		// =========================================================
		// 工具
		// =========================================================

		public bool IsValidConfig()
		{
			if (string.IsNullOrEmpty(EntityId))
				return false;

			if (Count <= 0)
				return false;

			return true;
		}
	}

	public enum StartingEntityKind
	{
		Unit,
		Structure,
		Neutral,
		ResourceNode
	}

	public enum StartingEntityTeamMode
	{
		OwnerTeam,
		Neutral,
		EnemyTeam
	}
}
