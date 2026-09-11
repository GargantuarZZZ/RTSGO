using Godot;

namespace RTS.UI
{
	// =========================================================
	// UI 栅格规范（唯一来源）
	//
	// 为什么需要它：仓库里此前**没有**任何 UI 栅格约定，
	// 每个面板、每个按钮都是各自硬编码 Position/Size。
	// 结果就是反复出问题：控件互相遮挡、宽窗口下挤出画面、
	// 同一类元素在不同页面大小不一致。
	//
	// 本文件只定义**常量与取整函数**，不碰任何具体面板。
	// 所有 UI 必须从这里取尺寸，不允许再写字面量坐标。
	//
	// 基准：8px 栅格。所有间距/尺寸都是 8 的倍数，
	// 这样任何两个元素只要都在栅格上对齐，就不会出现"差 1~3px 的边缘打架"。
	// =========================================================

	public static class UiLayout
	{
		/// <summary>栅格基准单位（px）。所有间距与尺寸都取它的整数倍。</summary>
		public const int Unit = 8;

		// ---- 间距刻度（都用 Unit 表达，改 Unit 即可整体缩放）----
		public const int Space1 = Unit * 1;    // 8   紧凑（图标与文字）
		public const int Space2 = Unit * 2;    // 16  同组元素之间
		public const int Space3 = Unit * 3;    // 24  标准间距
		public const int Space4 = Unit * 4;    // 32  分组之间
		public const int Space6 = Unit * 6;    // 48  大分区之间
		public const int Space8 = Unit * 8;    // 64  页面边距

		/// <summary>页面外边距。</summary>
		public const int PageMargin = Space6;

		// ---- 控件尺寸 ----
		public const int RowHeight = Unit * 4;      // 32  列表行 / 下拉框
		public const int ButtonHeight = Unit * 4;   // 32  普通按钮
		public const int ButtonHeightLg = Unit * 5; // 40  主操作按钮
		public const int HeaderHeight = Unit * 3;   // 24  分段标题
		public const int LabelWidth = Unit * 14;    // 112 列表里的名称列

		// ---- 字号 ----
		public const int FontTitle = 40;   // 页面标题（用字号实现，不用 scale 缩放）
		public const int FontSection = 16; // 分段标题
		public const int FontBody = 14;    // 正文/按钮
		public const int FontSmall = 12;   // 次要说明

		/// <summary>把任意值吸附到栅格（就近取整，最小 1 格）。</summary>
		public static int Snap(float v)
		{
			int n = Mathf.RoundToInt(v / Unit);
			if (n < 1) n = 1;
			return n * Unit;
		}

		/// <summary>构造一个吸附到栅格的 Vector2 尺寸。</summary>
		public static Vector2 Size(float w, float h) => new(Snap(w), Snap(h));

		/// <summary>构造一个吸附到栅格的坐标。</summary>
		public static Vector2 At(float x, float y) => new(Snap(x), Snap(y));

		/// <summary>
		/// 内容区宽度 = 视口宽 - 两侧页边距。
		/// 面板宽度一律用"两列"表达：左列固定内容宽，右列吃掉剩余。
		/// </summary>
		public static float ContentWidth(Control root) =>
			Mathf.Max(Unit * 20, root.Size.X - PageMargin * 2);

		/// <summary>左列宽度（按钮/开关等操作控件）。</summary>
		public const int LeftColumnWidth = Unit * 24;   // 192

		/// <summary>
		/// 右列（列表）左边界：左列宽 + 间距。
		/// 用这个而不是硬编码 386，任何页面都一致。
		/// </summary>
		public static float RightColumnX(Control root) => PageMargin + LeftColumnWidth + Space4;
	}
}
