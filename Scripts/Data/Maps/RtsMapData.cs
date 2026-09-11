using Godot;
using System;
using System.Collections.Generic;

namespace RTS.Data.Maps
{
	// =========================================================
	// 地图数据格式（地图编辑器与游戏共用的唯一权威）
	//
	// 设计要点：
	//   1. **纯数据 Resource**：`.tres` 可版本管理、可在 Godot 里检视、可被主菜单动态扫描。
	//   2. **地形必须显式存储，不做运行期镜像**。程序化生成器只在"作者点生成"时写数据；
	//      若改成加载时按对称性现算，任何一处算法差异都会让双端地形不同 → 脱步。
	//   3. 地形编码按行主序（index = y * Width + x），每个格子 1 字节：
	//        bit0-3 (低 4 位) = tileset source id   (0=Grass 可走 / 1=Wall 阻挡)
	//        bit4-7 (高 4 位) = atlas 变体索引
	//      低 4 位定"能不能走"，高 4 位定"长什么样"——这样换皮不改变寻路结果。
	//   4. 尺寸上限 256×256（64px 格 → 16384px 世界），足够 8 人对战且序列化可控。
	// =========================================================

	[GlobalClass]
	public partial class RtsMapData : Resource
	{
		public const int MaxSize = 256;
		public const int TileSize = 64;

		/// <summary>地形 source 0 = 草地（可走）。</summary>
		public const int SourceGrass = 0;
		/// <summary>地形 source 1 = 墙（阻挡）。这两条与 Set/St.tres 的 source 顺序绑定。</summary>
		public const int SourceWall = 1;

		// --- 标识 ---
		/// <summary>稳定 ID（= 文件名），存档/回放/网络同步都用它，改名等于换地图。</summary>
		[Export] public string MapId { get; set; } = "";

		[Export] public string DisplayName { get; set; } = "";

		/// <summary>本地化 key（可选）；为空时 UI 直接用 DisplayName。</summary>
		[Export] public string DisplayNameKey { get; set; } = "";

		[Export] public string Author { get; set; } = "";

		[Export] public string Description { get; set; } = "";

		/// <summary>地图格式版本，便于以后迁移。</summary>
		[Export] public int FormatVersion { get; set; } = 1;

		// --- 尺寸 ---
		[Export] public int Width { get; set; }
		[Export] public int Height { get; set; }

		/// <summary>
		/// 地形字节。长度必须 = Width * Height。
		/// 用 PackedByteArray 而非嵌套数组：`.tres` 里是单行 Base64，体积小、加载快。
		/// </summary>
		[Export] public byte[] Terrain { get; set; } = Array.Empty<byte>();

		/// <summary>
		/// 建造禁区掩码（可选，与 Terrain 等长，1 = 禁止放置建筑）。
		/// 用于"资源点周围禁建""圣地保护区"等作者意图，独立于地形可通行性。
		/// </summary>
		[Export] public byte[] BuildBlocked { get; set; } = Array.Empty<byte>();

		/// <summary>允许的最大玩家数（受出生点数量约束）。</summary>
		[Export] public int MaxPlayers { get; set; } = 8;

		/// <summary>推荐人数，仅作 UI 提示。</summary>
		[Export] public int RecommendedPlayers { get; set; } = 2;

		// --- 内容 ---
		[Export] public Godot.Collections.Array<MapSpawnPoint> SpawnPoints { get; set; } = new();
		[Export] public Godot.Collections.Array<MapEntityPlacement> Entities { get; set; } = new();
		[Export] public Godot.Collections.Array<MapRegion> Regions { get; set; } = new();
		[Export] public Godot.Collections.Array<TriggerVariable> Variables { get; set; } = new();
		[Export] public Godot.Collections.Array<TriggerDefinition> Triggers { get; set; } = new();

		// --- 缓存的校验结果（编辑器写入，运行时不依赖）---
		[Export] public string LastValidationReport { get; set; } = "";

		// --- 场景迁移用的坐标系平移量 ---
		/// <summary>
		/// 从 TileMapLayer 导出时，原始格坐标被平移了多少（terrain 新坐标 = 原坐标 - offset）。
		/// 导出出生点标记时必须用同一个平移量，否则出生点会落在导出后地图的坐标系之外。
		/// 运行时不需要这两个值。
		/// </summary>
		[Export] public int ExportOffsetX { get; set; }
		[Export] public int ExportOffsetY { get; set; }

		// =========================================================
		// 地形访问
		// =========================================================

		public bool HasTerrain => Width > 0 && Height > 0 && Terrain != null && Terrain.Length == Width * Height;

		public int Index(int x, int y) => y * Width + x;

		public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

		public static int Encode(int source, int variant) =>
			(source & 0x0F) | ((variant & 0x0F) << 4);

		public static int DecodeSource(byte cell) => cell & 0x0F;

		public static int DecodeVariant(byte cell) => (cell >> 4) & 0x0F;

		/// <summary>该格是否为可通行地面（source = Grass）。</summary>
		public bool IsWalkableCell(int x, int y)
		{
			if (!InBounds(x, y) || !HasTerrain) return false;
			return DecodeSource(Terrain[Index(x, y)]) == SourceGrass;
		}

		/// <summary>该格是否为阻挡（source = Wall）。</summary>
		public bool IsWallCell(int x, int y)
		{
			if (!InBounds(x, y) || !HasTerrain) return false;
			return DecodeSource(Terrain[Index(x, y)]) == SourceWall;
		}

		public bool IsBuildBlocked(int x, int y)
		{
			if (!InBounds(x, y) || BuildBlocked == null || BuildBlocked.Length != Width * Height)
				return false;
			return BuildBlocked[Index(x, y)] != 0;
		}

		public void SetCell(int x, int y, int source, int variant = 0)
		{
			if (!InBounds(x, y)) return;
			EnsureTerrain();
			Terrain[Index(x, y)] = (byte)Encode(source, variant);
		}

		public void SetBuildBlocked(int x, int y, bool blocked)
		{
			if (!InBounds(x, y)) return;
			EnsureBuildMask();
			BuildBlocked[Index(x, y)] = (byte)(blocked ? 1 : 0);
		}

		// =========================================================
		// 尺寸变更
		// =========================================================

		/// <summary>
		/// 重新分配地形（会保留重叠区域的内容）。
		/// 出生点/实体若落到新范围外会被标记为"越界"由校验器报出——刻意不自动删除，
		/// 否则作者把地图改小再改回来会永久丢内容。
		/// </summary>
		public void Resize(int newWidth, int newHeight)
		{
			newWidth = Math.Clamp(newWidth, 8, MaxSize);
			newHeight = Math.Clamp(newHeight, 8, MaxSize);

			byte[] oldTerrain = (HasTerrain || (Terrain != null && Terrain.Length == Width * Height)) ? Terrain : null;
			byte[] oldBlocked = (BuildBlocked != null && BuildBlocked.Length == Width * Height) ? BuildBlocked : null;
			int oldWidth = Width;
			int oldHeight = Height;

			Width = newWidth;
			Height = newHeight;
			Terrain = new byte[newWidth * newHeight];
			BuildBlocked = new byte[newWidth * newHeight];

			if (oldTerrain != null && oldWidth > 0 && oldHeight > 0)
			{
				int copyW = Math.Min(oldWidth, newWidth);
				int copyH = Math.Min(oldHeight, newHeight);
				for (int y = 0; y < copyH; y++)
				{
					for (int x = 0; x < copyW; x++)
					{
						Terrain[y * newWidth + x] = oldTerrain[y * oldWidth + x];
						if (oldBlocked != null)
							BuildBlocked[y * newWidth + x] = oldBlocked[y * oldWidth + x];
					}
				}
			}
		}

		/// <summary>用同一格填满整张地图。</summary>
		public void Fill(int source, int variant = 0)
		{
			EnsureTerrain();
			byte encoded = (byte)Encode(source, variant);
			for (int i = 0; i < Terrain.Length; i++)
				Terrain[i] = encoded;
		}

		private void EnsureTerrain()
		{
			if (Terrain == null || Terrain.Length != Width * Height)
				Terrain = new byte[Width * Height];
		}

		private void EnsureBuildMask()
		{
			if (BuildBlocked == null || BuildBlocked.Length != Width * Height)
				BuildBlocked = new byte[Width * Height];
		}

		// =========================================================
		// 查询辅助
		// =========================================================

		public MapSpawnPoint GetSpawnForTeam(int teamId)
		{
			if (SpawnPoints == null) return null;
			foreach (var sp in SpawnPoints)
			{
				if (sp != null && sp.TeamSlot == teamId) return sp;
			}
			return null;
		}

		public MapRegion FindRegion(string regionId)
		{
			if (Regions == null || string.IsNullOrEmpty(regionId)) return null;
			foreach (var r in Regions)
			{
				if (r != null && r.RegionId == regionId) return r;
			}
			return null;
		}

		public TriggerDefinition FindTrigger(string triggerId)
		{
			if (Triggers == null || string.IsNullOrEmpty(triggerId)) return null;
			foreach (var t in Triggers)
			{
				if (t != null && t.TriggerId == triggerId) return t;
			}
			return null;
		}

		/// <summary>把格子坐标转成世界坐标（格中心）。与 MapGrid.GridToWorldCentered 一致。</summary>
		public static Vector2 GridToWorld(int gridX, int gridY) =>
			new Vector2(gridX * TileSize + TileSize * 0.5f, gridY * TileSize + TileSize * 0.5f);

		/// <summary>世界坐标转格子坐标。与 MapGrid.WorldToGrid 一致（纯数学，可跨线程）。</summary>
		public static Vector2I WorldToGrid(Vector2 world) =>
			new Vector2I((int)Mathf.Floor(world.X / TileSize), (int)Mathf.Floor(world.Y / TileSize));
	}
}
