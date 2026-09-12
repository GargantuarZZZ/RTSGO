using Godot;
using System.Collections.Generic;
using System.Text;

namespace RTS.Tools
{
	// =========================================================
	// 硬编码中文审计
	//
	// 背景：这个项目里"用户可见文案"大量直接写成中文字面量。
	// 靠肉眼找必然漏 —— 实测漏掉了主菜单上最显眼的「教程」「地图编辑器」
	// 两个按钮（它们就在 MainMenuController 里写着中文）。
	//
	// 本工具把"还剩多少硬编码中文、分别是哪一行"变成**可审计的数字**，
	// 而且区分**用户可见**与**仅日志/诊断**：
	//   · 日志（GD.Print*/PrintErr*）不需要翻译，翻译了反而看不懂调试信息；
	//   · 注释不需要翻译；
	//   · 其余（UI 文本、字符串常量、异常消息）都是候选。
	//
	// 为什么不自动替换：文案要**双语一起改写**（去口语化 + 统一 RTS 术语），
	// 机器替换只能给出机翻，质量比中文回退更差（错的英文更难发现）。
	// 所以本工具只负责"找全 + 计数 + 给清单"，改写由人做。
	//
	// 运行：HW_ZH_AUDIT=1 godot --headless --path . res://Scenes/Menu/UI_MainMenu.tscn
	// =========================================================

	public partial class HardcodedZhAudit : Node
	{
		public override void _Ready()
		{
			if (System.Environment.GetEnvironmentVariable("HW_ZH_AUDIT") != "1")
			{
				QueueFree();
				return;
			}
			Callable.From(Run).CallDeferred();
		}

		private void Run()
		{
			var byFile = new Dictionary<string, int>();
			var uiHits = new List<string>();
			var otherHits = new List<string>();
			int totalUi = 0, totalOther = 0;

			ScanDir("res://Scripts", byFile, uiHits, otherHits, ref totalUi, ref totalOther);

			var sb = new StringBuilder();
			sb.AppendLine($"[HwZh] 硬编码中文：用户可见 {totalUi} 处 / 其他(含异常消息) {totalOther} 处");

			sb.AppendLine("[HwZh] --- 按文件 ---");
			foreach (var kv in SortedByCount(byFile))
				sb.AppendLine($"    {kv.Key,-52} {kv.Value}");

			sb.AppendLine("[HwZh] --- 用户可见（前 60 条）---");
			int n = 0;
			foreach (string h in uiHits)
			{
				sb.AppendLine("    " + h);
				if (++n >= 60) { sb.AppendLine("    …"); break; }
			}

			// 完整清单写入文件：1197 条不可能在日志里看
			string outPath = "user://hardcoded_zh.csv";
			using (var f = FileAccess.Open(outPath, FileAccess.ModeFlags.Write))
			{
				if (f != null)
				{
					f.StoreLine("kind,file_line,text");
					foreach (string h in uiHits)
						f.StoreLine("ui,\"" + h.Replace("\"", "\"\"") + "\"");
					foreach (string h in otherHits)
						f.StoreLine("log,\"" + h.Replace("\"", "\"\"") + "\"");
				}
			}
			sb.AppendLine($"[HwZh] 完整清单 → {ProjectSettings.GlobalizePath(outPath)}");

			GD.Print(sb.ToString());
		}

		private static List<KeyValuePair<string, int>> SortedByCount(Dictionary<string, int> d)
		{
			var list = new List<KeyValuePair<string, int>>(d);
			list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value)
				: string.CompareOrdinal(a.Key, b.Key));
			return list;
		}

		private static void ScanDir(string dir, Dictionary<string, int> byFile,
			List<string> uiHits, List<string> otherHits, ref int totalUi, ref int totalOther)
		{
			using var d = DirAccess.Open(dir);
			if (d == null) return;

			d.ListDirBegin();
			string name = d.GetNext();
			while (name != "")
			{
				string full = dir + "/" + name;
				if (d.CurrentIsDir())
				{
					if (name != "obj" && name != "bin") ScanDir(full, byFile, uiHits, otherHits, ref totalUi, ref totalOther);
				}
				else if (name.EndsWith(".cs"))
				{
					ScanFile(full, byFile, uiHits, otherHits, ref totalUi, ref totalOther);
				}
				name = d.GetNext();
			}
			d.ListDirEnd();
		}

		private static void ScanFile(string path, Dictionary<string, int> byFile,
			List<string> uiHits, List<string> otherHits, ref int totalUi, ref int totalOther)
		{
			using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
			if (f == null) return;

			int lineNo = 0;
			while (!f.EofReached())
			{
				string raw = f.GetLine();
				lineNo++;
				if (!ContainsHan(raw)) continue;

				string t = raw.TrimStart();

				// 注释：不算
				if (t.StartsWith("//") || t.StartsWith("///") || t.StartsWith("*") || t.StartsWith("/*"))
					continue;

				// 日志/诊断：不算用户可见文案
				bool isLog = t.Contains("GD.Print") || t.Contains("GD.PushError")
					|| t.Contains("GD.PushWarning");

				string shortPath = path.Replace("res://Scripts/", "");
				if (!byFile.ContainsKey(shortPath)) byFile[shortPath] = 0;
				byFile[shortPath]++;

				string entry = $"{shortPath}:{lineNo}  {t}";
				if (isLog) otherHits.Add(entry);
				else { uiHits.Add(entry); totalUi++; }
				if (isLog) totalOther++;
			}
		}

		private static bool ContainsHan(string s)
		{
			foreach (char c in s)
				if (c >= 0x4E00 && c <= 0x9FFF) return true;
			return false;
		}
	}
}
