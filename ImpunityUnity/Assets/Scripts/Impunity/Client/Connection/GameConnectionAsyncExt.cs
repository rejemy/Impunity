using System.Collections.Generic;
using System.Threading.Tasks;

using UltraLiteDB;

using Impunity.GameState;

namespace Impunity.Connection
{
	public static class ConnectionAsyncExtensions
	{

		private static void CompleteTask(TaskCompletionSource<bool> taskSource, ImpunityError err)
		{
			if (err != null)
			{
				taskSource.SetException(new ImpuntyErrorException(err));
			}
			else
			{
				taskSource.SetResult(true);
			}
		}

		private static void CompleteTask<TResult>(TaskCompletionSource<TResult> taskSource, ImpunityError err, TResult result)
		{
			if (err != null)
			{
				taskSource.SetException(new ImpuntyErrorException(err));
			}
			else
			{
				taskSource.SetResult(result);
			}
		}

		// ---------- API

		public static Task ConnectAsync(this BaseGameConnection connection)
		{
			var t = new TaskCompletionSource<bool>();
			connection.Connect( (err) => CompleteTask(t, err) );
			return t.Task;
		}

		public static Task<List<ActionResult>> CompoundActionAsync(this BaseGameConnection connection, IEnumerable<GameStateActionBase> actions)
		{
			var t = new TaskCompletionSource<List<ActionResult>>();
			connection.CompoundAction(actions, (err, result) => CompleteTask(t, err, result));
			return t.Task;
		}

		// -------- DB actions

		public static Task SetGameSummaryAsync(this BaseGameConnection connection, BsonDocument summary)
		{
			var t = new TaskCompletionSource<bool>();
			connection.SetGameSummary(summary, (err) => CompleteTask(t, err) );
			return t.Task;
		}

		public static Task<BsonDocument> GetGameSummaryAsync(this BaseGameConnection connection)
		{
			var t = new TaskCompletionSource<BsonDocument>();
			connection.GetGameSummary((err, result) => CompleteTask(t, err, result) );
			return t.Task;
		}

		public static Task EnsureFormatAsync(this BaseGameConnection connection, GameStateFormat format)
		{
			var t = new TaskCompletionSource<bool>();
			connection.EnsureFormat(format, (err) => CompleteTask(t, err) );
			return t.Task;
		}

		public static Task<BsonValue> InsertDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new TaskCompletionSource<BsonValue>();
			connection.InsertDocument(collectionId, doc, (err, result) => CompleteTask(t, err, result) );
			return t.Task;
		}

		public static Task<bool> UpdateDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new TaskCompletionSource<bool>();
			connection.UpdateDocument(collectionId, doc, (err, result) => CompleteTask(t, err, result));
			return t.Task;
		}

		public static Task<bool> UpsertDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new TaskCompletionSource<bool>();
			connection.UpsertDocument(collectionId, doc, (err, result) => CompleteTask(t, err, result));
			return t.Task;
		}

		public static Task<BsonDocument> FindDocumentByIdAsync(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var t = new TaskCompletionSource<BsonDocument>();
			connection.FindDocumentById(collectionId, id, (err, result) => CompleteTask(t, err, result));
			return t.Task;
		}

		public static Task<bool> DeleteDocumentAsync(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var t = new TaskCompletionSource<bool>();
			connection.DeleteDocument(collectionId, id, (err, result) => CompleteTask(t, err, result));
			return t.Task;
		}

		public static Task<List<BsonDocument>> ListDocumentsAsync(this BaseGameConnection connection, int collectionId)
		{
			var t = new TaskCompletionSource<List<BsonDocument>>();
			connection.ListDocuments(collectionId, (err, result) => CompleteTask(t, err, result));
			return t.Task;
		}

		// -------- Live game

		public static Task<bool> TryToLockAsync(this BaseGameConnection connection, string lockName, string key)
		{
			var t = new TaskCompletionSource<bool>();
			connection.TryToLock(lockName, key, (err, result) => CompleteTask(t, err, result));
			return t.Task;
		}

		public static Task<bool> UnlockAsync(this BaseGameConnection connection, string lockName, string key)
		{
			var t = new TaskCompletionSource<bool>();
			connection.Unlock(lockName, key, (err, result) => CompleteTask(t, err, result));
			return t.Task;
		}

	}
}