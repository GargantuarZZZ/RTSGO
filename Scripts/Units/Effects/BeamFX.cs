using Godot;

namespace RTS.Units
{
	// 光束类武器的专用直线光束特效（纯表现层）：从枪口到目标拉一根粗亮光束。
	// 点名特效（如“光束线”的曲线光束）不走这里。
	public static class BeamFX
	{
		public static void Spawn(Node root, Vector3 from, Vector3 to, Color color, float duration = 0.18f, float thickness = 4f)
		{
			if (root == null)
				return;

			Vector3 dir = to - from;
			float dist = dir.Length();
			if (dist < 2f)
				return;

			var holder = new Node3D { TopLevel = true };
			root.AddChild(holder);

			var mat = GlowMaterialFactory.CreateGlow(color, 5f);

			Vector3 dirN = dir / dist;
			Vector3 upRef = Mathf.Abs(dirN.Y) > 0.99f ? Vector3.Right : Vector3.Up;
			Vector3 xAxis = upRef.Cross(dirN).Normalized();
			Vector3 zAxis = xAxis.Cross(dirN);

			var beam = new MeshInstance3D
			{
				Name = "Beam",
				Mesh = new BoxMesh { Size = new Vector3(thickness, dist, thickness) },
				MaterialOverride = mat,
				Position = (from + to) * 0.5f,
				Basis = new Basis(xAxis, dirN, zAxis)
			};
			holder.AddChild(beam);

			var tween = holder.CreateTween();
			tween.TweenProperty(mat, "albedo_color:a", 0f, Mathf.Max(0.05f, duration));
			tween.TweenCallback(Callable.From(() =>
			{
				if (GodotObject.IsInstanceValid(holder))
					holder.QueueFree();
			}));
		}
	}
}
