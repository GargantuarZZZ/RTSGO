// File: res://Scripts/Core/FormationSolver.cs
using System.Collections.Generic;
using FixMath.NET;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
	public static class FormationSolver
	{
		// 定点数版本，供逻辑层调用
		public static List<FPVector2> SolveFP(int count, FPVector2 targetCenter, FP spacing, FPVector2 avgPos)
		{
			if (count == 0) return new List<FPVector2>();
			if (count == 1) return new List<FPVector2> { targetCenter };

			// 计算行列
			int cols = (int)FP.Sqrt((FP)count);
			if (cols * cols < count) cols++;
			int rows = (count + cols - 1) / cols;

			FP matrixWidth = (FP)(cols - 1) * spacing;

			// 计算方向 (朝向目标)
			FPVector2 dir = (targetCenter - avgPos).Normalized();
			if (dir.MagnitudeSquared() < (FP)0.001m) dir = new FPVector2(FP.One, FP.Zero);

			var results = new List<FPVector2>();
			for (int i = 0; i < count; i++)
			{
				FP x = (FP)(i / cols) * -spacing + ((FP)(rows - 1) * spacing / (FP)2m);
				FP y = (FP)(i % cols) * spacing - (matrixWidth / (FP)2m);

				// 旋转矩阵
				FP rotX = x * dir.X - y * dir.Y;
				FP rotY = x * dir.Y + y * dir.X;

				results.Add(targetCenter + new FPVector2(rotX, rotY));
			}
			return results;
		}
	}
}
