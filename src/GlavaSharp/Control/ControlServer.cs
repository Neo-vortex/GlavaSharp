using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;

namespace GlavaSharp.Control;

/// <summary>
///     The live control channel: a plain <see cref="HttpListener" /> (no
///     Kestrel/ASP.NET Core -- HttpListener is already in the BCL, trims
///     cleanly under Native AOT, and doesn't drag any extra weight into the
///     single-file AppImage) serving one self-contained HTML/JS control page
///     plus a tiny JSON API over <see cref="PropertyStore" />.
///
///     Binds 127.0.0.1 by default -- see Program.cs's <c>--control-bind</c>/
///     <c>--control-port</c> for widening that to a LAN address, and
///     <c>--no-control</c> to skip starting this at all. A bind failure
///     (e.g. port already in use by another GlavaSharp instance) is
///     non-fatal: logged as a warning, the app keeps running without a
///     control channel rather than crashing over what's a nice-to-have.
///
///     Runs entirely on its own background thread and never touches OpenGL
///     directly -- <see cref="PropertyStore.TrySet" /> only queues a change;
///     applying it happens on the render thread via
///     <see cref="PropertyStore.DrainPending" /> (see
///     <see cref="Windowing.AppWindow.Run" />). This holds regardless of
///     windowed vs. <c>--desktop</c> (pinned/embedded) mode -- the control
///     server doesn't know or care which one is active.
/// </summary>
public sealed class ControlServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly PropertyStore _store;
    private readonly Thread _thread;
    private volatile bool _running = true;

    /// <exception cref="InvalidOperationException">
    ///     The listener couldn't bind (port in use, insufficient permission
    ///     for the requested host, etc.) -- callers should treat this as
    ///     non-fatal (log and continue without a control channel), not crash
    ///     the whole app over it.
    /// </exception>
    public ControlServer(PropertyStore store, string bindHost, int port)
    {
        _store = store;
        BoundPrefix = $"http://{bindHost}:{port}/";
        _listener.Prefixes.Add(BoundPrefix);
        try
        {
            _listener.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            throw new InvalidOperationException(
                $"Couldn't start the control channel on {BoundPrefix}: {ex.Message}", ex);
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "GlavaSharp-Control" };
        _thread.Start();
    }

    public string BoundPrefix { get; }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // already gone, nothing to clean up
        }

        // Listener's blocking GetContext() unblocks with an exception once
        // Stop()/Close() runs above, so Loop() exits on its own -- no need
        // to Join() and risk hanging Dispose() if that thread's mid-Handle.
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.GetContext();
            }
            catch (Exception)
            {
                break; // listener stopped/disposed -- normal shutdown path
            }

            try
            {
                Handle(ctx);
            }
            catch (Exception ex)
            {
                Log.Warn($"Control channel request failed: {ex.Message}");
                TryRespond(ctx.Response, 500, "text/plain", "internal error");
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "/";

        if (req.HttpMethod == "GET" && (path == "/" || path == "/index.html"))
        {
            TryRespond(res, 200, "text/html; charset=utf-8", ControlPageHtml);
            return;
        }

        if (req.HttpMethod == "GET" && path == "/api/properties")
        {
            TryRespond(res, 200, "application/json", BuildPropertiesJson());
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/properties")
        {
            var name = req.QueryString["name"];
            var valueStr = req.QueryString["value"];
            if (name is null || valueStr is null ||
                !float.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                TryRespond(res, 400, "text/plain", "expected POST /api/properties?name=<x>&value=<float>");
                return;
            }

            if (!_store.TrySet(name, value, out var error))
            {
                TryRespond(res, 400, "text/plain", error ?? "invalid property");
                return;
            }

            TryRespond(res, 200, "application/json", BuildPropertiesJson());
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/properties/feed")
        {
            var name = req.QueryString["name"];
            var enabledStr = req.QueryString["enabled"];
            if (name is null || enabledStr is null || !bool.TryParse(enabledStr, out var enabled))
            {
                TryRespond(res, 400, "text/plain", "expected POST /api/properties/feed?name=<x>&enabled=true|false");
                return;
            }

            if (!_store.TrySetFeedEnabled(name, enabled, out var error))
            {
                TryRespond(res, 400, "text/plain", error ?? "invalid property");
                return;
            }

            TryRespond(res, 200, "application/json", BuildPropertiesJson());
            return;
        }

        TryRespond(res, 404, "text/plain", "not found");
    }

    private static void TryRespond(HttpListenerResponse res, int statusCode, string contentType, string body)
    {
        try
        {
            res.StatusCode = statusCode;
            res.ContentType = contentType;
            var bytes = Encoding.UTF8.GetBytes(body);
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception)
        {
            // client disconnected mid-response or similar -- nothing to do
        }
        finally
        {
            try
            {
                res.OutputStream.Close();
            }
            catch (Exception)
            {
                // ignore
            }
        }
    }

    /// <summary>
    ///     Hand-rolled, not System.Text.Json -- the reflection-based
    ///     serializer trips the csproj's IL2026/IL3050 AOT warnings-as-errors
    ///     without a source-generated JsonSerializerContext, and the payload
    ///     shape here (a flat list of number/string fields) doesn't earn that
    ///     ceremony.
    /// </summary>
    private string BuildPropertiesJson()
    {
        var values = _store.CurrentValues;
        var sb = new StringBuilder();
        sb.Append('[');
        var first = true;
        foreach (var d in _store.Descriptors)
        {
            if (!first) sb.Append(',');
            first = false;
            var value = values.GetValueOrDefault(d.Name, d.Default);
            sb.Append('{');
            sb.Append("\"name\":").Append(JsonString(d.Name)).Append(',');
            sb.Append("\"category\":").Append(JsonString(d.Category)).Append(',');
            sb.Append("\"min\":").Append(JsonNumber(d.Min)).Append(',');
            sb.Append("\"max\":").Append(JsonNumber(d.Max)).Append(',');
            sb.Append("\"default\":").Append(JsonNumber(d.Default)).Append(',');
            sb.Append("\"value\":").Append(JsonNumber(value)).Append(',');
            sb.Append("\"feedSource\":").Append(d.FeedSource is null ? "null" : JsonString(d.FeedSource)).Append(',');
            sb.Append("\"feedEnabled\":").Append(_store.IsFeedEnabled(d.Name) ? "true" : "false");
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string JsonNumber(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string JsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default: sb.Append(c); break;
            }

        sb.Append('"');
        return sb.ToString();
    }

    // Single self-contained page: inline CSS/JS, no CDN, no build step.
    // Polls /api/properties every 400ms rather than a WebSocket -- simple
    // enough for a slider UI where a few hundred ms of staleness to reflect
    // an *external* change (another tab, or a CLI-driven default) is
    // imperceptible, and the slider you're actually dragging updates
    // instantly from its own "input" event regardless of the poll.
    private const string ControlPageHtml = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <title>GlavaSharp -- Live Control</title>
        <style>
          :root { color-scheme: dark; }
          body { font-family: system-ui, sans-serif; background: #14161a; color: #e8e8ea;
                 margin: 0; padding: 24px 32px 64px; }
          h1 { font-size: 1.1rem; font-weight: 600; color: #9fd; margin: 0 0 20px; }
          h2 { font-size: 0.85rem; text-transform: uppercase; letter-spacing: 0.08em;
               color: #888; margin: 28px 0 10px; border-bottom: 1px solid #2a2d33; padding-bottom: 6px; }
          .group:first-of-type h2 { margin-top: 0; }
          .row { display: grid; grid-template-columns: 160px 1fr auto 64px; align-items: center;
                 gap: 12px; padding: 6px 0; }
          label { font-size: 0.85rem; color: #ccc; }
          input[type=range] { width: 100%; accent-color: #6cf; }
          input[type=range]:disabled { opacity: 0.35; }
          .val { font-variant-numeric: tabular-nums; font-size: 0.8rem; color: #9ab; text-align: right; }
          .feed { display: flex; align-items: center; gap: 5px; font-size: 0.72rem;
                  color: #7a8; white-space: nowrap; }
          .feed input { accent-color: #7c6; }
          #empty { color: #777; font-size: 0.9rem; }
        </style>
        </head>
        <body>
        <h1>GlavaSharp -- Live Control</h1>
        <div id="empty" style="display:none">No live-tweakable properties registered.</div>
        <div id="groups"></div>
        <script>
          let dragging = null;
          let lastSent = 0;

          async function fetchProps() {
            try {
              const res = await fetch('/api/properties');
              render(await res.json());
            } catch (e) { /* server restarting/hot-reloading -- next poll retries */ }
          }

          function render(props) {
            document.getElementById('empty').style.display = props.length ? 'none' : 'block';
            const groups = {};
            for (const p of props) { (groups[p.category] ??= []).push(p); }
            const container = document.getElementById('groups');
            const names = props.map(p => p.name).join(',');
            if (container.dataset.names !== names) {
              container.dataset.names = names;
              container.innerHTML = '';
              for (const cat of Object.keys(groups)) {
                const section = document.createElement('div');
                section.className = 'group';
                const h2 = document.createElement('h2');
                h2.textContent = cat;
                section.appendChild(h2);
                for (const p of groups[cat]) section.appendChild(makeRow(p));
                container.appendChild(section);
              }
            }
            for (const p of props) {
              const input = document.getElementById('in-' + p.name);
              const val = document.getElementById('val-' + p.name);
              const feedBox = document.getElementById('feed-' + p.name);
              if (input) input.disabled = !!p.feedEnabled;
              if (feedBox) feedBox.checked = !!p.feedEnabled;
              if (dragging === p.name) continue;
              if (input && document.activeElement !== input) input.value = p.value;
              if (val) val.textContent = p.value.toFixed(3);
            }
          }

          function makeRow(p) {
            const row = document.createElement('div');
            row.className = 'row';
            const label = document.createElement('label');
            label.htmlFor = 'in-' + p.name;
            label.textContent = p.name;
            const input = document.createElement('input');
            input.type = 'range';
            input.id = 'in-' + p.name;
            input.min = p.min;
            input.max = p.max;
            input.step = (p.max - p.min) / 1000 || 0.001;
            input.value = p.value;
            input.disabled = !!p.feedEnabled;
            const val = document.createElement('span');
            val.className = 'val';
            val.id = 'val-' + p.name;
            val.textContent = Number(p.value).toFixed(3);
            input.addEventListener('input', () => {
              dragging = p.name;
              val.textContent = Number(input.value).toFixed(3);
              const now = performance.now();
              if (now - lastSent > 40) { lastSent = now; setProp(p.name, input.value); }
            });
            input.addEventListener('change', () => { dragging = null; setProp(p.name, input.value); });

            const feedCell = document.createElement('div');
            feedCell.className = 'feed';
            if (p.feedSource) {
              const feedBox = document.createElement('input');
              feedBox.type = 'checkbox';
              feedBox.id = 'feed-' + p.name;
              feedBox.checked = !!p.feedEnabled;
              feedBox.addEventListener('change', () => setFeed(p.name, feedBox.checked));
              const feedLabel = document.createElement('label');
              feedLabel.htmlFor = feedBox.id;
              feedLabel.textContent = 'auto: ' + p.feedSource;
              feedCell.append(feedBox, feedLabel);
            }

            row.append(label, input, feedCell, val);
            return row;
          }

          function setProp(name, value) {
            fetch('/api/properties?name=' + encodeURIComponent(name) + '&value=' + encodeURIComponent(value),
                  { method: 'POST' }).catch(() => {});
          }

          function setFeed(name, enabled) {
            fetch('/api/properties/feed?name=' + encodeURIComponent(name) + '&enabled=' + enabled,
                  { method: 'POST' }).catch(() => {});
          }

          fetchProps();
          setInterval(fetchProps, 400);
        </script>
        </body>
        </html>
        """;
}
