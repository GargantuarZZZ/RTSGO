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

            // 面板键位 = 键盘上那块矩形：QWERT / ASDFG / ZXCVB。
            // 期望字母在这里**手写**（而不是引用 InputActions 自己的常量），
            // 这样任何一个槽的字母被改错都会失败 ——
            // "面板快捷键全是错的"就是这类错字，以前没有任何断言守着。
            var grid = new[]
            {
                Key.Q, Key.W, Key.E, Key.R, Key.T,
                Key.A, Key.S, Key.D, Key.F, Key.G,
                Key.Z, Key.X, Key.C, Key.V, Key.B,
            };
            Check(InputActions.PanelGridKeys.Length == grid.Length, "Panel grid must declare 15 keys");
            for (int i = 0; i < grid.Length; i++)
            {
                Check(InputActions.PanelGridKeys[i] == grid[i],
                    $"Panel grid key {i + 1} is {InputActions.PanelGridKeys[i]}, expected {grid[i]}");
                var slot = InputActions.Get($"panel_slot_{i + 1}");
                Check(slot != null && slot.Default == grid[i],
                    $"panel_slot_{i + 1} default is {slot?.Default}, expected {grid[i]}");
            }
            Check(InputActions.PanelGridProblems().Count == 0, "Panel slot keys drifted from the grid");

            // 面板矩形与全局动作不许重叠：重合时同一个键既放技能又下指令。
            foreach (var def in InputActions.All)
            {
                if (def.Group == InputGroup.Panel || def.Default == Key.None) continue;
                Check(Array.IndexOf(grid, def.Default) < 0,
                    $"Global action {def.Action} uses panel key {def.Default}");
            }

            // 老存档迁移：仍然是旧默认值的项升级到新布局，玩家改过的一律不动。
            var legacy = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<InputBinding>>
            {
                ["game_attack_move"] = new() { InputBinding.Key(Key.A) },
                ["game_chat"] = new() { InputBinding.Key(Key.T), InputBinding.Key(Key.Enter) },
                ["panel_slot_6"] = new() { InputBinding.Key(Key.Z) },
                ["game_stop"] = new() { InputBinding.Key(Key.L) }, // 玩家自己改过的
            };
            int migrated = InputActions.MigrateLegacyDefaults(legacy);
            Check(legacy["game_attack_move"][0].AsKey == Key.H, "Legacy attack-move was not migrated to H");
            Check(legacy["game_chat"].Count == 2 && legacy["game_chat"][0].AsKey == Key.Y,
                "Legacy chat primary was not migrated to Y");
            Check(legacy["panel_slot_6"][0].AsKey == Key.A, "Legacy panel slot 6 was not migrated onto the grid");
            Check(legacy["game_stop"][0].AsKey == Key.L, "Player-customised bind must survive migration");
            Check(migrated == 3, $"Expected 3 migrated actions, got {migrated}");

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
