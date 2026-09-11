// File: res://Scripts/Units/Actions/Implementations/MoveAction.cs
using Godot;
using RTS.Simulation;
using RTS.Data;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	[GlobalClass]
	public partial class MoveAction : UnitAction
	{
		public FPVector2 TargetPosFP;

		public override void Setup(RTS.Simulation.FPVector2 targetPos, IEntity targetObj) { TargetPosFP = targetPos; }

		public override void OnEnter()
		{
			base.OnEnter();
			if (_unit.LogicEntity is SimUnit simUnit)
			{
#if DEBUG
				FP d0 = FP.Sqrt(FPVector2.DistanceSquared(simUnit.Position, TargetPosFP));
				if (d0 > (FP)640m)
					GD.Print($"[MoveDebug] MoveAction开始 {_unit.DisplayName} id={simUnit.ID} t={simUnit.TeamID} 指令={simUnit.ActiveActionName} 目标=({(long)(TargetPosFP.X * (FP)1000m)},{(long)(TargetPosFP.Y * (FP)1000m)}) 位置=({(long)(simUnit.Position.X * (FP)1000m)},{(long)(simUnit.Position.Y * (FP)1000m)}) 距={d0 / (FP)64m:F1}格");
#endif
				simUnit.CommandMove(TargetPosFP);
			}
		}

		public override void OnUpdate(double delta)
		{
			if (_unit.LogicEntity is SimUnit simUnit)
			{
				if (!simUnit.HasTarget)
				{
					// 只有真正到达目标（1.5 格内）才结束；否则（路径截断/被打断）
					// 立即从当前位置重发移动令，保证长距离行军连续，而不是走一步就停
					bool atTarget = FPVector2.DistanceSquared(simUnit.Position, TargetPosFP) <= (FP)(96 * 96);
					if (atTarget)
					{
#if DEBUG
						FP distDbg = FP.Sqrt(FPVector2.DistanceSquared(simUnit.Position, TargetPosFP));
						if (distDbg > (FP)192m)
							GD.Print($"[MoveDebug] MoveAction早停 {_unit.DisplayName} id={simUnit.ID} t={simUnit.TeamID} 指令={simUnit.ActiveActionName} 目标=({(long)(TargetPosFP.X * (FP)1000m)},{(long)(TargetPosFP.Y * (FP)1000m)}) 逻辑终点=({(long)(simUnit.FinalTargetPosition.X * (FP)1000m)},{(long)(simUnit.FinalTargetPosition.Y * (FP)1000m)}) 位置=({(long)(simUnit.Position.X * (FP)1000m)},{(long)(simUnit.Position.Y * (FP)1000m)}) 距={distDbg / (FP)64m:F1}格 path={simUnit.Path?.Count ?? 0} wp={simUnit.CurrentWaypointIndex} pending={simUnit.PathPending} combat={simUnit.CombatTargetId} deploy={simUnit.DeployState}");
#endif
						Finish();
					}
					else
					{
						simUnit.CommandMove(TargetPosFP);
					}
				}
			}
			else Finish();
		}
	}
}
