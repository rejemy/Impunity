using System;

using UltraLiteDB;


namespace Impunity.GameState
{

	public delegate void ImpunityActionCallback(GameStateActionBase action);
	public delegate void ServerActionMethod(GameStateServer game);

	public class ActionResult
	{
		[BsonField("e")]
		public ImpunityErrorResponse Error;
	}

	public class ActionResult<TResult> : ActionResult
	{
		[BsonField("r")]
		public TResult Result;
	}

	public abstract class GameStateActionBase
	{
		[BsonIgnore]
		public ImpunityErrorResponse Error;

		[BsonIgnore]
		internal IServerSideConnectionProxy Origin { get; set; }

		[BsonIgnore]
		public bool ResultsExpected { get; set; }

		[BsonIgnore]
		public bool CloseConnectionOnComplete { get; set; }

		[BsonIgnore]
		public bool AwaitingTask { get; set; } = false;

		public abstract ushort GetActionType();
		public abstract bool HasCallback();
		public abstract bool IsDBOperation();

		public virtual BsonDocument SerializeRequest()
		{
			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			BsonDocument requestBson = mapper.ToDocument(GetType(), this);

			return requestBson;
		}

		// Called from background thread
		// Called when message has been encoded or processed and the input fields
		// can be disposed of
		public virtual void Cleanup() { }


		// Executing the action
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


		// Results stuff
		public abstract ActionResult GetResult();

		public abstract Type GetResultType();

		public BsonDocument SerializeResults()
		{
			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			ActionResult reply = GetResult();
			return mapper.ToDocument(reply);
		}

		public abstract void DeserializeResults(BsonDocument resultBody);

		// Final callback
		public abstract void InvokeOnCompleteCallback();
	}


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

		public override void DeserializeResults(BsonDocument resultBody)
		{
			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			ActionResult reply = mapper.ToObject<ActionResult>(resultBody);
			Error = reply.Error;
		}

		public override void InvokeOnCompleteCallback()
		{
			OnCompleteCallback?.Invoke(Error);
		}
	}

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

		public override void DeserializeResults(BsonDocument resultBody)
		{
			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			ActionResult<TResult> reply = mapper.ToObject<ActionResult<TResult>>(resultBody);
			Error = reply.Error;
			Result = reply.Result;
		}

		public override void InvokeOnCompleteCallback()
		{
			OnCompleteCallback?.Invoke(Error, Result);
		}
	}


	public abstract class ServerActionBase : GameStateActionBase
	{
		public override bool IsDBOperation() { return false; }

		[BsonIgnore]
		public bool Guaranteed { get; set; } = true;

		public override void DeserializeResults(BsonDocument resultBody)
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