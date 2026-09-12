using Godot;
using RTS.UI;

public partial class MainMenuController
{
    private static readonly Color MenuAccent = new("67e8c0");
    private Panel _lobbySurface;
    private Label _menuKicker;
    private Label _menuCaption;

    private static StyleBoxFlat MenuBox(string fill, string border, int radius = 6)
    {
        return new StyleBoxFlat { BgColor = new Color(fill), BorderColor = new Color(border),
            BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
            ContentMarginLeft = UiLayout.Space2, ContentMarginRight = UiLayout.Space2,
            ContentMarginTop = UiLayout.Space1, ContentMarginBottom = UiLayout.Space1 };
    }

    private void ApplyMenuDesign()
    {
        var theme = new Theme { DefaultFontSize = UiLayout.FontBody };
        foreach (string type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, MenuBox("172a34", "30434c"));
            theme.SetStylebox("hover", type, MenuBox("243f48", "67e8c0"));
            theme.SetStylebox("pressed", type, MenuBox("28554e", "67e8c0"));
            theme.SetStylebox("disabled", type, MenuBox("142129", "26353d"));
            var focus = MenuBox("00000000", "a6ffe0");
            theme.SetStylebox("focus", type, focus);
            theme.SetColor("font_color", type, new Color("e2eee9"));
            theme.SetColor("font_hover_color", type, Colors.White);
            theme.SetColor("font_disabled_color", type, new Color("62767e"));
        }
        theme.SetColor("font_color", "Label", new Color("dce9e6"));
        theme.SetStylebox("panel", "Panel", MenuBox("10212af2", "2d424b"));
        theme.SetStylebox("panel", "PopupMenu", MenuBox("132630", "46625f"));
        theme.SetStylebox("hover", "PopupMenu", MenuBox("28554e", "67e8c0"));
        theme.SetConstant("separation", "VBoxContainer", UiLayout.Space1);
        Theme = theme;
        GetNodeOrNull<Control>("Background")?.Hide();
        var backdrop = new MenuBackdrop { Name = "TacticalBackdrop", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(backdrop);
        MoveChild(backdrop, 0);
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _lobbySurface = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        PageLobby.AddChild(_lobbySurface);
        PageLobby.MoveChild(_lobbySurface, 0);
        _menuKicker = new Label { Text = "RTS / ARCADE", MouseFilter = MouseFilterEnum.Ignore };
        _menuKicker.AddThemeColorOverride("font_color", MenuAccent);
        PageMain.AddChild(_menuKicker);
        _menuCaption = new Label { Text = RTS.Settings.Localization.Tr("menu.tagline"), MouseFilter = MouseFilterEnum.Ignore };
        _menuCaption.AddThemeColorOverride("font_color", new Color("80999f"));
        PageMain.AddChild(_menuCaption);
        foreach (var primary in new[] { BtnSinglePlayer, BtnStart, BtnReady })
        {
            primary.AddThemeStyleboxOverride("normal", MenuBox("67e8c0", "67e8c0"));
            primary.AddThemeColorOverride("font_color", new Color("102b26"));
        }
        Resized += LayoutMenuDesign;
        PageLobby.VisibilityChanged += LayoutMenuDesign;
        Callable.From(LayoutMenuDesign).CallDeferred();
    }

    private static void PlaceMenu(Control control, float x, float y, float w, float h)
    {
        if (control == null) return;
        control.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        control.Scale = Vector2.One;
        control.Position = new Vector2(x, y);
        control.Size = new Vector2(w, h);
    }

    private void LayoutMenuDesign()
    {
        float m = UiLayout.PageMargin, w = Size.X, h = Size.Y;
        float left = UiLayout.LeftColumnWidth + UiLayout.Space6;
        PlaceMenu(_menuKicker, m, m, left, 24);
        var title = PageMain.GetNodeOrNull<Label>("Label");
        title?.AddThemeFontSizeOverride("font_size", UiLayout.FontTitle);
        PlaceMenu(title, m, m + 32, w - m * 2, 56);
        PlaceMenu(_menuCaption, m, m + 96, w - m * 2, 24);
        var buttons = new[] { BtnSinglePlayer, BtnHost, BtnFind, _tutorialButton, _editorButton, _settingsButton, BtnQuit };
        for (int i = 0; i < buttons.Length; i++) PlaceMenu(buttons[i], m, m + 152 + i * 48, left, 40);
        PlaceMenu(VersionLabel, m, h - 32, w - m * 2, 24);
        var lobbyTitle = PageLobby.GetNodeOrNull<Label>("Label");
        lobbyTitle?.AddThemeFontSizeOverride("font_size", 32);
        PlaceMenu(lobbyTitle, m, m, w - m * 2, 48);
        float right = m + left + 32;
        PlaceMenu(_lobbySurface, right, 120, w - right - m, h - 168);
        PlaceMenu(PlayerListContainer, right + 16, 136, w - right - m - 32, h - 200);
        PlaceMenu(OptMap, m, 136, left, 40);
        if (_obsCheck != null)
        {
            _obsCheck.TooltipText = RTS.Settings.Localization.Tr("observer.check");
            _obsCheck.Text = RTS.Settings.Localization.Tr("observer.short");
            _obsCheck.CustomMinimumSize = Vector2.Zero;
        }
        PlaceMenu(_obsCheck, m, 192, left, 40);
        PlaceMenu(_btnAddBot, m, 248, left, 40);
        PlaceMenu(BtnReady, m, h - 208, left, 40);
        PlaceMenu(BtnStart, m, h - 152, left, 40);
        PlaceMenu(BtnBack, m, h - 96, left, 40);
        var roomTitle = PageRoomList.GetNodeOrNull<Label>("Label");
        roomTitle?.AddThemeFontSizeOverride("font_size", 32);
        PlaceMenu(roomTitle, m, m, w - m * 2, 56);
        PlaceMenu(BtnRefresh, m, 136, left, 40);
        PlaceMenu(BtnBackToMain, m, h - 96, left, 40);
        PlaceMenu(RoomContainer, right, 136, w - right - m, h - 200);
    }
}

public partial class MenuBackdrop : Control
{
    public override void _Ready() { Resized += QueueRedraw; }
    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("09151e"));
        for (int x = 0; x < Size.X; x += 64)
            DrawLine(new Vector2(x, 0), new Vector2(x, Size.Y), new Color("142630"));
        for (int y = 0; y < Size.Y; y += 64)
            DrawLine(new Vector2(0, y), new Vector2(Size.X, y), new Color("142630"));
        var center = new Vector2(Size.X * 0.76f, Size.Y * 0.45f);
        float radius = Size.Y * 0.38f;
        for (int i = 0; i < 3; i++)
            DrawArc(center, radius - i * 48, 0, Mathf.Tau, 128, new Color("28444c"), 1, true);
        for (int i = 0; i < 7; i++)
        {
            float angle = Mathf.Tau * i / 7;
            var point = center + Vector2.FromAngle(angle) * radius;
            var inner = center + Vector2.FromAngle(angle + 0.6f) * (radius - 96);
            DrawLine(point, inner, new Color("39635f"), 1, true);
            DrawCircle(point, 4, new Color("67e8c0"));
        }
        DrawLine(new Vector2(48, 24), new Vector2(112, 24), new Color("67e8c0"), 3);
    }
}
