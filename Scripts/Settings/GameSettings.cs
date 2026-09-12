using Godot;
using System.Collections.Generic;

namespace RTS.Settings
{
	// P2-5/P2-6：游戏设置（语言/键位/声音/画面），持久化到 user://settings.cfg
	public static class GameSettings
	{
		public static string Language = "zh_CN";
		public static float MasterVolume = 1f;
		public static float MusicVolume = 1f;
		public static float SfxVolume = 1f;
		public static bool Fullscreen = false;

		/// <summary>
		/// 玩家键位。
		///
		/// **唯一真相是 `InputActions.All`**：启动时按它的默认值填充，
		/// 存档只覆盖玩家改过的那部分。这样"设置菜单里能改的键"与
		/// "InputMap 里生效的键"不可能再分叉 ——
		/// 之前 Keybinds 只有 4 条，而 ActionPanel/UserController 各自写死了几十个键，
		/// 结果 A/S/T/F1 这些手边的键被面板抢走，玩家根本改不了。
		/// </summary>
		/// <summary>
		/// 主键的只读投影（保留给"只关心一个键"的老调用方：
		/// 技能槽显示、编队、菜单自检、测试）。
		/// 真正生效的是 `Bindings`（支持次要键位与修饰键），
		/// 这里由 SyncPrimaryView() / SetBinding() 保持一致。
		/// </summary>
		public static readonly Dictionary<string, Key> Keybinds = BuildDefaultKeybinds();

		private static Dictionary<string, Key> BuildDefaultKeybinds()
		{
			var map = new Dictionary<string, Key>();
			foreach (var def in InputActions.All)
				map[def.Action] = def.Default;
			return map;
		}

		/// <summary>
		/// **权威**绑定表：每个动作可以有多个绑定（主键 + 次要键），
		/// 每条绑定带修饰键（Ctrl/Shift/Alt）与设备（键盘/鼠标，含鼠标侧键）。
		///
		/// 与 `Keybinds` 的关系：
		///   · `Bindings` 是真正生效的东西，`ApplyKeybinds` 从它生成 InputMap 事件；
		///   · `Keybinds` 是**主键投影**，保留给老代码。
		///   两边由 SyncPrimaryView() 保持同步，不允许多处各存一份。
		/// </summary>
		public static readonly Dictionary<string, List<InputBinding>> Bindings = BuildDefaultBindings();

		private static Dictionary<string, List<InputBinding>> BuildDefaultBindings()
		{
			var map = new Dictionary<string, List<InputBinding>>();
			foreach (var def in InputActions.All)
				map[def.Action] = InputActions.DefaultBindings(def);
			return map;
		}

		/// <summary>某个动作的主键（没有主绑定时为 Key.None）。</summary>
		public static Key PrimaryKey(string action)
		{
			if (!Bindings.TryGetValue(action, out var list)) return Key.None;
			foreach (var b in list)
				if (b.Slot == BindingSlot.Primary && b.Device == BindingDevice.Keyboard)
					return b.AsKey;
			// 主槽是鼠标或为空时，退回第一条键盘绑定，保证老调用方仍有值
			foreach (var b in list)
				if (b.Device == BindingDevice.Keyboard && !b.IsEmpty)
					return b.AsKey;
			return Key.None;
		}

		/// <summary>把主键投影写回 Keybinds（在 Bindings 变更后调用）。</summary>
		private static void SyncPrimaryView()
		{
			foreach (var kv in Bindings)
				Keybinds[kv.Key] = PrimaryKey(kv.Key);
		}

		/// <summary>替换某动作的全部绑定。</summary>
		public static void SetBindings(string action, List<InputBinding> list)
		{
			if (action == null) return;
			Bindings[action] = list ?? new List<InputBinding>();
			Keybinds[action] = PrimaryKey(action);
		}

		/// <summary>
		/// 设置某个动作在指定槽位的绑定。
		/// slot 是 Primary / Secondary；index 用于次要槽位里放第几个（0 起）。
		/// 传空绑定表示清除该槽。
		/// </summary>
		public static void SetBinding(string action, BindingSlot slot, int index, InputBinding binding)
		{
			if (!Bindings.TryGetValue(action, out var list))
			{
				list = new List<InputBinding>();
				Bindings[action] = list;
			}

			binding.Slot = slot;

			// 先移除同槽同位已有项
			int seen = -1;
			for (int i = 0; i < list.Count; i++)
			{
				if (list[i].Slot != slot) continue;
				seen++;
				if (seen == index) { list.RemoveAt(i); break; }
			}

			if (!binding.IsEmpty)
			{
				// 插到合适位置：主键在前，次要键按顺序在后
				int insertAt = list.Count;
				if (slot == BindingSlot.Primary) insertAt = 0;
				else
				{
					for (int i = 0; i < list.Count; i++)
						if (list[i].Slot == BindingSlot.Primary) insertAt = i + 1;
				}
				list.Insert(Mathf.Min(insertAt, list.Count), binding);
			}

			Keybinds[action] = PrimaryKey(action);
		}

		/// <summary>取某动作在指定槽位的绑定（没有则返回空绑定）。</summary>
		public static InputBinding GetBinding(string action, BindingSlot slot, int index)
		{
			if (!Bindings.TryGetValue(action, out var list)) return InputBinding.None;
			int seen = -1;
			foreach (var b in list)
			{
				if (b.Slot != slot) continue;
				seen++;
				if (seen == index) return b;
			}
			return InputBinding.None;
		}

		/// <summary>某动作的全部绑定（供 UI 显示与冲突检测）。</summary>
		public static List<InputBinding> BindingsOf(string action) =>
			Bindings.TryGetValue(action, out var list) ? list : new List<InputBinding>();

		private const string SavePath = "user://settings.cfg";
		private static bool _loaded = false;

		public static void Load()
		{
			if (_loaded)
				return;
			_loaded = true;

			var cfg = new ConfigFile();
			if (cfg.Load(SavePath) != Error.Ok)
			{
				// 首次运行（无设置文件）：中文项目默认中文，用户可在设置里切换
				Language = "zh_CN";
				ApplyAll();
				return;
			}

			// 已保存过语言用存档值；旧存档没有语言字段时也走系统识别
			Language = (string)cfg.GetValue("general", "language", "");
			if (string.IsNullOrEmpty(Language))
				Language = "zh_CN";
			string overrideLang = OS.GetEnvironment("LANGUAGE");
			if (!string.IsNullOrEmpty(overrideLang))
				Language = overrideLang;
			MasterVolume = (float)cfg.GetValue("audio", "master", 1f);
			MusicVolume = (float)cfg.GetValue("audio", "music", 1f);
			SfxVolume = (float)cfg.GetValue("audio", "sfx", 1f);
			Fullscreen = (bool)cfg.GetValue("video", "fullscreen", false);

			foreach (string action in new List<string>(Keybinds.Keys))
			{
				// 新格式：绑定数组，每项 `<slot>|<device>|<code>|<mods>`
				// 旧格式：单个整数（只有主键、无修饰键）
				var raw = cfg.GetValue("keys", action, -1L);

				if (raw.VariantType == Variant.Type.PackedStringArray
					|| raw.VariantType == Variant.Type.Array)
				{
					var arr = raw.AsStringArray();
					var list = new List<InputBinding>();
					foreach (string s in arr)
					{
						var b = ParseBinding(s);
						if (!b.IsEmpty) list.Add(b);
					}
					Bindings[action] = list;
				}
				else
				{
					long keyCode = raw.AsInt64();
					if (keyCode >= 0)
					{
						// 旧存档：无修饰键、无次要键，只作主键
						Bindings[action] = keyCode == 0
							? new List<InputBinding>()
							: new List<InputBinding> { InputBinding.Key((Key)keyCode) };
					}
				}
			}

			SyncPrimaryView();
			ApplyAll();
		}

		/// <summary>序列化一条绑定：`slot|device|code|mods`（mods 为 C/S/A/M 的组合）。</summary>
		public static string FormatBinding(InputBinding b)
		{
			string mods = (b.Ctrl ? "C" : "") + (b.Shift ? "S" : "")
				+ (b.Alt ? "A" : "") + (b.Meta ? "M" : "");
			return $"{(int)b.Slot}|{(int)b.Device}|{b.Code}|{mods}";
		}

		/// <summary>反序列化一条绑定；格式不对时返回空绑定（不抛，坏存档不该让游戏起不来）。</summary>
		public static InputBinding ParseBinding(string s)
		{
			if (string.IsNullOrEmpty(s)) return InputBinding.None;
			var parts = s.Split('|');
			if (parts.Length < 3) return InputBinding.None;

			if (!int.TryParse(parts[0], out int slot)) return InputBinding.None;
			if (!int.TryParse(parts[1], out int device)) return InputBinding.None;
			if (!int.TryParse(parts[2], out int code)) return InputBinding.None;
			string mods = parts.Length > 3 ? parts[3] : "";

			return new InputBinding
			{
				Slot = (BindingSlot)slot,
				Device = (BindingDevice)device,
				Code = code,
				Ctrl = mods.Contains('C'),
				Shift = mods.Contains('S'),
				Alt = mods.Contains('A'),
				Meta = mods.Contains('M'),
			};
		}

		/// <summary>绑定的可读文本（含修饰键与鼠标侧键）。</summary>
		public static string BindingToText(InputBinding b)
		{
			if (b.IsEmpty) return "-";

			string core = b.Device == BindingDevice.Mouse
				? MouseToText((MouseButton)b.Code)
				: KeyToText((Key)b.Code);

			string prefix = "";
			if (b.Ctrl) prefix += "Ctrl+";
			if (b.Shift) prefix += "Shift+";
			if (b.Alt) prefix += "Alt+";
			if (b.Meta) prefix += "Win+";

			return prefix + core;
		}

		/// <summary>鼠标键可读文本。M4/M5 是侧键，是玩家最常用来做指令键的额外按键。</summary>
		public static string MouseToText(MouseButton b) => b switch
		{
			MouseButton.Left => "鼠标左键",
			MouseButton.Right => "鼠标右键",
			MouseButton.Middle => "鼠标中键",
			MouseButton.WheelUp => "滚轮上",
			MouseButton.WheelDown => "滚轮下",
			MouseButton.Xbutton1 => "鼠标侧键4",
			MouseButton.Xbutton2 => "鼠标侧键5",
			_ => "鼠标" + (int)b,
		};

		public static void Save()
		{
			var cfg = new ConfigFile();
			cfg.SetValue("general", "language", Language);
			cfg.SetValue("audio", "master", MasterVolume);
			cfg.SetValue("audio", "music", MusicVolume);
			cfg.SetValue("audio", "sfx", SfxVolume);
			cfg.SetValue("video", "fullscreen", Fullscreen);
			foreach (var kv in Bindings)
			{
				// 新格式：字符串数组，保留每个绑定的槽位/设备/修饰键。
				// 只存主键会让次要键位与鼠标绑定在重启后丢失。
				var arr = new Godot.Collections.Array<string>();
				foreach (var b in kv.Value)
					if (!b.IsEmpty) arr.Add(FormatBinding(b));
				cfg.SetValue("keys", kv.Key, arr);
			}
			cfg.Save(SavePath);
		}

		public static void ApplyAll()
		{
			Localization.SetLanguage(Language);
			Localization.ReportMissingKeys();
			ApplyAudio();
			ApplyVideo();
			ApplyKeybinds();
			ReportKeybindProblems();
		}

		/// <summary>
		/// 启动自检：把"当前键位"里的冲突报到日志。
		///
		/// 为什么值得单做一件事：本项目已经真实发生过一轮键位冲突
		/// （面板技能槽把 A/S/T/F1 全占了，而这几个是玩家最常用的指令键），
		/// 但当时没有任何地方会报出来，只能靠人肉读 5 个文件的硬编码才发现。
		/// 现在只要冲突就打印，改键位表时立刻能看见。
		/// </summary>
		public static void ReportKeybindProblems()
		{
			// 按**签名**去重：签名 = 设备 + 键 + 修饰键组合。
			// 这样 H 与 Ctrl+H 不会被误判成冲突（它们是不同的组合），
			// 而"同一个 Ctrl+H 绑给两个动作"仍然会被抓出来。
			var seen = new Dictionary<string, string>();
			foreach (var kv in Bindings)
			{
				var def = InputActions.Get(kv.Key);
				if (def != null && def.AllowsShare) continue;

				foreach (var b in kv.Value)
				{
					if (b.IsEmpty) continue;
					string sig = b.Signature();

					if (seen.TryGetValue(sig, out string other))
					{
						GD.PrintErr($"[Keys] 绑定冲突：{BindingToText(b)} 同时绑定 " +
							$"'{other}' 与 '{kv.Key}'。其中一个是技能槽、另一个是全局动作时，" +
							"面板会抢走按键（这正是 A/S/T/F1 曾经的问题）。");
						continue;
					}
					seen[sig] = kv.Key;
				}
			}
		}

		/// <summary>把键位编码成可读文本（A / F1 / ↑ / 空格 / …）。</summary>
		public static string KeyToText(Key key) => key switch
		{
			Key.None => "-",
			Key.Up => "↑",
			Key.Down => "↓",
			Key.Left => "←",
			Key.Right => "→",
			Key.Space => "空格",
			Key.Enter => "回车",
			Key.Escape => "Esc",
			Key.Tab => "Tab",
			Key.Backspace => "退格",
			>= Key.F1 and <= Key.F12 => "F" + ((int)key - (int)Key.F1 + 1),
			_ => key.ToString(),
		};

		public static void ApplyAudio()
		{
			AudioServer.SetBusVolumeDb(0, Mathf.LinearToDb(MasterVolume));
			EnsureBus("Music", MusicVolume);
			EnsureBus("Sfx", SfxVolume);
		}

		private static void EnsureBus(string name, float volume)
		{
			for (int i = 0; i < AudioServer.BusCount; i++)
			{
				if (AudioServer.GetBusName(i) == name)
				{
					AudioServer.SetBusVolumeDb(i, Mathf.LinearToDb(volume));
					return;
				}
			}
			AudioServer.AddBus();
			int index = AudioServer.BusCount - 1;
			AudioServer.SetBusName(index, name);
			AudioServer.SetBusVolumeDb(index, Mathf.LinearToDb(volume));
		}

		public static void ApplyVideo()
		{
			DisplayServer.WindowSetMode(Fullscreen
				? DisplayServer.WindowMode.Fullscreen
				: DisplayServer.WindowMode.Windowed);
		}

		public static void ApplyKeybinds()
		{
			// 主键投影先同步一次：老代码（自检/测试/菜单）会直接改 Keybinds，
			// 这里把它并回 Bindings 的主槽，保证两侧不会分叉。
			foreach (var kv in Keybinds)
			{
				if (!Bindings.TryGetValue(kv.Key, out var list)) continue;
				var cur = PrimaryKey(kv.Key);
				if (cur == kv.Value) continue;

				// Keybinds 被外部改过 → 以它为准更新主槽
				list.RemoveAll(b => b.Slot == BindingSlot.Primary);
				if (kv.Value != Key.None)
					list.Insert(0, InputBinding.Key(kv.Value, BindingSlot.Primary));
			}

			// 老存档把"选中类快捷键"落在 F1..F8 上，会和种族面板抢键。
			foreach (var def in InputActions.InGroup(InputGroup.Selection))
			{
				if (!Bindings.TryGetValue(def.Action, out var list)) continue;
				list.RemoveAll(b => b.Device == BindingDevice.Keyboard
					&& b.AsKey >= Key.F1 && b.AsKey <= Key.F8);
				Keybinds[def.Action] = PrimaryKey(def.Action);
			}

			foreach (var kv in Bindings)
			{
				if (!InputMap.HasAction(kv.Key))
					InputMap.AddAction(kv.Key);

				InputMap.ActionEraseEvents(kv.Key);

				// 面板技能槽与全局动作共用同一套 InputMap 动作，
				// 但**面板槽不注册物理键**：它们的按键由 ActionPanel 自己按
				// "格子位置 → 键" 直接读，注册进来只会和全局动作抢键。
				var def = InputActions.Get(kv.Key);
				if (def != null && def.Group == InputGroup.Panel)
					continue;

				foreach (var b in kv.Value)
				{
					if (b.IsEmpty) continue;
					InputMap.ActionAddEvent(kv.Key, b.ToEvent());
				}
			}
		}

		/// <summary>把某个动作恢复默认键。返回是否真的改了。</summary>
		public static bool ResetKeybind(string action)
		{
			var def = InputActions.Get(action);
			if (def == null) return false;
			var defs = InputActions.DefaultBindings(def);
			var cur = BindingsOf(action);
			if (cur.Count == defs.Count)
			{
				bool same = true;
				for (int i = 0; i < cur.Count; i++)
					if (!cur[i].SameAs(defs[i])) { same = false; break; }
				if (same) return false;
			}
			SetBindings(action, defs);
			return true;
		}

		/// <summary>把所有动作恢复默认键。返回改动条数。</summary>
		public static int ResetAllKeybinds()
		{
			int changed = 0;
			foreach (var def in InputActions.All)
			{
				var defs = InputActions.DefaultBindings(def);
				var cur = BindingsOf(def.Action);
				bool same = cur.Count == defs.Count;
				if (same)
					for (int i = 0; i < cur.Count; i++)
						if (!cur[i].SameAs(defs[i])) { same = false; break; }

				if (!same)
				{
					SetBindings(def.Action, defs);
					changed++;
				}
			}
			if (changed > 0)
				ApplyKeybinds();
			return changed;
		}

		/// <summary>是否有任何绑定与默认值不同（设置菜单用来决定"恢复默认"按钮是否可用）。</summary>
		public static bool HasCustomKeybinds()
		{
			foreach (var def in InputActions.All)
			{
				var defs = InputActions.DefaultBindings(def);
				var cur = BindingsOf(def.Action);
				bool same = cur.Count == defs.Count;
				if (same)
					for (int i = 0; i < cur.Count; i++)
						if (!cur[i].SameAs(defs[i])) { same = false; break; }
				if (!same) return true;
			}
			return false;
		}
	}
}
