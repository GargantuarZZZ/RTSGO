using System;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace SimulationDeterminismTest
{
    internal static partial class Program
    {
        // Diagnostic only: reproduce the current AI's command stream without Godot or combat.
        private static int RunWallProbe(bool regression = false)
        {
            foreach (string shape in new[] { "thin-wall", "thick-wall", "diagonal-corner" })
            foreach (string type in new[] { "Shepherd", "RifleMan" })
            foreach (int radius in new[] { 16, 32, 64 })
            foreach (bool hops in new[] { false, true })
            {
                if (regression && hops) continue; // The removed three-tile policy is retained only as a diagnostic control.
                var w = new SimWorld();
                for (int x = -20; x <= 20; x++)
                    for (int y = -20; y <= 20; y++) w.Grid.TerrainCells.Add(new SimVector2I(x,y));
                if (shape == "diagonal-corner")
                {
                    w.Grid.StaticObstacles.Add(new SimVector2I(0,1));
                    w.Grid.StaticObstacles.Add(new SimVector2I(1,0));
                }
                else
                    for (int x = 0; x < (shape == "thick-wall" ? 6 : 1); x++)
                        for (int y = -6; y <= 6; y++) w.Grid.StaticObstacles.Add(new SimVector2I(x,y));
                w.Grid.RefreshTerrainBounds();
                w.Grid.SyncStaticBlocking();
                var u = new SimUnit(1,1,new FPVector2((FP)(-320),(FP)32))
                    { UnitTypeId=type, Radius=(FP)radius, MaxSpeed=(FP)83 };
                if (shape == "diagonal-corner") u.Position = new FPVector2((FP)32,(FP)32);
                w.AddUnit(u);
                var goal = shape == "diagonal-corner" ? new FPVector2((FP)224,(FP)224) : new FPVector2((FP)800,(FP)32);
                FP maxY = FP.Zero;
                for (int t = 0; t < 1800; t++)
                {
                    if (t % 30 == 0 && !FPVector2.IsWithinRange(u.Position,goal,(FP)20))
                        u.CommandMove(hops ? u.Position+(goal-u.Position).Normalized()*(FP)192 : goal);
                    w.Tick();
                    if (FP.Abs(u.Position.Y) > maxY) maxY = FP.Abs(u.Position.Y);
                }
                Console.WriteLine($"WALL_PROBE shape={shape} type={type} radius={radius} command={(hops?"three-tile-hop":"stable-goal")} pos=({(int)u.Position.X},{(int)u.Position.Y}) distance={(int)(goal-u.Position).Magnitude()} maxY={(int)maxY} waypoint={u.CurrentWaypointIndex}/{u.Path?.Count} active={u.HasTarget}");
                if (regression) Check(FPVector2.IsWithinRange(u.Position,goal,(FP)20),$"{type} radius {radius} reaches goal around {shape}");
            }
            return 0;
        }
    }
}
