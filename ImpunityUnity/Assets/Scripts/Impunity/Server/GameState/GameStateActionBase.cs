using System;

using UltraLiteDB;

using Impunity.Networking;
using Impunity.Connection;

namespace Impunity.GameState
{

    public delegate void ImpunityActionCallback(GameStateActionBase action);


    public class ActionResult
    {
        [BsonField("e")]
        public ImpunityError Error;
    }

    public class ActionResult<TResult> : ActionResult
    {
        [BsonField("r")]
        public TResult Result;
    }

    public abstract class GameStateActionBase
    {
        [BsonIgnore]
        public ImpunityError Error;

        [BsonIgnore]
        public IServerSideConnectionProxy Origin { get; set; }

        [BsonIgnore]
        public bool ResultsExpected { get; set; }

        public abstract ushort GetActionType();
        public abstract bool HasCallback();


        public virtual BsonDocument SerializeRequest()
        {
            BsonMapper mapper = ImpunityNetworkingUtil.GetBsonMapper();
            BsonDocument requestBson = mapper.ToDocument(GetType(), this);

            return requestBson;
        }


        // Executing the action

        public void Run(GameStateServer game)
        {
            try
            {
                DoAction(game);
            }
            catch (Exception e)
            {
                Error = new ImpunityError(e);
            }
        }

        protected abstract void DoAction(GameStateServer game);


        // Results stuff
        public abstract ActionResult GetResult();

        public abstract Type GetResultType();

        public BsonDocument SerializeResults()
        {
            BsonMapper mapper = ImpunityNetworkingUtil.GetBsonMapper();
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
            BsonMapper mapper = ImpunityNetworkingUtil.GetBsonMapper();
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
            BsonMapper mapper = ImpunityNetworkingUtil.GetBsonMapper();
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

        public abstract void DoAction(BaseGameConnection connection);

    }


}