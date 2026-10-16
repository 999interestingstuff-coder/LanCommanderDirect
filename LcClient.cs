using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LanCommanderDirect
{
    /// <summary>
    /// Talks straight to the LANCommander server API.
    /// Flow: POST /api/Auth/Login {username,password,rememberMe} -> accessToken -> Bearer header.
    /// Tokens are short-lived, so on a 401 we simply log in again.
    /// </summary>
    public class LcClient : IDisposable
    {
        readonly HttpClient http;
        readonly LcConfig cfg;
        string token;

        public LcClient(LcConfig cfg)
        {
            this.cfg = cfg;
            http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        string Url(string path)
        {
            return cfg.ServerUrl.TrimEnd('/') + path;
        }

        async Task LoginAsync(CancellationToken ct)
        {
            var payload = JsonConvert.SerializeObject(new { username = cfg.Username, password = cfg.Password, rememberMe = false });
            var req = new HttpRequestMessage(HttpMethod.Post, Url("/api/Auth/Login"))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using (var resp = await http.SendAsync(req, ct).ConfigureAwait(false))
            {
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("LANCommander login failed (" + (int)resp.StatusCode + "): " + text);

                var json = JObject.Parse(text);
                token = (string)json["accessToken"];
                if (string.IsNullOrEmpty(token))
                    throw new InvalidOperationException("LANCommander login response had no accessToken.");
            }
        }

        Task<HttpResponseMessage> SendAuthedAsync(string path, HttpCompletionOption option, CancellationToken ct)
        {
            return SendAuthedAsync(HttpMethod.Get, path, null, option, ct);
        }

        /// <summary>makeContent is a factory because a retry after re-login needs a fresh request body.</summary>
        async Task<HttpResponseMessage> SendAuthedAsync(HttpMethod method, string path, Func<HttpContent> makeContent, HttpCompletionOption option, CancellationToken ct)
        {
            if (token == null)
                await LoginAsync(ct).ConfigureAwait(false);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var req = new HttpRequestMessage(method, Url(path));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (makeContent != null)
                    req.Content = makeContent();

                var resp = await http.SendAsync(req, option, ct).ConfigureAwait(false);
                if (req.Content != null)
                    req.Content.Dispose();

                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    resp.Dispose();
                    await LoginAsync(ct).ConfigureAwait(false);
                    continue;
                }
                return resp;
            }
            throw new InvalidOperationException("LANCommander request failed after re-login.");
        }

        static void EnsureOk(HttpResponseMessage resp, string what)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(what + " failed: HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
        }

        /// <summary>Light records for every game. Use GetGameAsync for full details.</summary>
        public async Task<List<LcGame>> GetGamesAsync(CancellationToken ct)
        {
            using (var resp = await SendAuthedAsync("/api/Games", HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                EnsureOk(resp, "Listing games");
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var tok = JToken.Parse(text);

                JArray arr = tok as JArray;
                if (arr == null)
                {
                    var obj = tok as JObject;
                    if (obj != null && obj["value"] is JArray)
                        arr = (JArray)obj["value"];
                    else if (obj != null && obj["id"] != null)
                        arr = new JArray(obj);
                    else
                        arr = new JArray();
                }

                return arr.Select(t => t.ToObject<LcGame>()).Where(g => g != null).ToList();
            }
        }

        public async Task<LcGame> GetGameAsync(Guid id, CancellationToken ct)
        {
            using (var resp = await SendAuthedAsync("/api/Games/" + id, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                EnsureOk(resp, "Loading game details");
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<LcGame>(text);
            }
        }

        public async Task DownloadMediaAsync(Guid mediaId, string destPath, CancellationToken ct)
        {
            using (var resp = await SendAuthedAsync("/api/Media/" + mediaId + "/Download", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                EnsureOk(resp, "Downloading media");
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(destPath))
                {
                    await src.CopyToAsync(dst, 81920, ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Streams the game's archive (a zip) to disk, reporting (bytesDone, bytesTotal).</summary>
        public async Task DownloadGameAsync(Guid id, string destPath, long totalHint, Action<long, long> progress, CancellationToken ct)
        {
            using (var resp = await SendAuthedAsync("/api/Games/" + id + "/Download", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                EnsureOk(resp, "Downloading game");

                long total = resp.Content.Headers.ContentLength ?? totalHint;

                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(destPath))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        done += read;
                        if (progress != null)
                            progress(done, total);
                    }
                }
            }
        }

        /// <summary>
        /// The manifest route carries the launch actions, which the plain detail route can leave empty.
        /// Parsed loosely so an unexpected field elsewhere in the manifest can't break it.
        /// </summary>
        public async Task<LcGame> GetManifestAsync(Guid id, CancellationToken ct)
        {
            using (var resp = await SendAuthedAsync("/api/Games/" + id + "/Manifest", HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                EnsureOk(resp, "Loading game manifest");
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var o = JObject.Parse(text);
                return new LcGame
                {
                    Id = id,
                    Actions = ObjectList(o["actions"])
                };
            }
        }

        static List<JObject> ObjectList(JToken token)
        {
            var list = new List<JObject>();
            var arr = token as JArray;
            if (arr != null)
            {
                foreach (var item in arr)
                {
                    var obj = item as JObject;
                    if (obj != null) list.Add(obj);
                }
            }
            return list;
        }

        // ---------- play sessions ----------

        public async Task ReportPlaySessionAsync(Guid gameId, bool start, CancellationToken ct)
        {
            var path = "/api/PlaySessions/" + (start ? "Start/" : "End/") + gameId;
            using (var resp = await SendAuthedAsync(HttpMethod.Post, path, () => new StringContent(""), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                EnsureOk(resp, start ? "Starting play session" : "Ending play session");
            }
        }

        public void Dispose()
        {
            http.Dispose();
        }
    }
}
