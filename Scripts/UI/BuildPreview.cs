using Godot;
using RTS.World;
using RTS.Data;

namespace RTS.Core
{
	// 3D 建造预览：半透明立方体贴在鼠标射线指向的地面格子上
	public partial class BuildPreview : Node3D
	{
		private MeshInstance3D _mesh;
		private StandardMaterial3D _material;
		private int _gridSize;
		private bool _canPlace;
		public bool ExtraBlocked { get; set; } = false;
		private CreepType _reqCreep;
		private int _ownerTeam;

		public Vector2 AlignedWorldPos { get; private set; }

		public void Setup(Texture2D tex, int gridSize, CreepType reqCreep, int ownerTeam = 0)
		{
			_gridSize = gridSize;
			_reqCreep = reqCreep;
			_ownerTeam = ownerTeam;

			_mesh = new MeshInstance3D
			{
				Name = "BuildPreviewMesh",
				Mesh = RTS.Core.CubeMeshBuilder.Build(new Vector3(gridSize * 64f, gridSize * 64f, gridSize * 64f)),
				Position = new Vector3(0f, gridSize * 64f * 0.5f, 0f)
			};

			_material = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.2f, 0.6f, 1.0f, 0.45f),
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				Uv1Scale = Vector3.One
			};

			if (tex != null)
				_material.AlbedoTexture = tex;

			_mesh.MaterialOverride = _material;
			AddChild(_mesh);
		}

		// 由 UserController 每帧传入 3D 鼠标射线坐标（逻辑 XZ）
		public void SetTargetPosition(Vector2 worldPos)
		{
			if (MapGrid.Instance == null)
				return;

			Vector2I centerGrid = MapGrid.Instance.WorldToGrid(worldPos);
			Vector2I topLeftGrid = MapGrid.Instance.GetTopLeftFromCenter(centerGrid, _gridSize);
			AlignedWorldPos = MapGrid.Instance.GetAlignedWorldPos(topLeftGrid, _gridSize);

			GlobalPosition = new Vector3(AlignedWorldPos.X, 0f, AlignedWorldPos.Y);
			_canPlace = MapGrid.Instance.IsPositionAvailableForBlueprint(topLeftGrid, _gridSize, _reqCreep, _ownerTeam);

			_material.AlbedoColor = (_canPlace && !ExtraBlocked)
				? new Color(0.2f, 0.8f, 0.3f, 0.45f)
				: new Color(1f, 0.25f, 0.25f, 0.45f);
		}

		public bool CanPlace => _canPlace && !ExtraBlocked;
	}
}
