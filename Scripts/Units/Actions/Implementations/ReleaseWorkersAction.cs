using System;
using Godot;
using RTS.Core;
using RTS.Data;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// 泰伦藻类工厂：把驻扎的工人放出来（有集结点则朝集结点方向出生）
	[GlobalClass]
	public partial class ReleaseWorkersAction : CompletedStructureAbilityAction
	{
		[Export] public string DisplayNameText = "放出工人";
		protected override string ActionId => "ReleaseWorkers";

		public override bool CanExecute()
		{
			return _unit is Structure st &&
				st.SimStructureData != null &&
				st.SimStructureData.GarrisonedCount > 0;
		}

		public override void OnEnter()
		{
			base.OnEnter();

			if (_unit is Structure st &&
				st.SimStructureData != null &&
				st.SimStructureData.GarrisonedCount > 0 &&
				EntitySpawner.Instance != null)
			{
				int count = st.SimStructureData.GarrisonedCount;
				st.SimStructureData.GarrisonedCount = 0;
				st.SimStructureData.MilitiaGarrisonDamage = 0;

				FPVector2 center = st.SimStructureData.Position;
				FPVector2 dir = FPVector2.Zero;

				// 有集结点：朝集结点方向出生
				if (st.RallyQueue.Count > 0)
				{
					FPVector2 target = st.RallyQueue[0].TargetPos;
					FPVector2 d = target - center;
					if (d.X != FP.Zero || d.Y != FP.Zero)
						dir = d.Normalized();
				}

				if (dir.X == FP.Zero && dir.Y == FP.Zero)
					dir = new FPVector2(FP.One, FP.Zero);

				FP offset = (FP)(Math.Max(st.GridSize * 64 / 2, 96) + 40);
				FPVector2 perp = new FPVector2(-dir.Y, dir.X);
				var queue = st.RallyQueue;

				var spawnPositions = new System.Collections.Generic.List<(float x, float y)>(count);
				for (int i = 0; i < count; i++)
				{
					FP side = (FP)(i - (count - 1) / 2f) * (FP)48m;
					FPVector2 pos = center + dir * offset + perp * side;
					spawnPositions.Add(((float)pos.X, (float)pos.Y));
				}

				int spawnTeam = st.TeamID;
				var stRef = st;
				RTS.Core.SimEventQueue.EnqueueMain(() =>
				{
					foreach (var (sx, sy) in spawnPositions)
					{
						var ent = EntitySpawner.Instance?.SpawnEntity(
							"Engineer",
							spawnTeam,
							new RTS.Simulation.FPVector2((FP)sx, (FP)sy));
						if (ent is Unit newUnit && stRef != null && GodotObject.IsInstanceValid(stRef))
						{
							var rallyQueue = stRef.RallyQueue;
							for (int qi = 0; qi < rallyQueue.Count; qi++)
							{
								OrderData order = rallyQueue[qi];
								newUnit.Brain.StartAction(order.ActionName, order.TargetPos, order.TargetObj, false, qi > 0);
							}
						}
					}
				});
			}

			Finish();
		}
	}
}
