using Godot;
using System.Linq;
using System.Collections.Generic;
using RTS.Core;
using RTS.Data;
using RTS.Units;
using RTS.World;
using RTS.Simulation;

namespace RTS.UI
{
	public partial class Minimap : Control
	{
		[ExportGroup("Visuals")]
		[Export] public Color CameraBoxColor = new Color(1, 1, 1, 0.7f);

		[ExportGroup("Terrain Colors")]
		[Export] public bool ShowTerrain = true;
		[Export] public Color WalkableColor = new Color(0.5f, 1f, 0.5f, 1.0f); // 地板 ID 0
		[Export] public Color WallColor = new Color(0.6f, 0.6f, 0.6f, 1.0f);     // 墙 ID 1
		[Export] public int TerrainSkipStep = 1;

		[ExportGroup("Marker Settings")]
		[Export] public float StructureMarkerSize = 3.0f; // 缩小图标
		[Export] public float UnitMarkerSize = 1.5f;      // 缩小圆点

		private RTSCamera _camera;
		private Rect2 _worldBounds;
		private Vector2 _scaleRatio;
		private Vector2 _drawOffset;
		private Vector2 _actualMinimapDrawSize;
		private ImageTexture _terrainTexture;
		private ulong _lastRedrawMs;
		private const ulong RedrawIntervalMs = 100; // 小地图实体/相机框 ~10fps 重绘即可
		private Vector2 _lastUiSize;
		/// <summary>上次用于计算范围的地形版本（地形变了要重算，见 _Process）。</summary>
		private int _lastDataRevision = -1;

		private ColorRect _fogOverlay; // 引用 Shader 节点
		private MinimapEntityLayer _entityLayer;

		public override void _Ready()
		{
			_camera = GetViewport().GetCamera3D() as RTSCamera;
			_fogOverlay = GetNodeOrNull<ColorRect>("FogOverlay");

			// 实体层追加在 FogOverlay 之后：地形(下) -> 迷雾 -> 实体/相机框(上)
			_entityLayer = new MinimapEntityLayer { Name = "EntityLayer" };
			_entityLayer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			_entityLayer.Setup(this);
			AddChild(_entityLayer);

			CallDeferred(MethodName.InitMapBounds);
		}

		private void InitMapBounds()
		{
			_worldBounds = ComputeMapBounds();

			// 等比例**放大到填满**（contain）：地图长边抵住窗口边缘，短边居中留空，
			// 不溢出裁剪。也就是"在框内尽可能大，并且居中"。
			//
			// 注意 uiSize 用的是 Size（帧尺寸），所以"把帧画大一点" = 小地图跟着变大；
			// 下面的 RefreshLayoutIfNeeded 会在帧尺寸或地形变化时重算。
			Vector2 uiSize = Size;

			// 尺寸无效时（布局尚未完成）不要写出一堆 0，等下一帧
			if (uiSize.X < 1f || uiSize.Y < 1f)
				return;

			float worldAspect = _worldBounds.Size.X / _worldBounds.Size.Y;
			float uiAspect = uiSize.X / uiSize.Y;

			if (worldAspect > uiAspect)
			{
				_actualMinimapDrawSize.X = uiSize.X;
				_actualMinimapDrawSize.Y = uiSize.X / worldAspect;
			}
			else
			{
				_actualMinimapDrawSize.Y = uiSize.Y;
				_actualMinimapDrawSize.X = uiSize.Y * worldAspect;
			}

			// 居中：短边两侧留等量空隙
			_drawOffset = (uiSize - _actualMinimapDrawSize) / 2.0f;
			_scaleRatio = _actualMinimapDrawSize / _worldBounds.Size;

			// 将迷雾纹理传递给 Shader
			if (_fogOverlay != null && FogOfWar.Instance != null)
			{
				var mat = _fogOverlay.Material as ShaderMaterial;
				if (mat != null)
				{
					if (FogOfWar.Instance.ExplorationTexture != null)
					{
						mat.SetShaderParameter("exploration_texture", FogOfWar.Instance.ExplorationTexture);
						mat.SetShaderParameter("vision_texture", FogOfWar.Instance.VisionTexture);
						mat.SetShaderParameter("map_origin", _worldBounds.Position);
						mat.SetShaderParameter("map_size", _worldBounds.Size);
					}
				}

				// 调整 Shader 节点大小以匹配地图实际显示区域
				_fogOverlay.Position = _drawOffset;
				_fogOverlay.Size = _actualMinimapDrawSize;
			}

			BuildTerrainTexture();
		}

		// 小地图范围 = 能包住所有墙壁与地板格的长方形（含 1 格外扩），
		// 而不是固定 20000x20000 的世界边界，放大后填满窗口
		private Rect2 ComputeMapBounds()
		{
			if (MapGrid.Instance?.BaseMapLayer is TileMapLayer layer)
			{
				var cells = layer.GetUsedCells();
				if (cells.Count > 0)
				{
					int minX = int.MaxValue;
					int minY = int.MaxValue;
					int maxX = int.MinValue;
					int maxY = int.MinValue;
					foreach (Vector2I c in cells)
					{
						minX = Mathf.Min(minX, c.X);
						minY = Mathf.Min(minY, c.Y);
						maxX = Mathf.Max(maxX, c.X);
						maxY = Mathf.Max(maxY, c.Y);
					}

					const float pad = 64f;
					float left = minX * 64f - pad;
					float top = minY * 64f - pad;
					float width = (maxX - minX + 1) * 64f + pad * 2f;
					float height = (maxY - minY + 1) * 64f + pad * 2f;
					return new Rect2(left, top, width, height);
				}
			}

			return MapGrid.Instance != null
				? MapGrid.WorldBounds
				: new Rect2(-2000, -2000, 4000, 4000);
		}

		public override void _Process(double delta)
		{
			// 布局尺寸可能在 _Ready 时还是 0（锚点布局未完成），
			// 尺寸有效或变化时重建底图/迷雾层，避免小地图一片空白。
			//
			// 另外监听 MapGrid.DataRevision：地图数据是在 Game.SetupMatch 里应用的，
			// 而本节点的 _Ready（含 CallDeferred(InitMapBounds)）**先于**它执行
			// （Godot 的 _ready 是子先父后）。不改判据的话，小地图会一直按
			// "应用地形之前"的旧范围来算 —— 表现就是范围不对、地图没填满/没居中。
			bool sizeChanged = Size != _lastUiSize;
			bool mapChanged = _lastDataRevision != MapGrid.DataRevision;

			if (sizeChanged || mapChanged)
			{
				_lastUiSize = Size;
				_lastDataRevision = MapGrid.DataRevision;
				if (Size.X > 1f && Size.Y > 1f)
					InitMapBounds();
			}

			// 每帧同步迷雾纹理（纹理对象会随帧更新）
			if (_fogOverlay != null && FogOfWar.Instance != null && FogOfWar.Instance.VisionTexture != null)
			{
				var mat = _fogOverlay.Material as ShaderMaterial;
				if (mat != null)
				{
					mat.SetShaderParameter("exploration_texture", FogOfWar.Instance.ExplorationTexture);
					mat.SetShaderParameter("vision_texture", FogOfWar.Instance.VisionTexture);
				}
			}

			ulong now = Time.GetTicksMsec();
			if (now - _lastRedrawMs >= RedrawIntervalMs)
			{
				_lastRedrawMs = now;
				QueueRedraw();
				_entityLayer?.QueueRedraw();
			}
		}

		public override void _Draw()
		{
			// 1. 绘制纯背景（用于承载 Shader 没覆盖的部分）
			DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 1));

			// 2. 绘制地形（底图）
			if (ShowTerrain) DrawTerrainThumbnail();

			// 3. 绘制实体（单位和建筑）
			// 实体由 EntityLayer 绘制（位于迷雾之上）

			// 4. 迷雾层 (FogOverlay 子节点) 覆盖在地形之上
			// 5. 相机视野框由 EntityLayer 绘制（迷雾上层）
		}

		// 实体层绘制入口：在迷雾之上画实体点与相机框
		public void DrawEntityLayer(Control target)
		{
			DrawEntitiesOnLayer(target);
			if (_camera != null)
				DrawCameraBox(target);
		}

		private void DrawTerrainThumbnail()
		{
			if (_terrainTexture != null)
			{
				DrawTextureRect(_terrainTexture, new Rect2(_drawOffset, _actualMinimapDrawSize), false);
				return;
			}

			if (MapGrid.Instance?.BaseMapLayer == null) return;
			var layer = MapGrid.Instance.BaseMapLayer;
			var cells = layer.GetUsedCells();
			int tileSize = layer.TileSet.TileSize.X;

			float px = Mathf.Max(1.0f, _scaleRatio.X * tileSize);
			float py = Mathf.Max(1.0f, _scaleRatio.Y * tileSize);
			Vector2 dotSize = new Vector2(px, py);

			for (int i = 0; i < cells.Count; i += TerrainSkipStep)
			{
				Vector2I cellPos = cells[i];
				int sourceId = layer.GetCellSourceId(cellPos);
				Color drawColor = (sourceId == 1) ? WallColor : WalkableColor;

				Vector2 worldPos = layer.ToGlobal(layer.MapToLocal(cellPos));
				Vector2 mapPos = WorldToMinimap(worldPos);
				DrawRect(new Rect2(mapPos, dotSize), drawColor);
			}
		}

		// 地形底图只生成一次：把全部地面格画进一张 ImageTexture，避免每帧 22k 次 DrawRect
		private void BuildTerrainTexture()
		{
			if (MapGrid.Instance?.BaseMapLayer == null)
				return;

			var layer = MapGrid.Instance.BaseMapLayer;

			int texW = Mathf.Max(1, (int)Mathf.Ceil(_actualMinimapDrawSize.X));
			int texH = Mathf.Max(1, (int)Mathf.Ceil(_actualMinimapDrawSize.Y));
			var image = Image.CreateEmpty(texW, texH, false, Image.Format.Rgba8);
			var simGrid = RTS.Core.SimManager.Instance?.World?.Grid;

			// 逐像素填充：每个纹理像素映射回世界格，保证随缩放每格连续无缝隙
			for (int py = 0; py < texH; py++)
			{
				for (int px = 0; px < texW; px++)
				{
					float wx = _worldBounds.Position.X + ((px + 0.5f) / texW) * _worldBounds.Size.X;
					float wy = _worldBounds.Position.Y + ((py + 0.5f) / texH) * _worldBounds.Size.Y;
					Vector2I cell = MapGrid.Instance.WorldToGrid(new Vector2(wx, wy));
					var simCell = new RTS.Simulation.SimVector2I(cell.X, cell.Y);

					Color c;
					if (simGrid != null && simGrid.StaticObstacles.Contains(simCell))
						c = WallColor;
					else if (simGrid != null && simGrid.TerrainCells.Contains(simCell))
						c = WalkableColor;
					else
						c = new Color(0.08f, 0.08f, 0.08f, 1f);
					image.SetPixel(px, py, c);
				}
			}

			_terrainTexture = ImageTexture.CreateFromImage(image);
		}

		// 矢量绘制实体：建筑按实际占地格数画方块，单位按 Footprint 画圆点，
		// 尺寸与单位大小挂钩（4x4 建筑 = 4x4 格的墙一样大），由渲染器保证清晰度
		private void DrawEntitiesOnLayer(Control target)
		{
			var entities = GetTree().GetNodesInGroup("entities");
			int localTeam = Main.Instance?.LocalPlayerID ?? 0;
			// 迷雾可见格未就绪（首帧/未初始化）时不过滤敌方，避免小地图一片空白
			bool fogReady = FogOfWar.Instance != null &&
				FogOfWar.Instance.CurrentVisibleCells.Count > 0;
			float cellsPerWorld = _worldBounds.Size.X / 64f;
			float pxPerCell = _actualMinimapDrawSize.X / cellsPerWorld;

			foreach (Node node in entities)
			{
				if (node is IEntity entity && !entity.IsDeadOrNull())
				{
					// 中立建筑/单位常驻显示；己方/盟友常驻显示；敌方按当前可见格子过滤
					int team = entity.TeamID;
					bool isAlly = team > 0 && team != localTeam &&
						FogOfWar.Instance != null && FogOfWar.Instance.CurrentViewerTeams.Contains(team);
					if (team > 0 && team != localTeam && !isAlly &&
						fogReady && !IsEnemyVisible(entity))
						continue;

					Vector2 mapPos = WorldToMinimap(entity.GlobalPosition);
					Color c = team <= 0
						? Colors.Gold
						: (team == localTeam
							? Colors.Green
							: (isAlly ? Colors.Cyan : Colors.Red));

					if (entity.IsStructure)
					{
						int gw = 1;
						int gh = 1;
						if (node is Structure st)
						{
							if (st.SimStructureData != null)
							{
								gw = st.SimStructureData.GridWidth;
								gh = st.SimStructureData.GridHeight;
							}
							else
							{
								gw = st.GridSize;
								gh = st.GridSize;
							}
						}
						float w = Mathf.Max(1.5f, gw * pxPerCell);
						float h = Mathf.Max(1.5f, gh * pxPerCell);
						// 最细黑色描边，提升在深色地形上的可读性
						float bw = w + 1f;
						float bh = h + 1f;
						target.DrawRect(new Rect2(mapPos - new Vector2(bw / 2f, bh / 2f), new Vector2(bw, bh)), Colors.Black);
						target.DrawRect(new Rect2(mapPos - new Vector2(w / 2f, h / 2f), new Vector2(w, h)), c);
					}
					else
					{
						float footprint = 1f;
						if (node is Unit u && u.LogicEntity is SimUnit su)
							footprint = (float)su.FootprintTiles;
						float radius = Mathf.Max(0.6f, footprint * pxPerCell / 2f);
						target.DrawCircle(mapPos, radius + 0.75f, Colors.Black);
						target.DrawCircle(mapPos, radius, c);
					}
				}
			}
		}

		// 敌方实体：单位看中心格，建筑看占地区域内任一格；仅在当前视野内绘制
		private bool IsEnemyVisible(IEntity entity)
		{
			if (MapGrid.Instance == null || FogOfWar.Instance == null)
				return false;

			Vector2I grid = MapGrid.Instance.WorldToGrid(entity.GlobalPosition);
			if (entity is Structure s)
			{
				Vector2I topLeft = MapGrid.Instance.GetTopLeftFromCenter(grid, s.GridSize);
				for (int x = 0; x < s.GridSize; x++)
				{
					for (int y = 0; y < s.GridSize; y++)
					{
						if (FogOfWar.Instance.CurrentVisibleCells.Contains(topLeft + new Vector2I(x, y)))
							return true;
					}
				}
				return false;
			}

			return FogOfWar.Instance.CurrentVisibleCells.Contains(grid);
		}

		private void DrawCameraBox(Control target)
		{
			// 倾斜视角：屏幕四角射线打地面，转小地图坐标后按顺序连线，
			// 透视投影下近大远小，视野框是梯形而非矩形
			Rect2 vp = GetViewportRect();
			Vector2[] screenCorners =
			{
				vp.Position,
				new Vector2(vp.End.X, vp.Position.Y),
				vp.End,
				new Vector2(vp.Position.X, vp.End.Y)
			};

			var ground = new Vector2[4];
			for (int i = 0; i < 4; i++)
			{
				Vector3 origin = _camera.ProjectRayOrigin(screenCorners[i]);
				Vector3 dir = _camera.ProjectRayNormal(screenCorners[i]);
				if (Mathf.IsZeroApprox(dir.Y))
					return;

				float t = -origin.Y / dir.Y;
				Vector3 point = origin + dir * t;
				ground[i] = WorldToMinimap(new Vector2(point.X, point.Z));
			}

			// 首点重复以闭合梯形线框
			var closed = new Vector2[5];
			System.Array.Copy(ground, closed, 4);
			closed[4] = ground[0];
			target.DrawPolyline(closed, CameraBoxColor, 1f, true);
		}

		private Vector2 WorldToMinimap(Vector2 worldPos)
		{
			Vector2 normalized = (worldPos - _worldBounds.Position) / _worldBounds.Size;
			return _drawOffset + (normalized * _actualMinimapDrawSize);
		}

		private Vector2 MinimapToWorld(Vector2 mapPos)
		{
			Vector2 normalized = (mapPos - _drawOffset) / _actualMinimapDrawSize;
			return _worldBounds.Position + (normalized * _worldBounds.Size);
		}

		public override void _GuiInput(InputEvent @event)
		{
			if (@event is InputEventMouseButton mb && mb.Pressed)
			{
				if (mb.ButtonIndex == MouseButton.Left)
					MoveCameraTo(mb.Position);
				else if (mb.ButtonIndex == MouseButton.Right)
				{
					// 右键小地图 = 下指令（选中单位移动到对应地点）
					var user = GetTree().Root.FindChild("UserController", true, false) as RTS.Core.UserController;
					user?.HandleMinimapRightClick(MinimapToWorld(mb.Position));
				}
			}
			else if (@event is InputEventMouseMotion mm && Input.IsMouseButtonPressed(MouseButton.Left))
				MoveCameraTo(mm.Position);
		}

		private void MoveCameraTo(Vector2 localUiPos)
		{
			if (_camera == null) return;
			Vector2 target = MinimapToWorld(localUiPos);
			target.X = Mathf.Clamp(target.X, _worldBounds.Position.X, _worldBounds.End.X);
			target.Y = Mathf.Clamp(target.Y, _worldBounds.Position.Y, _worldBounds.End.Y);
			// 让屏幕中心对准点击的格子（相机前方有 lookAhead 偏移，不能直接挪位置）
			if (_camera is RTSCamera rts)
				rts.SetViewCenter(target);
			else
				_camera.GlobalPosition = new Vector3(target.X, _camera.GlobalPosition.Y, target.Y);
		}
	}
}
