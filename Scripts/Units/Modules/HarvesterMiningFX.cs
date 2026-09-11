using Godot;
using System.Collections.Generic;
using RTS.Core;
using RTS.Data;
using RTS.Simulation;

namespace RTS.Units
{
	// 采集器采矿特效：向周围资源点拉出脉动的能量光束（纯表现层）
	public partial class HarvesterMiningFX : Node3D
	{
		private readonly List<MeshInstance3D> _beams = new();
		private StandardMaterial3D _material;
		private float _radiusWorld = 8f * 64f;
		private float _time = 0f;
		private RTS.Simulation.SimEntity _builderTarget;

		public override void _Ready()
		{
			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure("NanoHarvester");

			if (cfg != null && cfg.AutoHarvestRadiusTiles > 0)
				_radiusWorld = cfg.AutoHarvestRadiusTiles * 64f;

			// Unshaded 下 Emission 被引擎忽略，亮度只能写在 AlbedoColor 上；
			// 逐帧脉动色写在 _Process 里（见文件末尾），这里只定基础外观。
			_material = new StandardMaterial3D
			{
				AlbedoColor = RTS.Units.GlowMaterialFactory.Boost(new Color(0.2f, 0.9f, 0.45f), 4f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled
			};
		}

		public override void _Process(double delta)
		{
			_time += (float)delta;
			RebuildBeams();
		}

		private void RebuildBeams()
		{
			ClearBeams();

			var world = SimManager.Instance?.World;
			Vector3? start = null;

			if (GetParent() is Structure harvester &&
				harvester.CurrentState == Structure.StructureState.Completed &&
				harvester.LogicEntity != null)
			{
				// 采集器是 256 高的立方体，光束从顶部上方出发才不会被本体挡住
				start = new Vector3(harvester.GlobalPosition.X, 265f, harvester.GlobalPosition.Z);
			}
			else if (GetParent() is Unit behemoth &&
				behemoth.UnitName == "NanoBehemoth" &&
				behemoth.LogicEntity is SimUnit simUnit && simUnit.BehemothCanHarvest)
			{
				// 巨兽（先进采集器科技）：从 200 高身体上方拉光束
				start = new Vector3(behemoth.GlobalPosition.X, 210f, behemoth.GlobalPosition.Z);
			}
			else if (GetParent() is Unit builder && builder.LogicEntity is SimUnit builderSim)
			{
				var brain = builder.Brain;
				IEntity target = null;

				// 建造光束：施工中显示；采矿光束：只在真正装载（Harvesting）阶段显示
				if (builderSim.IsConstructing)
					target = brain?.GetActiveBuildTarget();
				else if (brain?.GetAction<RTS.Actions.Implementation.HarvestAction>("Harvest") is { } harvestAct &&
					harvestAct.IsHarvesting && harvestAct.HarvestedThisTick)
					target = brain?.GetActiveHarvestSource();

				if (target?.LogicEntity != null)
				{
					_builderTarget = target.LogicEntity;
					start = new Vector3(builder.GlobalPosition.X, 60f, builder.GlobalPosition.Z);
				}
				else
				{
					_builderTarget = null;
				}
			}

			if (world == null || start == null)
				return;

			float pulse = 0.55f + 0.35f * Mathf.Sin(_time * 6f);
			_material.AlbedoColor = new Color(0.3f, 1f, 0.55f, pulse);

			// 主线程视觉组件遍历 World 必须持 WorldLock，否则和模拟线程
			// 并发修改字典会抛 “Collection was modified” 异常
			lock (RTS.Core.SimManager.Instance.WorldLock)
			{
				foreach (var res in world.Structures.Values)
				{
					if (res.IsDead)
						continue;

					if (SimManager.Instance.FindEntityById(res.ID) is not Node3D node)
						continue;

					Vector3 end = new Vector3(node.GlobalPosition.X, 70f, node.GlobalPosition.Z);

					// 建造/采集单目标模式：只拉向当前目标
					if (_builderTarget != null)
					{
						if (res.ID == _builderTarget.ID)
							AddBeam(start.Value, end);
						continue;
					}

					if (res.ResourceAmount <= FixMath.NET.Fix64.Zero)
						continue;

					// 采集半径按地面平面（XZ）算，不把高度差算进去
					float dx = end.X - start.Value.X;
					float dz = end.Z - start.Value.Z;

					if (dx * dx + dz * dz > _radiusWorld * _radiusWorld)
						continue;

					AddBeam(start.Value, end);
				}
			}
		}

		private void AddBeam(Vector3 start, Vector3 end)
		{
			Vector3 dir = end - start;
			float dist = dir.Length();

			if (dist < 2f)
				return;

			var beam = new MeshInstance3D { Name = "MiningBeam", TopLevel = true };
			beam.Mesh = new SphereMesh
			{
				Radius = 0.5f,
				Height = 1f,
				RadialSegments = 8,
				Rings = 6
			};
			beam.Position = (start + end) * 0.5f;

			Vector3 dirN = dir / dist;
			Vector3 upRef = Mathf.Abs(dirN.Y) > 0.99f ? Vector3.Right : Vector3.Up;
			Vector3 xAxis = upRef.Cross(dirN).Normalized();
			Vector3 zAxis = xAxis.Cross(dirN);
			beam.Basis = new Basis(xAxis, dirN, zAxis);
			beam.Scale = new Vector3(3f, dist, 3f);
			beam.MaterialOverride = _material;

			AddChild(beam);
			_beams.Add(beam);
		}

		private void ClearBeams()
		{
			foreach (var b in _beams)
			{
				if (GodotObject.IsInstanceValid(b))
					b.QueueFree();
			}

			_beams.Clear();
		}
	}
}
