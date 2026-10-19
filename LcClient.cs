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
        readonly int answerTimeoutSeconds;
        string token;

        /// <summary>answerTimeoutSeconds = how long the server may take to start answering a request before it counts as unreachable.</summary>
        public LcClient(LcConfig cfg, int answerTimeoutSeconds = 30)
        {
            this.cfg = cfg;
            this.answerTimeoutSeconds = answerTimeoutSeconds;
            http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>
        /// Sends a request. A server that can't be reached, or doesn't answer in time, becomes an LcServerUnreachableException
        /// (an answer with an error status is not that). Only the wait for the answer is limited; a download that has started can take as long as it needs.
        /// </summary>
        async Task<HttpResponseMessage> SendGuardedAsync(HttpRequestMessage req, HttpCompletionOption option, CancellationToken ct)
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(answerTimeoutSeconds));
                try
                {
                    return await http.SendAsync(req, option, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new LcServerUnreachableException(cfg.ServerUrl.TrimEnd('/'), "no answer within " + answerTimeoutSeconds + " seconds", null);
                }
                catch (HttpRequestException e)
                {
                    var inner = e;
                    Exception x = e;
                    while (x.InnerException != null) x = x.InnerException;
                    throw new LcServerUnreachableException(cfg.ServerUrl.TrimEnd('/'), x.Message, inner);
                }
            }
        }

        /// <summary>Checks that the server answers and the login works. Throws LcServerUnreachableException if it can't be reached.</summary>
        public Task PingAsync(CancellationToken ct)
        {
            return LoginAsync(ct);
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

            using (var resp = await SendGuardedAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
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
        async Task<HttpResponseMessage> SendAuthedAsync(HttpMethod method, string path, Func<HttpContent> makeContent, HttpCompletionOption option, CancellationToken ct, Action<HttpRequestMessage> configure = null)
        {
            if (token == null)
                await LoginAsync(ct).ConfigureAwait(false);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var req = new HttpRequestMessage(method, Url(path));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (makeContent != null)
                    req.Content = makeContent();
                if (configure != null)
                    configure(req);

                var resp = await SendGuardedAsync(req, option, ct).ConfigureAwait(false);
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

        /// <summary>
        /// Streams the game's archive to partPath, reporting (bytesDone, bytesTotal). If partPath already holds the start of
        /// the download, it asks the server to continue from there (HTTP Range). A server that ignores the request sends the
        /// whole file again, and then the download simply starts over. The first progress call reports where it started.
        /// </summary>
        public async Task DownloadGameAsync(Guid id, string partPath, long totalHint, Action<long, long> progress, CancellationToken ct)
        {
            long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            bool startOver = false;

            using (var resp = await SendAuthedAsync(HttpMethod.Get, "/api/Games/" + id + "/Download", null,
                HttpCompletionOption.ResponseHeadersRead, ct,
                existing > 0 ? (Action<HttpRequestMessage>)(r => r.Headers.Range = new RangeHeaderValue(existing, null)) : null).ConfigureAwait(false))
            {
                if (existing > 0 && (int)resp.StatusCode == 416)
                {
                    // what we have is not usable (for example it is already as long as the file): download again from the start
                    startOver = true;
                }
                else
                {
                    EnsureOk(resp, "Downloading game");

                    bool resumed = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                    long start = resumed ? existing : 0;
                    long total;
                    if (resumed)
                    {
                        var range = resp.Content.Headers.ContentRange;
                        total = range != null && range.Length.HasValue
                            ? range.Length.Value
                            : (resp.Content.Headers.ContentLength ?? (totalHint - existing)) + existing;
                    }
                    else
                    {
                        total = resp.Content.Headers.ContentLength ?? totalHint;
                    }

                    if (progress != null)
                        progress(start, total);

                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        var buffer = new byte[81920];
                        long done = start;
                        int read;
                        while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                            done += read;
                            if (progress != null)
                                progress(done, total);
                        }

                        // the connection closed early: leave what we have so the next try continues from here
                        if (total > 0 && done < total)
                            throw new IOException("The connection closed before the download was finished (" + done + " of " + total + " bytes).");
                    }
                }
            }

            if (startOver)
            {
                try { File.Delete(partPath); } catch { }
                await DownloadGameAsync(id, partPath, totalHint, progress, ct).ConfigureAwait(false);
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
