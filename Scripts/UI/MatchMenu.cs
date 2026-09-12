using Godot;
using RTS.Core;
using RTS.Network;
using RTS.Settings;

namespace RTS.UI;

// Modal UI stays responsive while only the deterministic simulation is paused.
public partial class MatchMenu : CanvasLayer
{
    public static MatchMenu Instance { get; private set; }
    public static bool BlocksGameInput => SettingsMenu.IsOpen || (GodotObject.IsInstanceValid(Instance) && Instance._screen?.Visible == true);
    public static bool CanPause => NetworkManager.Instance == null || NetworkManager.OfflineMode || (LockstepManager.Instance?.ReplayMode ?? false);
    private Control _screen;
    private VBoxContainer _items;
    private bool _leaving;
    private static string T(string key) => Localization.Tr(key);

    public override void _Ready()
    {
        Instance = this;
        Layer = 180;
        _screen = new Control { Name = "MatchMenuScreen", Theme = HudTheme.Create(), Visible = false };
        _screen.Theme.DefaultFontSize = 16;
        AddChild(_screen);
        _screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color("08151cd9") };
        _screen.AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var center = new CenterContainer();
        _screen.AddChild(center);
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(360, 0) };
        panel.AddThemeStyleboxOverride("panel", HudTheme.Surface("10212aff", "67e8c0", 24));
        center.AddChild(panel);
        _items = new VBoxContainer();
        _items.AddThemeConstantOverride("separation", 10);
        panel.AddChild(_items);
    }

    private void Button(string key, System.Action action, bool disabled = false)
    {
        var button = new Button { Name = key.Replace('.', '_'), Text = T(key), CustomMinimumSize = new Vector2(0, 40), Disabled = disabled };
        button.Pressed += action;
        _items.AddChild(button);
    }
    private void Clear(string title)
    {
        foreach (Node child in _items.GetChildren()) { _items.RemoveChild(child); child.QueueFree(); }
        var label = new Label { Text = T(title), HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 24);
        _items.AddChild(label);
    }
    public void Open()
    {
        if (_leaving || SettingsMenu.IsOpen) return;
        _screen.Show();
        Clear("match.menu");
        Button("match.resume", Close);
        Button(SimManager.Instance?.IsPaused == true ? "match.unpause" : "match.pause", () => {
            if (CanPause && SimManager.Instance is { } sim) sim.SetPaused(!sim.IsPaused);
            Open();
        }, !CanPause);
        if (!CanPause) _items.AddChild(new Label { Text = T("match.online_pause"), HorizontalAlignment = HorizontalAlignment.Center });
        Button("settings", () => SettingsMenu.Open(this));
        Button("match.surrender", () => Confirm("match.surrender_confirm", Surrender),
            NetworkManager.Instance?.LocalIsObserver == true || LockstepManager.Instance == null || LockstepManager.Instance.ReplayMode);
        Button("match.return", () => Confirm("match.return_confirm", ReturnToMenu));
    }
    private void Confirm(string message, System.Action action)
    {
        Clear(message);
        Button("match.confirm", action);
        Button("back", Open);
    }
    public void Close()
    {
        if (CanPause) SimManager.Instance?.SetPaused(false);
        _screen.Hide();
    }
    private void Surrender()
    {
        if (CanPause) SimManager.Instance?.SetPaused(false);
        if (LockstepManager.Instance is { } lockstep)
            lockstep.SendAction(NetAction.GroundCommand("Surrender", lockstep.LocalPlayerID, Vector2.Zero));
        Close();
    }
    private void ReturnToMenu()
    {
        if (_leaving) return;
        _leaving = true;
        // Reset stops and joins the simulation thread before scene nodes are released.
        NetworkManager.Instance?.UnlockSessionForLobby();
        if (NetworkManager.Instance == null) SimManager.Instance?.ResetSimulation();
        SimManager.Instance?.SetPaused(false);
        GetNodeOrNull<Node>("/root/SteamService")?.Call("leave_lobby");
        MainMenuController.ReturningFromMatch = true;
        GetTree().ChangeSceneToFile("res://Scenes/Menu/UI_MainMenu.tscn");
    }
    public override void _Input(InputEvent @event)
    {
        if (SettingsMenu.IsOpen || UserUI.ChatOpen) return;
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
        {
            if (_screen.Visible) Close(); else Open();
            GetViewport().SetInputAsHandled();
        }
    }
    public override void _UnhandledInput(InputEvent @event)
    {
        if (_screen.Visible) GetViewport().SetInputAsHandled();
    }
    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }
}
