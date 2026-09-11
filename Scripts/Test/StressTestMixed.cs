using Godot;
using System.Collections.Generic;
using RTS.Core;
using RTS.Simulation;
using RTS.Units;
using RTS.World;
using FP = FixMath.NET.Fix64;

namespace RTS.Test
{
	// P1-2 混合种族压测：2000 单位（多族混编）确定性生成 + 回放可验证。
	// 用 REPLAY_DETERMINISTIC=1 同步生成；RECORD_REPLAY/REPLAY_FILE 支持回放录制与校验。
	public partial class StressTestMixed : StressTestBase
	{
		private const int UnitsPerSideDefault = 1000;

		// 混编单位池：每边按序循环出这些种族/兵种，覆盖步兵/重装/飞行/英雄
		private static readonly string[] UnitPool =
		{
			"RifleMan", "RocketMan", "Medic", "Biped", "Hp",
			"Marine", "HeavyInfantry", "CommandVehicle", "Fighter", "MissileVehicle",
			"DemonDog", "DemonFlyer", "HeavyTank", "FireDragon", "HellLord",
			"Hunter", "Harvester", "Tank", "PlasmaCannon", "NanoBehemoth"
		};

		protected override int DefaultUnitsPerSide => UnitsPerSideDefault;
		protected override string LogTag => "StressTestMixed";

		protected override void SpawnAndCharge(SimWorld world)
		{
			var grid = world.Grid;
			float minX = grid.TerrainMin.X * 64f + 192f;
			float maxX = (grid.TerrainMax.X + 1) * 64f - 192f;
			float minZ = grid.TerrainMin.Y * 64f + 192f;
			float maxZ = (grid.TerrainMax.Y + 1) * 64f - 192f;
			float midX = (minX + maxX) * 0.5f;
			float midZ = (minZ + maxZ) * 0.5f;
			Vector2 friendlyCenter = new Vector2(midX - 1000f, midZ);
			Vector2 enemyCenter = new Vector2(midX + 1000f, midZ);

			var friendly = new List<Unit>(UnitsPerSide);
			var enemy = new List<Unit>(UnitsPerSide);

			for (int side = 0; side < 2; side++)
			{
				int team = side + 1;
				Vector2 center = side == 0 ? friendlyCenter : enemyCenter;
				for (int i = 0; i < UnitsPerSide; i++)
				{
					int col = i % 20;
					int row = i / 20;
					float x = Mathf.Clamp(center.X + (col - 4.5f) * 72f, minX, maxX);
					float z = Mathf.Clamp(center.Y + (row - 4.5f) * 72f, minZ, maxZ);
					string unitName = UnitPool[i % UnitPool.Length];
					var entity = EntitySpawner.Instance.SpawnEntity(unitName, team, new FPVector2((FP)x, (FP)z));
					if (entity is Unit unit)
						(side == 0 ? friendly : enemy).Add(unit);
				}
			}

			IssueChargeCommands(world, friendly, enemy, friendlyCenter, enemyCenter);
			GD.Print($"[{LogTag}] 已生成 {friendly.Count + enemy.Count} 混编单位（左 {friendly.Count} / 右 {enemy.Count}）");
		}
	}
}
