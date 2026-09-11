using Godot;
using System.Collections.Generic;
using System.Text;

namespace RTS.Settings
{
	// P2-6 本地化框架：CSV 配置（res://Localization/<code>.csv），回退链 当前语言 -> en_US -> key。
	// 可用语言列表为现代游戏常见语言；未提供翻译的语言自动回退英文。
	// 支持：
	//  - Tr(key) 普通取词
	//  - Tr(key, args) 带占位符 {0}/{1}... 格式化
	//  - ApplySceneTranslations(root) 场景节点按 metadata/tr_key 批量翻译
	//  - DetectSystemLanguage() 首次运行自动识别系统语言
	public static class Localization
	{
		public readonly record struct LanguageInfo(string Code, string NativeName);

		public static readonly LanguageInfo[] Languages =
		{
			new("zh_CN", "简体中文"),
			new("en_US", "English"),
			new("ja_JP", "日本語"),
			new("ko_KR", "한국어"),
			new("de_DE", "Deutsch"),
			new("fr_FR", "Français"),
			new("es_ES", "Español"),
			new("pt_BR", "Português (Brasil)"),
			new("ru_RU", "Русский"),
			new("it_IT", "Italiano"),
		};

		private static readonly Dictionary<string, Dictionary<string, string>> Tables = new();
		// 导出包兜底：源 CSV 不可读时直接按 key 查 .text.translation 资源
		private static readonly Dictionary<string, Translation> TranslationCache = new();
		public static string Current = "zh_CN";
		// P2-6：语言切换事件（UI 订阅后即时刷新文案）
		public static event System.Action LanguageChanged;

		public static void SetLanguage(string code)
		{
			string next = IsKnown(code) ? code : "zh_CN";
			if (next == Current && Tables.ContainsKey(next))
				return;
			Current = next;
			EnsureLoaded(Current);
			EnsureLoaded("en_US");
			LanguageChanged?.Invoke();
		}

		public static bool IsKnown(string code)
		{
			foreach (var lang in Languages)
				if (lang.Code == code)
					return true;
			return false;
		}

		public static string Tr(string key)
		{
			if (string.IsNullOrEmpty(key))
				return key;
			if (Tables.TryGetValue(Current, out var current) && current.TryGetValue(key, out var text))
				return text;
			if (Tables.TryGetValue("en_US", out var english) && english.TryGetValue(key, out var englishText))
				return englishText;
			string fromTrans = TranslationLookup(Current, key);
			if (!string.IsNullOrEmpty(fromTrans))
				return fromTrans;
			fromTrans = TranslationLookup("en_US", key);
			if (!string.IsNullOrEmpty(fromTrans))
				return fromTrans;
			return key;
		}

		/// <summary>带占位符的翻译：{0} {1}... 由 string.Format 填充。</summary>
		public static string Tr(string key, params object[] args)
		{
			if (args == null || args.Length == 0)
				return Tr(key);
			string template = Tr(key);
			try
			{
				return string.Format(template, args);
			}
			catch (System.FormatException)
			{
				return template;
			}
		}

		public static bool Has(string key)
		{
			if ((Tables.TryGetValue(Current, out var current) && current.ContainsKey(key)) ||
				(Tables.TryGetValue("en_US", out var english) && english.ContainsKey(key)))
				return true;
			return !string.IsNullOrEmpty(TranslationLookup(Current, key)) ||
				!string.IsNullOrEmpty(TranslationLookup("en_US", key));
		}

		// 导出后 CSV 被重映射，翻译表可能为空：直接按 key 查导入产物。
		// 注意：.text.translation 的 get_message_list() 为空，只能按 key 取。
		private static string TranslationLookup(string code, string key)
		{
			if (!TranslationCache.TryGetValue(code, out var trans))
			{
				trans = GD.Load<Translation>($"res://Localization/{code}.text.translation");
				if (trans == null)
					return "";
				TranslationCache[code] = trans;
			}

			string msg = trans.GetMessage(new StringName(key)).ToString();
			return string.IsNullOrEmpty(msg) || msg == key ? "" : msg;
		}

		/// <summary>内容名称（单位/建筑/科技/武器/Buff 等）：key = "name.&lt;id&gt;"，缺省回退原始 id。</summary>
		public static string TrName(string id)
		{
			if (string.IsNullOrEmpty(id))
				return id;
			return Tr("name." + id);
		}

		/// <summary>
		/// 资源显示名：key = "resource.&lt;type小写&gt;"。
		///
		/// 为什么单独包一层：`Tr` 找不到条目时会**原样返回 key**，
		/// 于是缺翻译的资源会在 HUD 上直接显示 `resource.nanobots` 这种裸 key
		/// （实测 Nano 的资源条就是这个）——调用方完全无从察觉。
		/// 这里做两件事：缺条目时给出可读回退名，并把缺失报到日志。
		/// </summary>
		public static string ResourceName(ResourceType type)
		{
			string key = "resource." + type.ToString().ToLowerInvariant();
			if (Has(key))
				return Tr(key);

			if (!_missingResourceKeys.Contains(key))
			{
				_missingResourceKeys.Add(key);
				GD.PrintErr($"[Localization] 缺少资源本地化条目 '{key}'，" +
					$"HUD 会退化成可读名。请补 Localization/zh_CN.csv 与 en_US.csv。");
			}
			// 回退：把枚举名拆成可读形式（NanoBots → Nano Bots），至少不是裸 key
			return System.Text.RegularExpressions.Regex.Replace(
				type.ToString(), "(?<=[a-z])(?=[A-Z])", " ");
		}

		private static readonly System.Collections.Generic.HashSet<string> _missingResourceKeys = new();

		/// <summary>
		/// 自检：把代码里会动态拼出来、因而最容易漏翻译的 key 全查一遍。
		/// 启动时调用一次，缺什么直接报出来。
		/// </summary>
		public static void ReportMissingKeys()
		{
			var missing = new System.Collections.Generic.List<string>();

			// 资源：由枚举动态拼，新增资源类型时最容易漏
			foreach (ResourceType t in System.Enum.GetValues(typeof(ResourceType)))
			{
				string key = "resource." + t.ToString().ToLowerInvariant();
				if (!Has(key)) missing.Add(key);
			}

			if (missing.Count > 0)
				GD.PrintErr($"[Localization] 以下 key 缺翻译，界面会显示裸 key：{string.Join(", ", missing)}");
			else
				GD.Print("[Localization] 动态 key 自检通过（资源名齐全）");
		}

		/// <summary>翻译场景根节点下所有带 metadata/tr_key 的 Label / Button / RichTextLabel。</summary>
		public static void ApplySceneTranslations(Node root)
		{
			if (root == null)
				return;
			ApplyNode(root);
			foreach (Node child in root.GetChildren())
				ApplySceneTranslations(child);
		}

		private static void ApplyNode(Node node)
		{
			if (node is not Control control || !control.HasMeta("tr_key"))
				return;

			string key = control.GetMeta("tr_key").AsString();
			if (string.IsNullOrEmpty(key))
				return;

			string text = Tr(key);
			if (node is Label label)
				label.Text = text;
			else if (node is Button button)
				button.Text = text;
			else if (node is RichTextLabel rich)
				rich.Text = text;
		}

		/// <summary>首次运行：根据系统语言自动选择可用语言，匹配不到回退默认语言。</summary>
		public static string DetectSystemLanguage()
		{
			string loc = OS.GetLocale().Replace("-", "_");
			if (string.IsNullOrEmpty(loc))
				return "zh_CN";

			// 精确匹配（zh_CN / en_US / ja_JP ...）
			foreach (var lang in Languages)
			{
				if (loc.StartsWith(lang.Code, System.StringComparison.OrdinalIgnoreCase))
					return lang.Code;
			}

			// 语言代码前缀匹配（zh / en / ja ...）
			string baseLang = loc.Split('_')[0].ToLowerInvariant();
			foreach (var lang in Languages)
			{
				string codeBase = lang.Code.Split('_')[0].ToLowerInvariant();
				if (codeBase == baseLang)
					return lang.Code;
			}

			return "zh_CN";
		}

		public static void EnsureLoaded(string code)
		{
			if (Tables.ContainsKey(code))
				return;
			Tables[code] = LoadCsv(code);
		}

		public static void ReloadAll()
		{
			Tables.Clear();
			SetLanguage(Current);
		}

		private static Dictionary<string, string> LoadCsv(string code)
		{
			string path = $"res://Localization/{code}.csv";
			var table = new Dictionary<string, string>();
			if (!FileAccess.FileExists(path))
				return LoadTranslationFallback(code);

			using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
			if (file == null)
				return LoadTranslationFallback(code);

			bool first = true;
			while (!file.EofReached())
			{
				string line = file.GetLine().TrimEnd('\r', '\n');
				if (first)
				{
					first = false;
					continue;
				}
				if (line.Length == 0 || line.StartsWith("#"))
					continue;

				// key,value —— 值支持引号包裹（含逗号、"" 转义引号）
				int comma = line.IndexOf(',');
				if (comma <= 0)
					continue;
				string key = line[..comma].Trim();
				if (key.Length == 0)
					continue;
				table[key] = ParseCsvValue(line[(comma + 1)..]);
			}
			return table;
		}

		// 导出后源 CSV 会被 Godot 重映射（.csv -> .text.translation），
		// FileAccess 读不到源文件，改用导入产物 Translation 资源填充翻译表。
		private static Dictionary<string, string> LoadTranslationFallback(string code)
		{
			var table = new Dictionary<string, string>();
			string transPath = $"res://Localization/{code}.text.translation";
			if (!ResourceLoader.Exists(transPath))
				return table;

			var trans = GD.Load<Translation>(transPath);
			if (trans == null)
				return table;

			foreach (var key in trans.GetMessageList())
			{
				string text = trans.GetMessage(key).ToString();
				table[key.ToString()] = text;
			}
			return table;
		}

		private static string ParseCsvValue(string raw)
		{
			raw = raw.Trim();
			if (raw.Length < 2 || raw[0] != '"')
				return raw.Replace("\\n", "\n");

			var sb = new StringBuilder(raw.Length);
			int i = 1;
			while (i < raw.Length)
			{
				char c = raw[i];
				if (c == '"')
				{
					if (i + 1 < raw.Length && raw[i + 1] == '"')
					{
						sb.Append('"');
						i += 2;
						continue;
					}
					// 闭合引号，忽略其后内容
					break;
				}
				sb.Append(c);
				i++;
			}
			return sb.ToString().Replace("\\n", "\n");
		}
	}
}
