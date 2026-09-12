using Godot;
using System.Collections.Generic;
using System.Text;

namespace RTS.Tools
{
	// =========================================================
	// 教程/图鉴文案导出（本地化工作清单 + 缺口断言）
	//
	// 为什么需要它：教程与图鉴的文案是**代码里的中文字面量**（约 700 条），
	// 不是 CSV 里的 key。要本地化就必须先有确定的 `key → 文案` 对照表；
	// 手写必漏，而漏了不会报错（界面上直接显示中文或裸 key）。
	//
	// 本工具从**运行时真实的教程注册表**导出，所以不可能漏：
	// 玩家能看到的目标/理论页文案都在这里。
	//
	// 运行：
	//   godot --headless --path . res://Scenes/Tools/ExportTutorialText.tscn
	// 输出：
	//   user://tutorial_l10n_dump.csv   （key,zh_CN —— 直接并入 Localization/*.csv）
	//
	// 顺带当断言用：报告 Localization 表里还缺哪些 key。
	// =========================================================

	public partial class ExportTutorialText : Node
	{
		public override void _Ready()
		{
			RTS.Data.Configs.ConfigDatabase.LoadAll();
			RTS.Tutorial.TutorialCodex.HydrateAll(RTS.Tutorial.TutorialRegistry.All);

			var rows = new List<(string Key, string Text)>();
			var seen = new HashSet<string>();

			void Add(string key, string text)
			{
				if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return;
				if (!seen.Add(key)) return;
				rows.Add((key, text));
			}

			foreach (var def in RTS.Tutorial.TutorialRegistry.All)
			{
				string tid = $"{def.Tier}_{def.Id}";

				Add($"tutorial.{tid}.name", def.DisplayName);
				Add($"tutorial.{tid}.intro", def.Intro);

				foreach (var o in def.Objectives)
				{
					if (o == null) continue;
					string baseKey = $"tutorial.{tid}.{o.Id}";
					Add(baseKey + ".title", o.Title);
					Add(baseKey + ".detail", o.Detail);
					Add(baseKey + ".done", o.CompleteText);
				}

				// 理论页：分节标题 + 页标题
				foreach (var p in def.Pages)
				{
					if (p == null) continue;
					Add("codex.section." + p.Section, p.Section);
					Add("codex.page." + p.Title, p.Title);
				}
			}

			var sb = new StringBuilder();
			sb.AppendLine("key,zh_CN");
			foreach (var (k, v) in rows)
			{
				string esc = v.Replace("\"", "\"\"").Replace("\n", "\\n");
				sb.AppendLine($"{k},\"{esc}\"");
			}

			string path = "user://tutorial_l10n_dump.csv";
			using (var f = FileAccess.Open(path, FileAccess.ModeFlags.Write))
			{
				if (f == null)
				{
					GD.PrintErr($"[L10nDump] 无法写 {path}");
					GetTree().Quit(1);
					return;
				}
				f.StoreString(sb.ToString());
			}

			GD.Print($"[L10nDump] 导出 {rows.Count} 条 → {ProjectSettings.GlobalizePath(path)}");

			// 缺口断言：这些 key 在界面渲染时会被用到，缺失就回退中文
			int missing = 0;
			foreach (var (k, _) in rows)
				if (!RTS.Settings.Localization.Has(k)) missing++;

			GD.Print(missing == 0
				? "[L10nDump] Localization 表已覆盖全部教程文案"
				: $"[L10nDump] ★ Localization 表缺 {missing} 条（这些键在英文下会回退成中文）");

			// L10N_RESOLVE=1：把**当前语言下实际会显示**的文案也导出。
			// 这是唯一能证明"英文真的被用上"的检查 ——
			// 只比对 CSV 只能证明"表里有条目"，证明不了渲染路径用到了它。
			if (System.Environment.GetEnvironmentVariable("L10N_RESOLVE") == "1")
				DumpResolved(rows);

			GetTree().Quit(0);
		}

		/// <summary>导出当前语言下解析后的文案（用于核对英文是否生效）。</summary>
		private static void DumpResolved(List<(string Key, string Text)> rows)
		{
			var sb = new StringBuilder();
			int replaced = 0, fallback = 0;
			foreach (var (k, raw) in rows)
			{
				string v = RTS.Settings.Localization.Has(k) ? RTS.Settings.Localization.Tr(k) : null;
				if (v == null) { v = raw; fallback++; }
				else replaced++;
				string esc = v.Replace("\"", "\"\"").Replace("\n", "\\n");
				sb.AppendLine($"{k},\"{esc}\"");
			}

			string outPath = "user://tutorial_resolved.csv";
			using (var f = FileAccess.Open(outPath, FileAccess.ModeFlags.Write))
				f?.StoreString(sb.ToString());

			GD.Print($"[L10nResolve] 语言={RTS.Settings.Localization.Current} " +
				$"已翻译={replaced} 回退原文={fallback} → {ProjectSettings.GlobalizePath(outPath)}");
		}
	}
}
