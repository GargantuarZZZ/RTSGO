using Godot;
using System.Collections.Generic;
using RTS.Settings;

namespace RTS.UI
{
    // Independent modal layer with fixed navigation/footer and one scrollable content page.
	public partial class SettingsMenu : Control
	{
		public static SettingsMenu Instance { get; private set; }
        public static bool IsOpen => GodotObject.IsInstanceValid(Instance) && !Instance.IsQueuedForDeletion();
        private readonly Dictionary<Button, string> _translatedButtons = new();
        public static void Open(Node parent)
        {
            if (IsOpen) return;
            var layer = new CanvasLayer { Name = "SettingsOverlay", Layer = 200 };
            parent.AddChild(layer);
            var menu = new SettingsMenu { Name = "SettingsMenu" };
            menu.TreeExited += () => layer.QueueFree();
            layer.AddChild(menu);
        }
        public override void _ExitTree() { if (Instance == this) Instance = null; }
        private Button TextButton(string key)
        {
            var button = new Button { Text = Localization.Tr(key), CustomMinimumSize = new Vector2(140, 40) };
            _translatedButtons[button] = key;
            return button;
        }
        private OptionButton _languageOption;
		private readonly Dictionary<string, Button> _keyButtons = new();
		// 次要键位按钮：key = (动作, 次要槽下标)。目前每个动作只有一个次要槽。
		private readonly Dictionary<(string, int), Button> _secondaryButtons = new();
		private readonly Dictionary<string, Label> _keyWarnLabels = new();
		private string _awaitingAction = "";
		// 改键时正在等的槽位/下标（支持给"次要键"改键，而不是只能改主键）
		private BindingSlot _awaitingSlot = BindingSlot.Primary;
		private int _awaitingIndex = 0;
		private readonly List<Label> _labels = new();
		private CheckBox _fullscreenBox;
		private Button _resetKeysButton;
		private Label _statusLabel;

		public override void _Ready()
		{
			Instance = this;
            Theme = HudTheme.Create();
            Theme.DefaultFontSize = 16;
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			BuildUI();
			RefreshTexts();
		}

		private void BuildUI()
		{
            var dim = new ColorRect { Color = new Color("08151cf5") };
            AddChild(dim);
            dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var margin = new MarginContainer();
            AddChild(margin);
            margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 32);
            var layout = new VBoxContainer();
            layout.AddThemeConstantOverride("separation", 20);
            margin.AddChild(layout);
            var title = MakeLabel("settings");
            title.AddThemeFontSizeOverride("font_size", 32);
            layout.AddChild(title);
            var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 24);
            layout.AddChild(body);
            var nav = new VBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
            nav.AddThemeConstantOverride("separation", 8);
            body.AddChild(nav);
            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            panel.AddThemeStyleboxOverride("panel", HudTheme.Surface("10212aff", "304952", 20));
            body.AddChild(panel);
            var outerScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            panel.AddChild(outerScroll);
            var pages = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            outerScroll.AddChild(pages);
            var group = new ButtonGroup();
            VBoxContainer Page(string key)
            {
                var page = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = pages.GetChildCount() == 0 };
                page.AddThemeConstantOverride("separation", 16);
                pages.AddChild(page);
                var tab = TextButton(key);
                tab.SetMeta("settings_page_key", key);   // 供 ShowPageForTest 定位
                tab.ToggleMode = true;
                tab.ButtonGroup = group;
                tab.ButtonPressed = page.Visible;
                tab.Pressed += () => {
                    tab.ButtonPressed = true;
                    _awaitingAction = "";
                    RefreshKeyTexts();
                    foreach (Control item in pages.GetChildren()) item.Visible = item == page;
                    outerScroll.ScrollVertical = 0;
                };
                nav.AddChild(tab);
                return page;
            }
            var vbox = Page("language");
			// ---- 语言 ----
			vbox.AddChild(MakeLabel("language"));
			_languageOption = new OptionButton();
			int currentIndex = 0;
			for (int i = 0; i < RTS.Settings.Localization.Languages.Length; i++)
			{
				var lang = RTS.Settings.Localization.Languages[i];
				_languageOption.AddItem(lang.NativeName);
				if (lang.Code == RTS.Settings.GameSettings.Language)
					currentIndex = i;
			}
			_languageOption.Selected = currentIndex;
			_languageOption.ItemSelected += OnLanguageChanged;
			vbox.AddChild(_languageOption);

			vbox.AddChild(new HSeparator());

			// ---- 声音 ----
            vbox = Page("audio");
			vbox.AddChild(MakeLabel("audio"));
			vbox.AddChild(MakeVolumeSlider("master_volume", () => RTS.Settings.GameSettings.MasterVolume,
				v => RTS.Settings.GameSettings.MasterVolume = v));
			vbox.AddChild(MakeVolumeSlider("music_volume", () => RTS.Settings.GameSettings.MusicVolume,
				v => RTS.Settings.GameSettings.MusicVolume = v));
			vbox.AddChild(MakeVolumeSlider("sfx_volume", () => RTS.Settings.GameSettings.SfxVolume,
				v => RTS.Settings.GameSettings.SfxVolume = v));

			vbox.AddChild(new HSeparator());

			// ---- 画面 ----
            vbox = Page("video");
			vbox.AddChild(MakeLabel("video"));
			var fullscreen = new CheckBox
			{
				ButtonPressed = RTS.Settings.GameSettings.Fullscreen,
				Text = RTS.Settings.Localization.Tr("fullscreen")
			};
			_fullscreenBox = fullscreen;
			fullscreen.Toggled += on =>
			{
				RTS.Settings.GameSettings.Fullscreen = on;
				RTS.Settings.GameSettings.ApplyVideo();
			};
			vbox.AddChild(fullscreen);

			vbox.AddChild(new HSeparator());

			// ---- 键位 ----
            vbox = Page("keybinds");
			vbox.AddChild(MakeLabel("keybinds"));

			var keyHint = MakeLabel("keybind_hint");
            keyHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			keyHint.AddThemeFontSizeOverride("font_size", 11);
			keyHint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
			vbox.AddChild(keyHint);

            // Only the content area scrolls; navigation and footer stay fixed.
			var keyBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			keyBox.AddThemeConstantOverride("separation", 4);
			vbox.AddChild(keyBox);

			BuildKeybindSection(keyBox, InputGroup.Camera, "keygroup_camera");
			BuildKeybindSection(keyBox, InputGroup.Selection, "keygroup_selection");
			BuildKeybindSection(keyBox, InputGroup.Orders, "keygroup_orders");
			BuildKeybindSection(keyBox, InputGroup.Match, "keygroup_match");
			BuildKeybindSection(keyBox, InputGroup.Panel, "keygroup_panel");

			var keyButtons = new HBoxContainer();
			_resetKeysButton = new Button { Text = RTS.Settings.Localization.Tr("reset_keys") };
			_resetKeysButton.Pressed += OnResetKeysPressed;
			keyButtons.AddChild(_resetKeysButton);
			vbox.AddChild(keyButtons);

			_statusLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
			_statusLabel.AddThemeFontSizeOverride("font_size", 12);
			_statusLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.9f, 0.6f));
			vbox.AddChild(_statusLabel);

			vbox.AddChild(new HSeparator());

			// ---- 底部按钮 ----
			var buttons = new HBoxContainer();
			var save = TextButton("save");
			save.Pressed += OnSavePressed;
			var back = TextButton("back");
			back.Pressed += () => QueueFree();
			buttons.AddChild(save);
			buttons.AddChild(back);
			layout.AddChild(buttons);
		}

		/// <summary>渲染一组动作的键位行。</summary>
		private void BuildKeybindSection(VBoxContainer parent, InputGroup group, string titleKey)
		{
			var defs = RTS.Settings.InputActions.InGroup(group);
			if (defs.Count == 0)
				return;

			var header = MakeLabel(titleKey);
			header.AddThemeFontSizeOverride("font_size", 14);
			header.AddThemeColorOverride("font_color", new Color(0.7f, 0.85f, 1f));
			parent.AddChild(header);

			foreach (var def in defs)
			{
				var row = new HBoxContainer();
				row.AddThemeConstantOverride("separation", 8);

				var nameLabel = MakeLabel(def.LabelKey);
				nameLabel.CustomMinimumSize = new Vector2(190, 0);
				row.AddChild(nameLabel);

				// ---- 主键 ----
				var btn = MakeBindButton(def.Action, BindingSlot.Primary, 0, def);
				row.AddChild(btn);
				_keyButtons[def.Action] = btn;

				// ---- 次要键 ----
				// 只有可改键的动作才给次要槽：面板技能槽按"位置=按键"排布，
				// 给它们加次要键会破坏空间记忆。
				if (RTS.Settings.InputActions.SupportsSecondary(def))
				{
					var secBtn = MakeBindButton(def.Action, BindingSlot.Secondary, 0, def);
					secBtn.TooltipText = RTS.Settings.Localization.Tr("keybind_secondary_hint");
					row.AddChild(secBtn);
					_secondaryButtons[(def.Action, 0)] = secBtn;
				}

				var warn = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
				warn.AddThemeFontSizeOverride("font_size", 11);
				warn.AddThemeColorOverride("font_color", new Color(1f, 0.7f, 0.4f));
				row.AddChild(warn);
				_keyWarnLabels[def.Action] = warn;

				parent.AddChild(row);
			}
		}

		/// <summary>
		/// 造一个绑定按钮。槽位决定它绑 Primary 还是 Secondary，
		/// 按钮文本走 BindingToText（含 Ctrl/Shift 前缀与"鼠标侧键4"）。
		/// </summary>
		private Button MakeBindButton(string action, BindingSlot slot, int index, InputActionDef def)
		{
			var binding = RTS.Settings.GameSettings.GetBinding(action, slot, index);

			var btn = new Button
			{
				Text = RTS.Settings.GameSettings.BindingToText(binding),
				CustomMinimumSize = new Vector2(slot == BindingSlot.Primary ? 120 : 110, 0),
				Disabled = !def.Rebindable,
				// 不可改的键（面板技能槽）用 tooltip 说明为什么，避免玩家以为是 bug
				TooltipText = def.Rebindable
					? RTS.Settings.Localization.Tr("keybind_click_to_change")
					: RTS.Settings.Localization.Tr("keybind_fixed"),
				// 次要槽允许右键清除；主键不允许（主键是必需的）
				FocusMode = Control.FocusModeEnum.None,
			};

			if (def.Rebindable)
			{
				btn.Pressed += () => BeginRebind(action, btn, slot, index);
				if (slot == BindingSlot.Secondary)
				{
					btn.GuiInput += (ev) =>
					{
						if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
						{
							RTS.Settings.GameSettings.SetBinding(action, slot, index, InputBinding.None);
							RTS.Settings.GameSettings.ApplyKeybinds();
							RefreshKeyTexts();
							SetStatus(RTS.Settings.Localization.Tr("keybind_cleared"), false);
						}
					};
				}
			}
			return btn;
		}

		/// <summary>
		/// 截图/无头用：翻到指定页面（页面 key 如 "keybinds"）。
		/// 做法是找到导航栏里对应那个按钮并按一下 —— 复用真实切换路径，
		/// 而不是另写一套"直接设 Visible"，否则测的不是玩家看到的那个行为。
		/// </summary>
		public void ShowPageForTest(string pageKey)
		{
			var tabs = GetTree().Root.FindChildren("*", "Button", true, false);
			foreach (var n in tabs)
			{
				if (n is not Button b) continue;
				if (b.GetMeta("settings_page_key").AsString() == pageKey)
				{
					b.ButtonPressed = true;
					b.EmitSignal(BaseButton.SignalName.Pressed);
					return;
				}
			}
			GD.PrintErr($"[Settings] 找不到页面 '{pageKey}' 的导航按钮");
		}

		private Label MakeLabel(string key)
		{
			var label = new Label();
			_labels.Add(label);
			label.SetMeta("tr_key", key);
			return label;
		}

		private HBoxContainer MakeVolumeSlider(string key, System.Func<float> get, System.Action<float> set)
		{
			var row = new HBoxContainer();
			var label = MakeLabel(key);
			label.CustomMinimumSize = new Vector2(190, 0);
			row.AddChild(label);
			var slider = new HSlider
			{
				MinValue = 0f,
				MaxValue = 1f,
				Step = 0.01f,
				Value = get(),
				SizeFlagsHorizontal = SizeFlags.ExpandFill
			};
			slider.ValueChanged += v =>
			{
				set((float)v);
				RTS.Settings.GameSettings.ApplyAudio();
			};
			row.AddChild(slider);
			return row;
		}

		private void OnLanguageChanged(long index)
		{
			if (index < 0 || index >= RTS.Settings.Localization.Languages.Length)
				return;
			RTS.Settings.GameSettings.Language = RTS.Settings.Localization.Languages[index].Code;
			RTS.Settings.Localization.SetLanguage(RTS.Settings.GameSettings.Language);
			RefreshTexts();
		}

		private void RefreshTexts()
		{
			foreach (var item in _translatedButtons) item.Key.Text = Localization.Tr(item.Value);
            foreach (var label in _labels)
			{
				if (!label.HasMeta("tr_key")) continue;
				string key = (string)label.GetMeta("tr_key");
				label.Text = RTS.Settings.Localization.Has(key)
					? RTS.Settings.Localization.Tr(key)
					: key; // 本地化缺条目时退回 key，而不是显示空白
			}
			if (_fullscreenBox != null)
				_fullscreenBox.Text = RTS.Settings.Localization.Tr("fullscreen");
			if (_resetKeysButton != null)
				_resetKeysButton.Text = RTS.Settings.Localization.Tr("reset_keys");

			RefreshKeyTexts();
		}

		private void RefreshKeyTexts()
		{
			foreach (var kv in _keyButtons)
			{
				var def = RTS.Settings.InputActions.Get(kv.Key);
				if (def == null) continue;

				var primary = RTS.Settings.GameSettings.GetBinding(kv.Key, BindingSlot.Primary, 0);
				string text = RTS.Settings.GameSettings.BindingToText(primary);
				// 与默认值不同就加个星号，方便一眼看出自己改过哪些
				if (def.Rebindable && !primary.SameAs(InputBinding.Key(def.Default, BindingSlot.Primary, ctrl: def.DefaultCtrl)))
					text += " *";
				kv.Value.Text = text;
			}

			foreach (var kv in _secondaryButtons)
			{
				var b = RTS.Settings.GameSettings.GetBinding(kv.Key.Item1, BindingSlot.Secondary, kv.Key.Item2);
				kv.Value.Text = RTS.Settings.GameSettings.BindingToText(b);
			}

			RefreshConflictWarnings();
		}

		/// <summary>把当前冲突显示在对应行右侧（"被 XX 占用"）。</summary>
		private void RefreshConflictWarnings()
		{
			foreach (var kv in _keyWarnLabels)
				kv.Value.Text = "";

			// 逐条绑定检查（主键与次要键都要看），按签名比对，
			// 所以 H 与 Ctrl+H 这类不同组合不会被误报。
			foreach (var kv in _keyWarnLabels)
			{
				var def = RTS.Settings.InputActions.Get(kv.Key);
				if (def == null || !def.Rebindable) continue;

				var names = new List<string>();
				foreach (var b in RTS.Settings.GameSettings.BindingsOf(kv.Key))
				{
					if (b.IsEmpty) continue;
					var others = RTS.Settings.InputActions.ActionsUsing(
						RTS.Settings.GameSettings.Bindings, b, kv.Key);
					foreach (string a in others)
					{
						string n = ShortName(a);
						if (!names.Contains(n)) names.Add(n);
					}
				}

				if (names.Count == 0) continue;
				if (_keyWarnLabels.TryGetValue(kv.Key, out var warn))
					warn.Text = RTS.Settings.Localization.Tr("keybind_conflict") + " " + string.Join("/", names);
			}
		}

		private static string ShortName(string action)
		{
			var def = RTS.Settings.InputActions.Get(action);
			if (def == null) return action;
			return RTS.Settings.Localization.Has(def.LabelKey)
				? RTS.Settings.Localization.Tr(def.LabelKey)
				: action;
		}

		private void BeginRebind(string action, Button button, BindingSlot slot, int index)
		{
			_awaitingAction = action;
			_awaitingSlot = slot;
			_awaitingIndex = index;
			button.Text = RTS.Settings.Localization.Tr("press_key");
			SetStatus(RTS.Settings.Localization.Tr("keybind_waiting"), false);
		}

		public override void _Input(InputEvent @event)
		{
			if (_awaitingAction.Length == 0)
            {
                if (@event is InputEventKey esc && esc.Pressed && !esc.Echo && esc.Keycode == Key.Escape)
                { QueueFree(); GetViewport().SetInputAsHandled(); }
                return;
            }

			// Esc 取消改键（否则玩家想取消都没有退路）
			if (@event is InputEventKey cancel && cancel.Pressed && !cancel.Echo &&
				cancel.PhysicalKeycode == Key.Escape)
			{
				_awaitingAction = "";
				SetStatus(RTS.Settings.Localization.Tr("keybind_cancelled"), false);
				RefreshKeyTexts();
				GetViewport().SetInputAsHandled();
				return;
			}

			// ---- 捕获一个绑定：键盘 或 鼠标（含侧键）----
			//
			// 修饰键（Ctrl/Shift/Alt）不再被忽略：它们**参与**组合，
			// 例如 Ctrl+H 是"重开投票"。这里把按下状态读出来存进绑定。
			InputBinding captured;
			if (@event is InputEventKey key)
			{
				if (!key.Pressed || key.Echo) return;
				// 只按修饰键本身不作为绑定目标（玩家按 Ctrl 是想组合，不是想绑 Ctrl）
				if (key.PhysicalKeycode is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta)
					return;
				captured = InputBinding.Key(key.PhysicalKeycode, _awaitingSlot,
					key.CtrlPressed, key.ShiftPressed, key.AltPressed, key.MetaPressed);
			}
			else if (@event is InputEventMouseButton mb)
			{
				if (!mb.Pressed) return;
				// 左键用于点按钮本身，不当作绑定目标（否则点一下就把按钮绑成左键）
				if (mb.ButtonIndex == MouseButton.Left) return;
				captured = InputBinding.Mouse(mb.ButtonIndex, _awaitingSlot,
					mb.CtrlPressed, mb.ShiftPressed, mb.AltPressed, mb.MetaPressed);
			}
			else return;

			string action = _awaitingAction;
			_awaitingAction = "";

			// 冲突只提示、不阻止：玩家可能有自己的理由共用按键
			// （例如把"停止"和"驻守"放同一个键上分场景用）。
			// 按"签名"比对，所以 H 与 Ctrl+H 不会互相误报。
			var others = RTS.Settings.InputActions.ActionsUsing(
				RTS.Settings.GameSettings.Bindings, captured, action);

			RTS.Settings.GameSettings.SetBinding(action, _awaitingSlot, _awaitingIndex, captured);
			RTS.Settings.GameSettings.ApplyKeybinds();

			if (others.Count > 0)
			{
				var names = new List<string>();
				foreach (string a in others) names.Add(ShortName(a));
				SetStatus($"{RTS.Settings.Localization.Tr("keybind_conflict")} " +
					string.Join("/", names), true);
			}
			else
			{
				SetStatus(RTS.Settings.Localization.Tr("keybind_applied"), false);
			}

			RefreshKeyTexts();
			GetViewport().SetInputAsHandled();
		}

        public override void _UnhandledInput(InputEvent @event) => GetViewport().SetInputAsHandled();

		private void OnResetKeysPressed()
		{
			int changed = RTS.Settings.GameSettings.ResetAllKeybinds();
			RefreshKeyTexts();
			SetStatus(changed > 0
				? $"{RTS.Settings.Localization.Tr("reset_keys_done")} ({changed})"
				: RTS.Settings.Localization.Tr("reset_keys_none"), false);
		}

		private void SetStatus(string text, bool warning)
		{
			if (_statusLabel == null) return;
			_statusLabel.Text = text;
			_statusLabel.AddThemeColorOverride("font_color",
				warning ? new Color(1f, 0.7f, 0.4f) : new Color(0.6f, 0.9f, 0.6f));
		}

		private void OnSavePressed()
		{
			RTS.Settings.GameSettings.Save();
			GD.Print("[Settings] 设置已保存");
			QueueFree();
		}
	}
}
