// 文件: res://Scripts/Simulation/Math/FPVector2.cs
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	public struct FPVector2
	{
		public FP X;
		public FP Y;

		public FPVector2(FP x, FP y) { X = x; Y = y; }

		public static FPVector2 Zero => new FPVector2(FP.Zero, FP.Zero);

		// 基础运算符重载
		public static FPVector2 operator +(FPVector2 a, FPVector2 b) => new FPVector2(a.X + b.X, a.Y + b.Y);
		public static FPVector2 operator -(FPVector2 a, FPVector2 b) => new FPVector2(a.X - b.X, a.Y - b.Y);
		public static FPVector2 operator *(FPVector2 a, FP b) => new FPVector2(a.X * b, a.Y * b);
		public static FPVector2 operator /(FPVector2 a, FP b) => new FPVector2(a.X / b, a.Y / b);

		public FP MagnitudeSquared() => X * X + Y * Y;
		public FP Magnitude() => FP.Sqrt(X * X + Y * Y);

		public FPVector2 Normalized()
		{
			FP mag = Magnitude();
			if (mag == FP.Zero) return FPVector2.Zero;
			return new FPVector2(X / mag, Y / mag);
		}

		public static FP DistanceSquared(FPVector2 a, FPVector2 b)
		{
			FP dx = a.X - b.X;
			FP dy = a.Y - b.Y;
			return dx * dx + dy * dy;
		}

		// 圆形判定：两点距离是否 <= 半径
		public static bool IsWithinRange(FPVector2 a, FPVector2 b, FP radius)
		{
			return DistanceSquared(a, b) <= radius * radius;
		}

		// 圆形判定：两点距离平方是否 <= 半径平方（调用方已算好 rSq 时避免重复乘法）
		public static bool IsWithinRangeSq(FPVector2 a, FPVector2 b, FP radiusSq)
		{
			return DistanceSquared(a, b) <= radiusSq;
		}
	}
}
