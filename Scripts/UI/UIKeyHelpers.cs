using Godot;

namespace RTS.Core
{
	/// <summary>
	/// UI 快捷键显示工具：把 Key 渲染成按钮上那截 `[X]` 文本。
	///
	/// 直接转发到 `GameSettings.KeyToText` —— 之前这里单独实现了一份，
	/// 只认 F1~F12 和主键盘数字，遇到方向键/空格/Key.None 会算出负数
	/// （`(int)Key.None - (int)Key.Key0` 是负的），按钮上就会出现 `[-49]` 这种东西。
	/// 键位显示只能有一个实现，否则迟早分叉。
	/// </summary>
	public static class UIKeyHelpers
	{
		public static string KeyToText(Key key) => RTS.Settings.GameSettings.KeyToText(key);
	}
}
