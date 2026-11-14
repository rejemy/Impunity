using System;

namespace DevTools
{
	// What `dev status` reports about the game, filled in by the game's own code. Read by the
	// `dev status` Pipeline command and `tools/ucmd wait ingame` (which waits for InGame).
	public static class DevStatus
	{
		// True once the game is ready to be driven: connected, joined, in a scene — whatever "in
		// the game" means for it. Unset = false.
		public static Func<bool> InGame;

		// Anything else worth a glance from the shell, as a serializable object (an anonymous
		// object or a dictionary), reported under `game`: connection mode, players, … Null = none.
		public static Func<object> Details;

		public static bool IsInGame()
		{
			try
			{
				return InGame != null && InGame();
			}
			catch (Exception)
			{
				return false;
			}
		}

		public static object GetDetails()
		{
			try
			{
				return Details?.Invoke();
			}
			catch (Exception e)
			{
				return new { error = e.Message };
			}
		}
	}
}
