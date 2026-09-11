using Godot;
using RTS.Core;
using RTS.Data;

namespace RTS.Units
{
	// 矿点剩余量悬浮文字：实时显示资源剩余，采空后随节点一起消失
	public partial class ResourceAmountLabel : Node3D
	{
		private Label3D _label;
		private IEntity _owner;

		public override void _Ready()
		{
			_owner = GetParent() as IEntity;
			_label = new Label3D
			{
				Name = "AmountLabel",
				FontSize = 72,
				OutlineSize = 10,
				Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
				Modulate = Colors.White,
				OutlineModulate = Colors.Black,
				Position = new Vector3(0f, 110f, 0f)
			};
			AddChild(_label);
		}

		public override void _Process(double delta)
		{
			if (_owner?.LogicEntity == null)
				return;

			int amount = (int)Mathf.Floor((float)_owner.LogicEntity.ResourceAmount);
			_label.Text = amount.ToString();
			_label.Visible = amount > 0;
		}
	}
}
