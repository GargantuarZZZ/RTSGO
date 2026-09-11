using Godot;
using System.Collections.Generic;
using RTS.Core;
using RTS.Simulation;
using RTS.Units;
using RTS.World;
using FP = FixMath.NET.Fix64;

namespace RTS.Test
{
	// 联盟 vs 多足机械 混编 2000 人口对战场景（每边 1000 人口）。
	// 配比（供参考，可直接改 UnionArmy / WandererArmy）：
	//   联盟：枪兵400 + 医疗80 + 火箭80 + 双足60 + 直升机40 + 战巡14 = 1000 人口 / 674 单位
	//   多足：游猎400 + 收割160 + 建造30 + 战车70 + 电浆炮5 = 1000 人口 / 665 单位
	public partial class StressTest2000Mixed : StressTestBase
	{
		private const int SupplyPerSide = 1000;
		private const int BatchSize = 80;

		private readonly struct UnitGroup
		{
			public readonly string UnitId;
			public readonly int Count;
			public readonly int Supply;
			public readonly float Spacing;

			public UnitGroup(string unitId, int count, int supply, float spacing)
			{
				UnitId = unitId;
				Count = count;
				Supply = supply;
				Spacing = spacing;
			}
		}

		private static readonly UnitGroup[] UnionArmy =
		{
			new("RifleMan", 400, 1, 64f),
			new("Medic", 80, 1, 64f),
			new("RocketMan", 80, 1, 64f),
			new("Biped", 60, 3, 96f),
			new("Hp", 40, 3, 96f),
			new("BattleCruiser", 14, 10, 128f)
		};

		private static readonly UnitGroup[] WandererArmy =
		{
			new("Hunter", 400, 1, 64f),
			new("Harvester", 160, 1, 64f),
			new("Builder", 30, 2, 64f),
			new("Tank", 70, 4, 96f),
			new("PlasmaCannon", 5, 20, 160f)
		};

		private int _spawnedTotal = 0;

		protected override int DefaultUnitsPerSide => SupplyPerSide;
		protected override string LogTag => "StressTest2000Mixed";
		protected override int WallClusters => 60;

		protected override void SpawnAndCharge(SimWorld world)
		{
			SpawnArmies(world);
		}

		private async void SpawnArmies(SimWorld world)
		{
			var grid = world.Grid;
			float minX = grid.TerrainMin.X * 64f + 192f;
			float maxX = (grid.TerrainMax.X + 1) * 64f - 192f;
			float minZ = grid.TerrainMin.Y * 64f + 192f;
			float maxZ = (grid.TerrainMax.Y + 1) * 64f - 192f;

			// 固定以地图中心为对称轴，不依赖相机当前位置（开局相机可能已移到出生点）
			float midX = (minX + maxX) * 0.5f;
			float midZ = (minZ + maxZ) * 0.5f;

			// 敌我拉近：联盟在左、多足在右，X 向对阵
			Vector2 friendlyCenter = new Vector2(midX - 1200f, midZ);
			Vector2 enemyCenter = new Vector2(midX + 1200f, midZ);

			if (GetViewport().GetCamera3D() is RTSCamera rtsCam)
				rtsCam.SetViewCenter(new Vector2(midX, midZ + 1750f));

			var unionUnits = new List<Unit>();
			var wandererUnits = new List<Unit>();
			_spawnedTotal = 0;

			await SpawnSide(world, 1, UnionArmy, friendlyCenter, minX, maxX, minZ, maxZ, unionUnits);
			await SpawnSide(world, 2, WandererArmy, enemyCenter, minX, maxX, minZ, maxZ, wandererUnits);

			IssueChargeCommands(world, unionUnits, wandererUnits, friendlyCenter, enemyCenter);

			int totalSupply = 0;
			foreach (var g in UnionArmy) totalSupply += g.Supply * g.Count;
			foreach (var g in WandererArmy) totalSupply += g.Supply * g.Count;

			GD.Print($"[{LogTag}] 混编 {totalSupply} 人口已生成（左联盟 {unionUnits.Count} / 右多足 {wandererUnits.Count}，合计 {unionUnits.Count + wandererUnits.Count} 单位），出生点左 {friendlyCenter} / 右 {enemyCenter}，正在相向冲锋");
		}

		private async System.Threading.Tasks.Task SpawnSide(
			SimWorld world,
			int team,
			UnitGroup[] army,
			Vector2 center,
			float minX,
			float maxX,
			float minZ,
			float maxZ,
			List<Unit> units)
		{
			float bandCursor = 0f;

			// 每个兵种占一条 Z 带，X 向展开成列，避免混编阵型挤成一团
			foreach (var group in army)
			{
				int cols = group.Spacing >= 128f ? 16 : (group.Spacing >= 96f ? 24 : 40);
				int rows = Mathf.CeilToInt(group.Count / (float)cols);
				float bandOffset = bandCursor + (rows - 1) * group.Spacing * 0.5f;
				bandCursor += rows * group.Spacing + 96f;

				for (int i = 0; i < group.Count; i++)
				{
					int col = i % cols;
					int row = i / cols;
					float x = center.X + (col - (cols - 1) * 0.5f) * group.Spacing;
					float z = center.Y + bandOffset - row * group.Spacing;
					x = Mathf.Clamp(x, minX, maxX);
					z = Mathf.Clamp(z, minZ, maxZ);

					var entity = EntitySpawner.Instance.SpawnEntity(group.UnitId, team, new FPVector2((FP)x, (FP)z));
					if (entity is Unit unit)
						units.Add(unit);

					_spawnedTotal++;
					if (_spawnedTotal % BatchSize == 0)
						await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				}
			}
		}
	}
}
