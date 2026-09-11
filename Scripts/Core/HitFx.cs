// File: res://Scripts/Core/HitFx.cs
using Godot;
using System.Collections.Generic;

namespace RTS.Core
{
	// 命中特效：亮色光球 + 地面扩散光圈
	// 半径 / 时长 / 颜色全部由武器配置表传入
	public static class HitFx
	{
		// P1-3：特效对象池，避免每发子弹都创建/销毁节点（节点创建才是 GC 尖峰大头）
		private const int MaxTracerPool = 256;
		private const int MaxFlashPool = 128;
		private const int MaxHitPool = 256;
		private const int MaxConePool = 64;
		private static readonly Stack<MeshInstance3D> TracerPool = new();
		private static readonly Stack<MeshInstance3D> FlashPool = new();
		private static readonly Stack<Node3D> HitPool = new();
		private static readonly Stack<MeshInstance3D> ConePool = new();

		// 即时命中武器的弹道光束：从枪口到目标拉伸的发光柱体
		public static void SpawnTracer(Node parent, Vector3 start, Vector3 end, float thickness, float duration, Color color)
		{
			if (parent == null)
				return;

			// 抬高一点，避免光束一半埋进地面
			start += new Vector3(0f, 8f, 0f);
			end += new Vector3(0f, 8f, 0f);

			Vector3 dir = end - start;
			float dist = dir.Length();

			if (dist < 2f)
				return;

			Vector3 mid = (start + end) * 0.5f;
			Vector3 dirN = dir / dist;
			float life = Mathf.Max(0.05f, duration);
			float t = Mathf.Max(0.5f, thickness);

			MeshInstance3D beam = TracerPool.Count > 0 ? TracerPool.Pop() : new MeshInstance3D();
			if (!GodotObject.IsInstanceValid(beam))
				beam = new MeshInstance3D();
			// 池中节点回收时已摘除父节点；这里再防御一次，避免复用已挂载节点导致 AddChild 报错
			if (beam.GetParent() != null)
				beam.GetParent().RemoveChild(beam);
			beam.Name = "Tracer";
			beam.TopLevel = true;
			beam.Visible = true;
			// 用拉长的球体做光束：把局部 Y 轴对准 枪口→目标 方向
			if (beam.Mesh == null)
			{
				beam.Mesh = new SphereMesh
				{
					Radius = 0.5f,
					Height = 1f,
					RadialSegments = 8,
					Rings = 6
				};
			}
			beam.Position = mid;
			Vector3 upRef = Mathf.Abs(dirN.Y) > 0.99f ? Vector3.Right : Vector3.Up;
			Vector3 xAxis = upRef.Cross(dirN).Normalized();
			Vector3 zAxis = xAxis.Cross(dirN);
			beam.Basis = new Basis(xAxis, dirN, zAxis);
			beam.Scale = new Vector3(t, dist, t);

			var mat = beam.MaterialOverride as StandardMaterial3D;
			if (mat == null)
			{
				// Unshaded 下 Emission 被引擎忽略，亮度只能写在 AlbedoColor 上。
				mat = new StandardMaterial3D
				{
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
					CullMode = BaseMaterial3D.CullModeEnum.Disabled
				};
				beam.MaterialOverride = mat;
			}
			mat.AlbedoColor = new Color(RTS.Units.GlowMaterialFactory.Boost(color, 6f), 1f);
			beam.MaterialOverride = mat;
			parent.AddChild(beam);

			Tween tween = beam.CreateTween();
			tween.TweenProperty(mat, "albedo_color:a", 0f, life);
			tween.TweenCallback(Callable.From(() => ReturnTracer(beam)));
		}

		// 枪口闪光：开火瞬间的亮球
		public static void SpawnMuzzleFlash(Node parent, Vector3 pos, float radius, float duration, Color color)
		{
			if (parent == null)
				return;

			float r = Mathf.Max(4f, radius);
			float life = Mathf.Max(0.05f, duration);

			MeshInstance3D flash = FlashPool.Count > 0 ? FlashPool.Pop() : new MeshInstance3D();
			if (!GodotObject.IsInstanceValid(flash))
				flash = new MeshInstance3D();
			// 池中节点回收时已摘除父节点；这里再防御一次
			if (flash.GetParent() != null)
				flash.GetParent().RemoveChild(flash);
			flash.Name = "MuzzleFlash";
			flash.TopLevel = true;
			flash.Visible = true;
			if (flash.Mesh == null)
			{
				flash.Mesh = new SphereMesh
				{
					Radius = r,
					Height = r * 2f,
					RadialSegments = 12,
					Rings = 6
				};
			}
			else if (flash.Mesh is SphereMesh sphere)
			{
				sphere.Radius = r;
				sphere.Height = r * 2f;
			}
			flash.Position = pos;
			flash.Scale = Vector3.One * 0.5f;

			var mat = flash.MaterialOverride as StandardMaterial3D;
			if (mat == null)
			{
				// Unshaded 下 Emission 被引擎忽略，亮度只能写在 AlbedoColor 上。
				mat = new StandardMaterial3D
				{
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
					CullMode = BaseMaterial3D.CullModeEnum.Disabled
				};
				flash.MaterialOverride = mat;
			}
			mat.AlbedoColor = new Color(RTS.Units.GlowMaterialFactory.Boost(color, 6f), 1f);
			flash.MaterialOverride = mat;
			parent.AddChild(flash);

			Tween tween = flash.CreateTween();
			tween.TweenProperty(flash, "scale", Vector3.One * 1.4f, life * 0.6f)
				.SetTrans(Tween.TransitionType.Quad)
				.SetEase(Tween.EaseType.Out);
			tween.Parallel().TweenProperty(mat, "albedo_color:a", 0f, life);
			tween.TweenCallback(Callable.From(() => ReturnFlash(flash)));
		}

		private static void ReturnTracer(MeshInstance3D beam)
		{
			if (!GodotObject.IsInstanceValid(beam))
				return;
			beam.Visible = false;
			// 必须先摘除父节点再入池，否则下次复用 AddChild 会因“已有父节点”失败
			if (beam.GetParent() != null)
				beam.GetParent().RemoveChild(beam);
			if (TracerPool.Count < MaxTracerPool)
			{
				TracerPool.Push(beam);
				return;
			}
			beam.QueueFree();
		}

		private static void ReturnFlash(MeshInstance3D flash)
		{
			if (!GodotObject.IsInstanceValid(flash))
				return;
			flash.Visible = false;
			// 必须先摘除父节点再入池，否则下次复用 AddChild 会因“已有父节点”失败
			if (flash.GetParent() != null)
				flash.GetParent().RemoveChild(flash);
			if (FlashPool.Count < MaxFlashPool)
			{
				FlashPool.Push(flash);
				return;
			}
			flash.QueueFree();
		}

		public static void Spawn(Node parent, Vector2 pos, float radius, float duration, Color color, float aoeRadius = 0f)
		{
			Spawn(parent, new Vector3(pos.X, 0f, pos.Y), radius, duration, color, aoeRadius);
		}

		// 命中特效（指定世界坐标：用于打在敌人表面）
		public static void Spawn(Node parent, Vector3 pos, float radius, float duration, Color color, float aoeRadius = 0f)
		{
			if (parent == null)
				return;

			float r = Mathf.Max(6f, radius);
			float life = Mathf.Max(0.1f, duration);
			// 非溅射：小光球 + 小圈；溅射：小球 + 与范围匹配的大圈
			float ballRadius = aoeRadius > 0f
				? Mathf.Min(r * 0.3f, 16f)
				: Mathf.Min(r * 0.22f, 12f);
			float ringRadius = aoeRadius > 0f ? aoeRadius : Mathf.Min(r * 0.6f, 20f);

			Node3D fx = HitPool.Count > 0 ? HitPool.Pop() : new Node3D();
			if (!GodotObject.IsInstanceValid(fx))
				fx = new Node3D();
			if (fx.GetParent() != null)
				fx.GetParent().RemoveChild(fx);
			fx.Name = "HitFx";
			fx.TopLevel = true;
			fx.Visible = true;
			fx.Position = new Vector3(pos.X, pos.Y <= 1f ? Mathf.Max(10f, r * 0.35f) : pos.Y + 6f, pos.Z);
			fx.Scale = Vector3.One * 0.2f;

			var ball = fx.GetNodeOrNull<MeshInstance3D>("Ball");
			if (ball == null)
			{
				ball = new MeshInstance3D
				{
					Name = "Ball",
					Mesh = new SphereMesh
					{
						Radius = ballRadius,
						Height = ballRadius * 2f,
						RadialSegments = 16,
						Rings = 10
					}
				};
			}
			else if (ball.Mesh is SphereMesh ballSphere)
			{
				ballSphere.Radius = ballRadius;
				ballSphere.Height = ballRadius * 2f;
			}
			var mat = ball.MaterialOverride as StandardMaterial3D;
			if (mat == null)
			{
				// Unshaded 下 Emission 被引擎忽略，亮度只能写在 AlbedoColor 上。
				mat = new StandardMaterial3D
				{
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
					CullMode = BaseMaterial3D.CullModeEnum.Disabled
				};
			}
			mat.AlbedoColor = RTS.Units.GlowMaterialFactory.Boost(color, 5f);
			ball.MaterialOverride = mat;
			if (ball.GetParent() != fx)
				fx.AddChild(ball);

			// 只有溅射才显示范围圈；非溅射只保留小光球，不放大圈
			MeshInstance3D ring = fx.GetNodeOrNull<MeshInstance3D>("Ring");
			if (ring != null)
			{
				if (aoeRadius <= 0f)
				{
					ring.Visible = false;
				}
				else
				{
					ring.Visible = true;
					if (ring.Mesh is CylinderMesh ringCyl)
					{
						ringCyl.TopRadius = ringRadius;
						ringCyl.BottomRadius = ringRadius;
					}
				}
			}
			StandardMaterial3D ringMat = ring?.MaterialOverride as StandardMaterial3D;
			if (aoeRadius > 0f)
			{
				if (ring == null)
				{
					ring = new MeshInstance3D
					{
						Name = "Ring",
						Mesh = new CylinderMesh
						{
							TopRadius = ringRadius,
							BottomRadius = ringRadius,
							Height = 3f,
							RadialSegments = 32
						}
					};
				}
				if (ringMat == null)
				{
					// Unshaded 下 Emission 被引擎忽略，亮度只能写在 AlbedoColor 上。
					ringMat = new StandardMaterial3D
					{
						ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
						Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
						CullMode = BaseMaterial3D.CullModeEnum.Disabled
					};
				}
				ringMat.AlbedoColor = new Color(RTS.Units.GlowMaterialFactory.Boost(color, 3f), 0.9f);
				ring.MaterialOverride = ringMat;
				ring.Position = new Vector3(0f, -fx.Position.Y + 2f, 0f);
				if (ring.GetParent() != fx)
					fx.AddChild(ring);
			}

			parent.AddChild(fx);

			Tween tween = fx.CreateTween();
			tween.TweenProperty(fx, "scale", Vector3.One * 1.3f, life * 0.55f)
				.SetTrans(Tween.TransitionType.Quad)
				.SetEase(Tween.EaseType.Out);
			tween.Parallel().TweenProperty(mat, "albedo_color:a", 0f, life);
			if (ringMat != null)
				tween.Parallel().TweenProperty(ringMat, "albedo_color:a", 0f, life);
			tween.TweenCallback(Callable.From(() => ReturnHit(fx)));
		}

		// 命中特效回收（带守卫：节点可能已被外部释放）
		private static void ReturnHit(Node3D fx)
		{
			if (!GodotObject.IsInstanceValid(fx))
				return;
			if (fx.GetParent() != null)
				fx.GetParent().RemoveChild(fx);
			fx.Visible = false;
			if (HitPool.Count < MaxHitPool)
			{
				HitPool.Push(fx);
				return;
			}
			fx.QueueFree();
		}

		// 扇形喷吐（喷火龙龙息）：扁平扇形闪光
		public static void SpawnConeSweep(Node parent, Vector3 origin, Vector3 target, float range, float angleDeg, float duration, Color color, bool sweepArc = false)
		{
			if (parent == null || range <= 0f || angleDeg <= 0f)
				return;

			Vector3 dir = target - origin;
			dir.Y = 0f;
			float facing = Mathf.Atan2(dir.X, dir.Z);

			int segments = 20;
			var verts = new System.Collections.Generic.List<Vector3>(segments + 2);
			var uvs = new System.Collections.Generic.List<Vector2>(segments + 2);
			var indices = new System.Collections.Generic.List<int>(segments * 3);

			verts.Add(Vector3.Zero);
			uvs.Add(new Vector2(0.5f, 0.5f));

			float half = Mathf.DegToRad(angleDeg) * 0.5f;

			for (int i = 0; i <= segments; i++)
			{
				float a = -half + (2f * half) * i / segments;
				float x = Mathf.Sin(a) * range;
				float z = Mathf.Cos(a) * range;
				verts.Add(new Vector3(x, 0f, z));
				uvs.Add(new Vector2((x / range + 1f) * 0.5f, (z / range + 1f) * 0.5f));
			}

			for (int i = 0; i < segments; i++)
			{
				indices.Add(0);
				indices.Add(i + 1);
				indices.Add(i + 2);
			}

			var arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
			arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
			arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

			MeshInstance3D cone = ConePool.Count > 0 ? ConePool.Pop() : new MeshInstance3D();
			if (!GodotObject.IsInstanceValid(cone))
				cone = new MeshInstance3D();
			if (cone.GetParent() != null)
				cone.GetParent().RemoveChild(cone);
			cone.Name = "ConeSweep";
			cone.TopLevel = true;
			cone.Visible = true;
			cone.Position = new Vector3(origin.X, 9f, origin.Z);
			cone.Rotation = new Vector3(0f, facing, 0f);
			if (!(cone.Mesh is ArrayMesh coneMesh))
			{
				coneMesh = new ArrayMesh();
				cone.Mesh = coneMesh;
			}
			coneMesh.ClearSurfaces();
			coneMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

			var mat = cone.MaterialOverride as StandardMaterial3D;
			if (mat == null)
			{
				// Unshaded 下 Emission 被引擎忽略，亮度只能写在 AlbedoColor 上。
				mat = new StandardMaterial3D
				{
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
					CullMode = BaseMaterial3D.CullModeEnum.Disabled
				};
			}
			// 使用调用方传入的颜色（近战刀光/龙息各自配色），不再硬编码橙色
			Color c = color.A > 0f ? color : new Color(1f, 0.55f, 0.15f, 0.55f);
			mat.AlbedoColor = new Color(RTS.Units.GlowMaterialFactory.Boost(c, 4f), c.A > 0f ? c.A : 0.55f);
			cone.MaterialOverride = mat;
			parent.AddChild(cone);

			Tween tween = cone.CreateTween();

			if (sweepArc)
			{
				// 挥砍：扇形从一侧扫到另一侧，像刀光划过去
				float sweepHalf = Mathf.DegToRad(angleDeg) * 0.5f;
				cone.Rotation = new Vector3(0f, facing + sweepHalf, 0f);
				tween.TweenProperty(cone, "rotation:y", facing - sweepHalf, Mathf.Max(0.1f, duration))
					.SetTrans(Tween.TransitionType.Quad)
					.SetEase(Tween.EaseType.InOut);
				tween.Parallel().TweenProperty(mat, "albedo_color:a", 0f, Mathf.Max(0.1f, duration));
			}
			else
			{
				tween.TweenProperty(mat, "albedo_color:a", 0f, Mathf.Max(0.1f, duration));
			}

			tween.TweenCallback(Callable.From(() => ReturnCone(cone)));
		}

		private static void ReturnCone(MeshInstance3D cone)
		{
			if (!GodotObject.IsInstanceValid(cone))
				return;
			if (cone.GetParent() != null)
				cone.GetParent().RemoveChild(cone);
			cone.Visible = false;
			if (ConePool.Count < MaxConePool)
			{
				ConePool.Push(cone);
				return;
			}
			cone.QueueFree();
		}
	}
}
