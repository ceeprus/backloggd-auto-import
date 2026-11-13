using Microsoft.Web.WebView2.Core;

namespace BackloggdLogger;

// Backloggd's CDN answers 404 for its Font Awesome brands font (seen 2026-11-13), so the social
// icons draw as blanks; the same font from cdnjs fills in.
static class SiteFixes
{
    public static async Task AttachAsync(CoreWebView2 core)
    {
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(Script); }
        catch (Exception ex) { Log.Write("errors", "site fixes: " + ex.Message); }
    }

    const string BrandsFont =
        "@font-face{font-family:\"Font Awesome 7 Brands\";font-style:normal;font-weight:400;font-display:block;" +
        "src:url(https://cdnjs.cloudflare.com/ajax/libs/font-awesome/7.0.1/webfonts/fa-brands-400.woff2) format(\"woff2\")}";

    const string Script =
        "(() => { const h = location.hostname; if (h !== 'backloggd.com' && !h.endsWith('.backloggd.com')) return;" +
        "const add = () => { if (document.getElementById('backloggd-logger-fixes')) return; const s = document.createElement('style'); " +
        "s.id = 'backloggd-logger-fixes'; s.textContent = '" + BrandsFont + "'; (document.head || document.documentElement).appendChild(s); }; " +
        "if (document.documentElement) add(); else document.addEventListener('DOMContentLoaded', add, { once: true }); })();";
}
