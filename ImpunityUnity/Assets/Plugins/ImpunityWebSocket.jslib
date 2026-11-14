// Browser side of ImpunityWebSocketClient (Unity WebGL): the WebSocket that carries the Impunity connection, and
// the optional WebRTC data channel beside it for unguaranteed messages.
//
// Binary WebSocket messages are Impunity frames and go to C#. Text messages are WebRTC signaling and stay in here:
// when the socket opens this side sends {"t":"offer","sdp":...} and the server answers {"t":"answer","sdp":...} or
// {"t":"unavailable","r":...}. C# is only told when the data channel opens and closes, and is handed the Impunity
// frames that arrive on it. Everything about WebRTC failing means "keep using the WebSocket"; none of it is an
// error for the connection. Design: docs/designs/WebRtcTransport.md. Reference behaviour, with tests:
// ImpunityTests/Host/RtcWebSocketTestClient.cs.
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

        rtcOpenCallback: null,
        rtcCloseCallback: null,
        rtcMessageCallback: null,

        haveDynCall: false,

        // A send is skipped when this much is already waiting in the channel: old movement is worth less than none.
        rtcSendBudgetBytes: 16384,
        // With nothing else sent for this long, send a keepalive so the server can tell the path still works.
        rtcKeepAliveAfterMs: 1000,
        // Nothing received for this long on an open channel: the path is dead, go back to the WebSocket.
        rtcSilenceLimitMs: 5000,
        // An offer that hasn't produced an open channel by now never will.
        rtcNegotiationLimitMs: 15000,
        // One more attempt this long after a failure. Opening a channel earns another.
        rtcRetryAfterMs: 30000,
        // The client's header-only KEEPALIVE frame: length 12, type ClientActionType.KEEPALIVE (104). A plain array,
        // turned into bytes when sent: the build serializes this object as text, which a typed array doesn't survive.
        rtcKeepAliveFrame: [12, 0, 0, 0, 104, 0, 0, 0, 0, 0, 0, 0],
        // ServerActionType.KEEPALIVE, the server's probe of the channel. Answered by the silence clock, not by C#.
        rtcServerKeepAliveType: 2,

        callVi: function(callback, a)
        {
            if (callback === null)
                return;
            if (impunityWsState.haveDynCall)
                Module.dynCall_vi(callback, a);
            else
                {{{ makeDynCall('vi', 'callback') }}}(a);
        },

        callVii: function(callback, a, b)
        {
            if (callback === null)
                return;
            if (impunityWsState.haveDynCall)
                Module.dynCall_vii(callback, a, b);
            else
                {{{ makeDynCall('vii', 'callback') }}}(a, b);
        },

        // Hands bytes to C# through a temporary copy in the WASM heap, valid only for the duration of the call.
        callWithBytes: function(callback, instanceId, bytes)
        {
            if (callback === null)
                return;

            var buffer = _malloc(bytes.length);
            HEAPU8.set(bytes, buffer);
            try
            {
                if (impunityWsState.haveDynCall)
                    Module.dynCall_viii(callback, instanceId, buffer, bytes.length);
                else
                    {{{ makeDynCall('viii', 'callback') }}}(instanceId, buffer, bytes.length);
            }
            finally
            {
                _free(buffer);
            }
        },

        // Starts a WebRTC attempt on an instance whose WebSocket is open. Any failure, here or later, ends in
        // rtcStop, and the connection carries on over the WebSocket.
        rtcStart: function(instanceId)
        {
            var state = impunityWsState;
            var instance = state.instances[instanceId];
            if (!instance || !instance.rtcEnabled || instance.rtc || !instance.ws || instance.ws.readyState !== 1)
                return;

            if (typeof RTCPeerConnection === 'undefined')
                return;

            // Every handler below checks that this attempt is still the instance's current one: promises and events
            // can complete after the attempt, or the whole instance, is gone.
            var rtc = { pc: null, dc: null, open: false, lastSent: 0, lastReceived: 0, timer: null, startedAt: Date.now() };
            var current = function()
            {
                return state.instances[instanceId] === instance && instance.rtc === rtc;
            };

            try
            {
                rtc.pc = new RTCPeerConnection({ iceServers: [] });
                rtc.dc = rtc.pc.createDataChannel('impunity', { ordered: false, maxRetransmits: 0 });
                rtc.dc.binaryType = 'arraybuffer';
            }
            catch (err)
            {
                // Present but disabled by policy, or out of resources.
                try { if (rtc.pc) rtc.pc.close(); } catch (err2) {}
                return;
            }

            instance.rtc = rtc;

            rtc.dc.onopen = function()
            {
                if (!current())
                    return;
                rtc.open = true;
                rtc.lastSent = rtc.lastReceived = Date.now();
                instance.rtcRetried = false;
                state.callVi(state.rtcOpenCallback, instanceId);
            };

            rtc.dc.onmessage = function(event)
            {
                if (!current() || !(event.data instanceof ArrayBuffer))
                    return;

                rtc.lastReceived = Date.now();
                var bytes = new Uint8Array(event.data);
                if (bytes.length < 12)
                    return;
                if ((bytes[4] | (bytes[5] << 8)) === state.rtcServerKeepAliveType)
                    return;

                state.callWithBytes(state.rtcMessageCallback, instanceId, bytes);
            };

            rtc.dc.onclose = rtc.dc.onerror = function()
            {
                if (current())
                    state.rtcStop(instanceId, true);
            };

            rtc.pc.onconnectionstatechange = function()
            {
                if (current() && (rtc.pc.connectionState === 'failed' || rtc.pc.connectionState === 'closed'))
                    state.rtcStop(instanceId, true);
            };

            // Keepalives and the two deadlines. Timers slow down in a background tab; if the channel is lost to
            // that, it is simply negotiated again later.
            rtc.timer = setInterval(function()
            {
                if (!current())
                    return;

                var now = Date.now();
                if (!rtc.open)
                {
                    if (now - rtc.startedAt > state.rtcNegotiationLimitMs)
                        state.rtcStop(instanceId, true);
                    return;
                }

                if (now - rtc.lastReceived > state.rtcSilenceLimitMs)
                {
                    state.rtcStop(instanceId, true);
                }
                else if (now - rtc.lastSent > state.rtcKeepAliveAfterMs)
                {
                    try
                    {
                        rtc.dc.send(new Uint8Array(state.rtcKeepAliveFrame));
                        rtc.lastSent = now;
                    }
                    catch (err) {}
                }
            }, 250);

            // The offer goes out as soon as it exists, without waiting for candidates: the server ignores them and
            // learns this side's address from its first connectivity check.
            rtc.pc.createOffer().then(function(offer)
            {
                if (!current())
                    return;
                return rtc.pc.setLocalDescription(offer);
            }).then(function()
            {
                if (!current() || !rtc.pc.localDescription)
                    return;
                instance.ws.send(JSON.stringify({ t: 'offer', sdp: rtc.pc.localDescription.sdp }));
            }).catch(function()
            {
                if (current())
                    state.rtcStop(instanceId, true);
            });
        },

        // A text message from the server: the answer to our offer, or a refusal.
        rtcSignal: function(instanceId, text)
        {
            var state = impunityWsState;
            var instance = state.instances[instanceId];
            if (!instance || !instance.rtc)
                return;

            var message;
            try
            {
                message = JSON.parse(text);
            }
            catch (err)
            {
                return;
            }

            var rtc = instance.rtc;
            if (message && message.t === 'answer' && typeof message.sdp === 'string')
            {
                rtc.pc.setRemoteDescription({ type: 'answer', sdp: message.sdp }).catch(function()
                {
                    if (state.instances[instanceId] === instance && instance.rtc === rtc)
                        state.rtcStop(instanceId, true);
                });
            }
            else if (message && message.t === 'unavailable')
            {
                // The server said no; asking again would get the same answer.
                state.rtcStop(instanceId, false);
            }
        },

        // Ends the current attempt, tells C# if the channel had been open, and optionally schedules the one retry.
        rtcStop: function(instanceId, mayRetry)
        {
            var state = impunityWsState;
            var instance = state.instances[instanceId];
            if (!instance || !instance.rtc)
                return;

            var rtc = instance.rtc;
            instance.rtc = null;

            if (rtc.timer !== null)
                clearInterval(rtc.timer);
            rtc.dc.onopen = rtc.dc.onmessage = rtc.dc.onclose = rtc.dc.onerror = null;
            rtc.pc.onconnectionstatechange = null;
            try { rtc.dc.close(); } catch (err) {}
            try { rtc.pc.close(); } catch (err) {}

            if (rtc.open)
                state.callVi(state.rtcCloseCallback, instanceId);

            if (mayRetry && !instance.rtcRetried && instance.rtcRetryTimer === null)
            {
                instance.rtcRetried = true;
                instance.rtcRetryTimer = setTimeout(function()
                {
                    instance.rtcRetryTimer = null;
                    if (state.instances[instanceId] === instance)
                        state.rtcStart(instanceId);
                }, state.rtcRetryAfterMs);
            }
        },

        // The instance is going away or its socket closed: no channel, and no retry.
        rtcShutdown: function(instanceId)
        {
            var state = impunityWsState;
            var instance = state.instances[instanceId];
            if (!instance)
                return;

            if (instance.rtcRetryTimer !== null)
            {
                clearTimeout(instance.rtcRetryTimer);
                instance.rtcRetryTimer = null;
            }
            instance.rtcRetried = true;
            state.rtcStop(instanceId, false);
        }
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
            ws: null,
            rtcEnabled: false,
            rtc: null,
            rtcRetried: false,
            rtcRetryTimer: null
        };

        return instanceId;
    },

    ImpunityWsDelete: function(instanceId)
    {
        var instance = impunityWsState.instances[instanceId];
        if (!instance)
            return;

        impunityWsState.rtcShutdown(instanceId);

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
            impunityWsState.callVi(impunityWsState.openCallback, instanceId);

            // In parallel with the Impunity handshake; the connection never waits for it.
            impunityWsState.rtcStart(instanceId);
        };

        instance.ws.onmessage = function(event)
        {
            if (typeof event.data === 'string')
            {
                impunityWsState.rtcSignal(instanceId, event.data);
                return;
            }

            if (!(event.data instanceof ArrayBuffer))
                return;

            impunityWsState.callWithBytes(impunityWsState.messageCallback, instanceId, new Uint8Array(event.data));
        };

        instance.ws.onerror = function(event)
        {
            impunityWsState.callVi(impunityWsState.errorCallback, instanceId);
        };

        instance.ws.onclose = function(event)
        {
            impunityWsState.rtcShutdown(instanceId);
            impunityWsState.callVii(impunityWsState.closeCallback, instanceId, event.code);

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

    // Sends bufferLength bytes starting bufferOffset bytes into the array at bufferPtr.
    ImpunityWsSend: function(instanceId, bufferPtr, bufferOffset, bufferLength)
    {
        var instance = impunityWsState.instances[instanceId];
        if (!instance)
            return -1;

        if (!instance.ws)
            return -3;

        if (instance.ws.readyState !== 1)
            return -6;

        // slice copies: the heap may move or be reused as soon as this returns.
        var start = bufferPtr + bufferOffset;
        instance.ws.send(HEAPU8.buffer.slice(start, start + bufferLength));

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
    },

    // ----- WebRTC data channel

    // Whether this instance should try for a data channel when its socket opens. Call before ImpunityWsConnect.
    ImpunityRtcSetEnabled: function(instanceId, enabled)
    {
        var instance = impunityWsState.instances[instanceId];
        if (instance)
            instance.rtcEnabled = !!enabled;
    },

    // Sends one Impunity frame on the data channel. Returns 0 when sent, 1 when dropped because too much is already
    // queued, negative when there is no open channel (the caller then uses the WebSocket).
    ImpunityRtcSend: function(instanceId, bufferPtr, bufferOffset, bufferLength)
    {
        var instance = impunityWsState.instances[instanceId];
        if (!instance)
            return -1;

        var rtc = instance.rtc;
        if (!rtc || !rtc.open || rtc.dc.readyState !== 'open')
            return -3;

        if (rtc.dc.bufferedAmount > impunityWsState.rtcSendBudgetBytes)
            return 1;

        var start = bufferPtr + bufferOffset;
        try
        {
            rtc.dc.send(HEAPU8.buffer.slice(start, start + bufferLength));
        }
        catch (err)
        {
            return -7;
        }

        rtc.lastSent = Date.now();
        return 0;
    },

    ImpunityRtcSetOpenCallback: function(callback)
    {
        impunityWsState.rtcOpenCallback = callback;
    },

    ImpunityRtcSetCloseCallback: function(callback)
    {
        impunityWsState.rtcCloseCallback = callback;
    },

    ImpunityRtcSetMessageCallback: function(callback)
    {
        impunityWsState.rtcMessageCallback = callback;
    }
};

autoAddDeps(ImpunityWebSocketLib, '$impunityWsState');
mergeInto(LibraryManager.library, ImpunityWebSocketLib);
