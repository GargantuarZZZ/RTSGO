using Godot;
using System;
using System.Reflection;

namespace RTS.Test;
public partial class MinimapLayoutTest : Node
{
    public override async void _Ready()
    {
        try
        {
            var ui = GD.Load<PackedScene>("res://Scenes/UI/user_ui.tscn").Instantiate();
            AddChild(ui);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var map = ui.GetNode<RTS.UI.Minimap>("MainControl/BottomPanel/MiniMap/MiniMap2");
            var layer = map.GetNode<Control>("EntityLayer");
            var init = typeof(RTS.UI.Minimap).GetMethod("InitMapBounds", BindingFlags.Instance | BindingFlags.NonPublic);
            var forward = typeof(RTS.UI.Minimap).GetMethod("WorldToMinimap", BindingFlags.Instance | BindingFlags.NonPublic);
            var reverse = typeof(RTS.UI.Minimap).GetMethod("MinimapToWorld", BindingFlags.Instance | BindingFlags.NonPublic);
            map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
            foreach (var size in new[] { new Vector2(156,156), new Vector2(240,160), new Vector2(160,240) })
            {
                map.Position = new Vector2(12, 8);
                map.Size = size;
                init.Invoke(map, null);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!layer.GlobalPosition.IsEqualApprox(map.GlobalPosition) || !layer.Size.IsEqualApprox(map.Size))
                    throw new Exception("Minimap entity layer is shifted by theme/container margins: " + size);
                foreach (var world in new[] { Vector2.Zero, new Vector2(-1000,500), new Vector2(1500,-1200) })
                {
                    var pixel = (Vector2)forward.Invoke(map, new object[] { world });
                    var restored = (Vector2)reverse.Invoke(map, new object[] { pixel });
                    if (restored.DistanceTo(world) > 0.01f)
                        throw new Exception("Minimap click projection differs from marker projection");
                }
            }
            GD.Print("MINIMAP_LAYOUT_PASS");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PrintErr("MINIMAP_LAYOUT_FAIL: " + e); GetTree().Quit(1); }
    }
}
