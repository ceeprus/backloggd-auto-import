using System.Text.Json;
using BackloggdLogger.Core;
using Microsoft.Web.WebView2.Core;

namespace BackloggdLogger;

// Requests to the ad hosts get an empty 403; the filters let only those requests reach the handler,
// so Backloggd's own requests never wait on it. Empty ad slots are hidden.
sealed class AdBlock
{
    public int Blocked { get; private set; }

    public static async Task<AdBlock> AttachAsync(CoreWebView2 core)
    {
        var block = new AdBlock();
        foreach (var pattern in AdHosts.FilterPatterns)
        {
            try { core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All); }
            catch (Exception ex) when (ex is NotImplementedException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
            }
        }
        core.WebResourceRequested += (_, e) =>
        {
            if (!AdHosts.IsBlockedUrl(e.Request.Uri)) return;
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            block.Blocked++;
        };
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(SlotScript); }
        catch (Exception ex) { Log.Write("errors", "ad slot style: " + ex.Message); }
        return block;
    }

    static readonly string SlotScript =
        "(() => { const css = " + JsonSerializer.Serialize(AdHosts.SlotCss) + "; " +
        "const add = () => { if (document.getElementById('backloggd-logger-slots')) return; const s = document.createElement('style'); " +
        "s.id = 'backloggd-logger-slots'; s.textContent = css; (document.head || document.documentElement).appendChild(s); }; " +
        "if (document.documentElement) add(); else document.addEventListener('DOMContentLoaded', add, { once: true }); })();";
}
