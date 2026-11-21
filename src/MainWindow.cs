using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RogueCoreNexus
{
    internal sealed class MainWindow : Form
    {
        private readonly AppStorage storage;
        private readonly AppState state;
        private readonly NexusClient nexus;
        private readonly Func<bool> receiveDownloadLinks;
        private readonly Action<string> openAuthorizationPage;
        private readonly PackageImporter importer;
        private readonly GameDeployer deployer;
        private NexusInstallPlan pendingPlan;
        private readonly Queue<string> browserLinks = new Queue<string>();
        private readonly TextBox address = Theme.TextBox();
        private readonly ComboBox profileList = new ComboBox();
        private readonly DataGridView mods = new DataGridView();
        private readonly Label accountLabel = Theme.Label("", true);
        private readonly Label gameLabel = Theme.Label("", true);
        private readonly Label modCount = Theme.Label("", true);
        private readonly Label status = Theme.Label("", true);
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button downloadButton = Theme.Button("安装 Mod", true);
        private readonly Button importButton = Theme.Button("导入本地");
        private readonly Button settings = Theme.Button("设置", width: 84);
        private readonly Button apply = Theme.Button("应用配置");
        private readonly Button launch = Theme.Button("启动游戏", true);
        private readonly Button addProfile = Theme.Button("新建", width: 82);
        private readonly Button copyProfile = Theme.Button("复制", width: 82);
        private readonly Button exportLinks = Theme.Button("导出 Mod 链接", width: 126);
        private readonly Button update = Theme.Button("更换版本", width: 96);
        private readonly Button prerequisites = Theme.Button("补齐前置", width: 96);
        private readonly Button remove = Theme.Button("移除", width: 78);
        private readonly Button cancel = Theme.Button("取消", width: 64);
        private readonly ToolTip tooltip = new ToolTip();
        private NexusAccount account;
        private string apiKey;
        private CancellationTokenSource operation;
        private bool refreshing;
        private bool modal;
        private bool closeWhenFinished;

        internal MainWindow(AppStorage storage)
            : this(storage, new NexusClient(), BrowserRegistration.IsRegistered, Program.Open) { }

        internal MainWindow(AppStorage storage, NexusClient nexus, Func<bool> receiveDownloadLinks, Action<string> openAuthorizationPage)
        {
            this.storage = storage;
            this.nexus = nexus;
            this.receiveDownloadLinks = receiveDownloadLinks;
            this.openAuthorizationPage = openAuthorizationPage;
            importer = new PackageImporter(storage);
            deployer = new GameDeployer(storage);
            state = storage.LoadState();
            string startupStatus = null;
            try { apiKey = storage.LoadKey(); }
            catch (CryptographicException) { startupStatus = "请在设置中重新保存 Nexus API 密钥。"; }
            if (GameLocation.FindRoot(state.GameRoot) == null)
            {
                try { state.GameRoot = GameLocation.DetectSteam(); }
                catch (IOException) { startupStatus = "请在设置中选择游戏安装目录。"; }
            }

            Theme.Style(this);
            Text = Theme.Title;
            ClientSize = new Size(1040, 612);
            MinimumSize = new Size(850, 475);
            StartPosition = FormStartPosition.CenterScreen;

            BuildLayout();
            BindControls();
            RefreshProfiles();
            UpdateIdentity();
            SetStatus(startupStatus ?? (state.GameRoot == null ? "请在设置中选择游戏安装目录。" : "就绪"));
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 12),
                ColumnCount = 1, RowCount = 4
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            var title = Theme.Label("Rogue Core Mod 管理器");
            title.Font = new Font(Font.FontFamily, 19f, FontStyle.Bold);
            title.Margin = new Padding(0, 0, 0, 2);
            brand.Controls.Add(title);
            brand.Controls.Add(accountLabel);
            header.Controls.Add(brand, 0, 0);
            var actions = Theme.ButtonRow();
            actions.Anchor = AnchorStyles.Right;
            actions.Controls.Add(settings);
            actions.Controls.Add(apply);
            actions.Controls.Add(launch);
            header.Controls.Add(actions, 1, 0);
            root.Controls.Add(header, 0, 0);

            var input = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10, 8, 10, 4),
                BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 8) };
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            address.Font = new Font(Font.FontFamily, 11f);
            address.Margin = new Padding(0, 5, 8, 0);
            address.MaxLength = 8192;
            address.Name = "NexusAddress";
            address.AccessibleName = "Nexus Mod 链接";
            downloadButton.Name = "InstallNexusMod";
            Theme.Cue(address, "粘贴 Nexus Mod 页面或 NXM 下载链接");
            input.Controls.Add(address, 0, 0);
            input.Controls.Add(downloadButton, 1, 0);
            input.Controls.Add(importButton, 2, 0);
            root.Controls.Add(input, 0, 1);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 212));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.Controls.Add(BuildSidebar(), 0, 0);
            body.Controls.Add(BuildModList(), 1, 0);
            root.Controls.Add(body, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0, 8, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.AutoSize = false;
            status.Name = "Status";
            status.AutoEllipsis = true;
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            progress.Width = 156;
            progress.Height = 8;
            progress.Anchor = AnchorStyles.Right;
            progress.Visible = false;
            cancel.Visible = false;
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(progress, 1, 0);
            footer.Controls.Add(cancel, 2, 0);
            root.Controls.Add(footer, 0, 3);
        }

        private Control BuildSidebar()
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0, 0, 10, 0) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(Theme.Label("配置档"));
            profileList.DropDownStyle = ComboBoxStyle.DropDownList;
            profileList.FlatStyle = FlatStyle.Flat;
            profileList.BackColor = Theme.Field;
            profileList.ForeColor = Theme.Text;
            profileList.DrawMode = DrawMode.OwnerDrawFixed;
            profileList.DrawItem += (sender, e) =>
            {
                if (e.Index < 0 || e.Index >= profileList.Items.Count) return;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(selected ? Color.FromArgb(46, 75, 70) : Theme.Field))
                    e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, ((Profile)profileList.Items[e.Index]).Name, profileList.Font, e.Bounds, Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };
            profileList.Dock = DockStyle.Fill;
            profileList.Margin = new Padding(0, 4, 0, 10);
            profileList.DisplayMember = "Name";
            layout.Controls.Add(profileList);
            var buttons = Theme.ButtonRow();
            addProfile.Margin = Padding.Empty;
            buttons.Controls.Add(addProfile);
            buttons.Controls.Add(copyProfile);
            layout.Controls.Add(buttons);
            var label = Theme.Label("游戏目录");
            label.Margin = new Padding(0, 22, 0, 2);
            layout.Controls.Add(label);
            gameLabel.MaximumSize = new Size(170, 0);
            layout.Controls.Add(gameLabel);
            panel.Controls.Add(layout);
            return panel;
        }

        private Control BuildModList()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = Theme.Surface, Padding = new Padding(10, 4, 10, 10), Margin = Padding.Empty };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            modCount.Anchor = AnchorStyles.Left;
            bar.Controls.Add(modCount, 0, 0);
            var buttons = Theme.ButtonRow();
            buttons.Anchor = AnchorStyles.Right;
            tooltip.SetToolTip(exportLinks, "将当前配置档中所有 Nexus Mod 的链接复制到剪贴板");
            buttons.Controls.Add(exportLinks);
            buttons.Controls.Add(prerequisites);
            buttons.Controls.Add(update);
            buttons.Controls.Add(remove);
            bar.Controls.Add(buttons, 1, 0);
            panel.Controls.Add(bar, 0, 0);

            mods.Dock = DockStyle.Fill;
            mods.Margin = Padding.Empty;
            mods.BorderStyle = BorderStyle.None;
            mods.BackgroundColor = Theme.Background;
            mods.GridColor = Theme.Line;
            mods.EnableHeadersVisualStyles = false;
            mods.RowHeadersVisible = false;
            mods.AllowUserToAddRows = false;
            mods.AllowUserToDeleteRows = false;
            mods.AllowUserToResizeRows = false;
            mods.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            mods.MultiSelect = false;
            mods.RowTemplate.Height = 35;
            mods.ColumnHeadersHeight = 32;
            mods.ColumnHeadersDefaultCellStyle.BackColor = Theme.Field;
            mods.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
            mods.DefaultCellStyle.BackColor = Theme.Background;
            mods.DefaultCellStyle.ForeColor = Theme.Text;
            mods.DefaultCellStyle.SelectionBackColor = Color.FromArgb(46, 75, 70);
            mods.DefaultCellStyle.SelectionForeColor = Theme.Text;
            mods.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(23, 30, 34);
            mods.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            mods.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "启用", Width = 62 });
            mods.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "名称", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true, MinimumWidth = 160 });
            mods.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "版本", Width = 108, ReadOnly = true });
            mods.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "来源", Width = 108, ReadOnly = true });
            panel.Controls.Add(mods, 0, 1);
            return panel;
        }

        private void BindControls()
        {
            settings.Click += (sender, e) => OpenSettings();
            downloadButton.Click += async (sender, e) => await StartNexusAsync(address.Text, null, false);
            address.KeyDown += async (sender, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                await StartNexusAsync(address.Text, null, false);
            };
            importButton.Click += async (sender, e) => await ImportLocalAsync();
            apply.Click += async (sender, e) => await WorkAsync(token => ApplyAsync(state.Current(), token));
            launch.Click += async (sender, e) => await WorkAsync(async token =>
            {
                await ApplyAsync(state.Current(), token);
                Program.Open("steam://run/" + GameLocation.SteamId);
                SetStatus("已请求 Steam 启动《异动核心》。");
            });
            cancel.Click += (sender, e) =>
            {
                if (operation != null) operation.Cancel();
                else if (pendingPlan != null)
                {
                    DiscardPlan(pendingPlan);
                    SetStatus("已取消这批安装。");
                    UpdateButtons();
                }
            };
            addProfile.Click += (sender, e) => NewProfile(false);
            copyProfile.Click += (sender, e) => NewProfile(true);
            exportLinks.Click += (sender, e) => ExportModLinks();
            update.Click += async (sender, e) =>
            {
                ModEntry entry = SelectedMod();
                if (entry != null && entry.ModId != 0)
                    await StartNexusAsync(new NexusLink(entry.ModId).BrowserPage(), entry, false);
            };
            prerequisites.Click += async (sender, e) =>
            {
                ModEntry entry = SelectedMod();
                if (entry != null && entry.ModId > 0 && entry.FileId > 0)
                    await StartNexusAsync(new NexusLink(entry.ModId, entry.FileId).BrowserPage(), entry, false);
            };
            remove.Click += (sender, e) => RemoveFromProfile();
            profileList.SelectedIndexChanged += (sender, e) => SelectProfile();
            mods.SelectionChanged += (sender, e) => { if (!refreshing) UpdateSelectionButtons(); };
            mods.CurrentCellDirtyStateChanged += (sender, e) =>
            {
                if (mods.IsCurrentCellDirty) mods.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            mods.CellValueChanged += (sender, e) => ToggleMod(e.RowIndex, e.ColumnIndex);
            mods.CellDoubleClick += (sender, e) =>
            {
                ModEntry item = e.RowIndex >= 0 ? mods.Rows[e.RowIndex].Tag as ModEntry : null;
                if (e.ColumnIndex == 1 && item != null && item.ModId > 0)
                {
                    try { Program.Open(new NexusLink(item.ModId).BrowserPage()); }
                    catch (Exception error) { ShowError(error); }
                }
            };
        }

        internal void ReceiveBrowserLink(string raw)
        {
            if (IsDisposed || closeWhenFinished) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            if (String.IsNullOrWhiteSpace(raw)) return;
            // Each delivery is handled after the active installation or dialog finishes.
            browserLinks.Enqueue(raw);
            ProcessBrowserLinks();
        }

        private async void ProcessBrowserLinks()
        {
            if (operation != null || modal || browserLinks.Count == 0 || closeWhenFinished) return;
            await StartNexusAsync(browserLinks.Dequeue(), null, true);
        }

        private Task StartNexusAsync(string raw, ModEntry replaced, bool fromBrowser)
        {
            return WorkAsync(async token =>
            {
                NexusLink link = NexusLink.Parse(raw);
                if (fromBrowser && !link.IsNxm) throw new FormatException("浏览器回传需要有效的 NXM 下载链接。");
                if (!fromBrowser && link.IsNxm) address.Clear();
                if (!EnsureConfigured(true)) return;
                SetStatus("正在读取 Nexus 文件信息…");
                if (account == null) account = await nexus.VerifyKeyAsync(apiKey, token);
                UpdateIdentity();
                if (link.UserId > 0 && account.user_id > 0 && link.UserId != account.user_id)
                    throw new InvalidOperationException("网页授权使用了另一个 Nexus 账号。请切换到 API 密钥对应的账号再试。");

                NexusInstallPlan plan = pendingPlan;
                if (plan != null)
                {
                    if (!link.IsAuthorized || plan.Waiting == null || !SameFile(plan.Waiting.Link, link))
                    {
                        SetStatus("当前安装正在等待「" + (plan.Waiting == null ? plan.Main.Name : plan.Waiting.Name) + "」的网页授权。");
                        return;
                    }
                }
                else
                {
                    if (fromBrowser && !account.is_premium && !link.IsAuthorized)
                        throw new InvalidOperationException("浏览器返回的 NXM 链接缺少下载授权。请回到 Nexus 重新确认。");
                    plan = await PlanInstallAsync(link, replaced, fromBrowser, token);
                    if (plan == null) return;
                }

                pendingPlan = plan;
                try { await RunInstallPlanAsync(plan, link.IsAuthorized ? link : null, token); }
                catch
                {
                    if (pendingPlan == plan) DiscardPlan(plan);
                    throw;
                }
                if (!fromBrowser) address.Clear();
            });
        }

        private async Task<NexusInstallPlan> PlanInstallAsync(NexusLink link, ModEntry replaced, bool fromBrowser, CancellationToken token)
        {
            var nameTask = nexus.GetModAsync(link.ModId, apiKey, token);
            List<NexusFile> choices;
            if (link.FileId == 0)
            {
                var filesTask = nexus.GetFilesAsync(link.ModId, apiKey, token);
                await Task.WhenAll(nameTask, filesTask);
                choices = DependencyPlanner.OrderedFiles(filesTask.Result.files).Where(f => PackageImporter.CanImport(f.file_name)).ToList();
            }
            else
            {
                var fileTask = nexus.GetFileAsync(link, apiKey, token);
                await Task.WhenAll(nameTask, fileTask);
                NexusFile chosen = fileTask.Result;
                chosen.file_id = link.FileId;
                choices = new List<NexusFile> { chosen };
            }
            if (choices.Count == 0) throw new IOException("这个 Mod 当前没有可安装的下载文件。");
            string modName = nameTask.Result.name;
            if (String.IsNullOrWhiteSpace(modName)) modName = "Nexus #" + link.ModId;
            NexusFile file = choices[0];
            if (choices.Count > 1 || fromBrowser)
            {
                using (var pick = new SelectFileDialog(modName, choices, "安装及检查前置"))
                {
                    if (pick.ShowDialog(this) != DialogResult.OK) { SetStatus("未添加文件。"); return null; }
                    file = pick.SelectedFile;
                }
            }
            PackageImporter.ArchiveExtension(file.file_name);
            SetStatus("正在检查「" + modName + "」的前置…");
            var planner = new DependencyPlanner(storage, nexus, state.Current(), apiKey);
            return await planner.BuildAsync(new NexusLink(link.ModId, file.file_id), file, modName, replaced, token);
        }

        private async Task RunInstallPlanAsync(NexusInstallPlan plan, NexusLink authorized, CancellationToken token)
        {
            // Use a browser signature immediately, even when a dependent file was authorized first.
            if (authorized != null)
            {
                InstallFile current = plan.Files.FirstOrDefault(f => f.Entry == null && SameFile(f.Link, authorized));
                if (current != null) await PrepareFileAsync(plan, current, authorized, token);
            }

            foreach (InstallFile file in plan.Files.Where(f => f.Entry == null))
            {
                token.ThrowIfCancellationRequested();
                if (!account.is_premium)
                {
                    OpenAuthorization(plan, file);
                    return;
                }
                try { await PrepareFileAsync(plan, file, file.Link, token); }
                catch (NexusException error)
                {
                    if (error.Status != HttpStatusCode.Forbidden) throw;
                    OpenAuthorization(plan, file);
                    return;
                }
            }

            token.ThrowIfCancellationRequested();
            await CommitInstallPlanAsync(plan, token);
            pendingPlan = null;
            string summary = "已安装 " + plan.Main.Name + "。自动处理了 " + (plan.Files.Count - 1) + " 个 Nexus 前置。";
            if (plan.External.Count > 0)
            {
                SetStatus(summary + " 另有 " + plan.External.Count + " 个外部前置见详情。");
                MessageBox.Show(this, String.Join("\n\n", plan.External), "作者列出的外部前置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else SetStatus(summary);
        }

        private void OpenAuthorization(NexusInstallPlan plan, InstallFile file)
        {
            if (!receiveDownloadLinks())
            {
                OpenSettings();
                if (!receiveDownloadLinks()) throw new InvalidOperationException("请先在设置中启用浏览器回传，再重新添加链接。");
                if (String.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("请在设置中保存 Nexus API 密钥。");
            }
            plan.Waiting = file;
            openAuthorizationPage(file.Link.BrowserPage());
            SetStatus("请在 Nexus 网页确认「" + file.Name + "」的下载（剩余 " + plan.Files.Count(f => f.Entry == null) + " 个文件）。");
        }

        private async Task PrepareFileAsync(NexusInstallPlan plan, InstallFile file, NexusLink link, CancellationToken token)
        {
            string scratch = storage.CreateTemporaryDirectory();
            var entry = new ModEntry
            {
                ModId = link.ModId, FileId = link.FileId, Name = file.Name,
                FileName = file.File.file_name, Version = file.File.Version
            };
            try
            {
                string temp = Path.Combine(scratch, "download" + PackageImporter.ArchiveExtension(entry.FileName));
                SetStatus("正在下载 " + entry.Name + "…");
                progress.Visible = true;
                progress.Style = ProgressBarStyle.Marquee;
                await nexus.DownloadAsync(link, apiKey, temp, new Progress<int>(ShowProgress), token);
                progress.Style = ProgressBarStyle.Marquee;
                SetStatus("正在解包 " + entry.Name + "…");
                await Task.Run(() => importer.Import(temp, entry, token), token);
                file.Entry = entry;
                plan.NewPackages.Add(entry.PackageId);
            }
            finally { SafePath.TryDeleteOwnedDirectory(scratch); }
        }

        private async Task CommitInstallPlanAsync(NexusInstallPlan plan, CancellationToken token)
        {
            Profile profile = plan.Profile;
            List<ModEntry> previous = profile.Mods;
            var desired = previous.ToList();
            if (plan.Replacement != null && !plan.Files.Any(f => f.Entry == plan.Replacement)) desired.Remove(plan.Replacement);
            foreach (InstallFile file in plan.Files)
            {
                ModEntry entry = file.Entry;
                if (!entry.Enabled)
                {
                    entry = AppStorage.Json().Deserialize<ModEntry>(AppStorage.Json().Serialize(entry));
                    entry.Enabled = true;
                }
                int index = desired.FindIndex(m => m.ModId == entry.ModId && m.FileId == entry.FileId);
                if (index < 0) desired.Add(entry);
                else desired[index] = entry;
            }
            profile.Mods = desired;
            try { storage.SaveState(state); }
            catch { profile.Mods = previous; throw; }
            try { await ApplyAsync(profile, token); }
            catch
            {
                profile.Mods = previous;
                storage.SaveState(state);
                throw;
            }
        }

        private void DiscardPlan(NexusInstallPlan plan)
        {
            pendingPlan = null;
            foreach (string id in plan.NewPackages)
                SafePath.TryDeleteOwnedDirectory(SafePath.In(storage.Packages, SafePath.Id(id)));
        }

        private Task ImportLocalAsync()
        {
            return WorkAsync(async token =>
            {
                if (!EnsureConfigured(false)) return;
                using (var dialog = new OpenFileDialog { Filter = "Mod 文件|*.pak;*.zip;*.7z;*.rar", CheckFileExists = true })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    string file = dialog.FileName;
                    var entry = new ModEntry { Name = Path.GetFileNameWithoutExtension(file), FileName = Path.GetFileName(file) };
                    SetStatus("正在导入 " + entry.Name + "…");
                    await Task.Run(() => importer.Import(file, entry, token), token);
                    Profile profile = state.Current();
                    AddEntry(profile, entry, null);
                    await ApplyAsync(profile, token);
                    SetStatus("已安装 " + entry.Name + "。");
                }
            });
        }

        private void AddEntry(Profile profile, ModEntry entry, ModEntry replaced)
        {
            int index = replaced == null ? -1 : profile.Mods.IndexOf(replaced);
            if (index < 0) profile.Mods.Add(entry);
            else profile.Mods[index] = entry;
            try { storage.SaveState(state); }
            catch
            {
                if (index < 0) profile.Mods.Remove(entry);
                else profile.Mods[index] = replaced;
                throw;
            }
        }

        private async Task ApplyAsync(Profile profile, CancellationToken token)
        {
            if (!EnsureConfigured(false)) throw new OperationCanceledException();
            SetStatus("正在应用「" + profile.Name + "」…");
            IProgress<string> runtimeStatus = new Progress<string>(message => SetStatus(message));
            int count = await Task.Run(() => deployer.Apply(state.GameRoot, profile, token,
                () => runtimeStatus.Report("正在下载并校验 Mintcat 的 UE4SS Lite " + ScriptRuntime.Version + "…")), token);
            SetStatus("已应用「" + profile.Name + "」，共部署 " + count + " 个文件。");
        }

        private bool EnsureConfigured(bool needsKey)
        {
            if (GameLocation.FindRoot(state.GameRoot) != null && (!needsKey || !String.IsNullOrWhiteSpace(apiKey))) return true;
            OpenSettings();
            return GameLocation.FindRoot(state.GameRoot) != null && (!needsKey || !String.IsNullOrWhiteSpace(apiKey));
        }

        private void OpenSettings()
        {
            modal = true;
            try
            {
                using (var dialog = new SettingsDialog(storage, state, nexus, apiKey, account))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        apiKey = dialog.ApiKey;
                        account = dialog.Account;
                        UpdateIdentity();
                        SetStatus("已保存设置。");
                    }
                }
            }
            catch (Exception error) { ShowError(error); }
            finally { modal = false; ProcessBrowserLinks(); }
        }

        private async Task WorkAsync(Func<CancellationToken, Task> work)
        {
            if (operation != null || modal) return;
            using (var stop = new CancellationTokenSource())
            {
                operation = stop;
                UpdateButtons();
                try { await work(stop.Token); }
                catch (OperationCanceledException) { SetStatus("操作已取消。"); }
                catch (Exception error) { ShowError(error); }
                finally
                {
                    operation = null;
                    progress.Visible = false;
                    try { RefreshModRows(); }
                    catch (Exception error) { ShowError(error); UpdateButtons(); }
                    if (IsHandleCreated && !IsDisposed)
                    {
                        if (closeWhenFinished) BeginInvoke((Action)Close);
                        else BeginInvoke((Action)ProcessBrowserLinks);
                    }
                }
            }
        }

        private void SelectProfile()
        {
            if (refreshing || !(profileList.SelectedItem is Profile)) return;
            string previous = state.ActiveProfile;
            state.ActiveProfile = ((Profile)profileList.SelectedItem).Id;
            try { storage.SaveState(state); SetStatus("已切换配置档。点「应用配置」更新游戏文件。"); }
            catch (Exception error) { state.ActiveProfile = previous; RefreshProfiles(); ShowError(error); }
            RefreshModRows();
        }

        private void NewProfile(bool duplicate)
        {
            modal = true;
            try
            {
                using (var dialog = new Form { Text = duplicate ? "复制配置档" : "新建配置档", ClientSize = new Size(420, 110),
                    StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                    MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false })
                {
                    Theme.Style(dialog);
                    var name = Theme.TextBox();
                    name.Text = duplicate ? state.Current().Name + " 副本" : "";
                    Theme.Cue(name, "配置档名称");
                    var form = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 2 };
                    form.Controls.Add(name, 0, 0);
                    var button = Theme.Button("保存", true);
                    button.Anchor = AnchorStyles.Right;
                    button.DialogResult = DialogResult.OK;
                    form.Controls.Add(button, 0, 1);
                    dialog.Controls.Add(form);
                    dialog.AcceptButton = button;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    string caption = name.Text.Trim();
                    if (caption == "" || state.Profiles.Any(p => String.Equals(p.Name, caption, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("请填写一个新的配置档名称。");
                    Profile created = duplicate ? AppStorage.Json().Deserialize<Profile>(AppStorage.Json().Serialize(state.Current())) : new Profile();
                    created.Name = caption;
                    created.Id = Guid.NewGuid().ToString("N");
                    string previous = state.ActiveProfile;
                    state.ActiveProfile = created.Id;
                    state.Profiles.Add(created);
                    try { storage.SaveState(state); }
                    catch { state.Profiles.Remove(created); state.ActiveProfile = previous; throw; }
                    RefreshProfiles();
                    SetStatus("已创建「" + created.Name + "」。");
                }
            }
            catch (Exception error) { ShowError(error); }
            finally { modal = false; ProcessBrowserLinks(); }
        }

        private void ExportModLinks()
        {
            List<ModEntry> entries = state.Current().Mods;
            string[] links = entries.Where(mod => mod.ModId > 0)
                .Select(mod => new NexusLink(mod.ModId, mod.FileId).PublicPage())
                .Distinct(StringComparer.Ordinal).ToArray();
            if (links.Length == 0)
            {
                SetStatus(entries.Count == 0 ? "当前配置档没有 Mod。" : "本地导入的 Mod 没有记录 Nexus 链接。");
                return;
            }

            try { Clipboard.SetText(String.Join(Environment.NewLine, links)); }
            catch (ExternalException)
            {
                SetStatus("剪贴板暂时不可用，请重试。", true);
                return;
            }
            int localCount = entries.Count(mod => mod.ModId <= 0);
            string result = "已复制 " + links.Length + " 个 Nexus Mod 链接到剪贴板。";
            if (localCount > 0) result += " 另有 " + localCount + " 个本地 Mod 没有记录链接。";
            SetStatus(result);
        }

        private void RemoveFromProfile()
        {
            ModEntry entry = SelectedMod();
            if (entry == null) return;
            var entries = state.Current().Mods;
            int index = entries.IndexOf(entry);
            if (index < 0) return;
            entries.RemoveAt(index);
            try { storage.SaveState(state); SetStatus("已从配置档移除。点「应用配置」卸载对应文件。"); }
            catch (Exception error) { entries.Insert(index, entry); ShowError(error); }
            RefreshModRows();
        }

        private void ToggleMod(int row, int column)
        {
            if (refreshing || column != 0 || row < 0 || !(mods.Rows[row].Tag is ModEntry)) return;
            var entry = (ModEntry)mods.Rows[row].Tag;
            bool previous = entry.Enabled;
            entry.Enabled = Convert.ToBoolean(mods.Rows[row].Cells[0].Value);
            try { storage.SaveState(state); SetStatus("配置已保存。点「应用配置」生效。"); UpdateCount(); }
            catch (Exception error) { entry.Enabled = previous; RefreshModRows(); ShowError(error); }
        }

        private void RefreshProfiles()
        {
            refreshing = true;
            profileList.Items.Clear();
            foreach (Profile profile in state.Profiles) profileList.Items.Add(profile);
            profileList.SelectedItem = state.Current();
            refreshing = false;
            RefreshModRows();
        }

        private void RefreshModRows()
        {
            refreshing = true;
            mods.Rows.Clear();
            foreach (ModEntry entry in state.Current().Mods)
            {
                int index = mods.Rows.Add(entry.Enabled, entry.Name, entry.Version ?? "", entry.ModId == 0 ? "本地" : "Nexus #" + entry.ModId);
                mods.Rows[index].Tag = entry;
                mods.Rows[index].Cells[1].ToolTipText = entry.FileName;
            }
            refreshing = false;
            UpdateCount();
            UpdateButtons();
        }

        private void UpdateCount()
        {
            List<ModEntry> items = state.Current().Mods;
            modCount.Text = items.Count + " 个 Mod  ·  " + items.Count(m => m.Enabled) + " 个启用";
        }

        private void UpdateIdentity()
        {
            accountLabel.Text = account != null ? "Nexus  ·  " + account.name + (account.is_premium ? "  ·  Premium" : "  ·  普通账号") :
                String.IsNullOrEmpty(apiKey) ? "Nexus API 密钥未设置" : "Nexus API 密钥已保存";
            gameLabel.Text = state.GameRoot == null ? "未选择" : Path.GetFileName(state.GameRoot) + "\n" + Path.GetPathRoot(state.GameRoot);
            tooltip.SetToolTip(gameLabel, state.GameRoot ?? gameLabel.Text);
        }

        private void UpdateButtons()
        {
            bool idle = operation == null && pendingPlan == null;
            foreach (Control control in new Control[] { address, downloadButton, importButton, settings, apply, launch,
                profileList, addProfile, copyProfile, exportLinks, mods }) control.Enabled = idle;
            UpdateSelectionButtons();
            cancel.Visible = !idle;
        }

        private void UpdateSelectionButtons()
        {
            ModEntry selected = SelectedMod();
            bool idle = operation == null && pendingPlan == null;
            update.Enabled = idle && selected != null && selected.ModId > 0;
            prerequisites.Enabled = idle && selected != null && selected.ModId > 0 && selected.FileId > 0;
            remove.Enabled = idle && selected != null;
        }

        private ModEntry SelectedMod() { return mods.CurrentRow == null ? null : mods.CurrentRow.Tag as ModEntry; }

        private void ShowProgress(int percent)
        {
            if (operation == null || !progress.Visible) return;
            progress.Style = percent < 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (percent >= 0) progress.Value = percent;
        }

        private void SetStatus(string text, bool error = false)
        {
            status.Text = text;
            status.ForeColor = error ? Theme.Error : Theme.Dim;
            tooltip.SetToolTip(status, text);
        }

        private void ShowError(Exception error)
        {
            SetStatus(error.Message, true);
        }

        private static bool SameFile(NexusLink a, NexusLink b) { return a.ModId == b.ModId && a.FileId == b.FileId; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (operation != null)
            {
                closeWhenFinished = true;
                operation.Cancel();
                e.Cancel = true;
            }
            else if (pendingPlan != null) DiscardPlan(pendingPlan);
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { nexus.Dispose(); tooltip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
