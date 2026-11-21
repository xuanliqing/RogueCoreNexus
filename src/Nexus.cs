using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RogueCoreNexus
{
    internal sealed class NexusLink
    {
        internal const string Domain = "deeprockgalacticroguecore";
        internal const string Website = "https://www.nexusmods.com/";
        internal const string KeyPage = Website + "settings/api-keys";
        private static readonly Regex WebPath = new Regex(
            @"^/(?:games/)?deeprockgalacticroguecore/mods/([0-9]+)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NxmPath = new Regex(
            @"^/mods/([0-9]+)/files/([0-9]+)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal long ModId { get; private set; }
        internal long FileId { get; private set; }
        internal bool IsNxm { get; private set; }
        private string Key { get; set; }
        private long Expires { get; set; }
        internal long UserId { get; private set; }
        internal bool IsAuthorized { get { return Key != null; } }

        internal NexusLink(long modId, long fileId = 0)
        {
            ModId = modId;
            FileId = fileId;
        }

        internal static NexusLink Parse(string input)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(input) || input.Length > 8192 ||
                !Uri.TryCreate(input.Trim(), UriKind.Absolute, out uri) || !String.IsNullOrEmpty(uri.UserInfo))
                throw new FormatException("请粘贴《异动核心》的 Nexus Mod 链接或 NXM 下载链接。");

            var query = ReadQuery(uri.Query);
            if (uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase) &&
                uri.Host.Equals(Domain, StringComparison.OrdinalIgnoreCase) && uri.Port == -1)
            {
                Match match = NxmPath.Match(uri.AbsolutePath);
                if (!match.Success) throw new FormatException("NXM 链接缺少 Mod 或文件编号。");
                var link = new NexusLink(Number(match.Groups[1].Value), Number(match.Groups[2].Value)) { IsNxm = true };
                string key, expires, user;
                bool hasKey = query.TryGetValue("key", out key);
                bool hasExpiry = query.TryGetValue("expires", out expires);
                if (hasKey || hasExpiry)
                {
                    if (!hasKey || !hasExpiry || String.IsNullOrWhiteSpace(key))
                        throw new FormatException("NXM 下载授权不完整，请重新从 Nexus 打开文件。");
                    link.Key = key;
                    link.Expires = Number(expires);
                }
                if (query.TryGetValue("user_id", out user)) link.UserId = Number(user);
                return link;
            }

            if (uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                (uri.Host == "www.nexusmods.com" || uri.Host == "nexusmods.com"))
            {
                Match match = WebPath.Match(uri.AbsolutePath);
                if (match.Success)
                {
                    string file;
                    return new NexusLink(Number(match.Groups[1].Value), query.TryGetValue("file_id", out file) ? Number(file) : 0);
                }
            }
            throw new FormatException("这个链接未指向《异动核心》的 Nexus Mod。");
        }

        private static long Number(string value)
        {
            long result;
            if (!Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) || result <= 0)
                throw new FormatException("Nexus 链接里的编号或有效期无效。");
            return result;
        }

        private static Dictionary<string, string> ReadQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in query.TrimStart('?').Split('&'))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0) continue;
                string name = Uri.UnescapeDataString(part.Substring(0, equals)).ToLowerInvariant();
                if (name != "file_id" && name != "key" && name != "expires" && name != "user_id") continue;
                if (result.ContainsKey(name)) throw new FormatException("Nexus 链接包含重复参数。");
                result[name] = Uri.UnescapeDataString(part.Substring(equals + 1));
            }
            return result;
        }

        internal string AuthorizationQuery()
        {
            if (!IsAuthorized) return "";
            if (Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                throw new InvalidOperationException("NXM 下载授权已过期。请重新在 Nexus 页面确认管理器下载。");
            return "?key=" + Uri.EscapeDataString(Key) + "&expires=" + Expires.ToString(CultureInfo.InvariantCulture);
        }

        internal string PublicPage()
        {
            string page = Website + Domain + "/mods/" + ModId.ToString(CultureInfo.InvariantCulture);
            return FileId == 0 ? page : page + "?tab=files&file_id=" + FileId.ToString(CultureInfo.InvariantCulture);
        }

        internal string BrowserPage()
        {
            return PublicPage() + (FileId == 0 ? "" : "&nmm=1");
        }
    }

    public sealed class NexusAccount
    {
        public long user_id { get; set; }
        public string name { get; set; }
        public bool is_premium { get; set; }
    }

    public sealed class NexusMod
    {
        public string name { get; set; }
    }

    public sealed class NexusFile
    {
        public long file_id { get; set; }
        public string name { get; set; }
        public string file_name { get; set; }
        public string version { get; set; }
        public string mod_version { get; set; }
        public string category_name { get; set; }
        public bool is_primary { get; set; }
        public long uploaded_timestamp { get; set; }

        internal string Label { get { return String.IsNullOrWhiteSpace(name) ? file_name : name; } }
        internal string Version { get { return String.IsNullOrWhiteSpace(version) ? mod_version : version; } }

        internal bool IsAvailable
        {
            get
            {
                string category = (category_name ?? "").Replace(' ', '_').ToUpperInvariant();
                return file_id > 0 && category != "ARCHIVED" && category != "DELETED" && category != "REMOVED" && category != "OLD_VERSION";
            }
        }
    }

    public sealed class NexusFiles
    {
        public List<NexusFile> files { get; set; }
    }

    public sealed class NexusMirror
    {
        public string URI { get; set; }
    }

    internal sealed class NexusException : IOException
    {
        internal readonly HttpStatusCode Status;

        internal NexusException(HttpStatusCode status, string message) : base(message)
        {
            Status = status;
        }
    }

    internal sealed partial class NexusClient : IDisposable
    {
        private readonly HttpClient api;
        private readonly HttpClient download;
        private const string BaseApi = "https://api.nexusmods.com/";

        internal NexusClient() : this(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }, new HttpClientHandler { AllowAutoRedirect = false }) { }

        internal NexusClient(HttpMessageHandler apiHandler, HttpMessageHandler downloadHandler)
        {
            string version = typeof(NexusClient).Assembly.GetName().Version.ToString(3);
            api = new HttpClient(apiHandler) { Timeout = TimeSpan.FromSeconds(30) };
            download = new HttpClient(downloadHandler) { Timeout = TimeSpan.FromSeconds(60) };
            api.DefaultRequestHeaders.UserAgent.ParseAdd("RogueCoreNexus/" + version);
            api.DefaultRequestHeaders.TryAddWithoutValidation("Application-Name", "Rogue Core Nexus Manager");
            api.DefaultRequestHeaders.TryAddWithoutValidation("Application-Version", version);
            download.DefaultRequestHeaders.UserAgent.ParseAdd("RogueCoreNexus/" + version);
        }

        internal Task<NexusAccount> VerifyKeyAsync(string key, CancellationToken token)
        {
            return GetAsync<NexusAccount>("users/validate.json", key, token);
        }

        internal Task<NexusMod> GetModAsync(long modId, string key, CancellationToken token)
        {
            return GetAsync<NexusMod>(ModPath(modId) + ".json", key, token);
        }

        internal Task<NexusFiles> GetFilesAsync(long modId, string key, CancellationToken token)
        {
            return GetAsync<NexusFiles>(ModPath(modId) + "/files.json", key, token);
        }

        internal Task<NexusFile> GetFileAsync(NexusLink link, string key, CancellationToken token)
        {
            return GetAsync<NexusFile>(FilePath(link) + ".json", key, token);
        }

        internal async Task DownloadAsync(NexusLink link, string key, string destination,
            IProgress<int> progress, CancellationToken token)
        {
            var mirrors = await GetAsync<List<NexusMirror>>(FilePath(link) + "/download_link.json" +
                link.AuthorizationQuery(), key, token).ConfigureAwait(false);
            foreach (var mirror in mirrors.Where(m => m != null))
            {
                Uri uri;
                if (!Uri.TryCreate(mirror.URI, UriKind.Absolute, out uri) || !IsNexusDownload(uri)) continue;
                try
                {
                    await DownloadMirrorAsync(uri, destination, progress, token).ConfigureAwait(false);
                    return;
                }
                catch (HttpRequestException) { }
                catch (NexusException) { }
                catch (TaskCanceledException) { token.ThrowIfCancellationRequested(); }
            }
            throw new IOException("Nexus 的下载服务器未能返回完整文件，请重试下载。");
        }

        private async Task DownloadMirrorAsync(Uri uri, string destination, IProgress<int> progress, CancellationToken token)
        {
            for (int redirect = 0; redirect < 5; redirect++)
            {
                if (!IsNexusDownload(uri)) break;
                using (var response = await download.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 && response.Headers.Location != null)
                    {
                        uri = new Uri(uri, response.Headers.Location);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode) throw new NexusException(response.StatusCode, "下载镜像不可用。");
                    long length = response.Content.Headers.ContentLength ?? 0;
                    long copied = 0;
                    int lastPercent = -2;
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        byte[] buffer = new byte[65536];
                        while (true)
                        {
                            int read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                            if (read == 0) break;
                            await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                            copied += read;
                            int percent = length > 0 ? Math.Min(100, (int)(copied * 100.0 / length)) : -1;
                            if (percent != lastPercent && progress != null) progress.Report(percent);
                            lastPercent = percent;
                        }
                    }
                    if (copied == 0 || (length > 0 && copied != length))
                        throw new NexusException(response.StatusCode, "下载镜像返回的文件不完整。");
                    return;
                }
            }
            throw new NexusException(HttpStatusCode.BadGateway, "下载镜像重定向失败。");
        }

        private Task<T> GetAsync<T>(string path, string key, CancellationToken token) where T : class
        {
            return SendAsync<T>(HttpMethod.Get, "v1/" + path, null, key, token);
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, string key, CancellationToken token) where T : class
        {
            if (String.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("请先在设置中保存 Nexus API 密钥。");
            if (key.Length > 1024 || key.Any(Char.IsControl)) throw new FormatException("Nexus API 密钥格式无效。");
            using (var request = new HttpRequestMessage(method, BaseApi + path))
            {
                if (body != null) request.Content = new StringContent(AppStorage.Json().Serialize(body), Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("apikey", key);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                try
                {
                    using (var response = await api.SendAsync(request, token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) throw ApiError(response.StatusCode);
                        try
                        {
                            T data = AppStorage.Json().Deserialize<T>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                            if (data != null) return data;
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                        throw new IOException("Nexus API 返回的数据格式异常。");
                    }
                }
                catch (HttpRequestException error) { throw ConnectionError(error); }
                catch (TaskCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    throw new IOException("连接 Nexus API 超时，请重试。");
                }
            }
        }

        private static IOException ConnectionError(HttpRequestException error)
        {
            var cause = error.InnerException as WebException;
            switch (cause == null ? WebExceptionStatus.UnknownError : cause.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return new IOException("无法解析 Nexus API 的域名（api.nexusmods.com）。请检查 DNS 后重试。");
                case WebExceptionStatus.ProxyNameResolutionFailure:
                    return new IOException("无法解析 Windows 当前代理的地址。请检查系统代理后重试。");
                case WebExceptionStatus.SecureChannelFailure:
                    return new IOException("与 Nexus API 的 TLS 握手失败。请检查 Windows 的 TLS 或系统代理设置。");
                case WebExceptionStatus.TrustFailure:
                    return new IOException("Windows 无法验证 Nexus API 的 HTTPS 证书。请检查系统时间和证书设置。");
                default:
                    return new IOException("无法连接 Nexus API（api.nexusmods.com），请检查网络后重试。");
            }
        }

        private static NexusException ApiError(HttpStatusCode status)
        {
            switch ((int)status)
            {
                case 401: return new NexusException(status, "Nexus API 密钥已失效或填写错误。");
                case 403: return new NexusException(status, "Nexus 拒绝了下载授权。请确认网页和 API 密钥使用同一个账号，并重新申请下载链接。");
                case 404: return new NexusException(status, "Nexus 找不到这个 Mod 或文件。它可能已隐藏或移除。");
                case 429: return new NexusException(status, "Nexus API 请求次数已达上限，请稍后重试。");
                default: return new NexusException(status, "Nexus API 返回 HTTP " + (int)status + "，请稍后重试。");
            }
        }

        private static bool IsNexusDownload(Uri uri)
        {
            return uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo == "" &&
                (uri.Host.EndsWith(".nexus-cdn.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase));
        }

        private static string ModPath(long modId)
        {
            return "games/" + NexusLink.Domain + "/mods/" + modId.ToString(CultureInfo.InvariantCulture);
        }

        private static string FilePath(NexusLink link)
        {
            return ModPath(link.ModId) + "/files/" + link.FileId.ToString(CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            api.Dispose();
            download.Dispose();
        }
    }
}
