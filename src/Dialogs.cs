using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RogueCoreNexus
{
    internal static class Theme
    {
        internal const string Title = "Rogue Core Nexus Manager";
        internal static readonly Color Background = Color.FromArgb(19, 24, 28);
        internal static readonly Color Surface = Color.FromArgb(28, 35, 40);
        internal static readonly Color Field = Color.FromArgb(34, 43, 49);
        internal static readonly Color Line = Color.FromArgb(54, 67, 74);
        internal static readonly Color Text = Color.FromArgb(230, 238, 239);
        internal static readonly Color Dim = Color.FromArgb(159, 175, 180);
        internal static readonly Color Accent = Color.FromArgb(113, 220, 192);
        internal static readonly Color Error = Color.FromArgb(255, 162, 146);

        internal static void Style(Form form)
        {
            form.Font = new Font("Microsoft YaHei UI", 10f);
            form.AutoScaleMode = AutoScaleMode.Font;
            form.BackColor = Background;
            form.ForeColor = Text;
        }

        internal static Button Button(string text, bool primary = false, int width = 112)
        {
            var button = new Button {
                Text = text, Width = width, Height = 36, FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Field, ForeColor = primary ? Background : Text,
                UseVisualStyleBackColor = false, Cursor = Cursors.Hand, Margin = new Padding(4, 0, 0, 0)
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        internal static Label Label(string text, bool muted = false)
        {
            return new Label {
                Text = text, AutoSize = true, ForeColor = muted ? Dim : Text, UseMnemonic = false,
                Margin = new Padding(0, 4, 0, 4)
            };
        }

        internal static TextBox TextBox()
        {
            return new TextBox { BackColor = Field, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill };
        }

        internal static void Cue(TextBox box, string hint)
        {
            box.HandleCreated += (sender, e) => SendMessage(box.Handle, 0x1501, new IntPtr(1), hint);
            if (box.IsHandleCreated) SendMessage(box.Handle, 0x1501, new IntPtr(1), hint);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, string lParam);

        internal static FlowLayoutPanel ButtonRow()
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        }
    }

    internal sealed class SettingsDialog : Form
    {
        private readonly AppStorage storage;
        private readonly NexusClient nexus;
        private readonly AppState state;
        private readonly TextBox game = Theme.TextBox();
        private readonly TextBox key = Theme.TextBox();
        private readonly CheckBox clearKey = new CheckBox { Text = "清除已保存的密钥", AutoSize = true };
        private readonly Label protocol = Theme.Label("", true);
        private readonly Label status = Theme.Label("", true);
        private readonly Button register = Theme.Button("启用浏览器回传", width: 150);
        private readonly Button save = Theme.Button("保存设置", true);
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
            ClientSize = new Size(660, 378);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            layout.Controls.Add(Theme.Label("《异动核心》Steam 安装目录"));
            var givePath = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Top, Height = 40, Margin = Padding.Empty };
            givePath.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            givePath.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            givePath.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            game.Text = state.GameRoot ?? "";
            game.Margin = new Padding(0, 5, 4, 0);
            var browse = Theme.Button("浏览", width: 74);
            var detect = Theme.Button("自动查找", width: 96);
            browse.Click += (sender, e) => Browse();
            detect.Click += (sender, e) => Detect();
            givePath.Controls.Add(game, 0, 0);
            givePath.Controls.Add(browse, 1, 0);
            givePath.Controls.Add(detect, 2, 0);
            layout.Controls.Add(givePath);

            var nexusRow = Theme.ButtonRow();
            nexusRow.Margin = new Padding(0, 12, 0, 0);
            nexusRow.Controls.Add(Theme.Label("Nexus 个人 API 密钥"));
            var link = new LinkLabel { Text = "获取密钥 ↗", AutoSize = true, LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.Text, VisitedLinkColor = Theme.Accent, Margin = new Padding(12, 4, 0, 0) };
            link.LinkClicked += (sender, e) => TryOpen(NexusLink.KeyPage);
            nexusRow.Controls.Add(link);
            layout.Controls.Add(nexusRow);
            key.UseSystemPasswordChar = true;
            key.MaxLength = 1024;
            key.Margin = new Padding(0, 3, 0, 4);
            layout.Controls.Add(key);
            var keyHint = Theme.Label(String.IsNullOrEmpty(ApiKey) ? "密钥加密保存在当前 Windows 账户。" : "已保存密钥，留空继续使用。", true);
            layout.Controls.Add(keyHint);
            if (!String.IsNullOrEmpty(ApiKey)) layout.Controls.Add(clearKey);

            protocol.Margin = new Padding(0, 12, 0, 6);
            layout.Controls.Add(protocol);
            register.Margin = Padding.Empty;
            register.Click += (sender, e) => ChangeRegistration();
            layout.Controls.Add(register);
            RefreshProtocol();

            status.MaximumSize = new Size(610, 0);
            layout.Controls.Add(status);
            var buttons = Theme.ButtonRow();
            buttons.Anchor = AnchorStyles.Right;
            var cancel = Theme.Button("取消");
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
            protocol.Text = enabled ? "浏览器授权回传已启用。" : "普通 Nexus 账号需要启用浏览器回传。";
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
            layout.Controls.Add(Theme.Label("选择要安装的 Nexus 文件"), 0, 0);
            choices.Dock = DockStyle.Fill;
            choices.BackColor = Theme.Surface;
            choices.ForeColor = Theme.Text;
            choices.BorderStyle = BorderStyle.FixedSingle;
            choices.HorizontalScrollbar = true;
            choices.IntegralHeight = false;
            foreach (NexusFile file in files) choices.Items.Add(new Choice { File = file });
            choices.SelectedIndex = 0;
            choices.DoubleClick += (sender, e) => { if (choices.SelectedIndex >= 0) DialogResult = DialogResult.OK; };
            layout.Controls.Add(choices, 0, 1);

            var row = Theme.ButtonRow();
            row.Anchor = AnchorStyles.Right;
            row.Margin = new Padding(0, 10, 0, 0);
            var cancel = Theme.Button("取消");
            var ok = Theme.Button(confirm, true, 150);
            cancel.DialogResult = DialogResult.Cancel;
            ok.DialogResult = DialogResult.OK;
            row.Controls.Add(cancel);
            row.Controls.Add(ok);
            layout.Controls.Add(row, 0, 2);
            CancelButton = cancel;
            AcceptButton = ok;
            Controls.Add(layout);
        }
    }
}
