using Godot;

namespace RTS.Data
{
	public class EntityAction
	{
		public string ActionId;      // 逻辑标识符
		public int SlotIndex;        // UI 位置
		public string DisplayName;   // 显示文本
		public string StatusText = ""; // 切换类技能的当前状态，独立于技能名称
		public Texture2D Icon;
		public bool IsEnabled = true; // 默认为 true
		public Color Tint = Colors.White; // 按钮着色（已研究=绿）
		public float CooldownRemaining = 0f; // 技能冷却剩余（秒）
		public float CooldownMax = 0f;      // 技能冷却总量（秒）
		public string Tooltip = "";          // 附加悬浮提示（如建造前置条件）

		// --- [核心修复 1] 添加无参构造函数 ---
		// 有了这个，new EntityAction { ... } 写法才能生效
		public EntityAction() { }

		// --- [核心修复 2] 添加属性兼容 (可选) ---
		// 你的 Unit.cs 里写的是 ActionName = ...
		// 这里做一个属性转发，自动把它存到 ActionId 和 DisplayName 里
		public string ActionName
		{
			set
			{
				ActionId = value;
				DisplayName = value;
			}
		}

		// 保留原有的构造函数，防止其他代码报错
		public EntityAction(int slot, string id, string name, bool enabled = true, Texture2D icon = null)
		{
			SlotIndex = slot;
			ActionId = id;
			DisplayName = name;
			IsEnabled = enabled;
			Icon = icon;
		}

	}
}
