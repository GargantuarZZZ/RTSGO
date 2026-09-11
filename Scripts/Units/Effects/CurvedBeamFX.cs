using Godot;

namespace RTS.Units
{
	// 曲线光束特效（纯表现层）：每条线独立随机曲率/倾斜/起终点散布
	public static class CurvedBeamFX
	{
		private static readonly RandomNumberGenerator _rng = new();

		static CurvedBeamFX()
		{
			_rng.Randomize();
		}

		public static void Spawn(Node root, Vector3 from, Vector3 to, Color color, float duration = 0.45f)
		{
			var holder = new Node3D { TopLevel = true };
			root.AddChild(holder);

			// 起点/终点不随机偏移：起点来自各武器固定枪口，终点为目标表面
			Vector3 mid = (from + to) * 0.5f + new Vector3(
				(_rng.Randf() - 0.5f) * 180f,
				80f + _rng.Randf() * 260f,
				(_rng.Randf() - 0.5f) * 120f);

			var mat = GlowMaterialFactory.CreateGlow(color, 4f);

			const int Segments = 10;

			for (int i = 0; i < Segments; i++)
			{
				float t0 = i / (float)Segments;
				float t1 = (i + 1) / (float)Segments;
				Vector3 p0 = Quad(from, mid, to, t0);
				Vector3 p1 = Quad(from, mid, to, t1);

				var seg = new MeshInstance3D
				{
					Mesh = new BoxMesh { Size = new Vector3(2f, (p1 - p0).Length(), 2f) },
					MaterialOverride = mat
				};
				seg.Position = (p0 + p1) * 0.5f;
				Vector3 dir = (p1 - p0).Normalized();
				Vector3 upRef = Mathf.Abs(dir.Y) > 0.99f ? Vector3.Right : Vector3.Up;
				Vector3 xAxis = upRef.Cross(dir).Normalized();
				Vector3 zAxis = xAxis.Cross(dir);
				seg.Basis = new Basis(xAxis, dir, zAxis);
				holder.AddChild(seg);
			}

			var tween = holder.CreateTween();
			tween.TweenProperty(mat, "albedo_color:a", 0f, duration);
			tween.TweenCallback(Callable.From(() =>
			{
				if (GodotObject.IsInstanceValid(holder))
					holder.QueueFree();
			}));
		}

		private static Vector3 Quad(Vector3 a, Vector3 b, Vector3 c, float t)
		{
			float inv = 1f - t;
			return inv * inv * a + 2f * inv * t * b + t * t * c;
		}
	}
}
