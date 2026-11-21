using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace RogueCoreNexus
{
    public sealed class AppState
    {
        public string GameRoot { get; set; }
        public string ActiveProfile { get; set; }
        public List<Profile> Profiles { get; set; }

        public AppState()
        {
            var profile = new Profile { Name = "默认" };
            Profiles = new List<Profile> { profile };
            ActiveProfile = profile.Id;
        }

        public Profile Current()
        {
            return Profiles.FirstOrDefault(p => p.Id == ActiveProfile) ?? Profiles.First();
        }
    }

    public sealed class Profile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<ModEntry> Mods { get; set; }

        public Profile()
        {
            Id = Guid.NewGuid().ToString("N");
            Mods = new List<ModEntry>();
        }
    }

    public sealed class ModEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string FileName { get; set; }
        public string Version { get; set; }
        public long ModId { get; set; }
        public long FileId { get; set; }
        public bool Enabled { get; set; }
        public string PackageId { get; set; }
        public List<PayloadFile> Files { get; set; }

        public ModEntry()
        {
            Id = Guid.NewGuid().ToString("N");
            Enabled = true;
            Files = new List<PayloadFile>();
        }
    }

    public sealed class PayloadFile
    {
        public string CachePath { get; set; }
        public string Target { get; set; }
        public bool IsScript { get; set; }
        public string Hash { get; set; }
    }

    internal sealed class AppStorage
    {
        internal readonly string Root;
        internal readonly string Packages;
        internal readonly string Temporary;
        private readonly string stateFile;
        private readonly string keyFile;

        internal AppStorage(string root)
        {
            Root = root;
            Packages = Path.Combine(Root, "packages");
            Temporary = Path.Combine(Root, "temp");
            stateFile = Path.Combine(Root, "profiles.json");
            keyFile = Path.Combine(Root, "nexus-key.dat");
            Directory.CreateDirectory(Packages);
            Directory.CreateDirectory(Temporary);
        }

        internal AppState LoadState()
        {
            var state = ReadJson<AppState>(stateFile) ?? new AppState();
            if (state.Profiles == null || state.Profiles.Count == 0)
                throw new InvalidDataException("配置档文件缺少有效的配置档。");
            return state;
        }

        internal void SaveState(AppState state)
        {
            WriteJson(stateFile, state);
        }

        internal string GameStateDirectory(string gameRoot)
        {
            string key = HashBytes(Encoding.UTF8.GetBytes(gameRoot.ToUpperInvariant()));
            return Path.Combine(Root, "games", key);
        }

        internal string CreateTemporaryDirectory()
        {
            string directory = Path.Combine(Temporary, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        internal string LoadKey()
        {
            return File.Exists(keyFile) ? ReadProtectedText(keyFile) : null;
        }

        internal void SaveKey(string key)
        {
            if (String.IsNullOrEmpty(key))
            {
                File.Delete(keyFile);
                return;
            }
            WriteProtectedText(keyFile, key);
        }

        internal static void WriteProtectedText(string path, string text)
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(text);
            try { WriteBytes(path, ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(plaintext, 0, plaintext.Length); }
        }

        internal static string ReadProtectedText(string path)
        {
            byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { Array.Clear(plaintext, 0, plaintext.Length); }
        }

        internal static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        }

        internal static T ReadJson<T>(string path) where T : class
        {
            return File.Exists(path) ? Json().Deserialize<T>(File.ReadAllText(path)) : null;
        }

        internal static void WriteJson(string path, object data)
        {
            WriteBytes(path, Encoding.UTF8.GetBytes(Json().Serialize(data)));
        }

        internal static void WriteBytes(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { File.Delete(temp); }
        }

        internal static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return Hex(hash.ComputeHash(stream));
        }

        internal static string HashBytes(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(bytes));
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "");
        }
    }

    internal static class SafePath
    {
        private static readonly Regex DeviceName = new Regex(
            @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$)(\.|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static string Relative(string path)
        {
            if (String.IsNullOrEmpty(path)) throw new InvalidDataException("文件路径为空。");
            string value = path.Replace('/', '\\');
            while (value.StartsWith(".\\", StringComparison.Ordinal)) value = value.Substring(2);
            value = value.TrimEnd('\\');
            if (value.Length == 0 || Path.IsPathRooted(value) || value.IndexOf(':') >= 0)
                throw new InvalidDataException("压缩包包含不受支持的文件路径。");
            foreach (string part in value.Split('\\'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") ||
                    part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || DeviceName.IsMatch(part))
                    throw new InvalidDataException("压缩包包含不受支持的文件路径。");
            }
            return value;
        }

        internal static string In(string root, string relative)
        {
            string path = Relative(relative);
            string full = Path.GetFullPath(root);
            foreach (string part in path.Split('\\'))
            {
                full = Path.Combine(full, part);
                if (IsReparsePoint(full)) throw new IOException("目标位置包含符号链接或目录联接。" + full);
            }
            return full;
        }

        internal static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        internal static string Id(string value)
        {
            Guid guid;
            if (!Guid.TryParseExact(value, "N", out guid)) throw new InvalidDataException("无效的缓存或事务编号。");
            return guid.ToString("N");
        }

        internal static void TryDeleteOwnedDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                if (!IsReparsePoint(path))
                {
                    foreach (string file in Directory.EnumerateFiles(path)) File.Delete(file);
                    foreach (string child in Directory.EnumerateDirectories(path)) TryDeleteOwnedDirectory(child);
                }
                Directory.Delete(path, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static class GameLocation
    {
        internal const string SteamId = "2605790";
        internal const string Paks = @"RogueCore\Content\Paks";
        internal const string Win64 = @"RogueCore\Binaries\Win64";
        internal const string ScriptDirectory = Win64 + @"\ue4ss\Mods";
        internal const string LegacyScriptDirectory = Win64 + @"\Mods";
        internal const string ShippingExe = "RogueCore-Win64-Shipping.exe";

        internal static string FindRoot(string selected)
        {
            if (String.IsNullOrWhiteSpace(selected)) return null;
            string candidate = File.Exists(selected) ? Path.GetDirectoryName(selected) : selected;
            var directory = new DirectoryInfo(candidate);
            for (int depth = 0; directory != null && depth < 6; depth++, directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, Paks)) &&
                    File.Exists(Path.Combine(directory.FullName, Win64, ShippingExe)))
                    return directory.FullName;
            }
            return null;
        }

        internal static string DetectSteam(string steamFolder = null)
        {
            string userSteam = ReadRegistry(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath");
            string systemSteam = ReadRegistry(@"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Valve\Steam", "InstallPath");
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string steam in new[] { steamFolder, userSteam, systemSteam, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam") })
            {
                if (String.IsNullOrWhiteSpace(steam)) continue;
                libraries.Add(steam);
                string folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(folders)) continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(folders), @"""path""\s+""((?:\\.|[^""\\])*)"""))
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            foreach (string library in libraries)
            {
                string apps = Path.Combine(library, "steamapps");
                string manifest = Path.Combine(apps, "appmanifest_" + SteamId + ".acf");
                if (!File.Exists(manifest)) continue;
                Match match = Regex.Match(File.ReadAllText(manifest), @"""installdir""\s+""([^""]+)""");
                if (!match.Success) continue;
                string found = FindRoot(Path.Combine(apps, "common", match.Groups[1].Value));
                if (found != null) return found;
            }
            return null;
        }

        private static string ReadRegistry(string path, string name)
        {
            try { return Registry.GetValue(path, name, null) as string; }
            catch (SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static string FindClassicScriptMods(string root)
        {
            if (File.Exists(SafePath.In(root, Win64 + @"\ue4ss\UE4SS.dll"))) return ScriptDirectory;
            if (File.Exists(SafePath.In(root, Win64 + @"\UE4SS.dll"))) return LegacyScriptDirectory;
            return null;
        }
    }
}
