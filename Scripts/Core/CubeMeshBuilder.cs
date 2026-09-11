using Godot;

namespace RTS.Core
{
	// 六面体网格生成器：每个面显式 UV(0,0)~(1,1)，
	// 保证任意尺寸的方块每个面都显示完整贴图（配合 CullMode.Disabled 双面可见）
	public static class CubeMeshBuilder
	{
		public static ArrayMesh Build(Vector3 size)
		{
			Vector3 h = size * 0.5f;
			var st = new SurfaceTool();
			st.Begin(Mesh.PrimitiveType.Triangles);

			void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
			{
				st.SetUV(new Vector2(0, 0));
				st.AddVertex(a);
				st.SetUV(new Vector2(1, 0));
				st.AddVertex(b);
				st.SetUV(new Vector2(1, 1));
				st.AddVertex(c);
				st.SetUV(new Vector2(0, 0));
				st.AddVertex(a);
				st.SetUV(new Vector2(1, 1));
				st.AddVertex(c);
				st.SetUV(new Vector2(0, 1));
				st.AddVertex(d);
			}

			// +X
			Quad(
				new Vector3(h.X, -h.Y, -h.Z),
				new Vector3(h.X, -h.Y, h.Z),
				new Vector3(h.X, h.Y, h.Z),
				new Vector3(h.X, h.Y, -h.Z)
			);
			// -X
			Quad(
				new Vector3(-h.X, -h.Y, h.Z),
				new Vector3(-h.X, -h.Y, -h.Z),
				new Vector3(-h.X, h.Y, -h.Z),
				new Vector3(-h.X, h.Y, h.Z)
			);
			// +Y（顶）
			Quad(
				new Vector3(-h.X, h.Y, -h.Z),
				new Vector3(h.X, h.Y, -h.Z),
				new Vector3(h.X, h.Y, h.Z),
				new Vector3(-h.X, h.Y, h.Z)
			);
			// -Y（底）
			Quad(
				new Vector3(-h.X, -h.Y, h.Z),
				new Vector3(h.X, -h.Y, h.Z),
				new Vector3(h.X, -h.Y, -h.Z),
				new Vector3(-h.X, -h.Y, -h.Z)
			);
			// +Z
			Quad(
				new Vector3(-h.X, -h.Y, h.Z),
				new Vector3(h.X, -h.Y, h.Z),
				new Vector3(h.X, h.Y, h.Z),
				new Vector3(-h.X, h.Y, h.Z)
			);
			// -Z
			Quad(
				new Vector3(h.X, -h.Y, -h.Z),
				new Vector3(-h.X, -h.Y, -h.Z),
				new Vector3(-h.X, h.Y, -h.Z),
				new Vector3(h.X, h.Y, -h.Z)
			);

			st.GenerateNormals();
			return st.Commit();
		}
	}
}
