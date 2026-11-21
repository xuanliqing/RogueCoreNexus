using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RogueCoreNexus
{
    internal sealed class ScriptRuntime
    {
        internal const string Version = "0.31.0";
        internal const string Proxy = GameLocation.Win64 + @"\dwmapi.dll";
        internal const string Core = GameLocation.Win64 + @"\ue4ss\UE4SSL.dll";
        internal const string JavaScript = GameLocation.ScriptDirectory + @"\UE4SSL.JavaScript\main.dll";
        internal const string Framework = GameLocation.ScriptDirectory + @"\UE4SSL.JavaScript.Framework\js\main.js";
        internal const string Lua = GameLocation.ScriptDirectory + @"\UE4SSL.Lua\main.dll";
        internal const string Settings = GameLocation.Win64 + @"\ue4ss\UE4SS-settings.ini";

        private const int ArchiveLength = 4982822;
        private const string ArchiveHash = "E4AF3674571D79860A1522BB98CCDDBE27A7132718B4D1817E73C12F48625890";
        private const string ReleasePath = "/releases/ue4ssl/windows/stable/" + Version + "/UE4SSL.zip";
        private static readonly string[] Mirrors =
        {
            "https://yuri-oss-hz.oss-cn-hangzhou.aliyuncs.com",
            "https://yuri-oss-sg.oss-ap-southeast-1.aliyuncs.com"
        };
        private static readonly string[] RuntimeFiles = { JavaScript, Framework, Core, Proxy };

        private readonly AppStorage storage;

        internal ScriptRuntime(AppStorage storage) { this.storage = storage; }

        internal string ArchivePath
        {
            get { return SafePath.In(storage.Root, @"runtimes\UE4SSL-" + Version + ".zip"); }
        }

        internal static bool IsScriptEntry(PayloadFile file, string entry)
        {
            return file.IsScript && file.Target.Replace('/', '\\').EndsWith("\\" + entry, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool OwnsTarget(string target)
        {
            return RuntimeFiles.Contains(target, StringComparer.OrdinalIgnoreCase) ||
                String.Equals(target, Settings, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsInstalled(string root, bool javascript, bool lua)
        {
            return Exists(root, Proxy) && Exists(root, Core) &&
                (!javascript || Exists(root, JavaScript)) && (!lua || Exists(root, Lua));
        }

        internal static bool Exists(string root, string target)
        {
            return File.Exists(SafePath.In(root, target));
        }

        internal string[] Stage(string directory, Action downloading, CancellationToken token)
        {
            byte[] bytes = ReadArchiveAsync(downloading, token).GetAwaiter().GetResult();
            using (var stream = new MemoryStream(bytes, false))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (string target in RuntimeFiles)
                {
                    token.ThrowIfCancellationRequested();
                    string name = target.Substring(GameLocation.Win64.Length + 1).Replace('\\', '/').Replace("/Mods/", "/mods/");
                    ZipArchiveEntry entry = archive.GetEntry(name);
                    if (entry == null) throw new InvalidDataException("UE4SS Lite 压缩包缺少运行库文件。" + name);
                    string file = SafePath.In(directory, target);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    using (var input = entry.Open())
                    using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                        input.CopyToAsync(output, 65536, token).GetAwaiter().GetResult();
                }
            }

            // Mintcat enables these hooks and targets Unreal Engine 5.6 for Rogue Core.
            AppStorage.WriteBytes(SafePath.In(directory, Settings), Encoding.ASCII.GetBytes(
                "[EngineVersionOverride]\r\nMajorVersion = 5\r\nMinorVersion = 6\r\n\r\n" +
                "[Hooks]\r\nHookProcessInternal = 1\r\nHookProcessLocalScriptFunction = 1\r\n"));
            return RuntimeFiles.Concat(new[] { Settings }).ToArray();
        }

        private async Task<byte[]> ReadArchiveAsync(Action downloading, CancellationToken token)
        {
            string archive = ArchivePath;
            if (File.Exists(archive) && new FileInfo(archive).Length == ArchiveLength)
            {
                byte[] cached = File.ReadAllBytes(archive);
                if (ValidArchive(cached)) return cached;
            }

            token.ThrowIfCancellationRequested();
            if (downloading != null) downloading();
            Exception lastFailure = null;
            using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = ArchiveLength
            })
            {
                foreach (string mirror in Mirrors)
                {
                    try
                    {
                        using (var response = await client.GetAsync(mirror + ReleasePath, token).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            if (!ValidArchive(bytes)) throw new InvalidDataException("UE4SS Lite 下载文件的 SHA-256 不匹配。");
                            token.ThrowIfCancellationRequested();
                            AppStorage.WriteBytes(archive, bytes);
                            return bytes;
                        }
                    }
                    catch (HttpRequestException error) { lastFailure = error; }
                    catch (InvalidDataException error) { lastFailure = error; }
                    catch (TaskCanceledException error) { token.ThrowIfCancellationRequested(); lastFailure = error; }
                }
            }
            throw new IOException("无法下载或校验 Mintcat 的 UE4SS Lite " + Version + "。请检查网络后重试。", lastFailure);
        }

        private static bool ValidArchive(byte[] bytes)
        {
            return bytes.Length == ArchiveLength && String.Equals(AppStorage.HashBytes(bytes), ArchiveHash, StringComparison.Ordinal);
        }
    }
}
