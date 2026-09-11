using Godot;
using System.Collections.Generic;

namespace RTS.World
{
	// 伪 3D 地面：草地平面 + 战争迷雾遮罩（从 FogOfWar 的 SubViewport 采样）
	public partial class MapGround3D : Node3D
	{
		[Export] public float GroundY = 0f;
		[Export] public float FogY = 0.02f;

		private MeshInstance3D _ground;
		private MeshInstance3D _fogQuad;
		private ShaderMaterial _fogMaterial;

		/// <summary>上次用于建网格的地形版本；变化时重建（见 _Process）。</summary>
		private int _builtRevision = -1;
		private bool _built;

		public override void _Ready()
		{
			CallDeferred(MethodName.Build);
		}

		// =========================================================
		// 时序坑（这一条曾经让"换地图"完全看不到效果）：
		//
		// 本节点的 Build() 也是 CallDeferred，而 **MapGrid._Ready 早于 Game._Ready**
		// 执行（Godot 的 _ready 是子节点先于父节点）。等 Game 在 SetupMatch 里
		// 把地图数据写进 SimGrid 时，**地面网格和墙体 MultiMesh 早就按旧地形建好了**，
		// 之后没有任何东西触发重建。
		//
		// 于是表现就是：逻辑已经是新地图（墙壁/阻挡都在），但屏幕上是旧地形。
		//
		// 这里监听 MapGrid.DataRevision（每次应用地图数据 +1）来重建，
		// 不再依赖"谁先 _Ready"这种脆弱顺序。
		// =========================================================
		public override void _Process(double delta)
		{
			if (_built && _builtRevision != MapGrid.DataRevision)
			{
				_builtRevision = MapGrid.DataRevision;
				GD.Print($"[MapGround3D] 地形版本变化 → 重建地面与墙体（rev={MapGrid.DataRevision}）");
				RebuildTerrain();
			}

			// 截图自检用：TERRAIN_NO_FOG=1 强制隐藏雾面。
			// 放在**所有早退之前**——之前写在 `if (_fogMaterial == null) return;` 后面，
			// 迷雾没初始化时这个开关根本不生效，导致我误判过一次。
			if (System.Environment.GetEnvironmentVariable("TERRAIN_NO_FOG") == "1" && _fogQuad != null)
			{
				_fogQuad.Visible = false;
				return;
			}

			if (_fogMaterial == null || FogOfWar.Instance == null)
				return;

			// 全图视野时雾面没有意义，直接隐藏，避免纹理更新/采样竞态导致全屏闪烁
			if (_fogQuad != null)
				_fogQuad.Visible = !FogOfWar.Instance.RevealAll;

			if (FogOfWar.Instance.VisionTexture != null)
			{
				_fogMaterial.SetShaderParameter("vision_texture", FogOfWar.Instance.VisionTexture);
				_fogMaterial.SetShaderParameter("exploration_texture", FogOfWar.Instance.ExplorationTexture);
			}
		}

		private void RebuildTerrain()
		{
			var grid = RTS.Core.SimManager.Instance?.World?.Grid;
			if (grid == null || _ground == null)
				return;

			_ground.Mesh = grid.TerrainCells.Count > 0
				? BuildGroundCellMesh(grid)
				: new PlaneMesh { Size = MapGrid.WorldBounds.Size };

			BuildWalls();
		}

		private void Build()
		{
			if (MapGrid.Instance == null)
				return;

			// 草地只铺在合法地面格上（墙外空白不铺草）
			var grid = RTS.Core.SimManager.Instance?.World?.Grid;
			Mesh groundMesh;

			if (grid != null && grid.TerrainCells.Count > 0)
			{
				groundMesh = BuildGroundCellMesh(grid);
			}
			else
			{
				// 兜底：地图数据未同步时退回整块平面
				Rect2 fallbackBounds = MapGrid.WorldBounds;
				groundMesh = new PlaneMesh { Size = fallbackBounds.Size };
			}

			_ground = new MeshInstance3D { Name = "Ground", Mesh = groundMesh };

			// ---- 地面材质：项目原本的实现 + 提亮 ----
			//
			// 原本是：世界三平面 + 逐格尺度 + 只用 Grass.png，没有 PBR 三件套。
			// CullMode 必须是 Disabled：地形三角形是"背面朝向"的，
			// 开剔除会把整块地面剔掉（截图验证过的结论）。
			//
			// 提亮：Grass.png 本身偏暗（实测平均亮度约 102/255），
			// 再叠上阴影与环境光后地面就更闷。这里用 AlbedoColor 做**乘法提亮**
			// （albedo 会乘这个颜色），比重新生成贴图更轻、也能随时调回去。
			var groundMat = new StandardMaterial3D
			{
				Uv1Triplanar = true,
				Uv1WorldTriplanar = true,
				Uv1Scale = Vector3.One / 64f,
				AlbedoTexture = RTS.Core.TextureLoader3D.LoadPng("res://ArtRes/imgs3d/Grass.png"),
				AlbedoColor = new Color(GroundBrightness(), GroundBrightness(), GroundBrightness(), 1f),
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			};

			if (System.Environment.GetEnvironmentVariable("ART_DEBUG") == "1" && !_texLogged)
			{
				_texLogged = true;
				GD.Print($"[Art] 地面材质：albedo={(groundMat.AlbedoTexture != null)} " +
					$"提亮={groundMat.AlbedoColor.R:0.##} 三平面={groundMat.Uv1Triplanar} 背面剔除={groundMat.CullMode}");
			}

			_ground.MaterialOverride = groundMat;
			_ground.Position = new Vector3(0f, GroundY, 0f);
			AddChild(_ground);

			// 战争迷雾遮罩
			Rect2 worldBounds = MapGrid.WorldBounds;
			Vector2 center = worldBounds.Position + worldBounds.Size * 0.5f;
			_fogQuad = new MeshInstance3D
			{
				Name = "FogOverlay3D",
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Mesh = new PlaneMesh { Size = worldBounds.Size }
			};

			_fogMaterial = new ShaderMaterial
			{
				Shader = GD.Load<Shader>("res://Scenes/Game/fog_of_war_3d.gdshader")
			};
			_fogQuad.MaterialOverride = _fogMaterial;
			_fogQuad.Position = new Vector3(center.X, FogY, center.Y);
			AddChild(_fogQuad);

			BuildWalls();

			// 3D 菌毯视觉（只显示被点亮的格子）
			AddChild(new CreepVisual3D { Name = "CreepVisual3D" });

			_built = true;
			_builtRevision = MapGrid.DataRevision;
		}

		// 按 TerrainCells 逐格生成草地（含地图平移后的真实世界坐标）
		private static Mesh BuildGroundCellMesh(RTS.Simulation.SimGrid grid)
		{
			int cellCount = grid.TerrainCells.Count;
			var verts = new List<Vector3>(cellCount * 4);
			var normals = new List<Vector3>(cellCount * 4);
			var uvs = new List<Vector2>(cellCount * 4);
			var indices = new List<int>(cellCount * 6);

			foreach (var cell in grid.TerrainCells)
			{
				Vector2 center = MapGrid.Instance.GridToWorldCentered(new Vector2I(cell.X, cell.Y));
				int baseIdx = verts.Count;

				verts.Add(new Vector3(center.X - 32f, 0f, center.Y - 32f));
				verts.Add(new Vector3(center.X + 32f, 0f, center.Y - 32f));
				verts.Add(new Vector3(center.X + 32f, 0f, center.Y + 32f));
				verts.Add(new Vector3(center.X - 32f, 0f, center.Y + 32f));

				for (int k = 0; k < 4; k++)
					normals.Add(Vector3.Up);

				uvs.Add(new Vector2(0f, 0f));
				uvs.Add(new Vector2(1f, 0f));
				uvs.Add(new Vector2(1f, 1f));
				uvs.Add(new Vector2(0f, 1f));

				// 绕序必须是"从上方看逆时针"，三角形法线才朝 +Y（= 朝上）。
				//
				// 原来这里写的是 (0,1,2)(0,2,3)。顶点顺序是
				//   v0=(-,-) v1=(+,-) v2=(+,+) v3=(-,+)
				// 取 v0→v1→v2 的叉积得 (0,-1024,0)：法线朝**下**，
				// 也就是所有地面三角形从上方看都是背面。
				// 当年没找到原因，就用 CullMode.Disabled 把背面也画出来绕过 ——
				// 代价是地形填充率翻倍（每个像素最多画两遍）。
				// 现在把三角形绕过来，正面朝上，剔除可以正常开启。
				indices.Add(baseIdx);
				indices.Add(baseIdx + 2);
				indices.Add(baseIdx + 1);
				indices.Add(baseIdx);
				indices.Add(baseIdx + 3);
				indices.Add(baseIdx + 2);
			}

			var arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
			arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
			arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
			arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

			var mesh = new ArrayMesh();
			mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
			return mesh;
		}

		/// <summary>
		/// 地面提亮系数（乘在 Grass.png 的 albedo 上）。
		///
		/// 1.0 = 原样。Grass.png 偏暗（平均亮度约 102/255），叠上阴影与环境光后
		/// 地面会显得发闷，所以默认给一点提亮。
		/// 用环境变量 TERRAIN_BRIGHTNESS 可现场试值，找到合适数值后改这个默认值。
		/// </summary>
		private static float GroundBrightness()
		{
			if (float.TryParse(System.Environment.GetEnvironmentVariable("TERRAIN_BRIGHTNESS"), out float v) && v > 0f)
				return v;
			return 1.45f;
		}

		// 诊断只打一次，避免每次重建地形都刷日志
		private static bool _texLogged;

		/// <summary>
		/// 墙体高度（世界单位）。与原实现一致：立方体 64 高，中心在 y=32。
		/// </summary>
		private const float WallHeight = 64f;

		/// <summary>
		/// 由障碍格生成墙体网格。
		///
		/// ---- 为什么不再用"逐格立方体" ----
		///
		/// 原实现是 MultiMesh 铺 2170 个 64³ 立方体。问题有两层：
		///   1. **贴图必然重复**：每个立方体自带 per-face UV(0~1)，
		///      于是每一块都把整张贴图重复一遍；相邻方块之间就是
		///      "同一张图反复出现 + 双重网格"的观感。
		///      改成世界三平面投影只能把重复周期拉长，**重复本身仍在**。
		///   2. **面数浪费**：内部相邻面完全看不见，却照样提交。
		///
		/// 现在的做法：先把同高度的连续障碍格**贪心合并成大矩形**，
		/// 再只为合并块生成"顶面 + 外露侧面"。一面墙因此是一整片连续几何，
		/// 贴图按合并块的世界范围映射 —— 重复被压到"一整面墙最多几次"，
		/// 而且面数从 O(格数×6) 降到接近 O(外轮廓)。
		///
		/// <summary>
		/// 由静态障碍格生成墙体（逐格 64³ 立方体，MultiMesh 合批）。
		///
		/// 这是项目**原本的实现**。我一度改成"贪心矩形合并 + 外露侧面"，
		/// 想同时解决"贴图逐格重复"和面数，但侧面生成写得不对，
		/// 结果是满屏碎条（实测截图确认），已全部回调。
		/// </summary>
		private void BuildWalls()
		{
			// 重建前先移除旧墙体节点（压力测试动态加墙时会重复调用）
			var oldMesh = GetNodeOrNull<MeshInstance3D>("Walls");
			if (oldMesh != null) { RemoveChild(oldMesh); oldMesh.QueueFree(); }
			var oldMulti = GetNodeOrNull<MultiMeshInstance3D>("Walls");
			if (oldMulti != null) { RemoveChild(oldMulti); oldMulti.QueueFree(); }

			var grid = RTS.Core.SimManager.Instance?.World?.Grid;
			if (grid == null || grid.StaticObstacles.Count == 0)
				return;

			var multiMesh = new MultiMesh
			{
				TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
				Mesh = RTS.Core.CubeMeshBuilder.Build(new Vector3(64f, 64f, 64f)),
				InstanceCount = grid.StaticObstacles.Count
			};

			int index = 0;
			foreach (var cell in grid.StaticObstacles)
			{
				multiMesh.SetInstanceTransform(
					index,
					new Transform3D(
						Basis.Identity,
						new Vector3(cell.X * 64 + 32, 32f, cell.Y * 64 + 32)
					)
				);
				index++;
			}

			var walls = new MultiMeshInstance3D
			{
				Name = "Walls",
				Multimesh = multiMesh
			};

			// 墙面材质：恢复成项目原本的实现（立方体 UV + Wall.png）
			walls.MaterialOverride = new StandardMaterial3D
			{
				AlbedoTexture = RTS.Core.TextureLoader3D.LoadPng("res://ArtRes/imgs3d/Wall.png"),
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				Uv1Scale = Vector3.One
			};

			AddChild(walls);
		}

		/// <summary>动态添加墙体后重建 3D 视觉（压测等场景使用）。</summary>
		public void RebuildWalls()
		{
			BuildWalls();
		}

	}
}
