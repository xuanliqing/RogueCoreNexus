using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace RogueCoreNexus
{
    public sealed class InstalledFile
    {
        public string Target { get; set; }
        public string Hash { get; set; }
    }

    public sealed class InstalledManifest
    {
        public string TransactionId { get; set; }
        public string ProfileId { get; set; }
        public List<InstalledFile> Files { get; set; }

        public InstalledManifest() { Files = new List<InstalledFile>(); }
    }

    public sealed class DeploymentAction
    {
        public string Target { get; set; }
        public string OldHash { get; set; }
        public string NewHash { get; set; }
    }

    public sealed class DeploymentJournal
    {
        public string Id { get; set; }
        public List<DeploymentAction> Actions { get; set; }
    }

    internal sealed class GameDeployer
    {
        private sealed class DesiredFile
        {
            internal string Cache;
            internal string Hash;
            internal string ModName;
        }

        private readonly AppStorage storage;

        internal GameDeployer(AppStorage storage) { this.storage = storage; }

        internal int Apply(string selectedGame, Profile profile, CancellationToken token, Action downloadingRuntime = null)
        {
            string root = GameLocation.FindRoot(selectedGame);
            if (root == null) throw new DirectoryNotFoundException("请在设置中选择《异动核心》的安装目录。");
            EnsureGameClosed();

            string gameState = storage.GameStateDirectory(root);
            string manifestPath = Path.Combine(gameState, "installed.json");
            string pendingPath = Path.Combine(gameState, "pending.json");
            Recover(root, manifestPath, pendingPath);

            var previous = (AppStorage.ReadJson<InstalledManifest>(manifestPath) ?? new InstalledManifest())
                .Files.ToDictionary(f => f.Target, f => f.Hash, StringComparer.OrdinalIgnoreCase);
            string originalSettings;
            if (previous.TryGetValue(ScriptRuntime.Settings, out originalSettings))
            {
                string settingsPath = Destination(root, ScriptRuntime.Settings);
                // Keep a runtime configuration that the user or the loader has modified.
                if (File.Exists(settingsPath) && !Matches(settingsPath, originalSettings))
                    previous.Remove(ScriptRuntime.Settings);
            }

            bool deployLite;
            string scriptRoot = ResolveScriptMods(root, profile, previous, out deployLite);
            var desired = Plan(root, profile, scriptRoot);
            if (!deployLite)
                return ApplyFiles(root, profile.Id, manifestPath, pendingPath, previous, desired, token);

            string runtimeStage = storage.CreateTemporaryDirectory();
            try
            {
                var runtime = new ScriptRuntime(storage);
                foreach (string target in runtime.Stage(runtimeStage, downloadingRuntime, token))
                {
                    if (target == ScriptRuntime.Settings && ScriptRuntime.Exists(root, target) && !previous.ContainsKey(target))
                        continue;
                    if (desired.ContainsKey(target))
                        throw new IOException("Mod 与 UE4SS Lite 运行库使用了同一个安装位置。" + target);
                    string cache = SafePath.In(runtimeStage, target);
                    desired.Add(target, new DesiredFile { Cache = cache, Hash = AppStorage.HashFile(cache), ModName = "UE4SS Lite" });
                }
                return ApplyFiles(root, profile.Id, manifestPath, pendingPath, previous, desired, token);
            }
            finally { SafePath.TryDeleteOwnedDirectory(runtimeStage); }
        }

        private static int ApplyFiles(string root, string profileId, string manifestPath, string pendingPath,
            Dictionary<string, string> previous, Dictionary<string, DesiredFile> desired, CancellationToken token)
        {
            var journal = new DeploymentJournal { Id = Guid.NewGuid().ToString("N"), Actions = new List<DeploymentAction>() };
            var unmanagedMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string target in previous.Keys.Union(desired.Keys, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                string full = Destination(root, target);
                string recorded;
                previous.TryGetValue(target, out recorded);
                bool exists = File.Exists(full);
                DesiredFile next;
                string nextHash = desired.TryGetValue(target, out next) ? next.Hash : null;
                if (exists && recorded == null && Matches(full, nextHash))
                {
                    // An identical manually installed file already satisfies this payload. Keep it unmanaged.
                    unmanagedMatches.Add(target);
                    continue;
                }
                if (Directory.Exists(full) || (exists && recorded == null))
                    throw new IOException("安装位置已有未受本工具管理的同名文件。" + full);
                if (exists && !Matches(full, recorded))
                    throw new IOException("已安装文件被其他工具改动，请先处理这个文件。" + full);

                string oldHash = exists ? recorded : null;
                if (oldHash != nextHash)
                    journal.Actions.Add(new DeploymentAction { Target = target, OldHash = oldHash, NewHash = nextHash });
            }

            var manifest = new InstalledManifest
            {
                TransactionId = journal.Id,
                ProfileId = profileId,
                Files = desired.Where(p => !unmanagedMatches.Contains(p.Key))
                    .Select(p => new InstalledFile { Target = p.Key, Hash = p.Value.Hash }).ToList()
            };
            if (journal.Actions.Count == 0)
            {
                AppStorage.WriteJson(manifestPath, manifest);
                return desired.Count;
            }

            string stage = StageDirectory(root, journal.Id);
            Directory.CreateDirectory(stage);
            try
            {
                for (int i = 0; i < journal.Actions.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    DeploymentAction action = journal.Actions[i];
                    if (action.NewHash == null) continue;
                    string staged = StageFile(stage, i, ".new");
                    File.Copy(desired[action.Target].Cache, staged);
                    if (!Matches(staged, action.NewHash)) throw new IOException("缓存文件损坏，请重新下载对应的 Mod。");
                }

                token.ThrowIfCancellationRequested();
                EnsureGameClosed();
                AppStorage.WriteJson(pendingPath, journal);
                // From here, journal recovery owns the stage until the manifest has been committed.
                for (int i = 0; i < journal.Actions.Count; i++)
                {
                    DeploymentAction action = journal.Actions[i];
                    string full = Destination(root, action.Target);
                    if (action.OldHash != null)
                    {
                        if (!Matches(full, action.OldHash)) throw new IOException("应用期间文件发生变化。" + full);
                        File.Move(full, StageFile(stage, i, ".old"));
                    }
                    if (action.NewHash != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(full));
                        File.Move(StageFile(stage, i, ".new"), full);
                    }
                }
                AppStorage.WriteJson(manifestPath, manifest);
                Recover(root, manifestPath, pendingPath);
                return desired.Count;
            }
            catch
            {
                Recover(root, manifestPath, pendingPath);
                throw;
            }
            finally
            {
                if (!File.Exists(pendingPath)) SafePath.TryDeleteOwnedDirectory(stage);
            }
        }

        private static string ResolveScriptMods(string root, Profile profile, Dictionary<string, string> previous, out bool deployLite)
        {
            deployLite = false;
            var scripts = profile.Mods.Where(m => m.Enabled).SelectMany(m => m.Files).Where(f => f.IsScript).ToArray();
            if (scripts.Length == 0) return null;

            bool javascript = scripts.Any(f => ScriptRuntime.IsScriptEntry(f, @"js\main.js"));
            bool lua = !javascript || scripts.Any(f => ScriptRuntime.IsScriptEntry(f, @"Scripts\main.lua"));
            string classic = GameLocation.FindClassicScriptMods(root);
            if (!javascript && classic != null) return classic;

            if (ScriptRuntime.IsInstalled(root, javascript, lua))
            {
                deployLite = previous.Keys.Any(ScriptRuntime.OwnsTarget);
                return GameLocation.ScriptDirectory;
            }
            if (!javascript)
                throw new InvalidOperationException("此配置档包含 Lua Mod。请安装作者要求的 UE4SS 或带 Lua 插件的 UE4SS Lite。");
            if (classic != null)
                throw new InvalidOperationException("检测到标准 UE4SS。JavaScript Mod 需要带 JavaScript 插件的 UE4SS Lite，请先处理已有加载器。");
            if (lua && !ScriptRuntime.Exists(root, ScriptRuntime.Lua))
                throw new InvalidOperationException("此配置档同时启用了 JavaScript 和 Lua Mod，需要带有两种脚本插件的 UE4SS Lite。Mintcat 发布的运行库包包含 JavaScript 插件。");

            deployLite = true;
            return GameLocation.ScriptDirectory;
        }

        private Dictionary<string, DesiredFile> Plan(string root, Profile profile, string scriptRoot)
        {
            var desired = new Dictionary<string, DesiredFile>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry entry in profile.Mods.Where(m => m.Enabled))
            {
                string package = SafePath.In(storage.Packages, SafePath.Id(entry.PackageId));
                foreach (PayloadFile file in entry.Files)
                {
                    string target = Path.Combine(file.IsScript ? scriptRoot : GameLocation.Paks, SafePath.Relative(file.Target));
                    Destination(root, target);
                    string cache = SafePath.In(package, file.CachePath);
                    if (!File.Exists(cache)) throw new FileNotFoundException("缓存缺少文件，请重新添加 Mod。" + entry.Name);
                    DesiredFile other;
                    if (desired.TryGetValue(target, out other))
                        throw new IOException("两个已启用的 Mod 写入同一个位置。" + Environment.NewLine +
                            other.ModName + " / " + entry.Name + Environment.NewLine + target);
                    desired.Add(target, new DesiredFile { Cache = cache, Hash = file.Hash, ModName = entry.Name });
                }
            }
            return desired;
        }

        internal static void Recover(string root, string manifestPath, string pendingPath)
        {
            var journal = AppStorage.ReadJson<DeploymentJournal>(pendingPath);
            if (journal == null) return;
            string stage = StageDirectory(root, journal.Id);
            var installed = AppStorage.ReadJson<InstalledManifest>(manifestPath);
            if (installed == null || installed.TransactionId != journal.Id)
            {
                if (!Directory.Exists(stage)) throw new IOException("上次安装的暂存目录丢失，无法恢复。" + stage);
                for (int i = journal.Actions.Count - 1; i >= 0; i--)
                {
                    var action = journal.Actions[i];
                    string target = Destination(root, action.Target);
                    string backup = StageFile(stage, i, ".old");
                    bool hasBackup = File.Exists(backup);
                    bool placedNew = action.NewHash != null && !File.Exists(StageFile(stage, i, ".new"));
                    if (hasBackup || (action.OldHash == null && placedNew))
                    {
                        if (File.Exists(target))
                        {
                            if (!placedNew || !Matches(target, action.NewHash))
                                throw new IOException("无法恢复上次安装。目标文件已被更改。" + target);
                            File.Delete(target);
                        }
                        if (hasBackup) File.Move(backup, target);
                    }
                }
            }
            File.Delete(pendingPath);
            SafePath.TryDeleteOwnedDirectory(stage);
        }

        private static bool Matches(string path, string hash)
        {
            return hash != null && File.Exists(path) && String.Equals(AppStorage.HashFile(path), hash, StringComparison.OrdinalIgnoreCase);
        }

        internal static string StageDirectory(string root, string id)
        {
            return SafePath.In(root, ".rogue-core-nexus-" + SafePath.Id(id));
        }

        private static string StageFile(string stage, int index, string ext)
        {
            return SafePath.In(stage, index + ext);
        }

        private static string Destination(string root, string relative)
        {
            string target = SafePath.Relative(relative);
            if (!String.Equals(Path.GetDirectoryName(target), GameLocation.Paks, StringComparison.OrdinalIgnoreCase) &&
                !IsScriptTarget(target, GameLocation.ScriptDirectory) &&
                !IsScriptTarget(target, GameLocation.LegacyScriptDirectory) &&
                !ScriptRuntime.OwnsTarget(target))
                throw new IOException("Mod 的目标文件不在游戏的 Mod 目录内。" + target);
            return SafePath.In(root, target);
        }

        private static bool IsScriptTarget(string target, string prefix)
        {
            return target.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase) &&
                target.Substring(prefix.Length + 1).Contains("\\");
        }

        private static void EnsureGameClosed()
        {
            foreach (string processName in new[] { "RogueCore", Path.GetFileNameWithoutExtension(GameLocation.ShippingExe) })
            {
                Process[] found = Process.GetProcessesByName(processName);
                foreach (Process process in found) process.Dispose();
                if (found.Length > 0) throw new InvalidOperationException("请退出《异动核心》后再应用 Mod 配置。");
            }
        }
    }
}
