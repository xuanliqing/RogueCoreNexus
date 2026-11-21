using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RogueCoreNexus
{
    internal static class SmokeTests
    {
        private static readonly CancellationToken NoCancel = CancellationToken.None;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--probe-nexus") return ProbeNexus();
            string buildDirectory = Path.GetFullPath(args[0]);
            string root = SafePath.In(buildDirectory, "verification-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                if (args.Contains("--download-runtime"))
                {
                    var runtimeStorage = new AppStorage(Path.Combine(root, "appdata"));
                    var runtime = new ScriptRuntime(runtimeStorage);
                    int downloads = 0;
                    string stage = runtimeStorage.CreateTemporaryDirectory();
                    string[] files = runtime.Stage(stage, () => downloads++, NoCancel);
                    Assert(downloads == 1 && files.Length == 5 && files.All(file => File.Exists(SafePath.In(stage, file))),
                        "downloads and stages the verified publisher runtime");
                    runtime.Stage(runtimeStorage.CreateTemporaryDirectory(), () => downloads++, NoCancel);
                    Assert(downloads == 1, "stages a second copy from the verified cache");
                    Console.WriteLine("PASS Mintcat " + ScriptRuntime.Version + " live download, verification and cache reuse");
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (sender, e) => Console.Error.WriteLine(e.Exception.GetType().Name + ": " + e.Exception.Message);
                var storage = new AppStorage(Path.Combine(root, "appdata"));
                TestLinks();
                TestSteamLibrary(root);
                TestBrowserInbox(storage);
                byte[] zip = MakeArchive(new Dictionary<string, string> { { "ModHub_P.pak", "sample pak" } });
                TestNexusAsync(root, zip).GetAwaiter().GetResult();
                TestInstall(root, storage);
                TestRejectedArchive(root, storage);
                TestScriptsAndIoStore(root, storage);
                string runtimeArchive = Path.Combine(buildDirectory, "UE4SSL-" + ScriptRuntime.Version + ".zip");
                TestMintcatScripts(root, runtimeArchive);
                TestRecovery(root, storage);
                TestStorage(storage);
                TestNativeArchive(root, storage);
                CaptureUi(buildDirectory, storage);
                TestUiDownload(root, zip, true);
                TestUiDownload(root, zip, false);
                TestUiDownload(root, zip, true, true);
                if (File.Exists(runtimeArchive)) TestUiDownload(root, zip, false, false, runtimeArchive);
                Console.WriteLine("PASS versioned and recursive prerequisites, Premium/NXM queue, cache reuse, and rollback");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally { SafePath.TryDeleteOwnedDirectory(root); }
        }

        private static void TestLinks()
        {
            Assert(NexusLink.KeyPage == "https://www.nexusmods.com/settings/api-keys", "current API Keys page");
            NexusLink page = NexusLink.Parse("https://www.nexusmods.com/deeprockgalacticroguecore/mods/149?tab=description");
            Assert(page.ModId == 149 && page.FileId == 0 && !page.IsNxm, "ordinary mod URL");
            NexusLink files = NexusLink.Parse("https://www.nexusmods.com/games/deeprockgalacticroguecore/mods/149?file_id=355&tab=files");
            Assert(files.FileId == 355 && files.BrowserPage().EndsWith("file_id=355&nmm=1"), "file ID or browser manager link");
            var signed = NexusLink.Parse("nxm://deeprockgalacticroguecore/mods/149/files/355?key=test-key&expires=1&user_id=7");
            Assert(page.PublicPage() == "https://www.nexusmods.com/deeprockgalacticroguecore/mods/149", "export mod page for an entry without a file ID");
            Assert(signed.PublicPage() == "https://www.nexusmods.com/deeprockgalacticroguecore/mods/149?tab=files&file_id=355",
                "export the versioned file page without download authorization");
            Reject<FormatException>(() => NexusLink.Parse("https://www.nexusmods.com/deeprockgalactic/mods/149"));
            Reject<FormatException>(() => NexusLink.Parse("https://nexusmods.com.invalid/deeprockgalacticroguecore/mods/149"));
            Reject<FormatException>(() => NexusLink.Parse("https://www.nexusmods.com/deeprockgalacticroguecore/mods/149?file_id=355&file_id=356"));
            Reject<FormatException>(() => NexusLink.Parse("nxm://deeprockgalacticroguecore/mods/149/files/355?key=missing-expiry"));
            Reject<InvalidOperationException>(() => NexusLink.Parse("nxm://deeprockgalacticroguecore/mods/149/files/355?key=expired&expires=1").AuthorizationQuery());
        }

        private static int ProbeNexus()
        {
            try
            {
                using (var nexus = new NexusClient())
                    nexus.VerifyKeyAsync("invalid-transport-probe", NoCancel).GetAwaiter().GetResult();
                Console.Error.WriteLine("Unexpected success for an intentionally invalid probe key.");
                return 1;
            }
            catch (NexusException error)
            {
                Console.WriteLine("Nexus API responded HTTP " + (int)error.Status + " using TLS mode " + ServicePointManager.SecurityProtocol + ".");
                return error.Status == HttpStatusCode.Unauthorized ? 0 : 1;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }

        private static void TestBrowserInbox(AppStorage storage)
        {
            var first = new BrowserInbox(storage.Root);
            var second = new BrowserInbox(storage.Root);
            const string link = "nxm://deeprockgalacticroguecore/mods/3/files/355?key=authorized";
            using (var instance = first.TryClaimInstance())
            {
                Assert(instance != null, "main instance lock");
                Assert(second.TryClaimInstance() == null, "secondary launch uses browser inbox");
                second.Submit(link);
                string item = Directory.GetFiles(Path.Combine(storage.Root, "browser-inbox"), "*.request").Single();
                Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(item)).Contains(link), "browser authorization encrypted in transit");
                Assert(first.TakePending().Single() == link && !File.Exists(item), "receive and consume callback once");
                second.Submit("");
                Assert(first.TakePending().Single() == "", "ordinary relaunch activates existing window");
            }
        }

        private static void TestSteamLibrary(string root)
        {
            string steam = Path.Combine(root, "Steam");
            string apps = Path.Combine(steam, "steamapps");
            Directory.CreateDirectory(apps);
            string library = Path.Combine(root, "Extra Steam Library");
            const string folder = "Deep Rock Galactic RogueCore";
            string game = FakeGame(Path.Combine(library, "steamapps", "common"), folder);
            File.WriteAllText(Path.Combine(apps, "libraryfolders.vdf"), "\"libraryfolders\" { \"0\" { \"path\" \"" +
                library.Replace("\\", "\\\\") + "\" } }");
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_2605790.acf"), "\"AppState\" { \"installdir\" \"" + folder + "\" }");
            Assert(GameLocation.DetectSteam(steam) == game, "find Rogue Core in a secondary Steam library");
        }

        private static async Task TestNexusAsync(string root, byte[] zip)
        {
            const string fakeKey = "dummy-key-for-test";
            long exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
            var requests = new List<string>();
            using (var nexus = new NexusClient(new FakeHttp(request =>
            {
                Assert(request.RequestUri.Host == "api.nexusmods.com", "only Nexus API receives key");
                Assert(request.Headers.GetValues("apikey").Single() == fakeKey, "API key routing");
                string path = request.RequestUri.PathAndQuery;
                requests.Add(path);
                if (path.EndsWith("/users/validate.json")) return Json("{\"user_id\":7,\"name\":\"test-account\",\"is_premium\":true}");
                if (path.EndsWith("/mods/149.json")) return Json("{\"name\":\"Test archive\"}");
                const string file = "{\"file_id\":355,\"name\":\"Test file\",\"file_name\":\"test.zip\",\"version\":\"1.2\",\"category_name\":\"MAIN\",\"is_primary\":true}";
                if (path.EndsWith("/mods/149/files.json")) return Json("{\"files\":[" + file + "]}");
                if (path.EndsWith("/mods/149/files/355.json")) return Json(file);
                if (path.Contains("/mods/149/files/355/download_link.json"))
                    return Json("[{\"URI\":\"https://files.nexus-cdn.com/test/mod.zip\"}]");
                throw new Exception("Unexpected API request.");
            }), new FakeHttp(request =>
            {
                Assert(request.RequestUri.Host == "files.nexus-cdn.com" && !request.Headers.Contains("apikey"), "credentials do not reach CDN");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            })))
            {
                Assert((await nexus.VerifyKeyAsync(fakeKey, NoCancel)).is_premium, "account metadata");
                Assert((await nexus.GetModAsync(149, fakeKey, NoCancel)).name == "Test archive", "mod name");
                Assert((await nexus.GetFilesAsync(149, fakeKey, NoCancel)).files.Single().file_id == 355, "file list");
                var selected = new NexusLink(149, 355);
                Assert((await nexus.GetFileAsync(selected, fakeKey, NoCancel)).Version == "1.2", "file version");
                string filePath = Path.Combine(root, "download.zip");
                await nexus.DownloadAsync(selected, fakeKey, filePath, null, NoCancel);
                Assert(requests.Last().EndsWith("download_link.json"), "Premium uses ordinary mod link");
                var signed = NexusLink.Parse("nxm://deeprockgalacticroguecore/mods/149/files/355?key=a%2Bb&expires=" + exp + "&user_id=7");
                Assert(signed.IsAuthorized && signed.UserId == 7, "browser permission callback");
                await nexus.DownloadAsync(signed, fakeKey, filePath, null, NoCancel);
                Assert(requests.Last().EndsWith("?key=a%2Bb&expires=" + exp), "NXM authorization passed without corrupting key");
                Assert(File.ReadAllBytes(filePath).SequenceEqual(zip), "downloaded bytes");
            }
        }

        private static void TestInstall(string root, AppStorage storage)
        {
            string game = FakeGame(root, "game");
            var state = storage.LoadState();
            state.GameRoot = game;
            storage.SaveState(state);
            Profile profile = state.Current();
            string vanilla = Path.Combine(game, GameLocation.Paks, "RogueCore-Windows.pak");
            var importer = new PackageImporter(storage);
            ModEntry entry = importer.Import(Path.Combine(root, "download.zip"),
                new ModEntry { Name = "Mod Hub", FileName = "mod.zip", Version = "1.0", ModId = 3, FileId = 355 }, NoCancel);
            profile.Mods.Add(entry);
            string destination = Path.Combine(game, GameLocation.Paks, "ModHub_P.pak");
            string other = Path.Combine(game, GameLocation.Paks, "ManualMod_P.pak");
            File.WriteAllText(other, "outside manager");
            var deployer = new GameDeployer(storage);
            Assert(deployer.Apply(game, profile, NoCancel) == 1 && File.ReadAllText(destination) == "sample pak", "valid downloaded package deployed");
            Assert(deployer.Apply(game, profile, NoCancel) == 1, "reapply with identical contents");
            entry.Enabled = false;
            Assert(deployer.Apply(game, profile, NoCancel) == 0 && !File.Exists(destination), "disable without deleting cache");
            File.WriteAllText(destination, "outside manager");
            entry.Enabled = true;
            Reject<IOException>(() => deployer.Apply(game, profile, NoCancel));
            Assert(File.ReadAllText(destination) == "outside manager", "preserve external mod at conflicting path");
            File.Delete(destination);
            File.WriteAllText(destination, "sample pak");
            deployer.Apply(game, profile, NoCancel);
            entry.Enabled = false;
            deployer.Apply(game, profile, NoCancel);
            Assert(File.ReadAllText(destination) == "sample pak", "reuse a matching manually installed file without managing its removal");
            File.Delete(destination);
            entry.Enabled = true;
            deployer.Apply(game, profile, NoCancel);
            byte[] replacement = MakeArchive(new Dictionary<string, string> { { "ModHub_P.pak", "replacement" } });
            string upgraded = Path.Combine(root, "new.zip");
            File.WriteAllBytes(upgraded, replacement);
            ModEntry updated = importer.Import(upgraded,
                new ModEntry { Name = "Mod Hub", FileName = "new.zip", Version = "2.0", ModId = 3, FileId = 356 }, NoCancel);
            profile.Mods[0] = updated;
            deployer.Apply(game, profile, NoCancel);
            Assert(File.ReadAllText(destination) == "replacement", "replace managed version");
            File.WriteAllText(destination, "third party change");
            updated.Enabled = false;
            Reject<IOException>(() => deployer.Apply(game, profile, NoCancel));
            Assert(File.ReadAllText(destination) == "third party change", "preserve modified managed file");
            Assert(File.ReadAllText(vanilla) == "vanilla" && File.ReadAllText(other) == "outside manager", "vanilla and manual mods preserved");
            updated.Enabled = true;
            storage.SaveState(state);
        }

        private static void TestRejectedArchive(string root, AppStorage storage)
        {
            string unsafeZip = Path.Combine(root, "unsafe.zip");
            File.WriteAllBytes(unsafeZip, MakeArchive(new Dictionary<string, string> { { "../escape.pak", "garbage" } }));
            Reject<InvalidDataException>(() => new PackageImporter(storage).Import(unsafeZip,
                new ModEntry { Name = "Unsafe", FileName = "unsafe.zip" }, NoCancel));
            Reject<InvalidDataException>(() => SafePath.Relative("C:/bad.pak"));
            Reject<InvalidDataException>(() => SafePath.Relative("foo.pak:stream"));
            Reject<InvalidDataException>(() => SafePath.Relative("Foo/CON.txt"));
        }

        private static void TestScriptsAndIoStore(string root, AppStorage storage)
        {
            string game = FakeGame(root, "script-game");
            string fixture = Path.Combine(root, "mixed.zip");
            File.WriteAllBytes(fixture, MakeArchive(new Dictionary<string, string>
            {
                { "Assets/Mixed_P.pak", "pak" }, { "Assets/Mixed_P.utoc", "toc" }, { "Assets/Mixed_P.ucas", "cas" },
                { "ue4ss/Mods/Sample/Scripts/main.lua", "-- placeholder" }, { "ue4ss/Mods/Sample/settings.ini", "enabled=1" }
            }));
            var entry = new PackageImporter(storage).Import(fixture, new ModEntry { Name = "Mixed", FileName = "mixed.zip" }, NoCancel);
            var profile = new Profile { Mods = new List<ModEntry> { entry } };
            var deployer = new GameDeployer(storage);
            Reject<InvalidOperationException>(() => deployer.Apply(game, profile, NoCancel));
            Assert(!File.Exists(Path.Combine(game, GameLocation.Paks, "Mixed_P.pak")), "missing loader leaves game untouched");
            string ue4ss = Path.Combine(game, GameLocation.Win64, "ue4ss");
            Directory.CreateDirectory(ue4ss);
            File.WriteAllText(Path.Combine(ue4ss, "UE4SS.dll"), "fake loader");
            Assert(deployer.Apply(game, profile, NoCancel) == 6, "script, marker and IoStore files deployed");
            Assert(File.Exists(Path.Combine(ue4ss, "Mods", "Sample", "enabled.txt")), "UE4SS activation marker");
            Assert(File.ReadAllText(Path.Combine(game, GameLocation.Paks, "Mixed_P.ucas")) == "cas", "companion container");
            Assert(deployer.Apply(game, new Profile(), NoCancel) == 0, "switch to empty profile");
            Assert(File.Exists(Path.Combine(ue4ss, "UE4SS.dll")), "pre-existing loader preserved");
        }

        private static void TestMintcatScripts(string root, string releaseArchive)
        {
            var storage = new AppStorage(Path.Combine(root, "mintcat-appdata"));
            var runtime = new ScriptRuntime(storage);
            var deployer = new GameDeployer(storage);
            string fixture = Path.Combine(root, "mintcat-script.zip");
            File.WriteAllBytes(fixture, MakeArchive(new Dictionary<string, string>
            {
                { "RemoveClassLock/js/main.js", "import './helper.js';" },
                { "RemoveClassLock/js/helper.js", "print('test');" }
            }));
            var entry = new PackageImporter(storage).Import(fixture,
                new ModEntry { Name = "Mintcat script", FileName = "mintcat-script.zip", ModId = 82 }, NoCancel);
            var profile = new Profile { Mods = new List<ModEntry> { entry } };
            string main = Path.Combine(GameLocation.ScriptDirectory, "RemoveClassLock", "js", "main.js");

            string existingGame = FakeGame(root, "mintcat-existing");
            foreach (string target in new[] { ScriptRuntime.Proxy, ScriptRuntime.Core, ScriptRuntime.JavaScript })
            {
                string path = SafePath.In(existingGame, target);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "external runtime");
            }
            Assert(deployer.Apply(existingGame, profile, NoCancel) == 3, "reuses an existing UE4SS Lite JavaScript installation");
            Assert(File.Exists(SafePath.In(existingGame, main)) && !File.Exists(runtime.ArchivePath), "deploys the script without fetching a second runtime");
            deployer.Apply(existingGame, new Profile(), NoCancel);
            Assert(!File.Exists(SafePath.In(existingGame, main)) &&
                File.ReadAllText(SafePath.In(existingGame, ScriptRuntime.Proxy)) == "external runtime",
                "removes the JavaScript entry and preserves an external runtime");

            string classicGame = FakeGame(root, "mintcat-classic");
            File.WriteAllText(Path.Combine(classicGame, GameLocation.Win64, "UE4SS.dll"), "external classic loader");
            Reject<InvalidOperationException>(() => deployer.Apply(classicGame, profile, NoCancel));
            Assert(!File.Exists(runtime.ArchivePath), "standard UE4SS is not replaced automatically for JavaScript");

            if (!File.Exists(releaseArchive)) return;
            SeedRuntimeCache(storage, releaseArchive);
            string fresh = FakeGame(root, "mintcat-fresh");
            int fetches = 0;
            Assert(deployer.Apply(fresh, profile, NoCancel, () => fetches++) == 8, "deploys script and verified Mintcat runtime together");
            Assert(fetches == 0, "uses the verified runtime archive without a network request");
            foreach (string target in new[] { ScriptRuntime.Proxy, ScriptRuntime.Core, ScriptRuntime.JavaScript, ScriptRuntime.Framework, main })
                Assert(File.Exists(SafePath.In(fresh, target)), "required JavaScript runtime or script file: " + target);
            string settings = SafePath.In(fresh, ScriptRuntime.Settings);
            Assert(File.ReadAllText(settings).Contains("HookProcessLocalScriptFunction = 1"), "writes the Rogue Core hook settings");
            File.AppendAllText(settings, "\r\n; user setting\r\n");
            Assert(deployer.Apply(fresh, profile, NoCancel, () => fetches++) == 7, "retains modified settings and uses the cached runtime");
            Assert(fetches == 0, "reapplying does not fetch the runtime again");
            Assert(deployer.Apply(fresh, new Profile(), NoCancel) == 0 &&
                !File.Exists(SafePath.In(fresh, ScriptRuntime.Proxy)) && !File.Exists(SafePath.In(fresh, main)) &&
                File.ReadAllText(settings).Contains("; user setting"), "removes the managed runtime and preserves edited settings");

            string conflict = FakeGame(root, "mintcat-conflict");
            string proxy = SafePath.In(conflict, ScriptRuntime.Proxy);
            File.WriteAllText(proxy, "unrelated proxy");
            Reject<IOException>(() => deployer.Apply(conflict, profile, NoCancel));
            Assert(File.ReadAllText(proxy) == "unrelated proxy" && !File.Exists(SafePath.In(conflict, ScriptRuntime.Core)) &&
                !File.Exists(SafePath.In(conflict, main)), "runtime conflict leaves the game untouched");
            Console.WriteLine("PASS Mintcat " + ScriptRuntime.Version + " JavaScript runtime deployment");
        }

        private static void SeedRuntimeCache(AppStorage storage, string releaseArchive)
        {
            string path = new ScriptRuntime(storage).ArchivePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.Copy(releaseArchive, path);
        }

        private static void TestRecovery(string root, AppStorage storage)
        {
            string game = FakeGame(root, "crash-game");
            string save = storage.GameStateDirectory(game);
            string prior = Path.Combine(save, "installed.json");
            string pending = Path.Combine(save, "pending.json");
            string targetRel = Path.Combine(GameLocation.Paks, "Swap_P.pak");
            string target = Path.Combine(game, targetRel);
            string id = Guid.NewGuid().ToString("N");
            string stage = GameDeployer.StageDirectory(game, id);
            Directory.CreateDirectory(stage);
            string old = Path.Combine(stage, "0.old");
            File.WriteAllText(old, "prior");
            File.WriteAllText(target, "new");
            var file = new DeploymentAction { Target = targetRel, NewHash = AppStorage.HashFile(target), OldHash = AppStorage.HashFile(old) };
            AppStorage.WriteJson(pending, new DeploymentJournal { Id = id, Actions = new List<DeploymentAction> { file } });
            GameDeployer.Recover(game, prior, pending);
            Assert(File.ReadAllText(target) == "prior" && !File.Exists(pending) && !Directory.Exists(stage), "restore interrupted replacement");

            Directory.CreateDirectory(stage);
            File.WriteAllText(old, "prior");
            File.WriteAllText(target, "committed");
            AppStorage.WriteJson(prior, new InstalledManifest { TransactionId = id });
            AppStorage.WriteJson(pending, new DeploymentJournal { Id = id, Actions = new List<DeploymentAction> { file } });
            GameDeployer.Recover(game, prior, pending);
            Assert(File.ReadAllText(target) == "committed" && !File.Exists(pending) && !Directory.Exists(stage), "finish committed transaction cleanup");
        }

        private static void TestStorage(AppStorage storage)
        {
            AppState state = storage.LoadState();
            Assert(state.Current().Mods.Count == 1 && state.Current().Mods[0].Version == "2.0", "persist mod and version in profile");
            state.Profiles.Add(new Profile { Name = "空配置" });
            storage.SaveState(state);
            Assert(storage.LoadState().Profiles.Count == 2, "persist multiple profiles");
            const string fake = "private-test-token";
            storage.SaveKey(fake);
            Assert(storage.LoadKey() == fake, "DPAPI key round-trip");
            Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(storage.Root, "nexus-key.dat"))).Contains(fake), "key not stored in plaintext");
            storage.SaveKey(null);
        }

        private static void TestNativeArchive(string root, AppStorage storage)
        {
            string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            if (!File.Exists(tar)) return;
            string fixture = Path.Combine(root, "sample.7z");
            File.WriteAllText(Path.Combine(root, "Native_P.pak"), "archive content");
            using (var tool = Process.Start(new ProcessStartInfo(tar, "-caf \"" + fixture + "\" Native_P.pak")
                { UseShellExecute = false, WorkingDirectory = root, CreateNoWindow = true }))
            {
                tool.WaitForExit();
                Assert(tool.ExitCode == 0, "Windows tar creates 7z fixture");
            }
            var result = new PackageImporter(storage).Import(fixture,
                new ModEntry { Name = "Archive 7z", FileName = "sample.7z" }, NoCancel);
            Assert(result.Files.Single().Target == "Native_P.pak", "7z archive extraction");
        }

        private static void CaptureUi(string buildDirectory, AppStorage storage)
        {
            using (var form = new MainWindow(storage))
            {
                Capture(form, Path.Combine(buildDirectory, "main.png"));
                using (var api = new NexusClient())
                using (var settings = new SettingsDialog(storage, storage.LoadState(), api, null, null))
                    Capture(settings, Path.Combine(buildDirectory, "settings.png"));
            }
        }

        private static void TestUiDownload(string root, byte[] zip, bool premium, bool conflict = false, string runtimeArchive = null)
        {
            string mode = runtimeArchive != null ? "standard with JavaScript runtime" : conflict ? "conflict" : premium ? "premium" : "standard";
            Console.WriteLine("Checking UI connection mode " + mode);
            string game = FakeGame(root, "ui-game-" + mode);
            var storage = new AppStorage(Path.Combine(root, "ui-data-" + mode));
            string mainPayload = runtimeArchive != null ? "ScriptMod/js/main.js" : "Grappling_P.pak";
            if (runtimeArchive != null) SeedRuntimeCache(storage, runtimeArchive);
            var state = new AppState { GameRoot = game };
            string paks = Path.Combine(game, GameLocation.Paks);
            if (conflict) File.WriteAllText(Path.Combine(paks, "Grappling_P.pak"), "existing manual edit");
            if (premium && !conflict)
            {
                string cached = Path.Combine(root, "cached-hub.zip");
                File.WriteAllBytes(cached, zip);
                state.Current().Mods.Add(new PackageImporter(storage).Import(cached, new ModEntry
                {
                    Name = "Mod Hub", ModId = 3, FileId = 355, FileName = "cached-hub.zip", Enabled = false, Version = "1"
                }, NoCancel));
            }
            storage.SaveState(state);
            storage.SaveKey("ui-test-account");
            var browserPages = new List<string>();
            var downloadRequests = new List<string>();
            long[] expectedFiles = premium && !conflict ? new long[] { 603, 501 } : new long[] { 355, 603, 501 };
            var files = new Dictionary<long, NexusFile>
            {
                { 355, FixtureFile(355, "Mod Hub") }, { 603, FixtureFile(603, "RCMM") }, { 501, FixtureFile(501, "Grappling") }
            };
            var apiHandler = new FakeHttp(req =>
            {
                string path = req.RequestUri.PathAndQuery;
                if (path.EndsWith("/users/validate.json"))
                    return Json("{\"user_id\":7,\"name\":\"test-account\",\"is_premium\":" + (premium ? "true" : "false") + "}");
                if (path.EndsWith("/games/deeprockgalacticroguecore.json")) return Json("{\"id\":91}");
                if (path == "/v2/graphql")
                {
                    var body = AppStorage.Json().Deserialize<Dictionary<string, object>>(req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    string mod = (string)((Dictionary<string, object>)body["variables"])["modId"];
                    LegacyRequirement[] required = mod == "149" ? new[] { FixtureRequirement(6, "RCMM"), FixtureRequirement(96, "Optional Addon", "Not a requirement") } :
                        mod == "6" ? new[] { FixtureRequirement(3, "Mod Hub") } : new[] { FixtureRequirement(149, "Grappling") };
                    return Json(AppStorage.Json().Serialize(new { data = new { mod = new { modRequirements = new
                    {
                        nexusRequirements = new { nodes = required, totalCount = required.Length }, dlcRequirements = new object[0]
                    } } } }));
                }
                if (path.StartsWith("/v3/mod-file-versions/dependencies/", StringComparison.Ordinal))
                {
                    string body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var candidates = body.Contains(FixtureUid(501)) ? new[]
                    {
                        FixtureCandidate(501, 6, 603, 200), FixtureCandidate(501, 16, 610, 100)
                    } : new NexusDependencyCandidate[0];
                    return Json(AppStorage.Json().Serialize(new { data = new { candidates = candidates }, meta = new { total_count = candidates.Length } }));
                }
                foreach (var match in new[] { new { Mod = 149, File = 501L }, new { Mod = 6, File = 603L }, new { Mod = 3, File = 355L } })
                {
                    string modPath = "/mods/" + match.Mod;
                    NexusFile file = files[match.File];
                    if (path.EndsWith(modPath + ".json")) return Json(AppStorage.Json().Serialize(new { name = file.name }));
                    if (path.EndsWith(modPath + "/files.json")) return Json(AppStorage.Json().Serialize(new { files = new[] { file } }));
                    if (path.EndsWith(modPath + "/files/" + match.File + ".json")) return Json(AppStorage.Json().Serialize(file));
                    if (path.Contains(modPath + "/files/" + match.File + "/download_link.json"))
                    {
                        downloadRequests.Add(path);
                        return Json("[{\"URI\":\"https://files.nexus-cdn.com/test/" + match.File + ".zip\"}]");
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            var cdnHandler = new FakeHttp(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(req.RequestUri.AbsolutePath.EndsWith("/355.zip") ? zip : MakeArchive(new Dictionary<string, string>
                {
                    { req.RequestUri.AbsolutePath.EndsWith("/603.zip") ? "RCMM_P.pak" : mainPayload, "sample prerequisite or main" }
                }))
            });
            using (var form = new MainWindow(storage, new NexusClient(apiHandler, cdnHandler), () => true,
                url => browserPages.Add(url)))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                Exception failure = null;
                form.Shown += async (sender, e) =>
                {
                    var input = (TextBox)form.Controls.Find("NexusAddress", true).Single();
                    var install = (Button)form.Controls.Find("InstallNexusMod", true).Single();
                    var status = (Label)form.Controls.Find("Status", true).Single();
                    try
                    {
                        input.Text = "https://www.nexusmods.com/deeprockgalacticroguecore/mods/149";
                        install.PerformClick();
                        if (!premium)
                        {
                            for (int i = 0; i < expectedFiles.Length; i++)
                            {
                                long file = expectedFiles[i];
                                int mod = file == 355 ? 3 : file == 603 ? 6 : 149;
                                await UntilAsync(() => browserPages.Count > i && status.Text.StartsWith("请在 Nexus 网页", StringComparison.Ordinal), "browser authorization launched");
                                Assert(browserPages[i].EndsWith("file_id=" + file + "&nmm=1"), "correct prerequisite authorization order");
                                Assert(downloadRequests.Count == i, "each file waits for its own authorization");
                                Assert(Directory.GetFiles(paks).Length == 1, "no deployment before all files are authorized");
                                long exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
                                form.ReceiveBrowserLink("nxm://deeprockgalacticroguecore/mods/" + mod + "/files/" + file + "?key=mock&expires=" + exp + "&user_id=7");
                            }
                        }
                        if (conflict)
                        {
                            await UntilAsync(() => install.Enabled && status.Text.StartsWith("安装位置已有", StringComparison.Ordinal), "report install conflict");
                            Assert(storage.LoadState().Current().Mods.Count == 0 && !File.Exists(Path.Combine(paks, "RCMM_P.pak")), "failed batch leaves profile and game untouched");
                            Assert(File.ReadAllText(Path.Combine(paks, "Grappling_P.pak")) == "existing manual edit", "existing conflicting file preserved");
                            return;
                        }
                        await UntilAsync(() => install.Enabled && status.Text.StartsWith("已安装", StringComparison.Ordinal), "paste-to-deployment workflow");
                        string mainTarget = runtimeArchive != null
                            ? SafePath.In(game, Path.Combine(GameLocation.ScriptDirectory, mainPayload))
                            : Path.Combine(paks, mainPayload);
                        Assert(File.ReadAllText(Path.Combine(paks, "ModHub_P.pak")) == "sample pak" && File.Exists(Path.Combine(paks, "RCMM_P.pak")) &&
                            File.Exists(mainTarget), "UI deployed complete dependency chain");
                        if (runtimeArchive != null)
                            Assert(File.Exists(SafePath.In(game, ScriptRuntime.Proxy)) &&
                                File.Exists(SafePath.In(game, ScriptRuntime.JavaScript)), "NXM workflow deploys the underlying runtime");
                        Assert(storage.LoadState().Current().Mods.Where(m => m.Enabled).Select(m => m.FileId).SequenceEqual(new long[] { 355, 603, 501 }),
                            "UI persisted compatible versions once and enabled cached dependencies");
                        Assert(downloadRequests.Count == expectedFiles.Length, "shared and optional prerequisites avoid extra downloads");
                        Assert(premium ? browserPages.Count == 0 && downloadRequests.All(r => r.EndsWith("download_link.json")) :
                            downloadRequests.All(r => r.Contains("?key=mock&expires=")), "UI uses correct authorization mode");
                    }
                    catch (Exception error)
                    {
                        failure = new InvalidOperationException(mode + " workflow status: " + status.Text + ", install enabled: " + install.Enabled, error);
                    }
                    finally { Application.ExitThread(); }
                };
                Application.Run(form);
                if (failure != null) throw failure;
            }
        }

        private static NexusFile FixtureFile(long id, string name)
        {
            return new NexusFile { file_id = id, name = name, file_name = name + ".zip", version = "1", is_primary = true, category_name = "MAIN" };
        }

        private static LegacyRequirement FixtureRequirement(long mod, string name, string notes = null)
        {
            return new LegacyRequirement { modId = mod.ToString(), gameId = "91", modName = name, notes = notes };
        }

        private static NexusDependencyCandidate FixtureCandidate(long source, long mod, long file, int position)
        {
            return new NexusDependencyCandidate { source_version_id = FixtureUid(source), definition_id = "choice",
                mod_id = FixtureUid(mod), mod_file_id = "update-group-" + mod, version_id = FixtureUid(file),
                position = position.ToString(), category = "main", mod_status = "published" };
        }

        private static string FixtureUid(long id) { return ((91L << 32) | id).ToString(); }

        private static async Task UntilAsync(Func<bool> predicate, string operation)
        {
            var timer = Stopwatch.StartNew();
            while (!predicate())
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(operation);
                await Task.Delay(25);
            }
        }

        private static void Capture(Form form, string file)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-3000, -3000);
            form.Show();
            Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(file, ImageFormat.Png);
            }
            form.Hide();
        }

        private static string FakeGame(string root, string name)
        {
            string game = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(game, GameLocation.Paks));
            Directory.CreateDirectory(Path.Combine(game, GameLocation.Win64));
            File.WriteAllText(Path.Combine(game, GameLocation.Win64, GameLocation.ShippingExe), "test signature");
            File.WriteAllText(Path.Combine(game, GameLocation.Paks, "RogueCore-Windows.pak"), "vanilla");
            return game;
        }

        private static byte[] MakeArchive(IDictionary<string, string> entries)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                    foreach (var pair in entries)
                        using (var writer = new StreamWriter(zip.CreateEntry(pair.Key).Open(), new UTF8Encoding(false)))
                            writer.Write(pair.Value);
                return memory.ToArray();
            }
        }

        private static HttpResponseMessage Json(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private static void Assert(bool condition, string test)
        {
            if (!condition) throw new Exception("Failed: " + test);
        }

        private static void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }

        private sealed class FakeHttp : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
            internal FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(respond(request));
            }
        }
    }
}
