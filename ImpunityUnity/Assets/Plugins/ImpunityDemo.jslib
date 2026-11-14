// Page side of DemoWebBridge (the swarm demo, WebGL builds only). Gives the page, or a test driving it, three
// things on window.impunityDemo:
//   run(line)   queue a dev-console line for the game, e.g. run("demo spawn 16") or run("net channels")
//   results     what those lines printed: [{ command, output, failed }, ...], newest last (the last 50)
//   status      the demo's state and delay statistics, refreshed four times a second
var ImpunityDemoLib =
{
    ImpunityDemoInitialize: function()
    {
        if (window.impunityDemo)
            return;

        window.impunityDemo =
        {
            pending: [],
            results: [],
            status: null,
            run: function(line) { window.impunityDemo.pending.push(String(line)); }
        };
    },

    // The next queued line as a string the game owns, or an empty string when there is none.
    ImpunityDemoTakeCommand: function()
    {
        var line = (window.impunityDemo && window.impunityDemo.pending.length > 0) ? window.impunityDemo.pending.shift() : '';
        var size = lengthBytesUTF8(line) + 1;
        var buffer = _malloc(size);
        stringToUTF8(line, buffer, size);
        return buffer;
    },

    ImpunityDemoPostResult: function(command, output, failed)
    {
        if (!window.impunityDemo)
            return;

        var results = window.impunityDemo.results;
        results.push({ command: UTF8ToString(command), output: UTF8ToString(output), failed: failed !== 0 });
        if (results.length > 50)
            results.shift();
    },

    ImpunityDemoSetStatus: function(json)
    {
        if (!window.impunityDemo)
            return;

        try
        {
            window.impunityDemo.status = JSON.parse(UTF8ToString(json));
        }
        catch (err) {}
    }
};

mergeInto(LibraryManager.library, ImpunityDemoLib);
