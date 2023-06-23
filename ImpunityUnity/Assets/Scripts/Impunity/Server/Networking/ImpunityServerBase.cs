using System;
using System.Collections.Concurrent;
using System.Threading;

using UltraLiteDB;

using Impunity.GameState;


namespace Impunity.Networking
{

	public interface IClientContext
	{
		void SendActionResult(ushort messageId, ImpunityError error, BsonValue reply);
	}


	public abstract class ImpunityServerBase : IGameStateResultHandler, IDisposable
	{
		public GameStateServer GameState { get; private set; }

		BlockingCollection<IImpunityAction> PendingWrite;

		Thread ServerWriterThread;
		bool Running;

		public ImpunityServerBase(GameStateServer gameState)
		{
			GameState = gameState;
			PendingWrite = new BlockingCollection<IImpunityAction>();
		}


		public virtual void Start()
		{
			Running = true;
			ServerWriterThread = new Thread(new ThreadStart(WriterThreadMain));
			ServerWriterThread.IsBackground = false;
			ServerWriterThread.Name = "Network write";
			ServerWriterThread.Start();
		}

		public virtual void Dispose()
		{
			Running = false;
			PendingWrite.CompleteAdding();
		}

		private void WriterThreadMain()
		{
			while (Running)
			{
				IImpunityAction action = null;

				try
				{
					action = PendingWrite.Take();
				}
				catch (InvalidOperationException)
				{
					// Pending actions queue was closed
					break;
				}

				try
				{
					action.InvokeResultsCallback();
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Error writing reply to client");
				}
			}

			PendingWrite.Dispose();
		}

		// Called on game thread
		public void OnActionComplete(IImpunityAction action)
		{
			PendingWrite.Add(action);
		}

		// Called on write thread
		private void SendActionReply(IClientContext client, ushort messageId, ImpunityError error, BsonValue reply = null)
		{
			if (client == null)
            {
				return;
            }

			client.SendActionResult(messageId, error, reply);
		}

		public void HandleClientMessage(IClientContext client, byte[] buffer, int length)
		{
			try
			{
				HandleClientMessageInternal(client, buffer, length);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError(e, "Exception in server message handler");
			}
		}

		private void HandleClientMessageInternal(IClientContext client, byte[] buffer, int length)
		{
			MessageStruct msg;

			ImpunityNetworkingUtil.ReadMessage(buffer, length, out msg);

			IClientContext replyContext = null;
			if ((msg.Flags & ImpunityMessageFlags.NO_REPLY) == 0)
			{
                // Reply expected
                replyContext = client;
			}

			switch (msg.MessageType)
			{
				case ClientMessageTypes.SET_SUMMARY:
					{
						OnSetSummary(replyContext, msg);
						break;
					}
				case ClientMessageTypes.GET_SUMMARY:
					{
						OnGetSummary(replyContext, msg);
						break;
					}
				case ClientMessageTypes.ENSURE_FORMAT:
					{
						OnEnsureFormat(replyContext, msg);
						break;
					}
				case ClientMessageTypes.INSERT_DOCUMENT:
					{
						OnInsertDocument(replyContext, msg);
						break;
					}
				case ClientMessageTypes.UPDATE_DOCUMENT:
					{
						OnUpdateDocument(replyContext, msg);
						break;
					}
				case ClientMessageTypes.UPSERT_DOCUMENT:
					{
						OnUpsertDocument(replyContext, msg);
						break;
					}
				case ClientMessageTypes.FIND_DOCUMENT_BY_ID:
					{
						OnFindDocumentById(replyContext, msg);
						break;
					}
				case ClientMessageTypes.DELETE_DOCUMENT:
					{
						OnDeleteDocument(replyContext, msg);
						break;
					}
				default:
					{
						ImpunityLogger.LogError("Unknown message type: " + msg.MessageType);
						break;
					}
			}
		}


		private void OnSetSummary(IClientContext client, MessageStruct msg)
		{
			GameState.SetGameSummary(this, msg.Body, (ImpunityError err) =>
			{
				SendActionReply(client, msg.MessageId, err);
			});
		}

		private void OnGetSummary(IClientContext client, MessageStruct msg)
		{
			GameState.GetSummary(this, (ImpunityError err, BsonDocument summary) =>
			{
				SendActionReply(client, msg.MessageId, err, summary);
			});
		}

		private void OnEnsureFormat(IClientContext client, MessageStruct msg)
		{
			GameStateFormat format = ImpunityNetworkingUtil.GetBsonMapper().ToObject<GameStateFormat>(msg.Body);
			GameState.EnsureFormat(this, format, (ImpunityError err) =>
			{
				SendActionReply(client, msg.MessageId, err);
			});
		}

		private void OnInsertDocument(IClientContext client, MessageStruct msg)
		{
			CollectionDocMessage docMessage = ImpunityNetworkingUtil.GetBsonMapper().ToObject<CollectionDocMessage>(msg.Body);
			GameState.InsertDocument(this, docMessage.CollectionId, docMessage.Doc, (ImpunityError err, BsonValue id) =>
			{
				SendActionReply(client, msg.MessageId, err, id);
			});
		}

		private void OnUpdateDocument(IClientContext client, MessageStruct msg)
		{
			CollectionDocMessage docMessage = ImpunityNetworkingUtil.GetBsonMapper().ToObject<CollectionDocMessage>(msg.Body);
			GameState.UpdateDocument(this, docMessage.CollectionId, docMessage.Doc, (ImpunityError err, bool updated) =>
			{
				SendActionReply(client, msg.MessageId, err, updated);
			});
		}

		private void OnUpsertDocument(IClientContext client, MessageStruct msg)
		{
			CollectionDocMessage docMessage = ImpunityNetworkingUtil.GetBsonMapper().ToObject<CollectionDocMessage>(msg.Body);
			GameState.UpsertDocument(this, docMessage.CollectionId, docMessage.Doc, (ImpunityError err, bool updated) =>
			{
				SendActionReply(client, msg.MessageId, err, updated);
			});
		}

		private void OnFindDocumentById(IClientContext client, MessageStruct msg)
		{
			CollectionIdMessage idMessage = ImpunityNetworkingUtil.GetBsonMapper().ToObject<CollectionIdMessage>(msg.Body);
			GameState.FindDocumentById(this, idMessage.CollectionId, idMessage.Id, (ImpunityError err, BsonDocument doc) =>
			{
				SendActionReply(client, msg.MessageId, err, doc);
			});
		}

		private void OnDeleteDocument(IClientContext client, MessageStruct msg)
		{
			CollectionIdMessage idMessage = ImpunityNetworkingUtil.GetBsonMapper().ToObject<CollectionIdMessage>(msg.Body);
			GameState.DeleteDocument(this, idMessage.CollectionId, idMessage.Id, (ImpunityError err, bool deleted) =>
			{
				SendActionReply(client, msg.MessageId, err, deleted);
			});
		}
	}

}