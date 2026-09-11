// File: res://Scripts/Core/FogOfWar.cs

using Godot;
using System.Collections.Generic;
using RTS.Data;
using RTS.Core;
using RTS.Units;

namespace RTS.World
{
	public partial class FogOfWar : Node2D
	{
		// 建筑记忆幽灵：曾见建筑脱视野被摧毁后保留的灰色模型（重新看到才清除）
		private readonly System.Collections.Generic.List<Node3D> _structureGhosts = new();

		public void RememberStructureGhost(Structure structure)
		{
			var ghost = structure.VisualsModule?.CreateGhostCopy();
			if (ghost == null)
				return;

			ghost.TopLevel = true;
			ghost.Position = new Vector3(
				structure.GlobalPosition.X,
				structure.GlobalPosition.Y,
				structure.GlobalPosition.Z);
			AddChild(ghost);
			_structureGhosts.Add(ghost);
		}

		// 幽灵清理：该位置重新被当前视野点亮就移除记忆
		private void RefreshStructureGhosts()
		{
			for (int i = _structureGhosts.Count - 1; i >= 0; i--)
			{
				var g = _structureGhosts[i];
				if (!GodotObject.IsInstanceValid(g))
				{
					_structureGhosts.RemoveAt(i);
					continue;
				}

				var cell = MapGrid.Instance?.WorldToGrid(new Vector2(g.GlobalPosition.X, g.GlobalPosition.Z));
				if (cell.HasValue && _currentVisibleCells.Contains(cell.Value))
				{
					_structureGhosts.RemoveAt(i);
					g.QueueFree();
				}
			}
		}

		public static FogOfWar Instance { get; private set; }

		[ExportGroup("Dependencies")]
		[Export] public SubViewport ExplorVP;
		[Export] public SubViewport VisionVP;
		[Export] public Node2D ExplorPainter;
		[Export] public Node2D VisionPainter;
		[Export] public Sprite2D Overlay;
		[ExportGroup("Settings")]
		[Export] public int FogResolution = 1024;
		// 全图视野开关（压测/调试用）：雾图全白、所有实体可见
		[Export] public bool RevealAll = false;

		public int CurrentViewerTeam { get; set; } = -1;
		// P1-5：盟友共享视野（2v2 同组队伍互相可见）
		public readonly HashSet<int> CurrentViewerTeams = new();
		private Vector2 _mapOffset;
		private Vector2 _mapSize;
		private Vector2 _scaleRatio;

		private List<IEntity> _myVisionSources = new();
		// 临时视野（雷达等技能）：中心/半径/过期毫秒，纯表现层
		private readonly List<(Vector2 Center, float Radius, ulong ExpireMs)> _tempReveals = new();
		// 当前帧被视野点亮的格子集合（实体可见性与菌毯共用）
		private readonly HashSet<Vector2I> _currentVisibleCells = new();
		public HashSet<Vector2I> CurrentVisibleCells => _currentVisibleCells;
		private bool _revealAllReady = false;
		private bool _revealAllVisibilityApplied = false;

		// 代码生成的迷雾纹理（可见=白 / 已探索=灰 / 未探索=黑）
		private Image _visionImage;
		private Image _explorationImage;
		private ImageTexture _visionTexture;
		private ImageTexture _explorationTexture;
		private ulong _lastFogUpdateMs;
		private const ulong FogUpdateIntervalMs = 66; // 迷雾 ~15fps 刷新（1024² 纹理重传太贵，不值得每帧做）

		public Texture2D VisionTexture => _visionTexture;
		public Texture2D ExplorationTexture => _explorationTexture;

		public override void _Ready()
		{
			Instance = this;
			// 初始尝试获取一次，如果还没分配，Game.cs 稍后会覆盖它
			CurrentViewerTeam = Main.Instance?.LocalPlayerID ?? -1;
			CallDeferred(MethodName.InitFogBounds);

			// 修改给 CreepManager 的绑定逻辑，改用 CurrentViewerTeam
			if (CreepManager.Instance != null)
			{
				CreepManager.Instance.IsCellVisibleToPlayer = (cell) =>
				{
					var realTimeSources = new List<IEntity>();
					foreach (Node node in GetTree().GetNodesInGroup("entities"))
					{
						if (node is IEntity entity &&
							(CurrentViewerTeams.Contains(entity.TeamID) || entity.TeamID == CurrentViewerTeam) &&
							!entity.IsDeadOrNull())
						{
							if (entity is Structure s && s.CurrentState == Structure.StructureState.Blueprint) continue;
							realTimeSources.Add(entity);
						}
					}
					return IsCellVisible(cell, realTimeSources);
				};
			}
		}

		private void InitFogBounds()
		{
			Rect2 worldBounds;

			// 固定覆盖整个活动区域，避免出生点/地图外区域没有迷雾映射
			worldBounds = MapGrid.Instance != null
				? MapGrid.WorldBounds
				: new Rect2(-2000, -2000, 4000, 4000);

			_mapOffset = worldBounds.Position;
			_mapSize = worldBounds.Size;

			if (_mapSize.X > 0 && _mapSize.Y > 0)
			{
				float aspectRatio = _mapSize.X / _mapSize.Y;
				Vector2I newSize = (aspectRatio >= 1.0f) ?
					new Vector2I(FogResolution, (int)(FogResolution / aspectRatio)) :
					new Vector2I((int)(FogResolution * aspectRatio), FogResolution);

				ExplorVP.Size = newSize;
				VisionVP.Size = newSize;
				_scaleRatio = (Vector2)newSize / _mapSize;
			}
			else
			{
				_scaleRatio = Vector2.One;
			}

			if (Overlay != null)
			{
				Overlay.Centered = false;
				Overlay.GlobalPosition = _mapOffset;
				if (Overlay.Texture != null) Overlay.Scale = _mapSize / Overlay.Texture.GetSize();

				var mat = Overlay.Material as ShaderMaterial;
				if (mat != null)
				{
					mat.SetShaderParameter("vision_texture", VisionVP.GetTexture());
					mat.SetShaderParameter("exploration_texture", ExplorVP.GetTexture());
				}
			}
		}

		public override void _Process(double delta)
		{
			// 全图视野：第一帧点亮后无需再逐帧维护迷雾/实体显隐
			if (RevealAll && _revealAllReady)
			{
				// 晚开的全图视野也要补一次实体显隐（含之后生成的单位）
				if (!_revealAllVisibilityApplied)
				{
					_revealAllVisibilityApplied = true;
					UpdateEntityVisibility();
				}
				return;
			}

			ulong now = Time.GetTicksMsec();
			if (now - _lastFogUpdateMs < FogUpdateIntervalMs)
				return;
			_lastFogUpdateMs = now;

			ExplorPainter.QueueRedraw();
			VisionPainter.QueueRedraw();
			UpdateEntityVisibility();
			UpdateFogTextures();
		}

		// 每帧生成迷雾纹理：视野=圆盘，已探索=历史圆盘累积（不清空）
		private void UpdateFogTextures()
		{
			if (_visionImage == null)
			{
				_visionImage = Image.CreateEmpty(FogResolution, FogResolution, false, Image.Format.Rgba8);
				_explorationImage = Image.CreateEmpty(FogResolution, FogResolution, false, Image.Format.Rgba8);
				_explorationImage.Fill(new Color(0f, 0f, 0f, 1f));
				_visionTexture = ImageTexture.CreateFromImage(_visionImage);
				_explorationTexture = ImageTexture.CreateFromImage(_explorationImage);
			}

			_visionImage.Fill(new Color(0f, 0f, 0f, 1f));

			if (RevealAll)
			{
				_visionImage.Fill(Colors.White);
				_explorationImage.Fill(Colors.White);
				_visionTexture.Update(_visionImage);
				_explorationTexture.Update(_explorationImage);
				return;
			}

			foreach (var source in GetVisionSources())
			{
				DrawCircleOnImage(_visionImage, source.GlobalPosition, source.VisionRange, new Color(1f, 1f, 1f, 1f));
				// 已探索记忆：只增不清，自动累积成连续区域
				DrawCircleOnImage(_explorationImage, source.GlobalPosition, source.VisionRange, new Color(1f, 1f, 1f, 1f));
			}

			PruneExpiredReveals();

			foreach (var reveal in _tempReveals)
			{
				DrawCircleOnImage(_visionImage, reveal.Center, reveal.Radius, new Color(1f, 1f, 1f, 1f));
				DrawCircleOnImage(_explorationImage, reveal.Center, reveal.Radius, new Color(1f, 1f, 1f, 1f));
			}

			_visionTexture.Update(_visionImage);
			_explorationTexture.Update(_explorationImage);
		}

		// 在纹理上画一个实心圆（视野是连续圆盘，不是格子方块）
		private void DrawCircleOnImage(Image image, Vector2 centerWorld, float radiusWorld, Color color)
		{
			if (MapGrid.Instance == null)
				return;

			float scale = FogResolution / MapGrid.WorldSize;
			int cx = (int)((centerWorld.X - MapGrid.WorldMin) * scale);
			int cy = (int)((centerWorld.Y - MapGrid.WorldMin) * scale);
			int r = Mathf.Max(2, (int)(radiusWorld * scale));
			int rSq = r * r;

			int x0 = Mathf.Max(0, cx - r);
			int x1 = Mathf.Min(FogResolution - 1, cx + r);
			int y0 = Mathf.Max(0, cy - r);
			int y1 = Mathf.Min(FogResolution - 1, cy + r);

			for (int y = y0; y <= y1; y++)
			{
				for (int x = x0; x <= x1; x++)
				{
					int dx = x - cx;
					int dy = y - cy;

					if (dx * dx + dy * dy <= rSq)
						image.SetPixel(x, y, color);
				}
			}
		}

		public Vector2 WorldToFogPos(Vector2 worldPos) => (worldPos - _mapOffset) * _scaleRatio;
		public float WorldToFogRadius(float radius) => radius * _scaleRatio.X;

		// 圆形与矩形格子相交判定（用“中心到矩形的最短距离平方”与半径平方比较，避免开方）
		private static bool CircleIntersectsCell(Vector2 centerWorld, Vector2 cellCenter, float halfSize, float radius)
		{
			float dx = Mathf.Max(0f, Mathf.Abs(centerWorld.X - cellCenter.X) - halfSize);
			float dy = Mathf.Max(0f, Mathf.Abs(centerWorld.Y - cellCenter.Y) - halfSize);
			return dx * dx + dy * dy <= radius * radius;
		}

		public void AddTemporaryReveal(Vector2 centerWorld, float radiusWorld, float seconds)
		{
			if (radiusWorld <= 0f || seconds <= 0f)
				return;

			_tempReveals.Add((centerWorld, radiusWorld, Time.GetTicksMsec() + (ulong)(seconds * 1000.0)));
		}

		private void PruneExpiredReveals()
		{
			ulong now = Time.GetTicksMsec();
			_tempReveals.RemoveAll(r => now >= r.ExpireMs);
		}

		public List<IEntity> GetVisionSources()
		{
			_myVisionSources.Clear();
			var nodes = GetTree().GetNodesInGroup("entities");
			foreach (Node node in nodes)
			{
				if (node is IEntity entity && !entity.IsDeadOrNull())
				{
					bool isGlobalVision = node.HasMeta("GlobalVision") && node.GetMeta("GlobalVision").AsBool();
					// 圣地：中立但给所有人提供视野（作为视野光源参与点亮格子）
					if (isGlobalVision && entity is ShrineStructure)
					{
						_myVisionSources.Add(entity);
						continue;
					}

					if ((CurrentViewerTeams.Contains(entity.TeamID) || entity.TeamID == CurrentViewerTeam) &&
						!isGlobalVision)
					{
						if (entity is Structure s && s.CurrentState == Structure.StructureState.Blueprint) continue;
						_myVisionSources.Add(entity);
					}
				}
			}
			return _myVisionSources;
		}

		private void UpdateEntityVisibility()
		{
			int localId = Main.Instance?.LocalPlayerID ?? 0;
			var entities = GetTree().GetNodesInGroup("entities");
			var sources = GetVisionSources();

			// 1. 先计算当前帧被点亮的格子（实体与菌毯共用同一套判定）
			UpdateVisibleCells(sources);
			RefreshStructureGhosts();

			// 2. 实体显隐：单位/建筑按“所在格子是否被点亮”判定
			foreach (Node node in entities)
			{
				if (node is IEntity entity && !entity.IsDeadOrNull())
				{
					bool isGlobalVision = node.HasMeta("GlobalVision") && node.GetMeta("GlobalVision").AsBool();

					// 蓝图对敌方完全不可见：敌方的蓝图既不该被看到，也不阻挡/推出敌方单位。
					// 自己人与盟友的蓝图照常可见（走下面的己方分支）。
					if (!RevealAll && entity is Structure bpStruct &&
						bpStruct.CurrentState == Structure.StructureState.Blueprint &&
						!CurrentViewerTeams.Contains(entity.TeamID) &&
						entity.TeamID != CurrentViewerTeam &&
						entity.TeamID != localId)
					{
						if (node is Node3D nBp)
							nBp.Visible = false;
						continue;
					}

					// 己方/盟友与 GlobalVision 实体（圣地等）始终可见
					if (CurrentViewerTeams.Contains(entity.TeamID) ||
						entity.TeamID == CurrentViewerTeam ||
						entity.TeamID == localId ||
						isGlobalVision || RevealAll)
					{
						if (node is Node3D nSelf)
							nSelf.Visible = true;
						// 全图视野下敌方单位/建筑都不再迷雾隐藏，血条照常显示
						if (RevealAll && entity.VisualsModule != null)
							entity.VisualsModule?.SetFogHidden(false);
						continue;
					}

					bool isVisible = IsEntityVisible(entity);

					if (node is Node3D n3d)
					{
						if (entity is Unit)
						{
							n3d.Visible = isVisible;
						}
					else if (entity is Structure s)
					{
						// 建筑脱视野：模型保留灰色记忆显示（血条由 SetFogHidden 隐藏）
						n3d.Visible = true;
						entity.VisualsModule?.SetFogHidden(!isVisible);
					}
					}
				}
			}
		}

		// 实体可见性：单位看中心格；建筑看占地区域内是否有任意格被点亮
		private bool IsEntityVisible(IEntity entity)
		{
			if (MapGrid.Instance == null || _currentVisibleCells.Count == 0)
				return false;

			Vector2I grid = MapGrid.Instance.WorldToGrid(entity.GlobalPosition);

			if (entity is Structure s)
			{
				Vector2I topLeft = MapGrid.Instance.GetTopLeftFromCenter(grid, s.GridSize);

				for (int x = 0; x < s.GridSize; x++)
				{
					for (int y = 0; y < s.GridSize; y++)
					{
						if (_currentVisibleCells.Contains(topLeft + new Vector2I(x, y)))
							return true;
					}
				}

				return false;
			}

			return _currentVisibleCells.Contains(grid);
		}

			// 判断圆（视野）与正方形（格子）是否相交
		private bool IsCellVisible(Vector2I cell, List<IEntity> sources)
		{
			if (MapGrid.Instance == null) return false;

			int tileSize = MapGrid.Instance.BaseMapLayer?.TileSet?.TileSize.X ?? 64;
			float halfSize = tileSize / 2.0f;
			Vector2 cellCenter = MapGrid.Instance.GridToWorldCentered(cell);

			foreach (var source in sources)
			{
				float radius = source.VisionRange;

				if (CircleIntersectsCell(source.GlobalPosition, cellCenter, halfSize, radius))
					return true;
			}
			return false;
		}

		// 批量计算当前帧可见格子，并通知菌毯表现层
		private void UpdateVisibleCells(List<IEntity> sources)
		{
			_currentVisibleCells.Clear();

			if (RevealAll)
			{
				// 全图视野：只填充一次可见格缓存，不再每 66ms 重建 2.6 万格
				if (!_revealAllReady)
				{
					AddAllTerrainCells();
					CreepManager.Instance?.RevealCells(_currentVisibleCells);
					_revealAllReady = true;
				}
				return;
			}

			if (MapGrid.Instance == null)
				return;

			// 视野模式由种族配置驱动：1 = 地毯视野（纳米：只有地毯与巨兽提供视野）
			var viewerRace = Game.GetPlayerByTeam(CurrentViewerTeam)?.Race?.RaceName;
			int visionMode = viewerRace != null
				? (RTS.Data.Configs.ConfigDatabase.GetRace(viewerRace)?.VisionMode ?? 0)
				: 0;

			if (visionMode == 1)
			{
				// 地毯视野：菌毯即视野。但普通单位/建筑的视野也必须点亮，
				// 否则被摧毁的敌方菌毯格会掉出 visible，菌毯渲染记忆永远残留（“打了不掉”）
				foreach (var source in sources)
					AddSourceVisibleCells(source);

				AddCarpetVisibleCells();
				CreepManager.Instance?.RevealCells(_currentVisibleCells);
				return;
			}

			foreach (var source in sources)
				AddSourceVisibleCells(source);

			foreach (var reveal in _tempReveals)
				AddRevealVisibleCells(reveal.Center, reveal.Radius);

			CreepManager.Instance?.RevealCells(_currentVisibleCells);
		}

		private void AddAllTerrainCells()
		{
			var grid = RTS.Core.SimManager.Instance?.World?.Grid;
			if (grid == null)
				return;

			foreach (var cell in grid.TerrainCells)
				_currentVisibleCells.Add(new Vector2I(cell.X, cell.Y));
		}

		private void AddRevealVisibleCells(Vector2 centerWorld, float radius)
		{
			if (MapGrid.Instance == null)
				return;

			int tileSize = MapGrid.Instance.BaseMapLayer?.TileSet?.TileSize.X ?? 64;
			float halfSize = tileSize / 2.0f;
			int gridRadius = Mathf.CeilToInt(radius / tileSize) + 1;
			Vector2I centerGrid = MapGrid.Instance.WorldToGrid(centerWorld);

			for (int x = -gridRadius; x <= gridRadius; x++)
			{
				for (int y = -gridRadius; y <= gridRadius; y++)
				{
					Vector2I cell = centerGrid + new Vector2I(x, y);

					if (_currentVisibleCells.Contains(cell))
						continue;

					Vector2 cellCenter = MapGrid.Instance.GridToWorldCentered(cell);
					if (CircleIntersectsCell(centerWorld, cellCenter, halfSize, radius))
						_currentVisibleCells.Add(cell);
				}
			}
		}

		// 单个视野来源的点亮（圆与矩形相交判定）
		private void AddSourceVisibleCells(IEntity source)
		{
			if (MapGrid.Instance == null)
				return;

			int tileSize = MapGrid.Instance.BaseMapLayer?.TileSet?.TileSize.X ?? 64;
			float halfSize = tileSize / 2.0f;
			float radius = source.VisionRange;
			int gridRadius = Mathf.CeilToInt(radius / tileSize) + 1;
			Vector2I centerGrid = MapGrid.Instance.WorldToGrid(source.GlobalPosition);

			for (int x = -gridRadius; x <= gridRadius; x++)
			{
				for (int y = -gridRadius; y <= gridRadius; y++)
				{
					Vector2I cell = centerGrid + new Vector2I(x, y);

					if (_currentVisibleCells.Contains(cell))
						continue;

					Vector2 cellCenter = MapGrid.Instance.GridToWorldCentered(cell);
					if (CircleIntersectsCell(source.GlobalPosition, cellCenter, halfSize, radius))
						_currentVisibleCells.Add(cell);
				}
			}
		}

		// 纳米地毯整体点亮（玩家视野 = 地毯）
		private void AddCarpetVisibleCells()
		{
			var carpet = RTS.Core.SimManager.Instance?.World?.CreepGrid;
			if (carpet == null)
				return;

			// 纳米与植物：菌毯即视野（纳米=纳米毯，植物=植物毯）
			foreach (var kvp in carpet.GetAllCells())
			{
				if (kvp.Value != CreepType.NanoCreep && kvp.Value != CreepType.PlantCreep)
					continue;
				// 菌毯视野只覆盖自己阵营（含同组盟友）的菌毯；
				// 敌方菌毯不提供视野，靠单位/建筑视野正常点亮
				int owner = carpet.GetOwner(kvp.Key.X, kvp.Key.Y);
				if (owner != CurrentViewerTeam && !CurrentViewerTeams.Contains(owner))
					continue;
				_currentVisibleCells.Add(new Vector2I(kvp.Key.X, kvp.Key.Y));
			}
		}

		private bool CheckIfVisible(Vector2 targetPos, List<IEntity> sources)
		{
			foreach (var source in sources)
			{
				float range = source.VisionRange;
				if (targetPos.DistanceSquaredTo(source.GlobalPosition) < range * range) return true;
			}
			return false;
		}
	}
}
