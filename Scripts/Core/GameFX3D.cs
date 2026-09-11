using Godot;
using System.Collections.Generic;
using RTS.Data;
using RTS.Units;
using RTS.Core.Events;

// 3D 指令特效：选中单位路径线、框选矩形、点击标记
public partial class GameFX3D : Node3D
{
	private List<IEntity> _selectedEntities = new();
	private int _localPlayerID = 0;
	private MeshInstance3D _boxQuad;
	private MeshInstance3D _pathMesh;
	private ImmediateMesh _lineMesh;
	private ulong _lastPathDrawMs;
	private const ulong PathDrawIntervalMs = 100;

	public override void _Ready()
	{
		GameEventBus.SelectionChanged += OnBusSelectionChanged;
	}

	public override void _ExitTree()
	{
		GameEventBus.SelectionChanged -= OnBusSelectionChanged;
		base._ExitTree();
	}

	private void OnBusSelectionChanged(System.Collections.Generic.List<IEntity> selection, int team)
	{
		SetupPathDrawing(selection, team);
	}

	public void SetupPathDrawing(List<IEntity> selected, int localID)
	{
		_selectedEntities = selected;
		_localPlayerID = localID;
	}

	public void UpdateBox(Vector2 start, Vector2 end, bool active)
	{
		if (_boxQuad == null)
		{
			_boxQuad = new MeshInstance3D { Name = "SelectionBox", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			// QuadMesh 默认竖直（XY 平面），旋转放平到地面
			_boxQuad.RotationDegrees = new Vector3(-90f, 0f, 0f);
			// 框选画成**空心边框**而不是实心块：
			// 实心半透明绿块会把框内的单位颜色压掉，玩家反而看不清自己框到了谁。
			// 走线条基元（每段一个细长方形），四段组成边框。
			_boxQuad.Mesh = new ImmediateMesh();
			_boxMat = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.35f, 1f, 0.45f, 0.9f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				RenderPriority = 6,
			};
			_boxQuad.MaterialOverride = _boxMat;
			AddChild(_boxQuad);
		}

		_boxQuad.Visible = active;

		if (!active)
			return;

		Vector2 size = (end - start).Abs();
		Vector2 center = (start + end) * 0.5f;
		_boxQuad.Position = new Vector3(center.X, 1.5f, center.Y);

		// 边框宽度按相机高度自适应，缩远也能看清
		float camHeight = 2500f;
		var cam = GetViewport()?.GetCamera3D();
		if (cam != null)
			camHeight = Mathf.Max(1f, cam.GlobalPosition.Y);
		float w = Mathf.Clamp(camHeight * 0.0025f, 1.0f, 8f);

		DrawBoxFrame(Mathf.Max(size.X, 1f), Mathf.Max(size.Y, 1f), w);
	}

	private StandardMaterial3D _boxMat;

	/// <summary>用四条细长方形拼出空心边框（局部坐标系，父节点已放平）。</summary>
	private void DrawBoxFrame(float w, float h, float lineW)
	{
		var mesh = (ImmediateMesh)_boxQuad.Mesh;
		mesh.ClearSurfaces();
		mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);

		float hx = w * 0.5f;
		float hy = h * 0.5f;
		float t = lineW * 0.5f;

		// 局部坐标：QuadMesh 被 -90° 旋转放平，所以这里用 (x, y) 当作地面上的 (x, z)
		AddQuadMeshRect(-hx, -hy - t, hx, -hy + t); // 上边
		AddQuadMeshRect(-hx, hy - t, hx, hy + t);   // 下边
		AddQuadMeshRect(-hx - t, -hy, -hx + t, hy); // 左边
		AddQuadMeshRect(hx - t, -hy, hx + t, hy);   // 右边

		mesh.SurfaceEnd();
	}

	private void AddQuadMeshRect(float x0, float y0, float x1, float y1)
	{
		var mesh = (ImmediateMesh)_boxQuad.Mesh;
		mesh.SurfaceAddVertex(new Vector3(x0, y0, 0f));
		mesh.SurfaceAddVertex(new Vector3(x1, y0, 0f));
		mesh.SurfaceAddVertex(new Vector3(x1, y1, 0f));

		mesh.SurfaceAddVertex(new Vector3(x0, y0, 0f));
		mesh.SurfaceAddVertex(new Vector3(x1, y1, 0f));
		mesh.SurfaceAddVertex(new Vector3(x0, y1, 0f));
	}

	public void SpawnClickMarker(Vector2 pos, Color color)
	{
		var marker = new MeshInstance3D
		{
			Name = "ClickMarker",
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Mesh = new CylinderMesh
			{
				TopRadius = 18f,
				BottomRadius = 18f,
				Height = 1f,
				RadialSegments = 24
			},
			Position = new Vector3(pos.X, 1f, pos.Y)
		};
		var mat = new StandardMaterial3D
		{
			AlbedoColor = new Color(color.R, color.G, color.B, 0.7f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled
		};
		marker.MaterialOverride = mat;
		AddChild(marker);

		Tween tween = CreateTween();
		tween.TweenProperty(marker, "scale", Vector3.One * 1.4f, 0.25f);
		tween.TweenProperty(mat, "albedo_color:a", 0f, 0.4f);
		tween.TweenCallback(Callable.From(() =>
		{
			if (GodotObject.IsInstanceValid(marker))
				marker.QueueFree();
		}));
	}

	public override void _Process(double delta)
	{
		// 路径线按 10fps 重建：选中 100 个单位时每帧重建上千顶点会拖慢帧率
		ulong now = Time.GetTicksMsec();
		if (now - _lastPathDrawMs < PathDrawIntervalMs)
			return;
		_lastPathDrawMs = now;

		DrawPaths();
	}

	private void DrawPaths()
	{
		if (_pathMesh == null)
		{
			// 指令路径用**带状网格（ribbon）**而不是 PrimitiveType.Lines。
			//
			// 原来的 Lines 有两个问题：
			//   1. 线宽固定 1 像素，且在部分驱动/平台（尤其 Metal）支持很差；
			//   2. 反走样不生效，斜线会有明显锯齿。
			// 改成在 XZ 平面上把折线展开成四边形条带，宽度可控、各平台一致。
			_pathMesh = new MeshInstance3D { Name = "PathLines", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			_pathMaterial = new StandardMaterial3D
			{
				AlbedoColor = new Color(1f, 1f, 0.2f, 0.85f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				// 贴地薄片：给高渲染优先级，避免和地面 z-fighting
				RenderPriority = 5,
			};
			_pathMesh.MaterialOverride = _pathMaterial;
			_lineMesh = new ImmediateMesh();
			_pathMesh.Mesh = _lineMesh;
			AddChild(_pathMesh);
		}

		// 条带宽度按相机高度自适应：缩远变细、缩近变粗。
		// 固定世界宽度会导致"拉远后线比单位还粗"或"贴近后细到看不见"。
		float camHeight = 2500f;
		var cam = GetViewport()?.GetCamera3D();
		if (cam != null)
			camHeight = Mathf.Max(1f, cam.GlobalPosition.Y);
		float halfWidth = Mathf.Clamp(camHeight * 0.004f, 1.2f, 12f);

		// 先收集折线（不是零散线段：每条链要作为整体展开条带，
		// 否则相邻线段之间会出现缺口/重叠）
		var chains = new List<List<Vector3>>();
		var markers = new List<Vector3>();

		foreach (var entity in _selectedEntities)
		{
			if (entity == null || entity.IsDeadOrNull() || entity.TeamID != _localPlayerID)
				continue;

			if (entity is Unit unit)
			{
				var points = unit.GetWaypointPositions();
				if (points.Count == 0)
					continue;

				var chain = new List<Vector3>
				{
					new(((IEntity)unit).GlobalPosition.X, PathY, ((IEntity)unit).GlobalPosition.Y)
				};
				foreach (var point in points)
					chain.Add(new Vector3(point.X, PathY, point.Y));

				chains.Add(chain);
				markers.Add(chain[chain.Count - 1]);
			}
			else if (entity is Structure structure)
			{
				// 集结点与单位指令共享同一套路径视觉：从建筑画一条线到集结目标
				if (structure.RallyQueue.Count == 0)
					continue;

				var rally = structure.RallyQueue[0];
				var chain = new List<Vector3>
				{
					new(((IEntity)structure).GlobalPosition.X, PathY, ((IEntity)structure).GlobalPosition.Y),
					new((float)rally.TargetPos.X, PathY, (float)rally.TargetPos.Y),
				};
				chains.Add(chain);
				markers.Add(chain[1]);
			}
		}

		if (chains.Count == 0)
		{
			_lineMesh.ClearSurfaces();
			return;
		}

		_lineMesh.ClearSurfaces();

		// ---- 折线 → 条带 ----
		_lineMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
		foreach (var chain in chains)
			for (int i = 0; i + 1 < chain.Count; i++)
				EmitSegment(chain[i], chain[i + 1], halfWidth);

		// ---- 终点标记：细菱形，指示"最后要去哪" ----
		// 只有线段没有终点标记时，密集编队下根本看不出指令落在哪一格。
		float markerSize = halfWidth * 3.2f;
		foreach (var m in markers)
			EmitDiamond(m, markerSize);

		_lineMesh.SurfaceEnd();
	}

	private StandardMaterial3D _pathMaterial;
	private const float PathY = 2f;

	/// <summary>把一段线段展开成一定宽度的四边形（XZ 平面）。</summary>
	private void EmitSegment(Vector3 a, Vector3 b, float halfWidth)
	{
		Vector3 dir = b - a;
		dir.Y = 0f;
		if (dir.LengthSquared() < 0.0001f)
			return;
		dir = dir.Normalized();

		// XZ 平面内的垂线
		Vector3 perp = new Vector3(-dir.Z, 0f, dir.X) * halfWidth;

		Vector3 p0 = a - perp;
		Vector3 p1 = a + perp;
		Vector3 p2 = b + perp;
		Vector3 p3 = b - perp;

		_lineMesh.SurfaceAddVertex(p0);
		_lineMesh.SurfaceAddVertex(p1);
		_lineMesh.SurfaceAddVertex(p2);

		_lineMesh.SurfaceAddVertex(p0);
		_lineMesh.SurfaceAddVertex(p2);
		_lineMesh.SurfaceAddVertex(p3);
	}

	/// <summary>在落点画一个细菱形（比圆盘更"轻"，不遮挡地面信息）。</summary>
	private void EmitDiamond(Vector3 center, float size)
	{
		Vector3 top = center + new Vector3(0f, 0f, -size);
		Vector3 bottom = center + new Vector3(0f, 0f, size);
		Vector3 left = center + new Vector3(-size, 0f, 0f);
		Vector3 right = center + new Vector3(size, 0f, 0f);

		_lineMesh.SurfaceAddVertex(top);
		_lineMesh.SurfaceAddVertex(right);
		_lineMesh.SurfaceAddVertex(bottom);

		_lineMesh.SurfaceAddVertex(top);
		_lineMesh.SurfaceAddVertex(bottom);
		_lineMesh.SurfaceAddVertex(left);
	}
}
