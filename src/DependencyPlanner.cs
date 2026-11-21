using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RogueCoreNexus
{
    internal sealed class InstallFile
    {
        internal NexusLink Link;
        internal NexusFile File;
        internal string Name;
        internal ModEntry Entry;
    }

    internal sealed class NexusInstallPlan
    {
        internal Profile Profile;
        internal InstallFile Main;
        internal ModEntry Replacement;
        internal readonly List<InstallFile> Files = new List<InstallFile>();
        internal readonly List<string> External = new List<string>();
        internal readonly List<string> NewPackages = new List<string>();
        internal InstallFile Waiting;
    }

    internal sealed class DependencyPlanner
    {
        private readonly AppStorage storage;
        private readonly NexusClient nexus;
        private readonly Profile profile;
        private readonly string apiKey;
        private readonly NexusInstallPlan plan;
        private readonly List<InstallFile> resolved = new List<InstallFile>();
        private readonly Dictionary<string, long> chosenVersions = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<long, List<NexusFile>> fileLists = new Dictionary<long, List<NexusFile>>();

        internal DependencyPlanner(AppStorage storage, NexusClient nexus, Profile profile, string apiKey)
        {
            this.storage = storage;
            this.nexus = nexus;
            this.profile = profile;
            this.apiKey = apiKey;
            plan = new NexusInstallPlan { Profile = profile };
        }

        internal async Task<NexusInstallPlan> BuildAsync(NexusLink link, NexusFile file, string name, ModEntry replaced, CancellationToken token)
        {
            plan.Replacement = replaced;
            plan.Main = new InstallFile
            {
                Link = new NexusLink(link.ModId, link.FileId), File = file, Name = name,
                Entry = CachedEntry(link.ModId, link.FileId)
            };
            await VisitAsync(plan.Main, token).ConfigureAwait(false);
            return plan;
        }

        private async Task VisitAsync(InstallFile current, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Register before descending, so shared prerequisites and cycles both reuse one file.
            resolved.Add(current);
            RequirementSet requirements = await nexus.GetRequirementsAsync(current.Link, apiKey, token).ConfigureAwait(false);
            foreach (string external in requirements.External)
                if (!plan.External.Contains(external)) plan.External.Add(external);

            foreach (List<RequiredFile> group in requirements.Groups)
            {
                var alternatives = group.Where(CompatibleWithPlan).ToList();
                var ready = alternatives.Select(option => new { Option = option, File = resolved.FirstOrDefault(f =>
                    Matches(option, f.Link.ModId, f.Link.FileId)) }).FirstOrDefault(p => p.File != null);
                if (ready != null)
                {
                    Pin(ready.Option, ready.File.Link.FileId);
                    continue;
                }

                InstallFile next = await PickAsync(alternatives, current.Name, token).ConfigureAwait(false);
                await VisitAsync(next, token).ConfigureAwait(false);
            }
            plan.Files.Add(current);
        }

        private async Task<InstallFile> PickAsync(List<RequiredFile> alternatives, string requiredBy, CancellationToken token)
        {
            foreach (RequiredFile option in alternatives)
            {
                ModEntry cached = CachedEntry(option.ModId, option.FileId);
                if (cached == null || cached == plan.Replacement) continue;
                Pin(option, cached.FileId);
                return new InstallFile
                {
                    Link = new NexusLink(cached.ModId, cached.FileId), Name = cached.Name, Entry = cached,
                    File = new NexusFile { file_id = cached.FileId, name = cached.Name, file_name = cached.FileName, version = cached.Version }
                };
            }

            foreach (RequiredFile option in alternatives)
            {
                NexusFile file;
                try
                {
                    if (option.FileId == 0)
                    {
                        List<NexusFile> files;
                        if (!fileLists.TryGetValue(option.ModId, out files))
                        {
                            var response = await nexus.GetFilesAsync(option.ModId, apiKey, token).ConfigureAwait(false);
                            files = OrderedFiles(response.files);
                            fileLists.Add(option.ModId, files);
                        }
                        file = files.FirstOrDefault(f => PackageImporter.CanImport(f.file_name));
                    }
                    else
                    {
                        file = await nexus.GetFileAsync(new NexusLink(option.ModId, option.FileId), apiKey, token).ConfigureAwait(false);
                        file.file_id = option.FileId;
                    }
                }
                catch (NexusException error)
                {
                    if (error.Status != System.Net.HttpStatusCode.NotFound) throw;
                    continue;
                }
                if (file == null || !PackageImporter.CanImport(file.file_name)) continue;
                var link = new NexusLink(option.ModId, file.file_id);
                string name = option.Name;
                if (String.IsNullOrWhiteSpace(name))
                    name = (await nexus.GetModAsync(option.ModId, apiKey, token).ConfigureAwait(false)).name;
                Pin(option, link.FileId);
                return new InstallFile { Link = link, File = file, Name = name ?? ("Nexus #" + option.ModId) };
            }

            string nameList = String.Join(" / ", alternatives.Select(x => x.Name ?? "Nexus #" + x.ModId).Distinct().Take(4));
            throw new IOException(alternatives.Count == 0
                ? requiredBy + " 的前置需要互相冲突的文件版本。请查看 Nexus 文件的前置说明。"
                : requiredBy + " 缺少可安装的兼容前置。" + Environment.NewLine + nameList);
        }

        private bool CompatibleWithPlan(RequiredFile option)
        {
            long selected;
            return option.Group == null || !chosenVersions.TryGetValue(option.Group, out selected) || selected == option.FileId;
        }

        private void Pin(RequiredFile option, long selectedFile)
        {
            if (!String.IsNullOrWhiteSpace(option.Group)) chosenVersions[option.Group] = selectedFile;
        }

        private ModEntry CachedEntry(long mod, long file)
        {
            foreach (ModEntry entry in profile.Mods.Where(m => m.ModId == mod && m.FileId > 0 &&
                (file == 0 || m.FileId == file)).OrderByDescending(m => m.Enabled))
            {
                if (entry.Files == null || entry.Files.Count == 0 || String.IsNullOrWhiteSpace(entry.PackageId)) continue;
                string package = SafePath.In(storage.Packages, SafePath.Id(entry.PackageId));
                if (entry.Files.All(part => File.Exists(SafePath.In(package, part.CachePath)))) return entry;
            }
            return null;
        }

        private static bool Matches(RequiredFile option, long mod, long file)
        {
            return mod == option.ModId && (option.FileId == 0 || file == option.FileId);
        }

        internal static List<NexusFile> OrderedFiles(IEnumerable<NexusFile> files)
        {
            return (files ?? Enumerable.Empty<NexusFile>()).Where(f => f != null && f.IsAvailable)
                .OrderByDescending(f => f.is_primary)
                .ThenByDescending(f => String.Equals(f.category_name, "MAIN", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => f.uploaded_timestamp).ToList();
        }
    }
}
