using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RogueCoreNexus
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string link = args.Length == 2 && args[0] == "--nxm" ? args[1] : (args.Length == 1 ? args[0] : "");
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RogueCoreNexus");
                var storage = new AppStorage(root);
                var inbox = new BrowserInbox(storage.Root);
                using (var instance = inbox.TryClaimInstance())
                {
                    if (instance == null)
                    {
                        inbox.Submit(link);
                        return;
                    }
                    using (var form = new MainWindow(storage))
                    using (var timer = new Timer { Interval = 500 })
                    {
                        timer.Tick += (sender, e) =>
                        {
                            foreach (string received in inbox.TakePending()) form.ReceiveBrowserLink(received);
                        };
                        form.Shown += (sender, e) =>
                        {
                            timer.Start();
                            if (!String.IsNullOrEmpty(link)) form.ReceiveBrowserLink(link);
                        };
                        Application.Run(form);
                    }
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, Theme.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal static void Open(string url)
        {
            using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
        }
    }

    internal sealed class BrowserInbox
    {
        private readonly string inbox;
        private readonly string lockPath;

        internal BrowserInbox(string root)
        {
            inbox = SafePath.In(root, "browser-inbox");
            lockPath = SafePath.In(root, "instance.lock");
            Directory.CreateDirectory(inbox);
        }

        internal FileStream TryClaimInstance()
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error)
            {
                int systemCode = error.HResult & 0xFFFF;
                if (systemCode == 32 || systemCode == 33) return null;
                throw;
            }
        }

        internal void Submit(string link)
        {
            if (Encoding.UTF8.GetByteCount(link) > 32768) throw new FormatException("NXM 链接过长。");
            string path = SafePath.In(inbox, Guid.NewGuid().ToString("N") + ".request");
            AppStorage.WriteProtectedText(path, link);
        }

        internal List<string> TakePending()
        {
            var received = new List<string>();
            if (SafePath.IsReparsePoint(inbox) || !Directory.Exists(inbox)) return received;
            foreach (string file in Directory.GetFiles(inbox, "*.request"))
            {
                if (SafePath.IsReparsePoint(file)) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-10) || new FileInfo(file).Length > 65536)
                    {
                        File.Delete(file);
                        continue;
                    }
                    string link = AppStorage.ReadProtectedText(file);
                    File.Delete(file);
                    received.Add(link);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (CryptographicException) { File.Delete(file); }
            }
            return received;
        }
    }

    internal static class BrowserRegistration
    {
        private const string Nxm = @"Software\Classes\nxm";
        private const string OpenCommand = @"\shell\open\command";
        private const string Settings = @"Software\RogueCoreNexus\NxmBackup";

        private static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\" --nxm \"%1\""; }
        }

        internal static bool IsRegistered()
        {
            return String.Equals(Registry.GetValue(@"HKEY_CLASSES_ROOT\nxm" + OpenCommand, "", null) as string,
                Command, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasOtherHandler()
        {
            return !IsRegistered() && Registry.GetValue(@"HKEY_CLASSES_ROOT\nxm" + OpenCommand, "", null) is string;
        }

        internal static void Register()
        {
            if (IsRegistered()) return;
            using (RegistryKey protocol = Registry.CurrentUser.CreateSubKey(Nxm))
            using (RegistryKey command = Registry.CurrentUser.CreateSubKey(Nxm + OpenCommand))
            using (RegistryKey backup = Registry.CurrentUser.CreateSubKey(Settings))
            {
                SetValue(backup, "Command", command.GetValue("") as string);
                SetValue(backup, "Protocol", protocol.GetValue("URL Protocol") as string);
                SetValue(backup, "Caption", protocol.GetValue("") as string);
                if (protocol.GetValue("") == null) protocol.SetValue("", "URL:Nexus Mods");
                protocol.SetValue("URL Protocol", "");
                command.SetValue("", Command);
            }
            NotifyAssociationsChanged();
        }

        internal static void Restore()
        {
            using (RegistryKey protocol = Registry.CurrentUser.OpenSubKey(Nxm, true))
            using (RegistryKey command = Registry.CurrentUser.OpenSubKey(Nxm + OpenCommand, true))
            using (RegistryKey backup = Registry.CurrentUser.OpenSubKey(Settings, true))
            {
                if (protocol == null || command == null || backup == null ||
                    !String.Equals(command.GetValue("") as string, Command, StringComparison.OrdinalIgnoreCase)) return;
                SetValue(command, "", backup.GetValue("Command") as string);
                SetValue(protocol, "URL Protocol", backup.GetValue("Protocol") as string);
                SetValue(protocol, "", backup.GetValue("Caption") as string);
                backup.DeleteValue("Command", false);
                backup.DeleteValue("Protocol", false);
                backup.DeleteValue("Caption", false);
            }
            NotifyAssociationsChanged();
        }

        private static void SetValue(RegistryKey key, string name, string value)
        {
            if (value == null) key.DeleteValue(name, false);
            else key.SetValue(name, value);
        }

        private static void NotifyAssociationsChanged()
        {
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
