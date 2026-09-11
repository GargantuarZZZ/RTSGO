using Godot;

namespace RTS.UI
{
	// 小地图实体层：位于迷雾层之后绘制，保证单位点/建筑点/相机框不被迷雾盖住
	public partial class MinimapEntityLayer : Control
	{
		private Minimap _owner;

		public void Setup(Minimap owner)
		{
			_owner = owner;
			MouseFilter = MouseFilterEnum.Ignore;
		}

		public override void _Draw()
		{
			_owner?.DrawEntityLayer(this);
		}
	}
}
