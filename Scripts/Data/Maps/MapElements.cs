using Godot;

namespace RTS.Data.Maps
{
	// =========================================================
	// 地图内容元素：出生点 / 实体摆放 / 命名区域
	// 全部用**格子坐标**存储（不用世界像素）——格子是权威，改格子尺寸时意图不漂移。
	// =========================================================

	/// <summary>一个可选的出生位置（队伍槽位）。</summary>
	[GlobalClass]
	public partial class MapSpawnPoint : Resource
	{
		/// <summary>队伍槽位号（1..N），对应 Marker2D 命名 Spawn_&lt;slot&gt;。</summary>
		[Export] public int TeamSlot { get; set; } = 1;

		[Export] public int GridX { get; set; }
		[Export] public int GridY { get; set; }

		/// <summary>出生朝向（度），仅影响表现层初始相机与单位朝向。</summary>
		[Export] public float FacingDegrees { get; set; }

		/// <summary>作者备注（编辑器显示）。</summary>
		[Export] public string Comment { get; set; } = "";

		/// <summary>该点是否只给特定队伍预留（0 = 任意队伍可用）。</summary>
		[Export] public int ReservedForTeam { get; set; }

		public Vector2I GridPosition => new Vector2I(GridX, GridY);
	}

	public enum MapEntityOwner
	{
		/// <summary>中立不可攻击（圣地等）：TeamID = -1。</summary>
		Neutral = 0,
		/// <summary>中立可攻击（防御塔/纳米核心）：TeamID = -2。</summary>
		HostileNeutral = 1,
		/// <summary>归属该出生点槽位的队伍。</summary>
		PlayerSlot = 2,
	}

	/// <summary>地图上摆放的一个实体（资源点/中立建筑/预置单位）。</summary>
	[GlobalClass]
	public partial class MapEntityPlacement : Resource
	{
		/// <summary>实体 ID，与 Data/Configs/&lt;Id&gt;.tres 或预制体同名。</summary>
		[Export] public string EntityId { get; set; } = "";

		[Export] public MapEntityOwner Owner { get; set; } = MapEntityOwner.Neutral;

		/// <summary>Owner = PlayerSlot 时用哪个槽位。</summary>
		[Export] public int TeamSlot { get; set; } = 1;

		[Export] public int GridX { get; set; }
		[Export] public int GridY { get; set; }

		/// <summary>数量（资源点堆叠/群体刷新用；普通实体为 1）。</summary>
		[Export] public int Count { get; set; } = 1;

		/// <summary>
		/// 资源点是否在其周围自动散布资源（沿用 Game.SpawnTowerResources 的行为）。
		/// 中立塔勾上就会刷 2 铁 + 1 气。
		/// </summary>
		[Export] public bool ScatterResources { get; set; }

		/// <summary>散布的额外资源（留空用默认 2 铁 1 气）。格式 "IronOre:2,GasSpring:1"。</summary>
		[Export] public string ScatterSpec { get; set; } = "";

		[Export] public string Comment { get; set; } = "";

		public int ResolveTeamId() => Owner switch
		{
			MapEntityOwner.HostileNeutral => -2,
			MapEntityOwner.PlayerSlot => TeamSlot,
			_ => -1,
		};

		public Vector2I GridPosition => new Vector2I(GridX, GridY);
	}

	/// <summary>区域形状。</summary>
	public enum MapRegionShape
	{
		Rectangle = 0,
		Circle = 1,
	}

	/// <summary>
	/// 命名区域：触发器用它做"单位进入某地""区域内有几个敌人"这类判定。
	/// 命名引用而非内联坐标，作者挪动区域时不用改触发器。
	/// </summary>
	[GlobalClass]
	public partial class MapRegion : Resource
	{
		[Export] public string RegionId { get; set; } = "";

		[Export] public string DisplayName { get; set; } = "";

		[Export] public MapRegionShape Shape { get; set; } = MapRegionShape.Rectangle;

		/// <summary>矩形/圆的中心格。</summary>
		[Export] public int GridX { get; set; }
		[Export] public int GridY { get; set; }

		/// <summary>矩形用：半宽（格）。</summary>
		[Export] public int HalfWidth { get; set; } = 3;
		/// <summary>矩形用：半高（格）。</summary>
		[Export] public int HalfHeight { get; set; } = 3;

		/// <summary>圆用：半径（格）。</summary>
		[Export] public int Radius { get; set; } = 3;

		/// <summary>只在编辑器显示的配色（不参与逻辑）。</summary>
		[Export] public Color EditorColor { get; set; } = new Color(0.2f, 0.8f, 1f, 0.25f);

		/// <summary>格子是否落在该区域内（纯整数判定，确定性）。</summary>
		public bool ContainsCell(int x, int y)
		{
			if (Shape == MapRegionShape.Circle)
			{
				int dx = x - GridX;
				int dy = y - GridY;
				return dx * dx + dy * dy <= Radius * Radius;
			}
			return x >= GridX - HalfWidth && x <= GridX + HalfWidth &&
				   y >= GridY - HalfHeight && y <= GridY + HalfHeight;
		}

		/// <summary>区域覆盖的格子数（用于"区域内单位数"等判定的分母/上限）。</summary>
		public int ApproxCellCount => Shape == MapRegionShape.Circle
			? Radius * Radius * 3
			: (HalfWidth * 2 + 1) * (HalfHeight * 2 + 1);
	}
}
