using Godot;
using System.Collections.Generic;

namespace RTS.Core
{
	// 技能范围指示圈：施法射程圈（以施法者为圆心）+ 落点效果圈（跟随鼠标）。
	// 由 UserController / NanoPanelUI 在技能待确认阶段驱动，纯本地视觉。
	public partial class SkillRangeIndicator : Node3D
	{
		private static SkillRangeIndicator _instance;
		private MeshInstance3D _castRing;
		private MeshInstance3D _effectRing;
		private StandardMaterial3D _castMaterial;
		private StandardMaterial3D _effectMaterial;

		public static SkillRangeIndicator GetOrCreate(SceneTree tree)
		{
			if (_instance != null && GodotObject.IsInstanceValid(_instance))
				return _instance;

			_instance = new SkillRangeIndicator { Name = "SkillRangeIndicator", TopLevel = true };
			tree.Root.AddChild(_instance);
			return _instance;
		}

		public override void _Ready()
		{
			_castMaterial = CreateRingMaterial(new Color(0.35f, 0.85f, 1f, 0.55f));
			_effectMaterial = CreateRingMaterial(new Color(1f, 0.55f, 0.2f, 0.55f));

			_castRing = new MeshInstance3D { Name = "CastRangeRing", MaterialOverride = _castMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			_effectRing = new MeshInstance3D { Name = "EffectRangeRing", MaterialOverride = _effectMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			AddChild(_castRing);
			AddChild(_effectRing);
			HideRanges();
		}

		public void ShowCastRange(Vector3 center, float radius, Color color)
		{
			ShowRing(_castRing, _castMaterial, center, radius, color);
		}

		public void ShowEffectRange(Vector3 center, float radius, Color color)
		{
			ShowRing(_effectRing, _effectMaterial, center, radius, color);
		}

		public void HideRanges()
		{
			if (_castRing != null)
				_castRing.Visible = false;
			if (_effectRing != null)
				_effectRing.Visible = false;
		}

		private static void ShowRing(MeshInstance3D ring, StandardMaterial3D material, Vector3 center, float radius, Color color)
		{
			if (ring == null)
				return;

			if (radius <= 0.5f)
			{
				ring.Visible = false;
				return;
			}

			material.AlbedoColor = color;
			ring.Mesh = BuildRingMesh(radius);
			ring.GlobalPosition = new Vector3(center.X, 0.4f, center.Z);
			ring.Visible = true;
		}

		private static StandardMaterial3D CreateRingMaterial(Color color)
		{
			return new StandardMaterial3D
			{
				AlbedoColor = color,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled
			};
		}

		private static Mesh BuildRingMesh(float radius)
		{
			const int segments = 64;
			float inner = Mathf.Max(1.5f, radius - 9f);

			var verts = new List<Vector3>(segments * 4);
			var indices = new List<int>(segments * 6);

			for (int i = 0; i < segments; i++)
			{
				float a0 = Mathf.Tau * i / segments;
				float a1 = Mathf.Tau * (i + 1) / segments;
				float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0);
				float c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
				int b = verts.Count;

				verts.Add(new Vector3(c0 * inner, 0f, s0 * inner));
				verts.Add(new Vector3(c0 * radius, 0f, s0 * radius));
				verts.Add(new Vector3(c1 * radius, 0f, s1 * radius));
				verts.Add(new Vector3(c1 * inner, 0f, s1 * inner));

				indices.Add(b);
				indices.Add(b + 1);
				indices.Add(b + 2);
				indices.Add(b);
				indices.Add(b + 2);
				indices.Add(b + 3);
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
