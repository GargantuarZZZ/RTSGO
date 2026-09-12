using Godot;

namespace RTS.UI
{
// Shared visual tokens; faction variants can replace these without changing HUD behavior.
public static class HudTheme
{
    public static readonly Color Accent = new("67e8c0");
    public static StyleBoxFlat Surface(string color = "10212af5", string edge = "304952", int padding = 8)
        => new() { BgColor = new Color(color), BorderColor = new Color(edge),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding };

    public static Theme Create()
    {
        var theme = new Theme { DefaultFontSize = 13 };
        foreach (string type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Surface("1a303c", "36515e", 4));
            theme.SetStylebox("hover", type, Surface("284c55", "67e8c0", 4));
            theme.SetStylebox("pressed", type, Surface("316254", "a6ffe0", 4));
            theme.SetStylebox("disabled", type, Surface("101d26", "22343e", 4));
            theme.SetStylebox("focus", type, Surface("00000000", "a6ffe0", 0));
            theme.SetColor("font_color", type, new Color("e5f2f0"));
            theme.SetColor("font_disabled_color", type, new Color("73858e"));
        }
        theme.SetStylebox("panel", "Panel", Surface());
        theme.SetStylebox("panel", "PanelContainer", Surface());
        theme.SetStylebox("panel", "PopupPanel", Surface("10212aff", "67e8c0"));
        theme.SetColor("font_color", "Label", new Color("dcebe8"));
        theme.SetColor("default_color", "RichTextLabel", new Color("bfd0d5"));
        theme.SetStylebox("background", "ProgressBar", Surface("08151c", "233a44", 0));
        theme.SetStylebox("fill", "ProgressBar", Surface("67e8c0", "67e8c0", 0));
        theme.SetConstant("h_separation", "GridContainer", 4);
        theme.SetConstant("v_separation", "GridContainer", 4);
        return theme;
    }
    public static void ApplyTree(Node root)
    {
        if (root is Control c)
        {
            if (c is Panel or PanelContainer) c.AddThemeStyleboxOverride("panel", Surface());
            if (c is Button button)
            {
                button.AddThemeFontSizeOverride("font_size", 12);
                if (button.GetNodeOrNull<Label>("KeyLabel") is { } key)
                {
                    key.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
                    key.Position = new Vector2(4, 1);
                    key.Size = new Vector2(16, 14);
                    key.AddThemeFontSizeOverride("font_size", 10);
                    key.AddThemeColorOverride("font_color", Accent);
                    key.MouseFilter = Control.MouseFilterEnum.Ignore;
                }
            }
        }
        foreach (Node child in root.GetChildren()) ApplyTree(child);
    }
}
}
