using Godot;
using RTS.Data.Configs;
using RTS.Data;
using RTS.Actions.Implementation;
using RTS.Network;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Test
{
public partial class RaceAISmokeTest
{
    private void TestPlasmaAutoFire()
    {
        var cannon = (SimUnit)Spawn("PlasmaCannon", 7, -5000, 5000).LogicEntity;
        var cfg = ConfigDatabase.GetUnit("PlasmaCannon");
        var enemy = (SimUnit)Spawn("Marine", 2, -4600, 5000).LogicEntity;
        Check(cannon.PlasmaAutoFire, "new Plasma Cannon enables auto cast without a bot-only command");
        Call("TickPlasmaUnit", cannon, cfg);
        Check(cannon.PlasmaCastState == 1 && cannon.PlasmaTargetEntityId == enemy.ID,
            "Plasma auto cast acquires an enemy unit");
        int shells = _sim.World.Projectiles.Count;
        for (int i = 0; i < 160; i++) Call("TickPlasmaUnit", cannon, cfg);
        Check(cannon.PlasmaCastState == 2 && _sim.World.Projectiles.Count == shells + 1,
            "Plasma windup emits exactly one real shell");
        for (int i = 0; i < 100; i++) Call("TickPlasmaUnit", cannon, cfg);
        Check(cannon.PlasmaCastState == 0 && cannon.PlasmaCooldown > FP.Zero,
            "Plasma recovery enters cooldown");
        var mode = NetAction.GroundCommand("PlasmaMode", 7, Vector2.Zero, new[] { cannon.ID });
        Call("HandlePlasmaMode", mode);
        cannon.PlasmaCooldown = FP.Zero;
        Call("TickPlasmaUnit", cannon, cfg);
        Check(!cannon.PlasmaAutoFire && cannon.PlasmaCastState == 0, "manual mode suppresses automatic fire");
        enemy.Position = new FPVector2((FP)5000, (FP)5000);
        var building = (SimStructure)Spawn("FrontlineCamp", 2, -4400, 5000).LogicEntity;
        Call("HandlePlasmaMode", mode);
        Call("TickPlasmaUnit", cannon, cfg);
        Check(cannon.PlasmaCastState == 1 && cannon.PlasmaTargetEntityId == building.ID,
            "reenabling auto mode fires when only an enemy building is in range");
        cannon.PlasmaCastState = 0;
        building.Position = new FPVector2((FP)5000, (FP)5000);
        Spawn("PlasmaCannon", 7, -4600, 5000);
        Call("TickPlasmaUnit", cannon, cfg);
        Check(cannon.PlasmaCastState == 0, "auto cast ignores friendly units and out-of-range enemies");
        var tower = (SimStructure)Spawn("Tower", -2, -4400, 5000).LogicEntity;
        Call("TickPlasmaUnit", cannon, cfg);
        Check(cannon.PlasmaTargetEntityId == tower.ID && cannon.PlasmaCastState == 1,
            "auto cast can clear hostile neutral towers");
        TestModeButtonStates();
    }

    private void TestModeButtonStates()
    {
        var plasma = Spawn("PlasmaCannon", 7, -5000, 3000);
        var unit = (SimUnit)plasma.LogicEntity;
        var mode = plasma.Brain.GetAction<PlasmaModeAction>("PlasmaMode");
        var on = ActionViewFactory.Build(mode);
        unit.PlasmaAutoFire = false;
        var off = ActionViewFactory.Build(mode);
        Check(on.StatusText.Length > 0 && on.StatusText != off.StatusText, "mode button follows actual auto-fire state");
        var infantry = Spawn("HeavyInfantry", 2, -4500, 3000);
        var ammo = infantry.Brain.GetAction<SwitchAmmoAction>("SwitchAmmo");
        string first = ActionViewFactory.Build(ammo).StatusText;
        infantry.CombatModule.CycleActiveWeapon();
        Check(first.Length > 0 && first != ActionViewFactory.Build(ammo).StatusText, "weapon button shows the current weapon after switching");
        var deploy = new DeployAction();
        deploy.Initialize(plasma);
        var states = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < 4; i++) { unit.DeployState = i; states.Add(ActionViewFactory.Build(deploy).StatusText); }
        Check(states.Count == 4, "deploy button distinguishes mobile, deploying, deployed and packing states");
        deploy.Free();
        unit.DeployState = 0;
        var panel = new RTS.Core.ActionPanel();
        var button = new Button();
        var update = typeof(RTS.Core.ActionPanel).GetMethod("UpdateButton", Private);
        update.Invoke(panel, new object[] { button, on, 0, null });
        Check(button.GetNode<Label>("StatusLabel").Visible && button.GetNode<Label>("StatusLabel").Text == on.StatusText,
            "current state is rendered on the button itself");
        update.Invoke(panel, new object[] { button, off, 0, null });
        Check(button.GetNode<Label>("StatusLabel").Text == off.StatusText, "button state updates without reselection");
        update.Invoke(panel, new object[] { button, null, 0, null });
        Check(!button.GetNode<Label>("StatusLabel").Visible, "empty slots clear previous state labels");
        button.Free();
        panel.Free();
    }
}
}
