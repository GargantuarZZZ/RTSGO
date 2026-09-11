using Godot;
using System.Collections.Generic;
using RTS.Data;
using RTS.Core;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

// 3D 血条：两个 QuadMesh（背景 + 填充），填充从左端收缩，面向 -Z 相机
public partial class UnitHealthBar3D : Node3D
{
	private MeshInstance3D _background;
	private MeshInstance3D _fill;
	private MeshInstance3D _energyBackground;
	private MeshInstance3D _energyFill;
	private MeshInstance3D _lifeBackground;
	private MeshInstance3D _lifeFill;
	private MeshInstance3D _prodBackground;
	private MeshInstance3D _prodFill;
	private UnitLife _life;
	private IEntity _owner;
	private float _width = 48f;
	private bool _fogHidden = false;

	// 血条材质/网格全局共享：大量单位也不复制成百上千份资源
	private static readonly Dictionary<(float W, float H), QuadMesh> MeshCache = new();
	private static readonly StandardMaterial3D BgMaterial = CreateBarMaterial(new Color(0f, 0f, 0f, 1f));
	private static readonly StandardMaterial3D FillMaterial = CreateBarMaterial(Colors.Green);
	private static readonly StandardMaterial3D EnergyFillMaterial = CreateBarMaterial(new Color(0.25f, 0.7f, 1f, 1f));
	private static readonly StandardMaterial3D LifeFillMaterial = CreateBarMaterial(new Color(0.75f, 0.4f, 0.9f, 1f));
	private static readonly StandardMaterial3D ProdFillMaterial = CreateBarMaterial(new Color(1f, 0.88f, 0.3f, 1f));

	/// <summary>
	/// 血条材质：无光照 + **始终朝向相机**。
	///
	/// 之前漏了 Billboard，血条只是躺在 XY 平面上的方片：
	/// 相机 Pitch=55° 时先被透视压扁到 ~0.57×，拉到 6000 高度后 8 单位的条
	/// 直接低于一个像素 —— 放大看还行，缩远了完全读不出来。
	/// 用 Y-Billboard（只绕 Y 轴转）：条会跟着相机水平转，但始终水平，
	/// 不会像全 Billboard 那样在俯视角下歪掉。
	/// </summary>
	private static StandardMaterial3D CreateBarMaterial(Color color)
	{
		return new StandardMaterial3D
		{
			AlbedoColor = color,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
			BillboardKeepScale = true
		};
	}

	private static QuadMesh GetQuadMesh(float w, float h)
	{
		if (!MeshCache.TryGetValue((w, h), out var mesh))
		{
			mesh = new QuadMesh { Size = new Vector2(w, h) };
			MeshCache[(w, h)] = mesh;
		}
		return mesh;
	}

	public void Setup(UnitLife life, float width = 48f, IEntity owner = null)
	{
		_life = life;
		_owner = owner;
		_width = width;

		// 能量条（血条下方蓝条）：能量单位（英雄能量）与植物花田（FlowerEnergy）共用同一槽位
		bool hasEnergy = (owner is Unit energyUnit &&
			RTS.Data.Configs.ConfigDatabase.GetUnit(energyUnit.UnitName)?.HeroEnergyMax > 0f) ||
			(owner is Structure flowerStruct && flowerStruct.StructureName == "PlantFlowerField");
		bool hasProd = owner is Structure prodStruct &&
			RTS.Data.Configs.ConfigDatabase.GetStructure(prodStruct.StructureName)?.AutoProduceUnitIds.Count > 0;
		// 恶魔等带持续时间的单位：寿命条显示在血条下方（与蓝条同位）
		bool hasLifespan = owner is Unit lifeUnit &&
			RTS.Data.Configs.ConfigDatabase.GetUnit(lifeUnit.UnitName)?.LifespanSeconds > 0f;

		_background = MakeQuad(BgMaterial, width, 8f, 0f);
		_fill = MakeQuad(FillMaterial, width, 6f, 0.1f);

		AddChild(_background);
		AddChild(_fill);

		UpdateValues(life.CurrentHp, life.MaxHp);
		life.HealthChanged += UpdateValues;

		if (hasEnergy)
		{
			_energyBackground = MakeQuad(BgMaterial, width, 5f, 0f);
			_energyBackground.Position = new Vector3(0f, -8f, 0f);
			_energyFill = MakeQuad(EnergyFillMaterial, width, 3.5f, 0.1f);
			_energyFill.Position = new Vector3(0f, -8f, 0.1f);
			AddChild(_energyBackground);
			AddChild(_energyFill);
		}

		if (hasLifespan)
		{
			_lifeBackground = MakeQuad(BgMaterial, width, 5f, 0f);
			_lifeBackground.Position = new Vector3(0f, -8f, 0f);
			_lifeFill = MakeQuad(LifeFillMaterial, width, 3.5f, 0.1f);
			_lifeFill.Position = new Vector3(0f, -8f, 0.1f);
			AddChild(_lifeBackground);
			AddChild(_lifeFill);
		}

		if (hasProd)
		{
			_prodBackground = MakeQuad(BgMaterial, width, 5f, 0f);
			_prodBackground.Position = new Vector3(0f, -14f, 0f);
			_prodFill = MakeQuad(ProdFillMaterial, width, 4.5f, 0.1f);
			_prodFill.Position = new Vector3(0f, -14f, 0.1f);
			AddChild(_prodBackground);
			AddChild(_prodFill);
		}

		UpdateVisibility();
		// 普通血条只靠 HealthChanged 事件刷新，不需要每帧轮询；只有能量/生产条才开 _Process
		SetProcess(_energyFill != null || _prodFill != null || _lifeFill != null);
	}

	public void SetFogHidden(bool hidden)
	{
		_fogHidden = hidden;
		UpdateVisibility();
	}

	// 血条始终显示；被战争迷雾隐藏时整体隐藏。
	private void UpdateVisibility()
	{
		Visible = !_fogHidden;
	}

	private MeshInstance3D MakeQuad(StandardMaterial3D material, float w, float h, float z)
	{
		var mi = new MeshInstance3D
		{
			Mesh = GetQuadMesh(w, h),
			Position = new Vector3(0f, 0f, z)
		};
		mi.MaterialOverride = material;
		return mi;
	}

	private void UpdateValues(float current, float max)
	{
		if (_fill == null || max <= 0f)
			return;

		// 死亡后保持最后一刻血量，让血条跟着尸体一起沉入地底
		if (_life != null && _life.IsDead)
			return;

		float ratio = Mathf.Clamp(current / max, 0f, 1f);

		// 左端固定：填充 quad 中心随比例左移
		_fill.Scale = new Vector3(ratio, 1f, 1f);
		_fill.Position = new Vector3(_width * (ratio - 1f) * 0.5f, 0f, 0.1f);
	}

	public override void _Process(double delta)
	{
		if (_energyFill != null && _owner?.LogicEntity is SimStructure fs && fs.FlowerEnergyMax > FP.Zero)
		{
			// 花田能量：与能量单位同槽（血条下方）
			float energyRatio = Mathf.Clamp((float)(fs.FlowerEnergy / fs.FlowerEnergyMax), 0f, 1f);
			_energyFill.Scale = new Vector3(energyRatio, 1f, 1f);
			_energyFill.Position = new Vector3(_width * (energyRatio - 1f) * 0.5f, -8f, 0.1f);
		}
		else if (_energyFill != null && _owner?.LogicEntity is SimUnit su && su.HeroMaxEnergy > FP.Zero)
		{
			float energyRatio = Mathf.Clamp((float)(su.HeroEnergy / su.HeroMaxEnergy), 0f, 1f);
			_energyFill.Scale = new Vector3(energyRatio, 1f, 1f);
			_energyFill.Position = new Vector3(_width * (energyRatio - 1f) * 0.5f, -8f, 0.1f);
		}

		if (_prodFill != null && _owner?.LogicEntity is SimStructure ss && ss.AutoProduceIntervalCurrent > FP.Zero)
		{
			float prodRatio = Mathf.Clamp((float)(ss.AutoProduceTimer / ss.AutoProduceIntervalCurrent), 0f, 1f);
			_prodFill.Scale = new Vector3(prodRatio, 1f, 1f);
			_prodFill.Position = new Vector3(_width * (prodRatio - 1f) * 0.5f, -14f, 0.1f);
		}

		// 寿命条：剩余持续时间（恶魔等限时单位）
		if (_lifeFill != null && _owner?.LogicEntity is SimUnit lifespanUnit && lifespanUnit.LifespanMax > FP.Zero)
		{
			float lifeRatio = Mathf.Clamp((float)(lifespanUnit.LifespanTimer / lifespanUnit.LifespanMax), 0f, 1f);
			_lifeFill.Scale = new Vector3(lifeRatio, 1f, 1f);
			_lifeFill.Position = new Vector3(_width * (lifeRatio - 1f) * 0.5f, -8f, 0.1f);
		}
	}

	public override void _ExitTree()
	{
		if (_life != null)
			_life.HealthChanged -= UpdateValues;
	}
}
