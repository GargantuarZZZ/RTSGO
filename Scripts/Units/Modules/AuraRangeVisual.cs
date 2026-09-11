using Godot;
using System.Collections.Generic;
using RTS.Core;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.World;

namespace RTS.Units
{
	// 光环范围显示（战争迷雾式方形格子）：
	// - 所有光环实例把各自的格子规格上报给全局绘制器
	// - 合并绘制，重叠格子只画一次，颜色不会叠加变深
	public partial class AuraRangeVisual : Node3D
	{
		private struct Spec
		{
			public Vector2I CenterCell;
			public int RadiusTiles;
			public Color Color;
			public bool ClipToCircle;
		}

		private static readonly List<AuraRangeVisual> _instances = new();
		private static MeshInstance3D _mesh;
		private static StandardMaterial3D _material;
		private static bool _rebuildScheduled = false;
		private static PainterNode _painter;
		private static readonly float CellHeight = 10f;

		[Export] public string TechId = "";
		[Export] public Color RingColor = new Color(0.3f, 1f, 0.4f, 0.24f);
		[Export] public bool ClipToCircle = true;

		private Spec _current;
		private bool _hasCurrent = false;

		private partial class PainterNode : Node
		{
			public void RebuildMerged() => AuraRangeVisual.RebuildMerged();
		}

		public override void _Ready()
		{
			_instances.Add(this);
			EnsurePainter(GetTree());
			ScheduleRebuild();
		}

		public override void _ExitTree()
		{
			_instances.Remove(this);
			ScheduleRebuild();
		}

		public override void _Process(double delta)
		{
			if (!TryGetSpec(out var spec))
			{
				if (_hasCurrent)
				{
					_hasCurrent = false;
					ScheduleRebuild();
				}
				return;
			}

			if (!_hasCurrent ||
				spec.CenterCell != _current.CenterCell ||
				spec.RadiusTiles != _current.RadiusTiles ||
				spec.Color != _current.Color)
			{
				_current = spec;
				_hasCurrent = true;
				ScheduleRebuild();
			}
		}

		private bool TryGetSpec(out Spec spec)
		{
			spec = default;
			int tile = 64;

			if (SimManager.Instance?.World?.Grid != null)
				tile = SimManager.Instance.World.Grid.TileSize;

			if (!string.IsNullOrEmpty(TechId))
			{
				if (GetParent() is not Unit unit || unit.UnitName != "NanoBehemoth")
					return false;

				var pd = Game.GetPlayerByTeam(unit.TeamID)?.PlayerData;
				if (pd == null || !pd.HasTech(TechId))
					return false;

				var cfg = ConfigDatabase.GetTech(TechId);
				if (cfg == null || cfg.AuraRadiusTiles <= 0)
					return false;

				spec.CenterCell = new Vector2I(
					Mathf.FloorToInt(unit.GlobalPosition.X / tile),
					Mathf.FloorToInt(unit.GlobalPosition.Z / tile)
				);
				spec.RadiusTiles = cfg.AuraRadiusTiles;
				spec.Color = RingColor;
				spec.ClipToCircle = ClipToCircle;
				return true;
			}

			// 恶魔立场（单位/建筑）：圆形裁剪的战争迷雾式方格
			if (GetParent() is Unit fieldUnit)
			{
				if (fieldUnit.LogicEntity is not SimUnit fieldSim)
					return false;
				if (RTS.Core.Main.Instance?.LocalPlayerID != fieldUnit.TeamID)
					return false;

				var unitCfg = ConfigDatabase.GetUnit(fieldUnit.UnitName);
				int radius = 0;

				if (unitCfg != null && unitCfg.ControlRangeTiles > 0)
					radius = unitCfg.ControlRangeTiles; // 多足控制范围
				else if (fieldSim.FieldRadius > 0)
					radius = fieldSim.FieldRadius;      // 恶魔单位立场

				if (radius <= 0)
					return false;

				spec.CenterCell = new Vector2I(
					Mathf.FloorToInt(fieldUnit.GlobalPosition.X / tile),
					Mathf.FloorToInt(fieldUnit.GlobalPosition.Z / tile)
				);
				spec.RadiusTiles = radius;
				spec.Color = RingColor;
				spec.ClipToCircle = true;
				return true;
			}

			if (GetParent() is Structure st && st.CurrentState == Structure.StructureState.Completed)
			{
				var structCfg = ConfigDatabase.GetStructure(st.StructureName);
				if (structCfg == null)
					return false;

				int radius = st.LogicEntity is SimStructure fieldSim && fieldSim.FieldRadius > 0
					? fieldSim.FieldRadius
					: (structCfg.FieldRadius > 0 ? structCfg.FieldRadius : structCfg.AuraRange);

				if (radius <= 0)
					return false;
				if (RTS.Core.Main.Instance?.LocalPlayerID != st.TeamID)
					return false;

				spec.CenterCell = new Vector2I(
					Mathf.FloorToInt(st.GlobalPosition.X / tile),
					Mathf.FloorToInt(st.GlobalPosition.Z / tile)
				);
				spec.RadiusTiles = radius;
				spec.Color = RingColor;
				spec.ClipToCircle = structCfg.FieldRadius > 0;
				return true;
			}

			return false;
		}

		private static void EnsurePainter(SceneTree tree)
		{
			if (_painter != null || tree?.Root == null)
				return;

			_painter = new PainterNode { Name = "AuraCellPainter" };
			tree.Root.AddChild(_painter);

			_material = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				VertexColorUseAsAlbedo = true,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled
			};

			_mesh = new MeshInstance3D
			{
				Name = "AuraCells",
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				MaterialOverride = _material,
				TopLevel = true
			};
			_painter.AddChild(_mesh);
		}

		private static void ScheduleRebuild()
		{
			if (_rebuildScheduled || _painter == null)
				return;

			_rebuildScheduled = true;
			_painter.CallDeferred(nameof(PainterNode.RebuildMerged));
		}

		private static void RebuildMerged()
		{
			_rebuildScheduled = false;

			if (_mesh == null)
				return;

			int tile = 64;
			if (SimManager.Instance?.World?.Grid != null)
				tile = SimManager.Instance.World.Grid.TileSize;

			// 合并所有光环格子：重叠格先到先得，只画一次，不加深颜色
			var cells = new Dictionary<Vector2I, Color>();

			foreach (var inst in _instances)
			{
				if (!inst._hasCurrent || inst._current.RadiusTiles <= 0)
					continue;

				var spec = inst._current;

				for (int x = -spec.RadiusTiles; x <= spec.RadiusTiles; x++)
				{
					for (int y = -spec.RadiusTiles; y <= spec.RadiusTiles; y++)
					{
						if (spec.ClipToCircle && x * x + y * y > spec.RadiusTiles * spec.RadiusTiles)
							continue;

						var cell = new Vector2I(spec.CenterCell.X + x, spec.CenterCell.Y + y);

						if (!cells.ContainsKey(cell))
							cells[cell] = spec.Color;
					}
				}
			}

			if (cells.Count == 0)
			{
				_mesh.Visible = false;
				return;
			}

			float half = tile * 0.5f;
			var verts = new List<Vector3>(cells.Count * 4);
			var colors = new List<Color>(cells.Count * 4);
			var indices = new List<int>(cells.Count * 6);

			foreach (var kvp in cells)
			{
				float cx = kvp.Key.X * tile + half;
				float cz = kvp.Key.Y * tile + half;
				int baseIdx = verts.Count;

				verts.Add(new Vector3(cx - half, CellHeight, cz - half));
				verts.Add(new Vector3(cx + half, CellHeight, cz - half));
				verts.Add(new Vector3(cx + half, CellHeight, cz + half));
				verts.Add(new Vector3(cx - half, CellHeight, cz + half));

				for (int i = 0; i < 4; i++)
					colors.Add(kvp.Value);

				indices.Add(baseIdx);
				indices.Add(baseIdx + 1);
				indices.Add(baseIdx + 2);
				indices.Add(baseIdx);
				indices.Add(baseIdx + 2);
				indices.Add(baseIdx + 3);
			}

			var arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
			arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
			arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

			var mesh = new ArrayMesh();
			mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
			_mesh.Mesh = mesh;
			_mesh.Visible = true;
		}
	}
}
