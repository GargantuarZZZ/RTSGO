using Godot;
using RTS.UI;

namespace RTS.Core
{
public partial class UserUI
{
    private Control _hudRoot;
    private static void HudRect(Control c, float x, float y, float w, float h)
    {
        if (c == null) return;
        c.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        c.Position = new Vector2(x, y);
        c.Size = new Vector2(w, h);
    }
    private void BuildHudDesign()
    {
        _hudRoot = GetNodeOrNull<Control>("MainControl");
        if (_hudRoot == null) return;
        var matchMenu = new MatchMenu { Name = "MatchMenu" };
        AddChild(matchMenu);
        var esc = new Button { Name = "EscButton", Text = "ESC", TooltipText = RTS.Settings.Localization.Tr("match.menu") };
        _hudRoot.AddChild(esc);
        esc.Pressed += matchMenu.Open;
        _hudRoot.Theme = HudTheme.Create();
        HudTheme.ApplyTree(_hudRoot);
        // The unified background must render behind the skill buttons, not cover them.
        _hudRoot.MoveChild(_hudRoot.GetNode<Control>("TopBar"), 0);
        var bottom = _hudRoot.GetNode<Panel>("BottomPanel");
        bottom.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        ResourceContainer.Reparent(bottom);
        var resourceFrame = new Panel { Name = "ResourceFrame", MouseFilter = Control.MouseFilterEnum.Ignore };
        bottom.AddChild(resourceFrame);
        bottom.MoveChild(resourceFrame, 0);
        bottom.GetNode<Control>("ProductionQueue").AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        bottom.GetNode<Control>("SelectionInfoPanel").AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        var mapFrame = new Panel { Name = "MiniMapFrame", MouseFilter = Control.MouseFilterEnum.Ignore };
        bottom.AddChild(mapFrame);
        bottom.MoveChild(mapFrame, 0);
        if (bottom.GetNodeOrNull<RTS.UI.Minimap>("MiniMap/MiniMap2") is { } map)
        {
            map.WalkableColor = new Color("36534d");
            map.WallColor = new Color("809297");
            map.CameraBoxColor = HudTheme.Accent;
        }
        foreach (var section in new[] { ("ActionPanel", "hud.commands"), ("SelectionInfoPanel", "hud.selection"), ("ProductionQueue", "hud.production") })
        {
            var frame = new Panel { Name = section.Item1 + "Frame", MouseFilter = Control.MouseFilterEnum.Ignore };
            bottom.AddChild(frame);
            bottom.MoveChild(frame, 0);
            var label = new Label { Name = section.Item1 + "Caption", Text = RTS.Settings.Localization.Tr(section.Item2),
                MouseFilter = Control.MouseFilterEnum.Ignore };
            label.SetMeta("tr_key", section.Item2);
            label.AddThemeColorOverride("font_color", HudTheme.Accent);
            label.AddThemeFontSizeOverride("font_size", 11);
            bottom.AddChild(label);
        }
        _hudRoot.Resized += LayoutHudDesign;
        if (_timeLabel != null)
        {
            _timeLabel.LabelSettings = new LabelSettings { FontSize = 15, FontColor = HudTheme.Accent };
            _timeLabel.Position = new Vector2(16, 8);
        }
        if (SelectionInfoPanel.Instance is { } info)
        {
            info.PortraitSize = new Vector2(36, 36);
            if (info.StatsLabel != null) info.StatsLabel.CustomMinimumSize = new Vector2(0, 40);
            info.HpBar?.AddThemeStyleboxOverride("fill", HudTheme.Surface("67e8c0", "67e8c0", 0));
            info.ShieldBar?.AddThemeStyleboxOverride("fill", HudTheme.Surface("73bfff", "73bfff", 0));
            info.SecondaryBar?.AddThemeStyleboxOverride("fill", HudTheme.Surface("e4bc73", "e4bc73", 0));
        }
        Callable.From(LayoutHudDesign).CallDeferred();
    }
    private void LayoutHudDesign()
    {
        if (_hudRoot == null) return;
        float w = _hudRoot.Size.X, h = _hudRoot.Size.Y;
        HudRect(_hudRoot.GetNode<Control>("TopBar"), 0, 0, w, 40);
        HudRect(_hudRoot.GetNode<Control>("EscButton"), w - 64, 4, 60, 32);
        HudRect(ResourceContainer, w - 364, h - 148, 200, 64);
        if (ResourceContainer is BoxContainer resources) resources.Alignment = BoxContainer.AlignmentMode.Begin;
        ResourceContainer?.AddThemeConstantOverride("separation", 2);
        foreach (Node child in _hudRoot.GetChildren())
            if (child is Control c && c.Name.ToString().StartsWith("RacePanel_"))
            {
                c.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
                HudRect(c, 104, 4, w - 180, 32);
            }
        var bottom = _hudRoot.GetNode<Control>("BottomPanel");
        float y = h - 156;
        HudRect(bottom.GetNode<Control>("ActionPanelFrame"), 0, y, 224, 156);
        HudRect(bottom.GetNode<Control>("SelectionInfoPanelFrame"), 224, y, w - 604, 156);
        HudRect(bottom.GetNode<Control>("ResourceFrame"), w - 380, y, 224, 80);
        HudRect(bottom.GetNode<Control>("ProductionQueueFrame"), w - 380, h - 76, 224, 76);
        HudRect(bottom.GetNode<Control>("ActionPanel"), 0, y + 16, 224, 140);
        HudRect(bottom.GetNode<Control>("SelectionInfoPanel"), 232, y + 16, w - 620, 140);
        HudRect(bottom.GetNode<Control>("ProductionQueue"), w - 376, h - 56, 216, 56);
        HudRect(bottom.GetNode<Control>("MiniMap"), w - 156, y, 156, 156);
        HudRect(bottom.GetNode<Control>("MiniMapFrame"), w - 156, y, 156, 156);
        HudRect(bottom.GetNode<Control>("ActionPanelCaption"), 8, y, 208, 16);
        HudRect(bottom.GetNode<Control>("SelectionInfoPanelCaption"), 232, y, w - 620, 16);
        HudRect(bottom.GetNode<Control>("ProductionQueueCaption"), w - 372, h - 76, 208, 16);
        bottom.GetNodeOrNull<Control>("InfoPanel")?.Hide();
        if (bottom.GetNodeOrNull<GridContainer>("ProductionQueue/HBoxContainer") is { } queue) queue.Columns = 6;
    }
    private void RefreshHudVisibility()
    {
        if (_hudRoot == null) return;
        var bottom = _hudRoot.GetNode<Control>("BottomPanel");
        bool selected = SelectionInfoPanel.Instance is { } info &&
            (info.SingleContainer.Visible || info.MultiContainer.Visible);
        bool producing = bottom.GetNode<ProductionQueueUI>("ProductionQueue") is { } production &&
            production.Visible && production.IconContainer.GetChildCount() > 0;
        bottom.GetNode<Control>("SelectionInfoPanelFrame").Visible = true;
        bottom.GetNode<Control>("SelectionInfoPanelCaption").Visible = selected;
        bottom.GetNode<Control>("ProductionQueueFrame").Visible = true;
        bottom.GetNode<Control>("ProductionQueueCaption").Visible = producing;
    }
}
}
