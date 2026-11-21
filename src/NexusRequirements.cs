using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RogueCoreNexus
{
    internal sealed class RequiredFile
    {
        internal long ModId;
        internal long FileId;
        internal string Group;
        internal string Name;
        internal string Notes;
    }

    internal sealed class RequirementSet
    {
        internal readonly List<List<RequiredFile>> Groups = new List<List<RequiredFile>>();
        internal readonly List<string> External = new List<string>();
    }

    internal sealed partial class NexusClient
    {
        private const string LegacyRequirementsQuery = @"query RogueCoreRequirements($modId: ID!, $gameId: ID!, $offset: Int!) {
            mod(modId: $modId, gameId: $gameId) {
                modRequirements(skipDisabledRequirements: true) {
                    nexusRequirements(count: 10, offset: $offset) {
                        totalCount
                        nodes { modId gameId modName notes url externalRequirement }
                    }
                    dlcRequirements { gameExpansion { name } notes }
                }
            }
        }";
        private static readonly Regex OptionalRequirement = new Regex(
            @"^\s*(?:optional\b|not (?:a (?:hard )?requirement|(?:strictly )?required)\b|recommended only\b|可选|非必需|选装)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private long gameId;

        internal async Task<RequirementSet> GetRequirementsAsync(NexusLink source, string key, CancellationToken token)
        {
            long game = gameId;
            if (game == 0)
            {
                var info = await GetAsync<NexusGameInfo>("games/" + NexusLink.Domain + ".json", key, token).ConfigureAwait(false);
                if (info.id <= 0 || info.id > UInt32.MaxValue) throw new IOException("Nexus 返回了无效的游戏编号。");
                game = gameId = info.id;
            }
            var result = new RequirementSet();
            await ReadFileRequirementsAsync(source.FileId, game, result, key, token).ConfigureAwait(false);
            await ReadLegacyRequirementsAsync(source.ModId, game, result, key, token).ConfigureAwait(false);
            return result;
        }

        private async Task ReadLegacyRequirementsAsync(long mod, long game, RequirementSet result, string key, CancellationToken token)
        {
            int offset = 0;
            while (true)
            {
                var response = await SendAsync<NexusRequirementsResponse>(HttpMethod.Post, "v2/graphql", new
                {
                    query = LegacyRequirementsQuery,
                    variables = new { modId = IdText(mod), gameId = IdText(game), offset = offset }
                }, key, token).ConfigureAwait(false);
                var requirements = response.data != null && response.data.mod != null ? response.data.mod.modRequirements : null;
                if (requirements == null || requirements.nexusRequirements == null ||
                    requirements.nexusRequirements.nodes == null || (response.errors != null && response.errors.Length > 0))
                    throw new IOException("Nexus 未返回这个 Mod 的页面前置清单。请重试查询。");
                if (offset == 0)
                {
                    foreach (var dlc in requirements.dlcRequirements ?? new LegacyDlcRequirement[0])
                        if (dlc.gameExpansion != null && !OptionalRequirement.IsMatch(dlc.notes ?? ""))
                            result.External.Add("DLC  " + dlc.gameExpansion.name + FormatNotes(dlc.notes));
                }
                foreach (var item in requirements.nexusRequirements.nodes)
                {
                    if (OptionalRequirement.IsMatch(item.notes ?? "")) continue;
                    long requiredMod, requiredGame;
                    if (!item.externalRequirement && TryId(item.gameId, out requiredGame) && requiredGame == game && TryId(item.modId, out requiredMod))
                        result.Groups.Add(new List<RequiredFile> { new RequiredFile { ModId = requiredMod, Name = item.modName, Notes = item.notes } });
                    else result.External.Add((item.modName ?? "站外前置") + FormatNotes(item.notes) +
                        (String.IsNullOrWhiteSpace(item.url) ? "" : "\n" + item.url));
                }
                int received = requirements.nexusRequirements.nodes.Length;
                offset += received;
                if (offset >= requirements.nexusRequirements.totalCount) return;
                if (received == 0) throw new IOException("Nexus 的页面前置清单不完整。请重试查询。");
            }
        }

        private async Task ReadFileRequirementsAsync(long file, long game, RequirementSet result, string key, CancellationToken token)
        {
            if (file <= 0 || file > UInt32.MaxValue) throw new IOException("无法查询这个 Nexus 文件的版本前置。");
            const int pageSize = 200;
            string source = (((ulong)game << 32) | (ulong)file).ToString(CultureInfo.InvariantCulture);
            var candidates = new List<NexusDependencyCandidate>();
            for (int page = 1; ; page++)
            {
                var response = await SendAsync<NexusDependencyResponse>(HttpMethod.Post,
                    "v3/mod-file-versions/dependencies/ranges/materialized/batch",
                    new { version_ids = new[] { source }, page = page, page_size = pageSize }, key, token).ConfigureAwait(false);
                if (response.data == null || response.data.candidates == null || response.meta == null)
                    throw new IOException("Nexus 未返回可识别的文件版本前置清单。");
                candidates.AddRange(response.data.candidates);
                if (candidates.Count >= response.meta.total_count) break;
                if (response.data.candidates.Length == 0) throw new IOException("Nexus 的文件版本前置清单不完整。请重试查询。");
            }
            if (candidates.Any(c => c == null || String.IsNullOrWhiteSpace(c.definition_id) || c.source_version_id != source))
                throw new IOException("Nexus 的文件前置数据不完整。请重试查询。");

            foreach (var definition in candidates.GroupBy(r => r.definition_id))
            {
                var alternatives = new List<RequiredFile>();
                foreach (var row in definition.OrderByDescending(IsActiveCandidate).ThenByDescending(r => Position(r.position)))
                {
                    ulong modUid, fileUid;
                    if (!AvailableCandidate(row) || !TryUid(row.mod_id, game, out modUid) || !TryUid(row.version_id, game, out fileUid)) continue;
                    alternatives.Add(new RequiredFile { ModId = (uint)modUid, FileId = (uint)fileUid, Group = row.mod_file_id });
                }
                if (alternatives.Count == 0)
                    throw new IOException("这个文件的一个必需前置没有可下载的兼容《异动核心》版本。请查看该文件在 Nexus 上的前置说明。");
                result.Groups.Add(alternatives);
            }
        }

        private static bool TryUid(string value, long game, out ulong number)
        {
            return UInt64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
                number >> 32 == (ulong)game && (uint)number != 0;
        }

        private static bool IsActiveCandidate(NexusDependencyCandidate row)
        {
            return row.category == "main" || row.category == "update" || row.category == "optional" || row.category == "miscellaneous";
        }

        private static bool AvailableCandidate(NexusDependencyCandidate row)
        {
            return (row.mod_status == "published" || row.mod_status == "hidden") &&
                (IsActiveCandidate(row) || row.category == "old_version" || row.category == "archived");
        }

        private static decimal Position(string value)
        {
            decimal number;
            return Decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

        private static bool TryId(string value, out long id)
        {
            return Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        private static string IdText(long id) { return id.ToString(CultureInfo.InvariantCulture); }
        private static string FormatNotes(string notes) { return String.IsNullOrWhiteSpace(notes) ? "" : "  " + notes.Trim(); }
    }

    public sealed class NexusGameInfo { public long id { get; set; } }
    public sealed class NexusRequirementsResponse { public LegacyRequirementsData data { get; set; } public object[] errors { get; set; } }
    public sealed class LegacyRequirementsData { public LegacyRequiredMod mod { get; set; } }
    public sealed class LegacyRequiredMod { public LegacyRequirements modRequirements { get; set; } }
    public sealed class LegacyRequirements
    {
        public LegacyRequirementPage nexusRequirements { get; set; }
        public LegacyDlcRequirement[] dlcRequirements { get; set; }
    }
    public sealed class LegacyRequirementPage { public LegacyRequirement[] nodes { get; set; } public int totalCount { get; set; } }
    public sealed class LegacyRequirement
    {
        public string modId { get; set; }
        public string gameId { get; set; }
        public string modName { get; set; }
        public string notes { get; set; }
        public string url { get; set; }
        public bool externalRequirement { get; set; }
    }
    public sealed class LegacyDlcRequirement { public NexusMod gameExpansion { get; set; } public string notes { get; set; } }

    public sealed class NexusDependencyResponse { public NexusDependencyData data { get; set; } public NexusPageMeta meta { get; set; } }
    public sealed class NexusDependencyData { public NexusDependencyCandidate[] candidates { get; set; } }
    public sealed class NexusPageMeta { public int total_count { get; set; } }
    public sealed class NexusDependencyCandidate
    {
        public string source_version_id { get; set; }
        public string definition_id { get; set; }
        public string mod_id { get; set; }
        public string mod_file_id { get; set; }
        public string version_id { get; set; }
        public string position { get; set; }
        public string category { get; set; }
        public string mod_status { get; set; }
    }
}
