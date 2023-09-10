using UnityEngine;

namespace Dreamwing
{
	public static class Easers
	{
		public delegate float Easer(float fraction);

		// Transitional eases (0 - 1)

		public static float Linear(float fraction)
		{
			return fraction;
		}

		public static float Accel(float fraction)
		{
			return fraction * fraction;
		}

		public static float Deccel(float fraction)
		{
			return fraction * (2.0f - fraction);
		}

		public static float AccelDeccel(float fraction)
		{
			if (fraction < 0.5f)
			{
				return fraction * (fraction / 0.5f);
			}
			else
			{
				float decTime = fraction - 0.5f;
				float decProportion = decTime / 0.5f;
				return 0.5f + decTime * (2.0f - decProportion);
			}

		}

		public static float Bounce(float fraction)
		{
			if (fraction < (1.0f / 2.75f))
				return 7.5625f * fraction * fraction;
			else if (fraction < (2.0f / 2.75f))
				return 7.5625f * (fraction -= (1.5f / 2.75f)) * fraction + 0.75f;
			else if (fraction < (2.5f / 2.75f))
				return 7.5625f * (fraction -= (2.25f / 2.75f)) * fraction + 0.9375f;
			else if (fraction < 1.0f)
				return 7.5625f * (fraction -= (2.625f / 2.75f)) * fraction + 0.984375f;
			else
				return 1.0f;
		}

		public static float Elastic(float fraction)
		{
			if (fraction == 0.0f)
				return 0.0f;
			else if (fraction == 1.0f)
				return 1.0f;

			const float p = 0.3f;
			const float s = 0.3f / 4.0f;

			return Mathf.Pow(2.0f, -10.0f * fraction) * Mathf.Sin((fraction - s) * (2.0f * Mathf.PI) / p) + 1.0f;
		}

		// Transitory eases (0 - 1 - 0)

		public static float Sine(float fraction)
		{
			if (fraction < 1.0f)
				return Mathf.Sin(fraction * Mathf.PI);
			else
				return 0.0f;
		}

		public static float Sine4(float fraction)
		{
			if (fraction < 1.0f)
				return Mathf.Sin(fraction * 4.0f * Mathf.PI);
			else
				return 0.0f;
		}

		public static float Sine4Decay(float fraction)
		{
			if (fraction < 1.0f)
				return Mathf.Sin(fraction * 3.0f * Mathf.PI) * (1.0f - fraction);
			else
				return 0.0f;
		}

		public static float Hold(float fraction)
		{
			return fraction >= 1.0f ? 0.0f : 1.0f;
		}
	}

}
