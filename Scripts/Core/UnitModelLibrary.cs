using Godot;
using System.Collections.Generic;

namespace RTS.Core
{
	/// <summary>
	/// 实体 -> 3D 模型映射表。
	/// 数据来源：Quaternius Ultimate Space Kit (CC0, https://quaternius.com/packs/ultimatespacekit.html)。
	/// 模型原始单位为 Blender 单位（GLB 内已自带 scale=100），这里按 1 格 = 64 世界单位换算。
	/// </summary>
	public sealed class UnitModelInfo
	{
		public string ModelPath = "";
		public float Scale = 1f;
		public float OffsetY = 0f;
	public float RotationY = 0f;
	public float YMin = 0f;
	public float YMax = 1f;
	public string IdleAnimation = "Idle";
	public string MoveAnimation = "Walk";
	/// <summary>移动时是否让模型朝向移动方向（SCV 等保持格线对齐的载具设为 false）。</summary>
	public bool TurnWhileMoving = true;
	/// <summary>OBJ 模型自动缩放：水平最长边（世界单位）的目标尺寸，0 = 使用 Scale。</summary>
	public float AutoFitFootprint = 0f;
	/// <summary>旋转轴心相对模型包围盒中心的偏移（模型原始单位，未乘 Scale）。</summary>
	public float PivotOffsetX = 0f;
	public float PivotOffsetZ = 0f;
	/// <summary>模型朝向补偿（度）：模型自身正面不是 +Z 时，用这个值修正。</summary>
	public float FacingBias = 0f;
	/// <summary>Turret model: rotate the whole visible model toward the target when firing.</summary>
	public bool RotateToAim = false;
	/// <summary>Barrel node path relative to the aim node; empty = no separate barrel.</summary>
	public string BarrelPath = "";
	/// <summary>Barrel pitch (degrees, local X, relative to the prefab rest pose) when NOT deployed.</summary>
	public float BarrelNormalPitchDegrees = 0f;
	/// <summary>Barrel pitch (degrees, local X, relative to the prefab rest pose) when deployed.</summary>
	public float BarrelDeployPitchDegrees = 0f;
	/// <summary>Bottom muzzle node path relative to Model; used by bottom-aim deploy units (Liberator).</summary>
	public string BottomMuzzlePath = "";
	/// <summary>When deployed, rotate the whole model so its bottom points at the target.</summary>
	public bool DeployAimWithBottom = false;
	/// <summary>视觉中心相对包围盒中心的偏移（模型原始单位，未乘 Scale），用于让旋转中心对准主体。</summary>
	public float CenterOffsetX = 0f;
	public float CenterOffsetZ = 0f;

		public float VisualHeight => (YMax - YMin) * Scale;
		public float TopY => OffsetY + YMax * Scale;
	}

	public static class UnitModelLibrary
	{
		private static readonly Dictionary<string, UnitModelInfo> Map = new()
		{
			// ================= 联盟 =================
			["SCV"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/SCV.glb",
				Scale = 33.69f, OffsetY = 31.60f,
				YMin = -0.94f, YMax = 0.89f,
				TurnWhileMoving = false,
			},
			["RifleMan"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/RifleMan.glb",
				Scale = 61.92f, OffsetY = 61.69f,
				YMin = -1.00f, YMax = 0.93f,
				IdleAnimation = "Idle_Gun", MoveAnimation = "Run_Gun",
			},
			["RocketMan"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/RocketMan.glb",
				Scale = 54.39f, OffsetY = 54.18f,
				YMin = -1.00f, YMax = 0.93f,
				IdleAnimation = "Idle_Gun", MoveAnimation = "Run_Gun",
			},
			["Medic"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/Medic.glb",
				Scale = 69.02f, OffsetY = 68.78f,
				YMin = -1.00f, YMax = 0.93f,
				IdleAnimation = "Idle_Gun", MoveAnimation = "Run_Gun",
			},
			["Biped"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/Biped.glb",
				Scale = 67.38f, OffsetY = 67.14f,
				YMin = -1.00f, YMax = 0.93f,
				IdleAnimation = "Idle", MoveAnimation = "Walk",
			},
			["Hp"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/Hp.glb",
				Scale = 49.75f, OffsetY = 21.64f,
				YMin = -0.44f, YMax = 0.40f,
			},
			["BattleCruiser"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/BattleCruiser.glb",
				Scale = 99.52f, OffsetY = 26.17f,
				YMin = -0.26f, YMax = 0.22f,
			},

			["CommandCenter"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/CommandCenter.glb",
				Scale = 133.04f, OffsetY = 103.39f,
				YMin = -0.78f, YMax = 0.71f,
			},
			["BB"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/BB.glb",
				Scale = 89.52f, OffsetY = 89.19f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["VF"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/VF.glb",
				Scale = 99.37f, OffsetY = 67.95f,
				YMin = -0.68f, YMax = 0.62f,
			},
			["SupplyDepot"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/SupplyDepot.glb",
				Scale = 65.38f, OffsetY = 65.18f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["Academy"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/Academy.glb",
				Scale = 171.60f, OffsetY = 170.92f,
				YMin = -1.00f, YMax = 0.89f,
			},
			["Starport"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/Starport.glb",
				Scale = 99.59f, OffsetY = 68.38f,
				YMin = -0.69f, YMax = 0.62f,
			},
			["OrbitalControl"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Union/OrbitalControl.glb",
				Scale = 99.65f, OffsetY = 98.22f,
				YMin = -0.99f, YMax = 0.92f,
			},

			["NanoCore"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Nano/NanoCore.glb",
				Scale = 132.63f, OffsetY = 132.05f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["Tower"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/turrets/Cannon_1.obj",
				AutoFitFootprint = 60f,
				RotateToAim = true
			},
			["Shrine"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Planet.glb",
				Scale = 30f, OffsetY = 68.4f,
				YMin = -2.28f, YMax = 2.23f
			},

			["IronOre"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Rock-34W5ymEePk.glb",
				Scale = 19f, OffsetY = 7.0f,
				YMin = -0.37f, YMax = 3.46f
			},
			["GasSpring"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Pickup Jar.glb",
				Scale = 92f, OffsetY = 54.3f,
				YMin = -0.59f, YMax = 0.43f
			},

			// ================= 纳米虫 =================
			["NanoBehemoth"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Nano/NanoBehemoth.glb",
				Scale = 132.75f, OffsetY = 115.54f,
				YMin = -0.87f, YMax = 0.79f,
				IdleAnimation = "Idle", MoveAnimation = "Walk",
			},
			["NanoTurret"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Nano/NanoTurret.glb",
				Scale = 65.85f, OffsetY = 63.62f,
				YMin = -0.97f, YMax = 0.79f,
				RotateToAim = true,
			},
			["NanoSniper"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Nano/NanoSniper.glb",
				Scale = 66.37f, OffsetY = 44.98f,
				YMin = -0.68f, YMax = 0.61f,
				RotateToAim = true,
			},
			["NanoAA"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Nano/NanoAA.glb",
				Scale = 66.42f, OffsetY = 66.22f,
				YMin = -1.00f, YMax = 0.95f,
				RotateToAim = true,
			},
			["NanoHarvester"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Nano/NanoHarvester.glb",
				Scale = 132.87f, OffsetY = 66.10f,
				YMin = -0.50f, YMax = 0.43f,
			},
			["NanoActiveTower"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Nano/NanoActiveTower.glb",
				Scale = 93.61f, OffsetY = 93.24f,
				YMin = -1.00f, YMax = 0.88f,
			},
			["NanoSmokeTower"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Nano/NanoSmokeTower.glb",
				Scale = 121.11f, OffsetY = 120.69f,
				YMin = -1.00f, YMax = 0.95f,
			},

			// ================= 恶魔 =================
			["DemonWorker"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/DemonWorker.glb",
				Scale = 78.07f, OffsetY = 74.33f,
				YMin = -0.95f, YMax = 0.93f,
			},
			["DemonDog"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/DemonDog.glb",
				Scale = 33.16f, OffsetY = 23.61f,
				YMin = -0.71f, YMax = 0.65f,
			},
			["DemonFlyer"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/DemonFlyer.glb",
				Scale = 33.06f, OffsetY = 16.38f,
				YMin = -0.50f, YMax = 0.43f,
			},
			["HeavyTank"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/HeavyTank.glb",
				Scale = 66.46f, OffsetY = 61.99f,
				YMin = -0.93f, YMax = 0.87f,
			},
			["FireDragon"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/FireDragon.glb",
				Scale = 67.17f, OffsetY = 50.11f,
				YMin = -0.75f, YMax = 0.65f,
			},
			["LavaBanner"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/LavaBanner.glb",
				Scale = 52.99f, OffsetY = 52.78f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["FortGuard"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/FortGuard.glb",
				Scale = 33.56f, OffsetY = 33.44f,
				YMin = -1.00f, YMax = 0.92f,
			},
			["HellLord"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/HellLord.glb",
				Scale = 122.78f, OffsetY = 122.27f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["FireTitan"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/FireTitan.glb",
				Scale = 95.33f, OffsetY = 94.95f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["HellCity"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/HellCity.glb",
				Scale = 146.09f, OffsetY = 145.60f,
				YMin = -1.00f, YMax = 0.92f,
			},
			["GreatRift"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/GreatRift.glb",
				Scale = 160.15f, OffsetY = 159.52f,
				YMin = -1.00f, YMax = 0.92f,
			},
			["HeavyWorkshop"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/HeavyWorkshop.glb",
				Scale = 123.95f, OffsetY = 123.49f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["DragonNest"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/DragonNest.glb",
				Scale = 99.63f, OffsetY = 92.85f,
				YMin = -0.93f, YMax = 0.87f,
			},
			["ManaTower"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/ManaTower.glb",
				Scale = 55.19f, OffsetY = 54.98f,
				YMin = -1.00f, YMax = 0.89f,
				RotateToAim = true,
			},
			["SoulStone"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/SoulStone.glb",
				Scale = 42.90f, OffsetY = 42.73f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["LavaAltar"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/LavaAltar.glb",
				Scale = 132.85f, OffsetY = 107.40f,
				YMin = -0.81f, YMax = 0.75f,
			},
			["CurseFortress"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/CurseFortress.glb",
				Scale = 152.22f, OffsetY = 151.68f,
				YMin = -1.00f, YMax = 0.88f,
			},
			["DemonTower"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/DemonTower.glb",
				Scale = 215.56f, OffsetY = 214.71f,
				YMin = -1.00f, YMax = 0.83f,
			},
			["Banner"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Demon/Banner.glb",
				Scale = 82.41f, OffsetY = 82.10f,
				YMin = -1.00f, YMax = 0.93f,
			},

			// ================= 多足机械 =================
			["Builder"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Wanderer/Builder.glb",
				Scale = 65.95f, OffsetY = 65.75f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["Hunter"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Wanderer/Hunter.glb",
				Scale = 64.24f, OffsetY = 63.99f,
				YMin = -1.00f, YMax = 0.93f,
				IdleAnimation = "Flying_Idle", MoveAnimation = "Fast_Flying",
			},
			["Harvester"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Wanderer/Harvester.glb",
				Scale = 33.05f, OffsetY = 21.57f,
				YMin = -0.65f, YMax = 0.59f,
			},
			["Shepherd"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Wanderer/Shepherd.glb",
				Scale = 79.26f, OffsetY = 79.00f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["Tank"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Wanderer/Tank.glb",
				Scale = 66.92f, OffsetY = 49.94f,
				YMin = -0.75f, YMax = 0.68f,
			},
			["PlasmaCannon"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Wanderer/PlasmaCannon.glb",
				Scale = 131.64f, OffsetY = 98.19f,
				YMin = -0.75f, YMax = 0.68f,
			},
			["Blueprint_Builder"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Pickup Crate.glb",
				Scale = 20f, OffsetY = 12f,
				YMin = -0.5f, YMax = 0.5f
			},
			["Blueprint_Hunter"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Pickup Crate.glb",
				Scale = 14f, OffsetY = 8f,
				YMin = -0.5f, YMax = 0.5f
			},
			["Blueprint_Harvester"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Pickup Crate.glb",
				Scale = 14f, OffsetY = 8f,
				YMin = -0.5f, YMax = 0.5f
			},
			["Blueprint_Shepherd"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Pickup Crate.glb",
				Scale = 20f, OffsetY = 12f,
				YMin = -0.5f, YMax = 0.5f
			},
			["Blueprint_Tank"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Pickup Crate.glb",
				Scale = 20f, OffsetY = 12f,
				YMin = -0.5f, YMax = 0.5f
			},
			["Blueprint_PlasmaCannon"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Pickup Crate.glb",
				Scale = 30f, OffsetY = 16f,
				YMin = -0.5f, YMax = 0.5f
			},

			// ================= 泰伦（临时占位模型，正式模型待建模师） =================
			["Engineer"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/Engineer.glb",
				Scale = 56.59f, OffsetY = 56.39f,
				YMin = -1.00f, YMax = 0.93f,
				TurnWhileMoving = false,
			},
			["Marine"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/Marine.glb",
				Scale = 60.52f, OffsetY = 60.28f,
				YMin = -1.00f, YMax = 0.93f,
				IdleAnimation = "Idle_Gun", MoveAnimation = "Run_Gun",
			},
			["HeavyInfantry"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/HeavyInfantry.glb",
				Scale = 59.07f, OffsetY = 58.84f,
				YMin = -1.00f, YMax = 0.93f,
				IdleAnimation = "Idle_Gun", MoveAnimation = "Run_Gun",
			},
			["CommandVehicle"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/CommandVehicle.glb",
				Scale = 66.32f, OffsetY = 57.86f,
				YMin = -0.87f, YMax = 0.55f,
			},
			["Artillery"] = new UnitModelInfo
			{
				// 预制体内嵌了三份部件（dp=车体/sp=炮塔/g=炮管）；这里只作为无预制体时的回退模型
				ModelPath = "res://ArtRes/models/TerranArtillery/dp.fbx",
				Scale = 2376f, OffsetY = 0f,
				YMin = 0f, YMax = 0.0246f,
				BarrelPath = "Barrel",
				BarrelNormalPitchDegrees = 70f,
				BarrelDeployPitchDegrees = 25f
			},
			["Liberator"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/TerranLiberator/msr.fbx",
				Scale = 2000f, OffsetY = 90f,
				YMin = 0f, YMax = 0.1487f,
				BottomMuzzlePath = "Muzzle",
				DeployAimWithBottom = true
			},
			["Fighter"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/Fighter.glb",
				Scale = 49.03f, OffsetY = 16.54f,
				YMin = -0.34f, YMax = 0.28f,
			},
			["MissileVehicle"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/MissileVehicle.glb",
				Scale = 66.43f, OffsetY = 55.76f,
				YMin = -0.84f, YMax = 0.78f,
			},

			// ================= 泰伦建筑（临时占位模型，正式模型待建模师） =================
			["FortressCore"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/FortressCore.glb",
				Scale = 132.52f, OffsetY = 86.51f,
				YMin = -0.65f, YMax = 0.58f,
			},
			["FrontlineCamp"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/FrontlineCamp.glb",
				Scale = 97.80f, OffsetY = 97.52f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["FieldCamp"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/FieldCamp.glb",
				Scale = 66.36f, OffsetY = 66.02f,
				YMin = -0.99f, YMax = 0.93f,
			},
			["ArmorFactory"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/ArmorFactory.glb",
				Scale = 99.55f, OffsetY = 54.39f,
				YMin = -0.55f, YMax = 0.49f,
			},
			["Airfield"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/Airfield.glb",
				Scale = 99.48f, OffsetY = 61.59f,
				YMin = -0.62f, YMax = 0.56f,
			},
			["Armory"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/Armory.glb",
				Scale = 98.07f, OffsetY = 88.08f,
				YMin = -0.90f, YMax = 0.84f,
			},
			["AlgaeFactory"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Terran/AlgaeFactory.glb",
				Scale = 85.93f, OffsetY = 85.60f,
				YMin = -1.00f, YMax = 0.93f,
			},
			["MetalMine"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Rock-34W5ymEePk.glb",
				Scale = 19f, OffsetY = 7.0f,
				YMin = -0.37f, YMax = 3.46f
			},
			["WildFruit"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Pickup Sphere.glb",
				Scale = 50f, OffsetY = 25f,
				YMin = -0.5f, YMax = 0.5f
			},
			// ================= 洞穴族（Gemini + Hunyuan 生成） =================
			["CaveWorker"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveWorker.glb",
				Scale = 34.85f, OffsetY = 34.14f,
				YMin = -0.980f, YMax = 0.859f,
			},
			["CaveNestShooter"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveNestShooter.glb",
				Scale = 34.78f, OffsetY = 29.77f,
				YMin = -0.856f, YMax = 0.720f,
			},
			["CaveBreaker"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveBreaker.glb",
				Scale = 52.22f, OffsetY = 51.24f,
				YMin = -0.981f, YMax = 0.859f,
			},
			["CaveSage"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveSage.glb",
				Scale = 38.71f, OffsetY = 38.03f,
				YMin = -0.982f, YMax = 0.855f,
			},
			["CaveScorpion"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveScorpion.glb",
				Scale = 69.45f, OffsetY = 47.30f,
				YMin = -0.681f, YMax = 0.612f,
			},
			// ================= 植物（Gemini + Hunyuan 生成） =================
			["PlantLeafDog"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantLeafDog.glb",
				Scale = 34.42f, OffsetY = 31.61f,
				YMin = -0.918f, YMax = 0.782f,
			},
			["PlantSpider"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantSpider.glb",
				Scale = 34.84f, OffsetY = 23.05f,
				YMin = -0.661f, YMax = 0.612f,
			},
			["PlantTreeGuard"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantTreeGuard.glb",
				Scale = 74.66f, OffsetY = 73.40f,
				YMin = -0.983f, YMax = 0.858f,
			},
			// ================= 植物建筑（Gemini + Hunyuan 生成） =================
			["PlantLifeTree"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantLifeTree.glb",
				Scale = 158.02f, OffsetY = 154.86f,
				YMin = -0.980f, YMax = 0.860f,
			},
			["PlantBranchTree"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantBranchTree.glb",
				Scale = 112.28f, OffsetY = 110.03f,
				YMin = -0.980f, YMax = 0.860f,
			},
			["PlantForestNode"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantForestNode.glb",
				Scale = 36.36f, OffsetY = 26.54f,
				YMin = -0.730f, YMax = 0.660f,
			},
			["PlantLeafPit"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantLeafPit.glb",
				Scale = 68.82f, OffsetY = 36.47f,
				YMin = -0.530f, YMax = 0.380f,
			},
			["PlantSproutNest"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantSproutNest.glb",
				Scale = 73.56f, OffsetY = 51.49f,
				YMin = -0.700f, YMax = 0.480f,
			},
			["PlantFlowerField"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantFlowerField.glb",
				Scale = 76.19f, OffsetY = 64.76f,
				YMin = -0.850f, YMax = 0.770f,
			},
			["PlantNestFort"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantNestFort.glb",
				Scale = 104.35f, OffsetY = 83.48f,
				YMin = -0.800f, YMax = 0.670f,
			},
			["PlantSunlightTurret"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantSunlightTurret.glb",
				Scale = 44.14f, OffsetY = 43.26f,
				YMin = -0.980f, YMax = 0.720f,
			},
			["PlantLifeSpring"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantLifeSpring.glb",
				Scale = 74.85f, OffsetY = 73.35f,
				YMin = -0.980f, YMax = 0.860f,
			},
			["PlantTreeWall"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantTreeWall.glb",
				Scale = 34.78f, OffsetY = 27.82f,
				YMin = -0.800f, YMax = 0.670f,
			},
			["PlantEarthCore"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Plant/PlantEarthCore.glb",
				Scale = 67.37f, OffsetY = 66.02f,
				YMin = -0.980f, YMax = 0.850f,
			},
			// ================= 洞穴建筑（Gemini + Hunyuan 生成） =================
			["CaveMainNest"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveMainNest.glb",
				Scale = 140.66f, OffsetY = 111.12f,
				YMin = -0.790f, YMax = 0.620f,
			},
			["CaveTrainingGround"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveTrainingGround.glb",
				Scale = 104.35f, OffsetY = 76.18f,
				YMin = -0.730f, YMax = 0.610f,
			},
			["CaveDen"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveDen.glb",
				Scale = 69.57f, OffsetY = 46.61f,
				YMin = -0.670f, YMax = 0.550f,
			},
			["CaveExcavation"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveExcavation.glb",
				Scale = 70.33f, OffsetY = 26.02f,
				YMin = -0.370f, YMax = 0.240f,
			},
			["CaveFungusField"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveFungusField.glb",
				Scale = 73.56f, OffsetY = 36.04f,
				YMin = -0.490f, YMax = 0.540f,
			},
			["CaveNest"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveNest.glb",
				Scale = 139.13f, OffsetY = 84.87f,
				YMin = -0.610f, YMax = 0.480f,
			},
			["CaveAcademy"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveAcademy.glb",
				Scale = 152.38f, OffsetY = 149.33f,
				YMin = -0.980f, YMax = 0.750f,
			},
			["CaveSeismograph"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveSeismograph.glb",
				Scale = 301.18f, OffsetY = 295.16f,
				YMin = -0.980f, YMax = 0.770f,
			},
			["CaveRanch"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveRanch.glb",
				Scale = 102.67f, OffsetY = 31.83f,
				YMin = -0.310f, YMax = 0.180f,
			},
			["CaveOutpost"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveOutpost.glb",
				Scale = 130.61f, OffsetY = 128.00f,
				YMin = -0.980f, YMax = 0.850f,
			},
			["CaveCrustCracker"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveCrustCracker.glb",
				Scale = 147.98f, OffsetY = 145.02f,
				YMin = -0.980f, YMax = 0.850f,
			},
			["CaveWormholeCore"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveWormholeCore.glb",
				Scale = 103.23f, OffsetY = 101.17f,
				YMin = -0.980f, YMax = 0.860f,
			},
			["CaveWormhole"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveWormhole.glb",
				Scale = 104.35f, OffsetY = 69.91f,
				YMin = -0.670f, YMax = 0.500f,
			},
			["CaveSandwormPit"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveSandwormPit.glb",
				Scale = 139.13f, OffsetY = 84.87f,
				YMin = -0.610f, YMax = 0.490f,
			},
			// ================= 沙虫（头段模型复用 5 节） =================
			["CaveSandworm"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveSandworm.glb",
				Scale = 107.87f, OffsetY = 105.71f,
				YMin = -0.980f, YMax = 0.860f,
			},
			["CaveSandwormMid"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveSandworm.glb",
				Scale = 53.93f, OffsetY = 52.85f,
				YMin = -0.980f, YMax = 0.860f,
			},
			["CaveSandwormTail"] = new UnitModelInfo
			{
				ModelPath = "res://ArtRes/models/Cave/CaveSandworm.glb",
				Scale = 53.93f, OffsetY = 52.85f,
				YMin = -0.980f, YMax = 0.860f,
			}
		};

		public static UnitModelInfo Get(string entityName)
		{
			if (string.IsNullOrEmpty(entityName))
				return null;
			return Map.TryGetValue(entityName, out var info) ? info : null;
		}
	}
}
