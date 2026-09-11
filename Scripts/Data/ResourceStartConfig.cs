using Godot;

namespace RTS.Data // 必须和 RaceData 在同一个命名空间，或者被 RaceData 引用
{
	[GlobalClass]
	public partial class ResourceStartConfig : Resource
	{
		[Export] public ResourceType Type { get; set; } = ResourceType.Metal;
		[Export] public int Amount { get; set; } = 0;
		[Export] public int MaxCapacity { get; set; } = 1000;
	}
}
