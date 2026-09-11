using RTS.Core;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Test
{
public partial class RaceAISmokeTest
{
    private void TestResourceDepletion()
    {
        Call("DrainSimEvents");
        var resource = (ResourceStructure)Spawn("IronOre", -1, 5500, -5500);
        resource.LogicEntity.ResourceAmount = FP.Zero;
        resource.ResModule.Tick(0.05);
        Call("DrainSimEvents");
        Check(resource.LogicEntity.IsDead, "resource depletion signal destroys the logical resource");
        Check(!(bool)typeof(Structure).GetField("_deathVisualStarted", Private).GetValue(resource),
            "depletion queues visual work instead of starting it inside destruction");
        resource.ResModule.NotifyDepleted();
        resource.OnDestroyed();
        int actions = 0;
        while (SimEventQueue.TryDequeueMain(out var action)) { actions++; action(); }
        Check(actions == 2, "repeated depletion/death creates only one visual dispatch and one death animation");
        Check((bool)typeof(Structure).GetField("_deathVisualStarted", Private).GetValue(resource),
            "resource death animation starts at the main-thread dispatch barrier");
    }
}
}
