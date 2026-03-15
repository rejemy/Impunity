var ImpunityWebSocketLib =
{
    $impunityWsState:
    {
        instances: {},
        nextInstanceId: 1,

        openCallback: null,
        messageCallback: null,
        errorCallback: null,
        closeCallback: null,

        haveDynCall: false
    },

    ImpunityWsInitialize: function()
    {
        impunityWsState.haveDynCall = (typeof dynCall !== 'undefined');
    },

    ImpunityWsCreate: function(url)
    {
        var urlStr = UTF8ToString(url);
        var instanceId = impunityWsState.nextInstanceId++;

        impunityWsState.instances[instanceId] =
        {
            url: urlStr,
            ws: null
        };

        return instanceId;
    },

    ImpunityWsDelete: function(instanceId)
    {
        var instance = impunityWsState.instances[instanceId];
        if (!instance)
            return;

        if (instance.ws && instance.ws.readyState < 2)
            instance.ws.close();

        delete impunityWsState.instances[instanceId];
    },

    ImpunityWsConnect: function(instanceId)
    {
        var instance = impunityWsState.instances[instanceId];
        if (!instance)
            return -1;

        if (instance.ws !== null)
            return -2;

        instance.ws = new WebSocket(instance.url);
        instance.ws.binaryType = 'arraybuffer';

        instance.ws.onopen = function(event)
        {
            if (impunityWsState.openCallback)
            {
                if (impunityWsState.haveDynCall)
                    Module.dynCall_vi(impunityWsState.openCallback, instanceId);
                else
                    {{{ makeDynCall('vi', 'impunityWsState.openCallback') }}}(instanceId);
            }
        };

        instance.ws.onmessage = function(event)
        {
            if (!(event.data instanceof ArrayBuffer))
                return;

            if (impunityWsState.messageCallback === null)
                return;

            var dataBuffer = new Uint8Array(event.data);
            var buffer = _malloc(dataBuffer.length);
            HEAPU8.set(dataBuffer, buffer);

            try
            {
                if (impunityWsState.haveDynCall)
                    Module.dynCall_viii(impunityWsState.messageCallback, instanceId, buffer, dataBuffer.length);
                else
                    {{{ makeDynCall('viii', 'impunityWsState.messageCallback') }}}(instanceId, buffer, dataBuffer.length);
            }
            finally
            {
                _free(buffer);
            }
        };

        instance.ws.onerror = function(event)
        {
            if (impunityWsState.errorCallback === null)
                return;

            if (impunityWsState.haveDynCall)
                Module.dynCall_vi(impunityWsState.errorCallback, instanceId);
            else
                {{{ makeDynCall('vi', 'impunityWsState.errorCallback') }}}(instanceId);
        };

        instance.ws.onclose = function(event)
        {
            if (impunityWsState.closeCallback)
            {
                if (impunityWsState.haveDynCall)
                    Module.dynCall_vii(impunityWsState.closeCallback, instanceId, event.code);
                else
                    {{{ makeDynCall('vii', 'impunityWsState.closeCallback') }}}(instanceId, event.code);
            }

            delete instance.ws;
        };

        return 0;
    },

    ImpunityWsClose: function(instanceId, code)
    {
        var instance = impunityWsState.instances[instanceId];
        if (!instance)
            return -1;

        if (!instance.ws)
            return -3;

        if (instance.ws.readyState >= 2)
            return -4;

        try
        {
            instance.ws.close(code);
        }
        catch (err)
        {
            return -7;
        }

        return 0;
    },

    ImpunityWsSend: function(instanceId, bufferPtr, bufferLength)
    {
        var instance = impunityWsState.instances[instanceId];
        if (!instance)
            return -1;

        if (!instance.ws)
            return -3;

        if (instance.ws.readyState !== 1)
            return -6;

        instance.ws.send(HEAPU8.buffer.slice(bufferPtr, bufferPtr + bufferLength));

        return 0;
    },

    ImpunityWsGetState: function(instanceId)
    {
        var instance = impunityWsState.instances[instanceId];
        if (!instance)
            return -1;

        if (instance.ws)
            return instance.ws.readyState;
        else
            return 3; // Closed
    },

    ImpunityWsSetOpenCallback: function(callback)
    {
        impunityWsState.openCallback = callback;
    },

    ImpunityWsSetMessageCallback: function(callback)
    {
        impunityWsState.messageCallback = callback;
    },

    ImpunityWsSetCloseCallback: function(callback)
    {
        impunityWsState.closeCallback = callback;
    },

    ImpunityWsSetErrorCallback: function(callback)
    {
        impunityWsState.errorCallback = callback;
    }
};

autoAddDeps(ImpunityWebSocketLib, '$impunityWsState');
mergeInto(LibraryManager.library, ImpunityWebSocketLib);
