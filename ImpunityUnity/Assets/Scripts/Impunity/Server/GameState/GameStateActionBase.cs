using System;

using UltraLiteDB;


namespace Impunity.GameState
{

	/// <summary>Callback invoked when a game state action completes.</summary>
	public delegate void ImpunityActionCallback(GameStateActionBase action);
	/// <summary>A method that operates on a game state server, used for inline action execution.</summary>
	public delegate void ServerActionMethod(GameStateServer game);

	/// <summary>Base class for serializable action results sent back to the client.</summary>
	public class ActionResult
	{
		/// <summary>Error info, or null on success.</summary>
		[BsonField("e")]
		public ImpunityErrorResponse Error;
	}

	/// <summary>Action result with a typed return value.</summary>
	public class ActionResult<TResult> : ActionResult
	{
		/// <summary>The result payload.</summary>
		[BsonField("r")]
		public TResult Result;
	}

	/// <summary>Base class for all client-to-server and server-to-client actions. Actions are serialized via BSON, routed through the network layer, and executed on the game server thread.</summary>
	public abstract class GameStateActionBase
	{
		/// <summary>Error response set during execution, or null on success.</summary>
		[BsonIgnore]
		public ImpunityErrorResponse Error;

		/// <summary>The connection that sent this action (server-side only).</summary>
		[BsonIgnore]
		internal IServerSideConnectionProxy Origin { get; set; }

		/// <summary>Whether the client expects a reply for this action.</summary>
		[BsonIgnore]
		public bool ResultsExpected { get; set; }

		/// <summary>If true, the connection will be closed after the result is sent.</summary>
		[BsonIgnore]
		public bool CloseConnectionOnComplete { get; set; }

		/// <summary>Whether this action is awaiting an async task before completion.</summary>
		[BsonIgnore]
		public bool AwaitingTask { get; set; } = false;

		/// <summary>Timestamp when the action was sent (client-side, for timeout tracking).</summary>
		[BsonIgnore]
		public DateTimeOffset SentAt { get; set; }

		/// <summary>Returns the action type ID used for message routing.</summary>
		public abstract ushort GetActionType();
		/// <summary>Whether this action has a callback to invoke on completion.</summary>
		public abstract bool HasCallback();
		/// <summary>Whether this action requires database access.</summary>
		public abstract bool IsDBOperation();
		/// <summary>Whether this action should be processed immediately rather than queued.</summary>
		public virtual bool IsImmediate() { return false; }

		/// <summary>Serializes this action's request fields to BSON for network transmission.</summary>
		public virtual BsonDocument SerializeRequest()
		{
			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			BsonDocument requestBson = mapper.ToDocument(GetType(), this);

			return requestBson;
		}

		/// <summary>Called after the action has been serialized/sent. Allows releasing input field resources. Called from background thread.</summary>
		public virtual void Cleanup() { }


		/// <summary>Executes this action on the game server thread. Catches exceptions and converts them to error responses.</summary>
		public void Run(GameStateServer game)
		{
			try
			{
				DoAction(game);
			}
			catch (ImpunityServerException e)
			{
				Error = new ImpunityErrorResponse(e);
				if (e is ImpunityServerFatalException)
				{
					CloseConnectionOnComplete = true;
				}
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception running action", e);
				Error = new ImpunityErrorResponse(ImpunityErrorCode.InternalServerError, e);
			}
		}

		/// <summary>Executes a delegate as this action's logic, with standard error handling.</summary>
		public void RunWithMethod(GameStateServer game, ServerActionMethod method)
		{
			try
			{
				method(game);
			}
			catch (ImpunityServerException e)
			{
				Error = new ImpunityErrorResponse(e);
				if (e is ImpunityServerFatalException)
				{
					CloseConnectionOnComplete = true;
				}
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception running action", e);
				Error = new ImpunityErrorResponse(ImpunityErrorCode.InternalServerError, e);
			}
		}

		protected abstract void DoAction(GameStateServer game);


		/// <summary>Creates the action result object to be serialized and sent back to the client.</summary>
		public abstract ActionResult GetResult();

		/// <summary>Returns the CLR type of this action's result, for deserialization.</summary>
		public abstract Type GetResultType();

		/// <summary>Serializes the action result to a BSON document.</summary>
		public BsonDocument SerializeResults()
		{
			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			ActionResult reply = GetResult();
			return mapper.ToDocument(reply);
		}

		/// <summary>Deserializes result bytes received from the server into this action's result fields.</summary>
		public abstract void DeserializeResults(ArraySegment<byte> messageBytes);

		/// <summary>Invokes the user's completion callback with the result. Called on the main thread.</summary>
		public abstract void InvokeOnCompleteCallback();
	}


	/// <summary>Base class for client actions that return no data, only success/error.</summary>
	public abstract class ClientActionResultlessBase : GameStateActionBase
	{
		[BsonIgnore]
		public ImpunityCallback OnCompleteCallback;

		public override bool HasCallback()
		{
			return OnCompleteCallback != null;
		}

		public override ActionResult GetResult()
		{
			ActionResult reply = new ActionResult();
			reply.Error = Error;
			return reply;
		}

		public override Type GetResultType()
		{
			return typeof(ActionResult);
		}

		public override void DeserializeResults(ArraySegment<byte> messageBytes)
		{
			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			ActionResult reply = mapper.DeserializeFromBytes<ActionResult>(messageBytes);
			Error = reply.Error;
		}

		public override void InvokeOnCompleteCallback()
		{
			OnCompleteCallback?.Invoke(Error);
		}
	}

	/// <summary>Base class for client actions that return a typed result.</summary>
	/// <typeparam name="TResult">The type of the result payload.</typeparam>
	public abstract class ClientActionResultBase<TResult> : GameStateActionBase
	{
		[BsonIgnore]
		public TResult Result;

		[BsonIgnore]
		public ImpunityCallback<TResult> OnCompleteCallback;

		public override bool HasCallback()
		{
			return OnCompleteCallback != null;
		}

		public override ActionResult GetResult()
		{
			ActionResult<TResult> reply = new ActionResult<TResult>();
			reply.Error = Error;
			reply.Result = Result;
			return reply;
		}

		public override Type GetResultType()
		{
			return typeof(ActionResult<TResult>);
		}

		public override void DeserializeResults(ArraySegment<byte> messageBytes)
		{
			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			ActionResult<TResult> reply = mapper.DeserializeFromBytes<ActionResult<TResult>>(messageBytes);
			Error = reply.Error;
			Result = reply.Result;
		}

		public override void InvokeOnCompleteCallback()
		{
			OnCompleteCallback?.Invoke(Error, Result);
		}
	}


	/// <summary>Base class for server-originated messages pushed to clients (e.g., state updates). Not used for client request/reply.</summary>
	public abstract class ServerActionBase : GameStateActionBase
	{
		public override bool IsDBOperation() { return false; }

		[BsonIgnore]
		public bool Guaranteed { get; set; } = true;

		public override void DeserializeResults(ArraySegment<byte> messageBytes)
		{
			throw new NotImplementedException();
		}

		public override ActionResult GetResult()
		{
			throw new NotImplementedException();
		}

		public override Type GetResultType()
		{
			throw new NotImplementedException();
		}

		public override bool HasCallback()
		{
			return true;
		}

		protected override void DoAction(GameStateServer game)
		{
			throw new NotImplementedException();
		}

		public override void InvokeOnCompleteCallback()
		{
			throw new NotImplementedException();
		}

		public abstract void DoAction(IServerMessageHandler handler);

	}


}