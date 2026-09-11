using Godot;
using System.Collections.Generic;
using RTS.Settings;

namespace RTS.UI
{
	// =========================================================
	// 设置菜单：语言 / 键位 / 声音 / 画面
	//
	// 代码构建 UI（不手写 .tscn），与项目其它面板保持一致。
	//
	// ---- 这次重做的原因 ----
	// 旧版键位区只列 `GameSettings.Keybinds` 的 4 条（P/K/T/F1），而实际
	// 游戏里几十个键写死在 ActionPanel / UserController / RaceBuildPanelUI 里，
	// 玩家既看不到也改不了 —— 更糟的是这些写死的键**会把 A/S/T/F1 抢走**。
	//
	// 现在键位区由 `InputActions` 注册表驱动：
	//   · 按组显示（镜头 / 选择与编队 / 单位指令 / 对局与社交 / 面板技能槽）
	//   · 面板技能槽标为"固定"，让玩家知道这些键被谁占着（不再隐形）
	//   · 改键时**实时检测冲突**并提示谁占了
	//   · 一键恢复默认
	// =========================================================

	public partial class SettingsMenu : Control
	{
		private OptionButton _languageOption;
		private readonly Dictionary<string, Button> _keyButtons = new();
		private readonly Dictionary<string, Label> _keyWarnLabels = new();
		private string _awaitingAction = "";
		private readonly List<Label> _labels = new();
		private CheckBox _fullscreenBox;
		private Button _resetKeysButton;
		private Label _statusLabel;

		public override void _Ready()
		{
			SetAnchorsPreset(LayoutPreset.FullRect);
			BuildUI();
			RefreshTexts();
		}

		private void BuildUI()
		{
			var dim = new ColorRect
			{
				Color = new Color(0f, 0f, 0f, 0.6f)
			};
			dim.SetAnchorsPreset(LayoutPreset.FullRect);
			AddChild(dim);

			// ---- 居中 + 限高 + 可滚动的面板 ----
			//
			// 设置项从 4 条键位涨到 32 条以后，内容高度轻松超过 700px；
			// 而窗口默认只有 648px。如果直接把 VBox 放进居中的 PanelContainer，
			// 底部"保存/返回"会被挤出屏幕**且点不到**（不是滚动，是真的裁掉）。
			// 所以外层套一个限高的 ScrollContainer：
			//   面板高度 = min(内容高度, 90% 窗口高)，超出部分整体滚动。
			var outerScroll = new ScrollContainer();
			outerScroll.SetAnchorsPreset(LayoutPreset.FullRect);
			outerScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
			AddChild(outerScroll);

			var center = new CenterContainer
			{
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				SizeFlagsVertical = SizeFlags.ExpandFill,
			};
			outerScroll.AddChild(center);

			var panel = new PanelContainer { CustomMinimumSize = new Vector2(620, 0) };
			center.AddChild(panel);

			var margin = new MarginContainer();
			margin.AddThemeConstantOverride("margin_left", 24);
			margin.AddThemeConstantOverride("margin_right", 24);
			margin.AddThemeConstantOverride("margin_top", 16);
			margin.AddThemeConstantOverride("margin_bottom", 16);
			panel.AddChild(margin);

			var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			margin.AddChild(vbox);

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
			vbox.AddChild(MakeLabel("audio"));
			vbox.AddChild(MakeVolumeSlider("master_volume", () => RTS.Settings.GameSettings.MasterVolume,
				v => RTS.Settings.GameSettings.MasterVolume = v));
			vbox.AddChild(MakeVolumeSlider("music_volume", () => RTS.Settings.GameSettings.MusicVolume,
				v => RTS.Settings.GameSettings.MusicVolume = v));
			vbox.AddChild(MakeVolumeSlider("sfx_volume", () => RTS.Settings.GameSettings.SfxVolume,
				v => RTS.Settings.GameSettings.SfxVolume = v));

			vbox.AddChild(new HSeparator());

			// ---- 画面 ----
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

			// ---- 键位（可滚动：动作多了以后不会把面板撑爆）----
			vbox.AddChild(MakeLabel("keybinds"));

			var keyHint = new Label
			{
				Text = RTS.Settings.Localization.Tr("keybind_hint"),
				AutowrapMode = TextServer.AutowrapMode.WordSmart,
			};
			keyHint.AddThemeFontSizeOverride("font_size", 11);
			keyHint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
			vbox.AddChild(keyHint);

			// 键位区不再自己滚：外层已经整体可滚，
			// 这里用自然高度，让"有多少键位就有多长"，避免双重滚动条互相打架。
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
			var save = new Button { Text = RTS.Settings.Localization.Tr("save") };
			save.Pressed += OnSavePressed;
			var back = new Button { Text = RTS.Settings.Localization.Tr("back") };
			back.Pressed += () => QueueFree();
			buttons.AddChild(save);
			buttons.AddChild(back);
			vbox.AddChild(buttons);
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

				var btn = new Button
				{
					Text = RTS.Settings.GameSettings.KeyToText(RTS.Settings.GameSettings.Keybinds[def.Action]),
					CustomMinimumSize = new Vector2(120, 0),
					Disabled = !def.Rebindable,
					// 不可改的键（面板技能槽）用 tooltip 说明为什么，避免玩家以为是 bug
					TooltipText = def.Rebindable
						? RTS.Settings.Localization.Tr("keybind_click_to_change")
						: RTS.Settings.Localization.Tr("keybind_fixed"),
				};
				string action = def.Action;
				if (def.Rebindable)
					btn.Pressed += () => BeginRebind(action, btn);
				row.AddChild(btn);
				_keyButtons[def.Action] = btn;

				var warn = new Label();
				warn.AddThemeFontSizeOverride("font_size", 11);
				warn.AddThemeColorOverride("font_color", new Color(1f, 0.7f, 0.4f));
				row.AddChild(warn);
				_keyWarnLabels[def.Action] = warn;

				parent.AddChild(row);
			}
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

				string text = RTS.Settings.GameSettings.KeyToText(RTS.Settings.GameSettings.Keybinds[kv.Key]);
				// 与默认值不同就加个星号，方便一眼看出自己改过哪些
				if (def.Rebindable && RTS.Settings.GameSettings.Keybinds[kv.Key] != def.Default)
					text += " *";
				kv.Value.Text = text;
			}
			RefreshConflictWarnings();
		}

		/// <summary>把当前冲突显示在对应行右侧（"被 XX 占用"）。</summary>
		private void RefreshConflictWarnings()
		{
			foreach (var kv in _keyWarnLabels)
				kv.Value.Text = "";

			foreach (var kv in _keyButtons)
			{
				var def = RTS.Settings.InputActions.Get(kv.Key);
				if (def == null || !def.Rebindable) continue;

				Key myKey = RTS.Settings.GameSettings.Keybinds[kv.Key];
				if (myKey == Key.None) continue;

				var others = RTS.Settings.InputActions.ActionsUsing(
					RTS.Settings.GameSettings.Keybinds, myKey, kv.Key);
				if (others.Count == 0) continue;

				var names = new List<string>();
				foreach (string a in others) names.Add(ShortName(a));
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

		private void BeginRebind(string action, Button button)
		{
			_awaitingAction = action;
			button.Text = RTS.Settings.Localization.Tr("press_key");
			SetStatus(RTS.Settings.Localization.Tr("keybind_waiting"), false);
		}

		public override void _UnhandledInput(InputEvent @event)
		{
			if (_awaitingAction.Length == 0)
				return;

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

			if (@event is not InputEventKey key || !key.Pressed || key.Echo)
				return;

			// 修饰键本身不作为绑定目标（玩家按 Ctrl 是想组合，不是想绑 Ctrl）
			if (key.PhysicalKeycode is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta)
				return;

			string action = _awaitingAction;
			_awaitingAction = "";

			// 冲突只提示、不阻止：玩家可能有自己的理由共用按键
			// （例如把"停止"和"驻守"放同一个键上分场景用）。
			var others = RTS.Settings.InputActions.ActionsUsing(
				RTS.Settings.GameSettings.Keybinds, key.PhysicalKeycode, action);

			RTS.Settings.GameSettings.Keybinds[action] = key.PhysicalKeycode;
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
