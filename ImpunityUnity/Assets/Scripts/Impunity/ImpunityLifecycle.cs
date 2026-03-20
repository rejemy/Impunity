namespace Impunity
{
	/// <summary>Global shutdown flag for all Impunity networking threads. Call <see cref="CleanupAll"/> at application shutdown to signal all socket listener threads to exit cleanly.</summary>
	public static class ImpunityLifecycle
	{
		/// <summary>When true, all Impunity networking threads should exit their loops.</summary>
		public static volatile bool ShuttingDown;

		/// <summary>Signals all Impunity networking threads to shut down. Socket listener loops will exit within ~1 second.</summary>
		public static void CleanupAll()
		{
			ShuttingDown = true;
		}

		/// <summary>Resets the shutdown flag. Call this if re-initializing Impunity after a previous shutdown.</summary>
		public static void Reset()
		{
			ShuttingDown = false;
		}
	}
}
