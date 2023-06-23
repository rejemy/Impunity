using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Net;

using UltraLiteDB;

using Impunity.Networking;

namespace Impunity.Connection
{

	interface IImpunityNetworkAction
	{
		ushort MessageId { get; }
		ushort MessageType { get; }
		object Request { get; }
		ImpunityError Err { get; set; }

		void SetResult(BsonValue result);

		bool HasCallback();
		void InvokeCallback();
	}

	class ImpunityNetworkAction : IImpunityNetworkAction
	{
		public ushort MessageId { get; set; }
		public ushort MessageType { get; set; }
		public object Request { get; set; }
		public ImpunityError Err { get; set; }

		ImpunityCallback OnComplete;
		

		public ImpunityNetworkAction(ushort messageType, ushort messageId, object requestBody, ImpunityCallback callback)
		{
			MessageType = messageType;
			MessageId = messageId;
			Request = requestBody;
			OnComplete = callback;
		}

		public bool HasCallback()
		{
			return OnComplete != null;
		}


		public void SetResult(BsonValue result)
		{
			ImpunityLogger.LogError("Got response body when we didn't expect one for message type " + MessageType + " id " + MessageId);
		}

		public void InvokeCallback()
		{
			OnComplete.Invoke(Err);
		}
	}

	class ImpunityNetworkActionBool : IImpunityNetworkAction
	{
		public ushort MessageId { get; set; }
		public ushort MessageType { get; set; }
		public object Request { get; set; }
		public ImpunityError Err { get; set; }

		ImpunityCallback<bool> OnComplete;

		bool Result;

		public ImpunityNetworkActionBool(ushort messageType, ushort messageId, object requestBody, ImpunityCallback<bool> callback)
		{
			MessageType = messageType;
			MessageId = messageId;
			Request = requestBody;
			OnComplete = callback;
		}

		public bool HasCallback()
		{
			return OnComplete != null;
		}

		public void SetResult(BsonValue result)
		{
			Result = result;
		}

		public void InvokeCallback()
		{
			OnComplete.Invoke(Err, Result);
		}
	}

	class ImpunityNetworkActionValue : IImpunityNetworkAction
	{
		public ushort MessageId { get; set; }
		public ushort MessageType { get; set; }
		public object Request { get; set; }
		public ImpunityError Err { get; set; }

		ImpunityCallback<BsonValue> OnComplete;

		BsonValue Result;

		public ImpunityNetworkActionValue(ushort messageType, ushort messageId, object requestBody, ImpunityCallback<BsonValue> callback)
		{
			MessageType = messageType;
			MessageId = messageId;
			Request = requestBody;
			OnComplete = callback;
		}

		public bool HasCallback()
		{
			return OnComplete != null;
		}

		public void SetResult(BsonValue result)
		{
			Result = result;
		}

		public void InvokeCallback()
		{
			OnComplete.Invoke(Err, Result);
		}
	}


	class ImpunityNetworkAction<TResult> : IImpunityNetworkAction
	{
		public ushort MessageId { get; set; }
		public ushort MessageType { get; set; }
		public object Request { get; set; }
		public ImpunityError Err { get; set; }

		ImpunityCallback<TResult> OnComplete;

		TResult Result;

		public ImpunityNetworkAction(ushort messageType, ushort messageId, object requestBody, ImpunityCallback<TResult> callback)
		{
			MessageType = messageType;
			MessageId = messageId;
			Request = requestBody;
			OnComplete = callback;
		}

		public bool HasCallback()
		{
			return OnComplete != null;
		}

		public void SetResult(BsonValue result)
		{
			BsonDocument resultDoc = (BsonDocument)result;
			Result = ImpunityNetworkingUtil.GetBsonMapper().ToObject<TResult>(resultDoc);
		}

		public void InvokeCallback()
		{
			OnComplete.Invoke(Err, Result);
		}
	}

	public class RemoteGameConnection : IGameStateConnection
	{
		BlockingCollection<IImpunityNetworkAction> PendingWrite;
		ConcurrentQueue<IImpunityNetworkAction> PendingResponse;
		ConcurrentQueue<IImpunityNetworkAction> PendingCallbacks;

		public ImpunityCallback OnNetworkError { get; set; }

		IImpunityClient NetworkClient;
		Thread NetworkWriterThread;
		bool Running;

		ushort NextMessageId = 1;
		byte[] SendBuffer;

		public RemoteGameConnection(IPEndPoint serverEndpoint, ImpunityOptions options = null)
		{
			PendingWrite = new BlockingCollection<IImpunityNetworkAction>();
			PendingResponse = new ConcurrentQueue<IImpunityNetworkAction>();
			PendingCallbacks = new ConcurrentQueue<IImpunityNetworkAction>();

			NetworkClient = ImpunityTCPClient.MakeTCPClient(serverEndpoint, options);
			NetworkClient.OnNetworkError = OnClientNetworkError;
			NetworkClient.OnMessageRecieved = OnClientNetworkMessage;

			SendBuffer = new byte[ImpunityConstants.MaxMessageSize];
		}

		public void Connect(ImpunityCallback onComplete)
		{
			NetworkClient.Connect((ImpunityError err) =>
			{
				ImpunityNetworkAction connectAction = new ImpunityNetworkAction(0, 0, null, onComplete);

				if (err != null)
				{
					connectAction.Err = err;
					PendingCallbacks.Enqueue(connectAction);
					return;
				}

				Running = true;
				NetworkWriterThread = new Thread(new ThreadStart(NetworkWriterThreadMain));
				NetworkWriterThread.IsBackground = true;
				NetworkWriterThread.Name = "Network writer";
				NetworkWriterThread.Start();

				PendingCallbacks.Enqueue(connectAction);
			});

			
		}

		public void Update()
		{
			while (PendingCallbacks.TryDequeue(out IImpunityNetworkAction action))
			{
				try
				{
					if (action.HasCallback())
					{
						action.InvokeCallback();
					}
					else if (action.Err != null)
                    {
						OnNetworkError?.Invoke(action.Err);
					}
					else
                    {
						ImpunityLogger.LogError("Got action callback with no callback or error");
                    }
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in remote connection callback");
				}
			}
		}

		private void NetworkWriterThreadMain()
		{
			while (Running)
			{
				IImpunityNetworkAction action = null;

				try
				{
					action = PendingWrite.Take();
				}
				catch (InvalidOperationException)
				{
					// Pending actions queue was closed
					return;
				}

				try
				{
					SendMessage(action);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in remote connection send attempt");
				}

				PendingResponse.Enqueue(action);
			}
		}

		private void SendMessage(IImpunityNetworkAction action)
		{
			object requestBody = action.Request;
			BsonDocument requestBson = null;
			if (requestBody != null)
			{
				BsonMapper mapper = ImpunityNetworkingUtil.GetBsonMapper();
				requestBson = mapper.ToDocument(requestBody.GetType(), requestBody);
			}

			ushort flags = 0;
			if (!action.HasCallback())
			{
				flags |= ImpunityMessageFlags.NO_REPLY;
			}

			ArraySegment<byte> encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBuffer, action.MessageId, flags, action.MessageType, requestBson);

			NetworkClient.SendGuaranteedMessage(encodedMessage.Array, 0, encodedMessage.Count);

		}

		// On dotnet internal socket thread
		private void OnClientNetworkMessage(byte[] buffer, int length)
		{
			MessageStruct msg;

			ImpunityNetworkingUtil.ReadMessage(buffer, length, out msg);

			switch (msg.MessageType)
			{
				case ServerMessageTypes.REPLY:
					{
						OnReplyMessage(msg.MessageId, msg.Body);
						break;
					}
				default:
					{
						ImpunityLogger.LogError("Got unknown message type: " + msg.MessageType);
						break;
					}
			}
		}



		// On dotnet internal socket thread
		private void OnClientNetworkError(ImpunityError error)
		{
			ImpunityNetworkAction errAction = new ImpunityNetworkAction(0, 0, null, null);
			errAction.Err = error;
			PendingCallbacks.Enqueue(errAction);
		}


		private void OnReplyMessage(ushort messageId, BsonDocument body)
		{
			IImpunityNetworkAction action;
			if (!PendingResponse.TryDequeue(out action))
			{
				ImpunityLogger.LogError("Got response with id " + messageId + " when we weren't expecting any responses");
				return;
			}

			if (action.MessageId != messageId)
			{
				ImpunityLogger.LogError("Got response with id " + messageId + " when we were expecting response " + action.MessageId);
				return;
			}

			try
			{
				BsonMapper mapper = ImpunityNetworkingUtil.GetBsonMapper();
				ServerReply reply = mapper.ToObject<ServerReply>(body);
				if (reply.Error != null)
				{
					action.Err = reply.Error;
				}
				else if (reply.Result != null)
				{
					action.SetResult(reply.Result);
				}
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError(e, "Error deserializing reply message body for message type " + action.MessageType + " id " + action.MessageId);
				return;
			}

			// Ready for callback!
			PendingCallbacks.Enqueue(action);
		}

		public void Dispose()
		{
			PendingWrite.CompleteAdding();

			NetworkClient.Dispose();
		}

		private void QueueMessage(ushort type, object requestBody, ImpunityCallback onComplete)
		{
			IImpunityNetworkAction action = new ImpunityNetworkAction(type, NextMessageId++, requestBody, onComplete);
			PendingWrite.Add(action);
		}

		private void QueueMessageBool(ushort type, object requestBody, ImpunityCallback<bool> onComplete)
		{
			IImpunityNetworkAction action = new ImpunityNetworkActionBool(type, NextMessageId++, requestBody, onComplete);
			PendingWrite.Add(action);
		}

		private void QueueMessageValue(ushort type, object requestBody, ImpunityCallback<BsonValue> onComplete)
		{
			IImpunityNetworkAction action = new ImpunityNetworkActionValue(type, NextMessageId++, requestBody, onComplete);
			PendingWrite.Add(action);
		}

		private void QueueMessage<TResult>(ushort type, object requestBody, ImpunityCallback<TResult> onComplete)
		{
			IImpunityNetworkAction action = new ImpunityNetworkAction<TResult>(type, NextMessageId++, requestBody, onComplete);
			PendingWrite.Add(action);
		}

		// ---------- API ----------

		public void SetGameSummary(BsonDocument summary, ImpunityCallback onComplete)
		{
			QueueMessage(ClientMessageTypes.SET_SUMMARY, summary, onComplete);
		}

		public void GetSummary(ImpunityCallback<BsonDocument> onComplete)
		{
			QueueMessage(ClientMessageTypes.GET_SUMMARY, null, onComplete);
		}


		public void EnsureFormat(GameStateFormat format, ImpunityCallback onComplete)
		{
			QueueMessage(ClientMessageTypes.ENSURE_FORMAT, format, onComplete);
		}

		public void InsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue> onComplete)
		{
			CollectionDocMessage message = new CollectionDocMessage
			{
				CollectionId = collectionId,
				Doc = doc
			};

			QueueMessageValue(ClientMessageTypes.INSERT_DOCUMENT, message, onComplete);
		}

		public void UpdateDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			CollectionDocMessage message = new CollectionDocMessage
			{
				CollectionId = collectionId,
				Doc = doc
			};

			QueueMessageBool(ClientMessageTypes.UPDATE_DOCUMENT, message, onComplete);
		}

		public void UpsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			CollectionDocMessage message = new CollectionDocMessage
			{
				CollectionId = collectionId,
				Doc = doc
			};

			QueueMessageBool(ClientMessageTypes.UPSERT_DOCUMENT, message, onComplete);
		}

		public void FindDocumentById(int collectionId, BsonValue id, ImpunityCallback<BsonDocument> onComplete)
		{
			CollectionIdMessage message = new CollectionIdMessage
			{
				CollectionId = collectionId,
				Id = id
			};

			QueueMessage(ClientMessageTypes.FIND_DOCUMENT_BY_ID, message, onComplete);
		}

		public void DeleteDocument(int collectionId, BsonValue id, ImpunityCallback<bool> onComplete)
		{
			CollectionIdMessage message = new CollectionIdMessage
			{
				CollectionId = collectionId,
				Id = id
			};

			QueueMessageBool(ClientMessageTypes.DELETE_DOCUMENT, message, onComplete);
		}

	}

}