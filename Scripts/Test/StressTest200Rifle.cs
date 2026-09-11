using Godot;
using System;
using System.Collections.Generic;
using RTS.Core;
using RTS.Simulation;
using RTS.Units;
using RTS.World;
using FP = FixMath.NET.Fix64;

namespace RTS.Test
{
	// 压力测试场景：实例化主战斗场景（单机正常开局），
	// 再追加 200 个枪兵（100 vs 100 相向冲锋）和一批随机墙体。
	public partial class StressTest200Rifle : StressTestBase
	{
		private const int UnitsPerSideDefault = 200;

		// 场景导出参数：直接在编辑器中设置每边单位数（如 1000 = 总 2000），不依赖环境变量
		[Export(PropertyHint.Range, "1,1000")]
		public int UnitsPerSideOverride = 0;

		protected override int DefaultUnitsPerSide => UnitsPerSideDefault;
		protected override string LogTag => "StressTest";
		protected override bool SpawnWallsEnabled => true;

		protected override int ResolveUnitsPerSide(int fallback)
		{
			if (UnitsPerSideOverride > 0)
				return Math.Clamp(UnitsPerSideOverride, 1, 1000);
			return base.ResolveUnitsPerSide(fallback);
		}

		protected override async void SpawnAndCharge(SimWorld world)
		{
			var grid = world.Grid;
			float minX = grid.TerrainMin.X * 64f + 192f;
			float maxX = (grid.TerrainMax.X + 1) * 64f - 192f;
			float minZ = grid.TerrainMin.Y * 64f + 192f;
			float maxZ = (grid.TerrainMax.Y + 1) * 64f - 192f;

			// 固定以地图中心为对称轴，不依赖相机当前位置（开局相机可能已移到出生点）
			float midX = (minX + maxX) * 0.5f;
			float midZ = (minZ + maxZ) * 0.5f;

			// 敌我拉近：左右各 1000（20x10 阵型宽度约 1368），很快接战
			Vector2 friendlyCenter = new Vector2(midX - 1000f, midZ);
			Vector2 enemyCenter = new Vector2(midX + 1000f, midZ);

			if (GetViewport().GetCamera3D() is RTSCamera rtsCam)
				rtsCam.SetViewCenter(new Vector2(midX, midZ + 1750f));

			var friendly = new List<Unit>(UnitsPerSide);
			var enemy = new List<Unit>(UnitsPerSide);

			// 分批生成（每帧一批），避免 2000 个单位同一帧实例化重型场景导致开局卡死
			const int batchSize = 100;
			int spawned = 0;
			for (int side = 0; side < 2; side++)
			{
				int team = side + 1;
				Vector2 center = side == 0 ? friendlyCenter : enemyCenter;

				for (int i = 0; i < UnitsPerSide; i++)
				{
					int col = i % 20;
					int row = i / 20;
					float x = center.X + (col - 4.5f) * 72f;
					float z = center.Y + (row - 4.5f) * 72f;
					x = Mathf.Clamp(x, minX, maxX);
					z = Mathf.Clamp(z, minZ, maxZ);

					var entity = EntitySpawner.Instance.SpawnEntity("RifleMan", team, new FPVector2((FP)x, (FP)z));
					if (entity is Unit unit)
						(side == 0 ? friendly : enemy).Add(unit);
					spawned++;
					if (spawned % batchSize == 0)
					{
						IssueChargeCommands(world, friendly, enemy, friendlyCenter, enemyCenter);
						// 回放/确定性测试：同一帧内同步生成，避免跨帧时序导致两次运行生成 tick 不一致
						if (OS.GetEnvironment("REPLAY_DETERMINISTIC") != "1")
							await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
					}
				}
			}

			IssueChargeCommands(world, friendly, enemy, friendlyCenter, enemyCenter);
			GD.Print($"[{LogTag}] 已生成 {friendly.Count + enemy.Count} 枪兵（左 {friendly.Count} / 右 {enemy.Count}），出生点左 {friendlyCenter} / 右 {enemyCenter}，正在相向冲锋");
		}
	}
}
