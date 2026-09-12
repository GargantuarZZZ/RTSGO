using Godot;
using System;
using System.Linq;
using RTS.UI;
using RTS.Core;
using RTS.Settings;
using RTS.Network;
namespace RTS.Test;
public partial class MenuFlowTest : Node
{
    private async System.Threading.Tasks.Task Frames(int n = 3)
    { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static Button Find(Node root, string text) => root.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text == Localization.Tr(text));
    private static void Press(Node root, string text) => Find(root, text).EmitSignal(BaseButton.SignalName.Pressed);
    public override void _Ready() => Callable.From(Run).CallDeferred();
    private async void Run()
    {
        try
        {
            // Regression: legacy saves and resetting to an unbound key must erase InputMap events.
            foreach (var def in InputActions.InGroup(InputGroup.Selection))
            {
                Check(def.Default == Key.None, "Selection shortcut has a default binding");
                var saved = GameSettings.Keybinds[def.Action];
                GameSettings.Keybinds[def.Action] = Key.F1;
                if (!InputMap.HasAction(def.Action)) InputMap.AddAction(def.Action);
                InputMap.ActionAddEvent(def.Action, new InputEventKey { PhysicalKeycode = Key.F1 });
                GameSettings.ApplyKeybinds();
                Check(GameSettings.Keybinds[def.Action] == Key.None && InputMap.ActionGetEvents(def.Action).Count == 0, "Legacy selection key still active");
                GameSettings.Keybinds[def.Action] = saved;
            }
            GameSettings.ApplyKeybinds();
            Check(InputActions.FindDefaultConflicts().Count == 0, "Default shortcut conflict");
            // Keep the test runner outside CurrentScene so the real return transition can finish.
            GetTree().CurrentScene = null;
            MainMenuController.ReturningFromMatch = true;
            var main = GD.Load<PackedScene>("res://Scenes/Menu/UI_MainMenu.tscn").Instantiate();
            GetTree().Root.AddChild(main);
            GetTree().CurrentScene = main;
            await Frames();
            Press(main, "settings");
            await Frames();
            Check(SettingsMenu.IsOpen, "Main menu settings button failed");
            var settings = SettingsMenu.Instance;
            foreach (var size in new[] { new Vector2I(1152,648), new Vector2I(960,540) })
            {
                GetTree().Root.Size = size;
                await Frames();
                Press(settings, "keybinds");
                await Frames();
                var save = Find(settings, "save");
                Check(save.GetGlobalRect().End.Y <= settings.Size.Y && save.IsVisibleInTree(), "Settings footer outside viewport");
                Check(settings.Size.X > 900 && settings.Size.Y >= 540, "Settings has collapsed bounds");
            }
            if (DisplayServer.GetName() != "headless") GetViewport().GetTexture().GetImage().SavePng("res://tmp/settings_keys.png");
            settings._Input(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
            await Frames();
            Check(!SettingsMenu.IsOpen, "Settings escape did not close");
            main.QueueFree();
            await Frames();
            var game = GD.Load<PackedScene>("res://Scenes/UI/user_ui.tscn").Instantiate();
            GetTree().Root.AddChild(game);
            GetTree().CurrentScene = game;
            NetworkManager.OfflineMode = true;
            await Frames();
            game.GetNode<Button>("MainControl/EscButton").EmitSignal(BaseButton.SignalName.Pressed);
            Check(MatchMenu.BlocksGameInput, "HUD escape button failed");
            var menu = MatchMenu.Instance;
            Press(menu, "match.pause");
            Check(SimManager.Instance.IsPaused, "Pause failed");
            Press(menu, "settings"); await Frames();
            Check(SettingsMenu.IsOpen, "In-game settings failed");
            SettingsMenu.Instance.QueueFree(); await Frames();
            Check(MatchMenu.BlocksGameInput && SimManager.Instance.IsPaused, "Nested settings lost menu or pause");
            Press(menu, "match.resume");
            Check(!SimManager.Instance.IsPaused && !MatchMenu.BlocksGameInput, "Resume failed");
            menu._Input(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await Frames();
            if (DisplayServer.GetName() != "headless") GetViewport().GetTexture().GetImage().SavePng("res://tmp/match_menu.png");
            NetworkManager.OfflineMode = false;
            menu.Open();
            Check(Find(menu, "match.pause").Disabled, "Online local pause must be disabled");
            NetworkManager.OfflineMode = true;
            menu.Open();
            Press(menu, "match.surrender");
            Check(Find(menu, "match.confirm") != null, "Surrender confirmation missing");
            Press(menu, "back");
            Press(menu, "match.return");
            Press(menu, "match.confirm");
            await Frames(10);
            Check(GetTree().CurrentScene is MainMenuController, "Return did not reach main menu");
            Check(!SettingsMenu.IsOpen && !MatchMenu.BlocksGameInput && !SimManager.Instance.IsPaused, "Modal/pause leaked on exit");
            Press(GetTree().CurrentScene, "settings"); await Frames();
            Check(SettingsMenu.IsOpen, "Settings failed after return");
            GD.Print("MENU_FLOW_PASS");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PrintErr(e.ToString()); GetTree().Quit(1); }
    }
}
