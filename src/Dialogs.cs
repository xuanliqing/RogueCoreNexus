using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace RogueCoreNexus
{
    internal sealed class SettingsDialog : Form
    {
        private readonly AppStorage storage;
        private readonly NexusClient nexus;
        private readonly AppState state;
        private readonly TextBox game = Theme.TextBox();
        private readonly TextBox key = Theme.TextBox();
        private readonly CheckBox clearKey = new CheckBox { Text = "清除已保存的密钥", AutoSize = true, FlatStyle = FlatStyle.Flat };
        private readonly Label protocol = Theme.Label("", true);
        private readonly Label status = Theme.Label("", true);
        private readonly Button register = Theme.Button("启用浏览器回传", width: 172, glyph: Glyph.Globe);
        private readonly Button save = Theme.Button("保存设置", true, 124, Glyph.Accept);
        private CancellationTokenSource verifying;
        internal NexusAccount Account { get; private set; }
        internal string ApiKey { get; private set; }

        internal SettingsDialog(AppStorage storage, AppState state, NexusClient nexus, string apiKey, NexusAccount account)
        {
            this.storage = storage;
            this.state = state;
            this.nexus = nexus;
            ApiKey = apiKey;
            Account = account;
            Theme.Style(this);
            Text = "设置";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(680, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 18), ColumnCount = 1, AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            var banner = new BannerPanel { Dock = DockStyle.Top, Height = 74, Padding = new Padding(22, 10, 22, 14) };
            var mark = new TableLayoutPanel { Dock = DockStyle.Left, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
            mark.Controls.Add(new ArtBox(Theme.Emblem) { Size = new Size(46, 46), Margin = new Padding(0, 0, 10, 0) }, 0, 0);
            mark.Controls.Add(new TitleMark("SETTINGS", "设置", 22f) { Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 1, 0);
            banner.Controls.Add(mark);
            Controls.Add(banner);

            layout.Controls.Add(new Heading("《异动核心》Steam 安装目录", "GAME DIR"));
            var givePath = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Top, Height = 40, Margin = Padding.Empty };
            givePath.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            givePath.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            givePath.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            game.Text = state.GameRoot ?? "";
            var browse = Theme.Button("浏览", width: 86, glyph: Glyph.Folder);
            var detect = Theme.Button("自动查找", width: 116, glyph: Glyph.Search);
            browse.Click += (sender, e) => Browse();
            detect.Click += (sender, e) => Detect();
            Control gameField = Theme.Framed(game);
            gameField.Margin = new Padding(0, 0, 4, 0);
            givePath.Controls.Add(gameField, 0, 0);
            givePath.Controls.Add(browse, 1, 0);
            givePath.Controls.Add(detect, 2, 0);
            layout.Controls.Add(givePath);

            var nexusRow = Theme.ButtonRow();
            nexusRow.Margin = new Padding(0, 14, 0, 0);
            nexusRow.Controls.Add(new Heading("Nexus 个人 API 密钥", "API KEY"));
            var link = new LinkLabel { Text = "获取密钥 ↗", AutoSize = true, LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.Text, VisitedLinkColor = Theme.Accent, Margin = new Padding(12, 4, 0, 0) };
            link.LinkClicked += (sender, e) => TryOpen(NexusLink.KeyPage);
            nexusRow.Controls.Add(link);
            layout.Controls.Add(nexusRow);
            key.UseSystemPasswordChar = true;
            key.MaxLength = 1024;
            Control keyField = Theme.Framed(key);
            keyField.Margin = new Padding(0, 3, 0, 4);
            layout.Controls.Add(keyField);
            var keyHint = Theme.Label(String.IsNullOrEmpty(ApiKey) ? "密钥加密保存，只有当前 Windows 用户能读取。" : "已保存密钥。留空表示继续使用已保存的密钥。", true);
            layout.Controls.Add(keyHint);
            clearKey.FlatAppearance.BorderColor = Theme.Line;
            clearKey.FlatAppearance.CheckedBackColor = Theme.AccentDeep;
            if (!String.IsNullOrEmpty(ApiKey)) layout.Controls.Add(clearKey);

            layout.Controls.Add(new Heading("浏览器回传", "NXM LINK") { Margin = new Padding(0, 14, 0, 0) });
            protocol.Margin = new Padding(0, 2, 0, 6);
            protocol.MaximumSize = new Size(610, 0);
            layout.Controls.Add(protocol);
            register.Margin = Padding.Empty;
            register.Click += (sender, e) => ChangeRegistration();
            layout.Controls.Add(register);
            RefreshProtocol();

            status.MaximumSize = new Size(610, 0);
            layout.Controls.Add(status);
            var buttons = Theme.ButtonRow();
            buttons.Anchor = AnchorStyles.Right;
            var cancel = Theme.Button("取消", glyph: Glyph.Cancel);
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;
            AcceptButton = save;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            layout.Controls.Add(buttons);
            save.Click += async (sender, e) => await SaveAsync();
        }

        private void Browse()
        {
            using (var folder = new FolderBrowserDialog { Description = "选择《深岩银河：异动核心》的游戏目录", ShowNewFolderButton = false, SelectedPath = game.Text })
            {
                if (folder.ShowDialog(this) == DialogResult.OK) game.Text = folder.SelectedPath;
            }
        }

        private void Detect()
        {
            try
            {
                string found = GameLocation.DetectSteam();
                if (found != null)
                {
                    game.Text = found;
                    SetStatus("已找到 Steam 安装目录。");
                }
                else SetStatus("请浏览并选择游戏目录。", true);
            }
            catch (Exception error) { SetStatus(error.Message, true); }
        }

        private void RefreshProtocol()
        {
            bool enabled = BrowserRegistration.IsRegistered();
            protocol.Text = enabled ? "已启用。在 Nexus 网页确认下载后，下载链接会交给本管理器。" :
                "免费 Nexus 账号需要启用：本管理器会成为 NXM 链接的打开程序，接收你在网页上确认的下载。Premium 账号可不启用。";
            register.Text = enabled ? "恢复原回传设置" : "启用浏览器回传";
        }

        private void ChangeRegistration()
        {
            try
            {
                if (BrowserRegistration.IsRegistered()) BrowserRegistration.Restore();
                else
                {
                    if (BrowserRegistration.HasOtherHandler() && MessageBox.Show(this,
                        "Windows 已有关联 NXM 链接的程序。要切换到此管理器吗？可以在这里恢复原关联。",
                        Theme.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    BrowserRegistration.Register();
                    if (!BrowserRegistration.IsRegistered()) SetStatus("请在 Windows 默认应用中为 NXM 链接选中此管理器。", true);
                }
                RefreshProtocol();
            }
            catch (Exception error) { SetStatus(error.Message, true); }
        }

        private async System.Threading.Tasks.Task SaveAsync()
        {
            if (verifying != null) return;
            bool saved = false;
            using (var stop = new CancellationTokenSource())
            {
                verifying = stop;
                save.Enabled = false;
                try
                {
                    string root = GameLocation.FindRoot(game.Text.Trim());
                    if (root == null && !String.IsNullOrWhiteSpace(game.Text))
                        throw new DirectoryNotFoundException("此路径中没有找到《异动核心》的游戏文件。");
                    string typed = key.Text.Trim();
                    string desiredKey = clearKey.Checked ? null : typed.Length == 0 ? ApiKey : typed;
                    NexusAccount desiredAccount = Account;
                    if (desiredKey != ApiKey)
                    {
                        if (desiredKey == null) desiredAccount = null;
                        else
                        {
                            SetStatus("正在验证 Nexus 密钥…");
                            desiredAccount = await nexus.VerifyKeyAsync(desiredKey, stop.Token);
                        }
                    }
                    string oldRoot = state.GameRoot;
                    state.GameRoot = root;
                    try
                    {
                        storage.SaveState(state);
                        if (desiredKey != ApiKey)
                        {
                            try { storage.SaveKey(desiredKey); }
                            catch
                            {
                                state.GameRoot = oldRoot;
                                storage.SaveState(state);
                                throw;
                            }
                        }
                    }
                    catch { state.GameRoot = oldRoot; throw; }
                    ApiKey = desiredKey;
                    Account = desiredAccount;
                    saved = true;
                }
                catch (OperationCanceledException) { SetStatus("已取消验证。"); }
                catch (Exception error) { SetStatus(error.Message, true); }
                finally { verifying = null; save.Enabled = true; }
            }
            if (saved) DialogResult = DialogResult.OK;
        }

        private void SetStatus(string text, bool error = false)
        {
            status.Text = text;
            status.ForeColor = error ? Theme.Error : Theme.Dim;
        }

        private void TryOpen(string page)
        {
            try { Program.Open(page); }
            catch (Exception error) { SetStatus(error.Message, true); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (verifying != null) { verifying.Cancel(); e.Cancel = true; }
            base.OnFormClosing(e);
        }
    }

    internal sealed class SelectFileDialog : Form
    {
        private sealed class Choice
        {
            internal NexusFile File;
            public override string ToString()
            {
                return File.Label + (String.IsNullOrEmpty(File.Version) ? "" : "   v" + File.Version) +
                    (String.IsNullOrEmpty(File.category_name) ? "" : "   ·   " + File.category_name);
            }
        }

        private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        private readonly ListBox choices = new ListBox();
        internal NexusFile SelectedFile { get { return ((Choice)choices.SelectedItem).File; } }

        internal SelectFileDialog(string title, IList<NexusFile> files, string confirm)
        {
            Theme.Style(this);
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            MinimumSize = new Size(560, 290);
            ClientSize = new Size(690, 350);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Heading("选择要安装的 Nexus 文件", "FILES") { Margin = new Padding(0, 0, 0, 8) }, 0, 0);
            choices.Dock = DockStyle.Fill;
            choices.BackColor = Theme.Surface;
            choices.ForeColor = Theme.Text;
            choices.BorderStyle = BorderStyle.FixedSingle;
            choices.IntegralHeight = false;
            choices.DrawMode = DrawMode.OwnerDrawFixed;
            choices.ItemHeight = Font.Height * 2 + 10;
            choices.DrawItem += (sender, e) => DrawChoice(e);
            foreach (NexusFile file in files) choices.Items.Add(new Choice { File = file });
            choices.SelectedIndex = 0;
            choices.DoubleClick += (sender, e) => { if (choices.SelectedIndex >= 0) DialogResult = DialogResult.OK; };
            layout.Controls.Add(choices, 0, 1);

            var row = Theme.ButtonRow();
            row.Anchor = AnchorStyles.Right;
            row.Margin = new Padding(0, 10, 0, 0);
            var cancel = Theme.Button("取消", glyph: Glyph.Cancel);
            var ok = Theme.Button(confirm, true, 180, Glyph.Download);
            cancel.DialogResult = DialogResult.Cancel;
            ok.DialogResult = DialogResult.OK;
            row.Controls.Add(cancel);
            row.Controls.Add(ok);
            layout.Controls.Add(row, 0, 2);
            CancelButton = cancel;
            AcceptButton = ok;
            Controls.Add(layout);
        }

        private void DrawChoice(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= choices.Items.Count) return;
            NexusFile file = ((Choice)choices.Items[e.Index]).File;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Graphics g = e.Graphics;
            float k = Theme.Scale(g);
            using (var back = new SolidBrush(selected ? Theme.Selection : e.Index % 2 == 0 ? Theme.Surface : Theme.Background))
                g.FillRectangle(back, e.Bounds);
            using (var line = new Pen(Theme.Line)) g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            if (selected)
                using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, e.Bounds.Left, e.Bounds.Top, 3 * k, e.Bounds.Height - 1);
            int left = e.Bounds.Left + (int)(14 * k), half = (e.Bounds.Height - 1) / 2;
            TextRenderer.DrawText(g, file.Label, Font, new Rectangle(left, e.Bounds.Top + 3, e.Bounds.Width - left - 8, half), Theme.Text, Flags);
            string details = (String.IsNullOrEmpty(file.Version) ? "" : "v" + file.Version) +
                (String.IsNullOrEmpty(file.category_name) ? "" : "   " + file.category_name);
            TextRenderer.DrawText(g, details, Theme.Display(10.5f), new Rectangle(left, e.Bounds.Top + half, e.Bounds.Width - left - 8, half - 3),
                selected ? Theme.Accent : Theme.AccentText, Flags);
        }
    }
}
