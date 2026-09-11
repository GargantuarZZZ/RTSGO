using Godot;

// 3D 选择圈：地面上的半透明圆盘，选中时显示
public partial class SelectionRing3D : Node3D
{
	private MeshInstance3D _ring;
	private StandardMaterial3D _material;

	public void Setup(float radius)
	{
		_ring = new MeshInstance3D
		{
			Name = "SelectionRing",
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Mesh = new CylinderMesh
			{
				TopRadius = radius,
				BottomRadius = radius,
				Height = 1f,
				RadialSegments = 32
			},
			// 抬高到菌毯之上（菌毯视觉层约 y=0~1，光环用 y=10）
			Position = new Vector3(0f, 10.5f, 0f)
		};
		_material = new StandardMaterial3D
		{
			AlbedoColor = new Color(0f, 1f, 0f, 0.45f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled
		};
		_ring.MaterialOverride = _material;
		AddChild(_ring);
		Visible = false;
	}

	public void SetSelected(bool selected)
	{
		Visible = selected;
	}

	// 架设状态：选中圈从绿色变为黄色。
	public void SetDeployed(bool deployed)
	{
		if (_material == null)
			return;

		_material.AlbedoColor = deployed
			? new Color(1f, 0.85f, 0.1f, 0.5f)
			: new Color(0f, 1f, 0f, 0.45f);
	}
}
