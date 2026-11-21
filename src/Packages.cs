using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RogueCoreNexus
{
    internal sealed class PackageImporter
    {
        private static readonly HashSet<string> PakExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".pak", ".sig", ".utoc", ".ucas" };
        private static readonly HashSet<string> ScriptDataExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".lua", ".js", ".json", ".ini", ".cfg", ".txt", ".md", ".csv", ".xml", ".yaml", ".yml", ".png" };
        private readonly AppStorage storage;

        internal PackageImporter(AppStorage storage) { this.storage = storage; }

        internal static string ArchiveExtension(string name)
        {
            string ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            if (CanImport(name)) return ext;
            throw new InvalidDataException("这个文件的格式不受支持。请选择 PAK、ZIP、7z 或 RAR 文件。");
        }

        internal static bool CanImport(string name)
        {
            string ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            return ext == ".pak" || ext == ".zip" || ext == ".7z" || ext == ".rar";
        }

        internal ModEntry Import(string source, ModEntry entry, CancellationToken token)
        {
            string ext = ArchiveExtension(entry.FileName);
            string work = storage.CreateTemporaryDirectory();
            try
            {
                string extracted = Path.Combine(work, "extracted");
                string package = Path.Combine(work, "payload");
                Directory.CreateDirectory(extracted);
                Directory.CreateDirectory(package);

                if (ext == ".pak")
                {
                    string name = SafePath.Relative(Path.GetFileName(entry.FileName));
                    File.Copy(source, SafePath.In(extracted, name));
                    ImportPakSidecars(source, name, extracted);
                }
                else if (ext == ".zip") ExtractZip(source, extracted, token);
                else ExtractNativeArchive(source, extracted, token);

                token.ThrowIfCancellationRequested();
                string[] files = EnumerateFiles(extracted, extracted).ToArray();
                var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ImportPaks(extracted, package, files, entry, targets, token);
                ImportScripts(extracted, package, files, entry, targets, token);
                if (entry.Files.Count == 0)
                    throw new InvalidDataException("包内未找到 PAK、IoStore 文件组或带有 main.lua / main.js 的 UE4SS 模组。");

                token.ThrowIfCancellationRequested();
                entry.PackageId = Guid.NewGuid().ToString("N");
                Directory.Move(package, SafePath.In(storage.Packages, entry.PackageId));
                return entry;
            }
            finally { SafePath.TryDeleteOwnedDirectory(work); }
        }

        private static void ImportPakSidecars(string source, string name, string extracted)
        {
            string sourceStem = Path.GetFileNameWithoutExtension(source);
            string sourceFolder = Path.GetDirectoryName(source);
            string newStem = Path.GetFileNameWithoutExtension(name);
            foreach (string ext in new[] { ".sig", ".utoc", ".ucas" })
            {
                string companion = Path.Combine(sourceFolder, sourceStem + ext);
                if (File.Exists(companion)) File.Copy(companion, SafePath.In(extracted, newStem + ext));
            }
        }

        private static void ExtractZip(string source, string extracted, CancellationToken token)
        {
            var extractedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var stream = File.OpenRead(source))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.FullName == "./" || entry.FullName == ".") continue;
                    string path = SafePath.In(extracted, entry.FullName);
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    if (!extractedFiles.Add(path)) throw new InvalidDataException("压缩包中有重复文件路径。");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open())
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                        input.CopyToAsync(output, 65536, token).GetAwaiter().GetResult();
                }
            }
        }

        private static void ExtractNativeArchive(string source, string extracted, CancellationToken token)
        {
            string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            if (!File.Exists(tar)) throw new InvalidOperationException("解包 7z 和 RAR 需要系统随 Windows 安装的 tar.exe。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in Lines(RunTar(tar, "-tf " + Quote(source), token)))
            {
                if (name == "." || name == "./") continue;
                if (!names.Add(SafePath.Relative(name))) throw new InvalidDataException("压缩包中有重复文件路径。");
            }
            foreach (string description in Lines(RunTar(tar, "-tvf " + Quote(source), token)))
            {
                if ((description[0] != '-' && description[0] != 'd') || description.Contains(" link to "))
                    throw new InvalidDataException("压缩包包含链接或其他非普通文件。");
            }
            RunTar(tar, "-xf " + Quote(source) + " -C " + Quote(extracted) + " --no-same-owner --no-same-permissions", token);
        }

        private static IEnumerable<string> Lines(string text)
        {
            return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string Quote(string path)
        {
            // Windows paths cannot contain quote characters. These paths are files or non-root directories.
            return "\"" + path + "\"";
        }

        private static string RunTar(string tar, string arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var process = Process.Start(new ProcessStartInfo(tar, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                using (token.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } }))
                    process.WaitForExit();
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                {
                    string error = errors.GetAwaiter().GetResult().Trim();
                    throw new InvalidDataException("Windows 无法读取这个压缩包。" + (error.Length > 300 ? error.Substring(0, 300) : error));
                }
                return output.GetAwaiter().GetResult();
            }
        }

        private static IEnumerable<string> EnumerateFiles(string root, string directory)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("压缩包包含符号链接。");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    foreach (string subfile in EnumerateFiles(root, child)) yield return subfile;
                }
                else yield return SafePath.Relative(child.Substring(root.Length + 1));
            }
        }

        private static void ImportPaks(string extracted, string package, IEnumerable<string> files,
            ModEntry entry, HashSet<string> targets, CancellationToken token)
        {
            var pakFiles = files.Where(f => PakExtensions.Contains(Path.GetExtension(f))).ToArray();
            var names = new HashSet<string>(pakFiles, StringComparer.OrdinalIgnoreCase);
            foreach (string file in pakFiles)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                string stem = Path.ChangeExtension(file, null);
                if (ext == ".sig" && !names.Contains(stem + ".pak")) continue;
                if (ext == ".utoc" && !names.Contains(stem + ".ucas"))
                    throw new InvalidDataException("IoStore 文件缺少配套的 UCAS。" + Path.GetFileName(file));
                if (ext == ".ucas" && !names.Contains(stem + ".utoc") &&
                    !names.Any(n => n.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) &&
                        stem.StartsWith(Path.ChangeExtension(n, null) + "_s", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("IoStore 文件缺少配套的 UTOC。" + Path.GetFileName(file));
                AddFile(extracted, file, package, Path.GetFileName(file), false, entry, targets, token);
            }
        }

        private static void ImportScripts(string extracted, string package, string[] files,
            ModEntry entry, HashSet<string> targets, CancellationToken token)
        {
            var scriptRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string parent = Path.GetDirectoryName(file) ?? "";
                string scriptFolder = Path.GetFileName(parent);
                string name = Path.GetFileName(file);
                if ((String.Equals(scriptFolder, "Scripts", StringComparison.OrdinalIgnoreCase) && name.Equals("main.lua", StringComparison.OrdinalIgnoreCase)) ||
                    (String.Equals(scriptFolder, "js", StringComparison.OrdinalIgnoreCase) && name.Equals("main.js", StringComparison.OrdinalIgnoreCase)))
                    scriptRoots.Add(Path.GetDirectoryName(parent) ?? "");
            }

            foreach (string root in scriptRoots)
            {
                string folder = root.Length > 0 ? Path.GetFileName(root) : "";
                if (folder.Length == 0 || folder.Equals("Mods", StringComparison.OrdinalIgnoreCase))
                    folder = "RogueCoreNexus_" + (entry.ModId > 0 ? entry.ModId + "_" + entry.FileId : SafePath.Id(entry.Id));
                string prefix = root.Length == 0 ? "" : root + "\\";
                foreach (string file in files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!ScriptDataExtensions.Contains(Path.GetExtension(file))) continue;
                    string relative = file.Substring(prefix.Length);
                    if (relative.Equals("enabled.txt", StringComparison.OrdinalIgnoreCase) || relative.Equals("mods.txt", StringComparison.OrdinalIgnoreCase)) continue;
                    AddFile(extracted, file, package, Path.Combine(folder, relative), true, entry, targets, token);
                }
                string marker = Path.Combine(folder, "enabled.txt");
                string cached = Path.Combine("scripts", marker);
                string markerPath = SafePath.In(package, cached);
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath));
                using (File.Create(markerPath)) { }
                Record(entry, targets, cached, marker, true, markerPath);
            }
        }

        private static void AddFile(string extracted, string source, string package, string target,
            bool script, ModEntry entry, HashSet<string> targets, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.Combine(script ? "scripts" : "paks", target);
            string cached = SafePath.In(package, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(cached));
            File.Copy(SafePath.In(extracted, source), cached, false);
            File.SetAttributes(cached, FileAttributes.Normal);
            Record(entry, targets, relative, target, script, cached);
        }

        private static void Record(ModEntry entry, HashSet<string> targets, string relative, string target, bool script, string path)
        {
            if (!targets.Add(relative)) throw new InvalidDataException("包内多个文件使用了相同的安装路径。" + target);
            entry.Files.Add(new PayloadFile { CachePath = relative, Target = target, IsScript = script, Hash = AppStorage.HashFile(path) });
        }
    }
}
