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
		public static readonly Dictionary<string, Key> Keybinds = BuildDefaultKeybinds();

		private static Dictionary<string, Key> BuildDefaultKeybinds()
		{
			var map = new Dictionary<string, Key>();
			foreach (var def in InputActions.All)
				map[def.Action] = def.Default;
			return map;
		}

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
				var raw = cfg.GetValue("keys", action, -1L);
				long keyCode = raw.AsInt64();
				if (keyCode > 0)
					Keybinds[action] = (Key)keyCode;
			}

			ApplyAll();
		}

		public static void Save()
		{
			var cfg = new ConfigFile();
			cfg.SetValue("general", "language", Language);
			cfg.SetValue("audio", "master", MasterVolume);
			cfg.SetValue("audio", "music", MusicVolume);
			cfg.SetValue("audio", "sfx", SfxVolume);
			cfg.SetValue("video", "fullscreen", Fullscreen);
			foreach (var kv in Keybinds)
				cfg.SetValue("keys", kv.Key, (long)kv.Value);
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
			var seen = new Dictionary<Key, string>();
			foreach (var kv in Keybinds)
			{
				if (kv.Value == Key.None) continue;

				var def = InputActions.Get(kv.Key);
				if (def != null && def.AllowsShare) continue;

				if (seen.TryGetValue(kv.Value, out string other))
				{
					GD.PrintErr($"[Keys] 键位冲突：{kv.Value} 同时绑定 " +
						$"'{other}' 与 '{kv.Key}'。其中一个是技能槽、另一个是全局动作时，" +
						"面板会抢走按键（这正是 A/S/T/F1 曾经的问题）。");
					continue;
				}
				seen[kv.Value] = kv.Key;
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
			foreach (var kv in Keybinds)
			{
				if (kv.Value == Key.None)
					continue;

				if (!InputMap.HasAction(kv.Key))
					InputMap.AddAction(kv.Key);

				InputMap.ActionEraseEvents(kv.Key);

				// 面板技能槽与全局动作共用同一套 InputMap 动作，
				// 但**面板槽不注册物理键**：它们的按键由 ActionPanel 自己按
				// "格子位置 → 键" 直接读，注册进来只会和全局动作抢键。
				var def = InputActions.Get(kv.Key);
				if (def != null && def.Group == InputGroup.Panel)
					continue;

				InputMap.ActionAddEvent(kv.Key, new InputEventKey { PhysicalKeycode = kv.Value });
			}
		}

		/// <summary>把某个动作恢复默认键。返回是否真的改了。</summary>
		public static bool ResetKeybind(string action)
		{
			var def = InputActions.Get(action);
			if (def == null || Keybinds[action] == def.Default)
				return false;
			Keybinds[action] = def.Default;
			return true;
		}

		/// <summary>把所有动作恢复默认键。返回改动条数。</summary>
		public static int ResetAllKeybinds()
		{
			int changed = 0;
			foreach (var def in InputActions.All)
			{
				if (!Keybinds.TryGetValue(def.Action, out var cur) || cur != def.Default)
				{
					Keybinds[def.Action] = def.Default;
					changed++;
				}
			}
			if (changed > 0)
				ApplyKeybinds();
			return changed;
		}

		/// <summary>是否有任何键位与默认值不同（设置菜单用来决定"恢复默认"按钮是否可用）。</summary>
		public static bool HasCustomKeybinds()
		{
			foreach (var def in InputActions.All)
				if (Keybinds.TryGetValue(def.Action, out var cur) && cur != def.Default)
					return true;
			return false;
		}
	}
}
