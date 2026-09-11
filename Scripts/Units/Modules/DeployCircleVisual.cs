using Godot;
using System.Collections.Generic;

namespace RTS.Units
{
	// 架设射击圈：地面虚线圆环（挂在场景根节点，全员可见、不受迷雾影响）
	public partial class DeployCircleVisual : Node3D
	{
		private MeshInstance3D _ring;
		private static Mesh _cachedMesh;
		private static float _cachedRadius;

		public void UpdateCircle(Vector3 center, float radius, Color color)
		{
			GlobalPosition = new Vector3(center.X, 2f, center.Z);

			if (_ring == null)
			{
				_ring = new MeshInstance3D
				{
					MaterialOverride = new StandardMaterial3D
					{
						AlbedoColor = new Color(1f, 0.6f, 0.25f, 0.9f),
						ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
						Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
						CullMode = BaseMaterial3D.CullModeEnum.Disabled
					}
				};
				AddChild(_ring);
			}
			if (_ring.MaterialOverride is StandardMaterial3D mat)
				mat.AlbedoColor = color;

			if (_cachedMesh == null || Mathf.Abs(_cachedRadius - radius) > 0.5f)
			{
				_cachedMesh = BuildDashedRing(radius);
				_cachedRadius = radius;
			}
			_ring.Mesh = _cachedMesh;
		}

		// 虚线圆：沿圆周排布的短弧段（顶面 + 底面 + 内外侧面，俯视清晰可见）
		private static Mesh BuildDashedRing(float radius)
		{
			const int DashCount = 36;
			float dashSpan = Mathf.DegToRad(7f);
			float thickness = 6f;
			float topY = 4f;
			float bottomY = 0.6f;

			var verts = new List<Vector3>();
			var indices = new List<int>();

			for (int i = 0; i < DashCount; i++)
			{
				float a0 = Mathf.Tau * i / DashCount;
				float a1 = a0 + dashSpan;
				float c0 = Mathf.Cos(a0);
				float s0 = Mathf.Sin(a0);
				float c1 = Mathf.Cos(a1);
				float s1 = Mathf.Sin(a1);

				float rIn = radius - thickness;
				float rOut = radius + thickness;

				int b = verts.Count;
				verts.Add(new Vector3(c0 * rIn, topY, s0 * rIn));
				verts.Add(new Vector3(c1 * rIn, topY, s1 * rIn));
				verts.Add(new Vector3(c1 * rOut, topY, s1 * rOut));
				verts.Add(new Vector3(c0 * rOut, topY, s0 * rOut));
				verts.Add(new Vector3(c0 * rIn, bottomY, s0 * rIn));
				verts.Add(new Vector3(c1 * rIn, bottomY, s1 * rIn));
				verts.Add(new Vector3(c1 * rOut, bottomY, s1 * rOut));
				verts.Add(new Vector3(c0 * rOut, bottomY, s0 * rOut));

				indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
				indices.Add(b); indices.Add(b + 2); indices.Add(b + 3);
				indices.Add(b + 5); indices.Add(b + 4); indices.Add(b + 7);
				indices.Add(b + 5); indices.Add(b + 7); indices.Add(b + 6);
				indices.Add(b + 3); indices.Add(b + 2); indices.Add(b + 6);
				indices.Add(b + 3); indices.Add(b + 6); indices.Add(b + 7);
				indices.Add(b + 1); indices.Add(b); indices.Add(b + 4);
				indices.Add(b + 1); indices.Add(b + 4); indices.Add(b + 5);
			}

			var arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
			arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

			var mesh = new ArrayMesh();
			mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
			return mesh;
		}
	}
}
