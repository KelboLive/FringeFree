// FringeFree - tiny tray utility to switch Windows text antialiasing presets.
// Build: build.bat (uses the C# compiler that ships with Windows, no installs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("FringeFree")]
[assembly: AssemblyVersion("1.0.0.0")]

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (Mutex m = new Mutex(true, "OledTextFix_SingleInstance", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.Run(new TrayApp());
        }
    }
}

class Preset
{
    public string Name;
    public string Description;
    public int SmoothingType;   // 1 = grayscale (standard), 2 = ClearType
    public int Level;           // Avalon ClearTypeLevel: 0, 50 or 100
    public Color IconColor;
    public Preset(string n, string d, int t, int l, Color c)
    { Name = n; Description = d; SmoothingType = t; Level = l; IconColor = c; }
}

class TrayApp : ApplicationContext
{
    // NOTE: internal names (OledTextFix) are kept so existing backups/preferences from earlier builds still work.
    const string DesktopKey = @"Control Panel\Desktop";
    const string AvalonKey = @"SOFTWARE\Microsoft\Avalon.Graphics";
    const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "OledTextFix";

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, IntPtr data, uint flags);
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);

    const uint SPI_SETFONTSMOOTHING = 0x004B;
    const uint SPI_SETFONTSMOOTHINGTYPE = 0x200B;
    const uint SPIF_UPDATE_AND_BROADCAST = 0x3;

    readonly Preset[] presets = new Preset[] {
        new Preset("Grayscale (no colour fringing)", "Best for WOLED", 1, 0, Color.FromArgb(46, 160, 67)),
        new Preset("Reduced ClearType (50%)", "Good compromise, try on QD-OLED", 2, 50, Color.FromArgb(214, 140, 20)),
        new Preset("Windows default (ClearType)", "Standard Windows behaviour", 2, 100, Color.FromArgb(110, 118, 129)),
    };

    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu;
    readonly List<ToolStripMenuItem> presetItems = new List<ToolStripMenuItem>();
    ToolStripMenuItem startupItem;
    ToolStripMenuItem braveItem;
    const string BraveFlag = "--disable-lcd-text";
    const string PrefKey = @"SOFTWARE\OledTextFix";
    IntPtr currentHIcon = IntPtr.Zero;
    readonly string dataDir;
    readonly string backupFile;

    public TrayApp()
    {
        dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OledTextFix");
        backupFile = Path.Combine(dataDir, "backup.ini");
        try { Directory.CreateDirectory(dataDir); } catch { }
        EnsureBackup();

        menu = new ContextMenuStrip();
        ToolStripMenuItem header = new ToolStripMenuItem("FringeFree");
        header.Enabled = false;
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        foreach (Preset p in presets)
        {
            Preset captured = p;
            ToolStripMenuItem it = new ToolStripMenuItem(p.Name);
            it.ToolTipText = p.Description;
            it.Click += delegate { ApplyPreset(captured, true); };
            presetItems.Add(it);
            menu.Items.Add(it);
        }

        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem restore = new ToolStripMenuItem("Restore my original settings");
        restore.Click += delegate { RestoreOriginal(); };
        menu.Items.Add(restore);

        ToolStripMenuItem tuner = new ToolStripMenuItem("Open Windows ClearType tuner");
        tuner.Click += delegate { try { Process.Start("cttune.exe"); } catch { } };
        menu.Items.Add(tuner);

        menu.Items.Add(new ToolStripSeparator());
        braveItem = new ToolStripMenuItem("Brave: force grayscale text (patch shortcuts)");
        braveItem.ToolTipText = "Adds --disable-lcd-text to Brave's shortcuts so Brave never uses colour subpixel text.";
        braveItem.Click += delegate { ToggleBrave(); };
        menu.Items.Add(braveItem);

        ToolStripMenuItem braveRestart = new ToolStripMenuItem("Restart Brave now");
        braveRestart.Click += delegate { RestartBrave(); };
        menu.Items.Add(braveRestart);

        menu.Items.Add(new ToolStripSeparator());
        startupItem = new ToolStripMenuItem("Start with Windows");
        startupItem.Click += delegate { ToggleStartup(); };
        menu.Items.Add(startupItem);

        ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
        exit.Click += delegate { ExitApp(); };
        menu.Items.Add(exit);

        menu.Opening += delegate { RefreshChecks(); };

        tray = new NotifyIcon();
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        // Left click opens the same menu as right click.
        tray.MouseUp += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                MethodInfo mi = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(tray, null);
            }
        };

        RefreshChecks();

        // If the exe was renamed or moved, keep the "Start with Windows" entry pointing at it.
        if (IsStartupEnabled())
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                    k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
            }
            catch { }
        }

        // Brave updates can recreate its shortcuts; quietly re-apply the flag if it is switched on.
        if (BravePref()) { try { PatchBraveShortcuts(true); } catch { } }
    }

    // ---------- Brave support ----------

    static bool BravePref()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(PrefKey))
                return k != null && Convert.ToInt32(k.GetValue("BraveGrayscale", 0), CultureInfo.InvariantCulture) == 1;
        }
        catch { return false; }
    }

    static void SetBravePref(bool on)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(PrefKey))
            k.SetValue("BraveGrayscale", on ? 1 : 0, RegistryValueKind.DWord);
    }

    static object ComGet(object o, string name)
    { return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null); }
    static void ComSet(object o, string name, object v)
    { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new object[] { v }); }

    static List<string> ShortcutFolders(out List<bool> recursive)
    {
        List<bool> rec = new List<bool>();
        recursive = rec;
        List<string> dirs = new List<string>();
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Action<string, bool> add = delegate(string p, bool r)
        { if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) { dirs.Add(p); rec.Add(r); } };
        add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false);
        add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false);
        add(Environment.GetFolderPath(Environment.SpecialFolder.Programs), true);
        add(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true);
        add(Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), false);
        add(Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu"), false);
        return dirs;
    }

    // Adds or removes the flag on every Brave .lnk we can write to. Returns how many were changed.
    static int PatchBraveShortcuts(bool on)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) throw new InvalidOperationException("WScript.Shell is not available.");
        object shell = Activator.CreateInstance(t);
        int changed = 0;
        List<bool> rec;
        List<string> dirs = ShortcutFolders(out rec);
        for (int i = 0; i < dirs.Count; i++)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(dirs[i], "*.lnk",
                    rec[i] ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            }
            catch { continue; }
            foreach (string f in files)
            {
                try
                {
                    object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { f });
                    string target = Convert.ToString(ComGet(sc, "TargetPath"));
                    if (!target.EndsWith("brave.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string args = Convert.ToString(ComGet(sc, "Arguments")) ?? "";
                    bool has = args.IndexOf(BraveFlag, StringComparison.OrdinalIgnoreCase) >= 0;
                    string newArgs = args;
                    if (on && !has) newArgs = (args + " " + BraveFlag).Trim();
                    else if (!on && has)
                        newArgs = args.Replace(BraveFlag, "").Replace("  ", " ").Trim();
                    if (newArgs == args) continue;
                    ComSet(sc, "Arguments", newArgs);
                    sc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                    changed++;
                }
                catch { /* not writable (e.g. all-users folder without admin): skip */ }
            }
        }
        return changed;
    }

    void ToggleBrave()
    {
        bool turnOn = !BravePref();
        try
        {
            int n = PatchBraveShortcuts(turnOn);
            SetBravePref(turnOn);
            RefreshChecks();
            string msg = turnOn
                ? "Flag added to " + n + " Brave shortcut(s). Use \"Restart Brave now\", then launch Brave from a shortcut (taskbar, desktop or Start)."
                : "Flag removed from " + n + " Brave shortcut(s). Restart Brave to go back to normal.";
            if (turnOn && n == 0)
                msg = "No writable Brave shortcuts were found. Pin or create a Brave shortcut, then toggle this again.";
            tray.ShowBalloonTip(7000, "FringeFree: Brave", msg, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(5000, "FringeFree: Brave", ex.Message, ToolTipIcon.Error);
        }
    }

    static string FindBraveExe()
    {
        string[] candidates = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
        };
        foreach (string c in candidates) if (File.Exists(c)) return c;
        try
        {
            foreach (Process p in Process.GetProcessesByName("brave"))
            {
                try { return p.MainModule.FileName; } catch { }
            }
        }
        catch { }
        return null;
    }

    void RestartBrave()
    {
        string exe = FindBraveExe();
        if (exe == null)
        {
            tray.ShowBalloonTip(4000, "FringeFree: Brave", "Could not find brave.exe.", ToolTipIcon.Warning);
            return;
        }
        DialogResult r = MessageBox.Show(
            "Brave will be closed and reopened" + (BravePref() ? " with colour-free text enabled" : "") + ".\n\n" +
            "Tabs only come back if Brave's startup setting is \"Continue where you left off\". " +
            "Save anything important first.\n\nRestart Brave now?",
            "FringeFree", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        try
        {
            foreach (Process p in Process.GetProcessesByName("brave"))
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
            }
            for (int i = 0; i < 40 && Process.GetProcessesByName("brave").Length > 0; i++) Thread.Sleep(250);
            // Brave may keep background processes alive; a new launch would join them and ignore the flag.
            foreach (Process p in Process.GetProcessesByName("brave"))
            {
                try { p.Kill(); } catch { }
            }
            Thread.Sleep(600);
            string args = BravePref() ? BraveFlag : "";
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(5000, "FringeFree: Brave", ex.Message, ToolTipIcon.Error);
        }
    }

    // ---------- registry helpers ----------

    static int ReadSmoothingType()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(DesktopKey))
            {
                object v = k == null ? null : k.GetValue("FontSmoothingType");
                if (v != null) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            }
        }
        catch { }
        return 2;
    }

    static int ReadLevel()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AvalonKey + @"\DISPLAY1"))
            {
                object v = k == null ? null : k.GetValue("ClearTypeLevel");
                if (v != null) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            }
        }
        catch { }
        return 100;
    }

    static List<string> AvalonDisplays()
    {
        List<string> names = new List<string>();
        names.Add("DISPLAY1");
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AvalonKey))
            {
                if (k != null)
                    foreach (string n in k.GetSubKeyNames())
                        if (n.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) && !names.Contains(n))
                            names.Add(n);
            }
        }
        catch { }
        return names;
    }

    // ---------- backup / restore ----------

    void EnsureBackup()
    {
        try
        {
            if (File.Exists(backupFile)) return;
            List<string> lines = new List<string>();
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(DesktopKey))
            {
                lines.Add("FontSmoothing=" + Stringify(k == null ? null : k.GetValue("FontSmoothing")));
                lines.Add("FontSmoothingType=" + Stringify(k == null ? null : k.GetValue("FontSmoothingType")));
            }
            foreach (string d in AvalonDisplays())
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AvalonKey + "\\" + d))
                    lines.Add("Level:" + d + "=" + Stringify(k == null ? null : k.GetValue("ClearTypeLevel")));
            }
            File.WriteAllLines(backupFile, lines.ToArray());
        }
        catch { }
    }

    static string Stringify(object v) { return v == null ? "null" : Convert.ToString(v, CultureInfo.InvariantCulture); }

    void RestoreOriginal()
    {
        try
        {
            if (!File.Exists(backupFile))
            {
                tray.ShowBalloonTip(3000, "FringeFree", "No backup found.", ToolTipIcon.Warning);
                return;
            }
            string fs = null, fst = null;
            Dictionary<string, string> levels = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(backupFile))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string key = line.Substring(0, i), val = line.Substring(i + 1);
                if (key == "FontSmoothing") fs = val;
                else if (key == "FontSmoothingType") fst = val;
                else if (key.StartsWith("Level:")) levels[key.Substring(6)] = val;
            }

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(DesktopKey))
            {
                if (fs == null || fs == "null") k.DeleteValue("FontSmoothing", false);
                else k.SetValue("FontSmoothing", fs, RegistryValueKind.String);
                if (fst == null || fst == "null") k.DeleteValue("FontSmoothingType", false);
                else k.SetValue("FontSmoothingType", int.Parse(fst, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
            }
            foreach (KeyValuePair<string, string> kv in levels)
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(AvalonKey + "\\" + kv.Key))
                {
                    if (kv.Value == "null") k.DeleteValue("ClearTypeLevel", false);
                    else k.SetValue("ClearTypeLevel", int.Parse(kv.Value, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                }
            }
            int t = (fst == null || fst == "null") ? 2 : int.Parse(fst, CultureInfo.InvariantCulture);
            Broadcast(t);
            RefreshChecks();
            tray.ShowBalloonTip(5000, "FringeFree", "Original settings restored. " + RestartHint(), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(5000, "FringeFree", "Restore failed: " + ex.Message, ToolTipIcon.Error);
        }
    }

    // ---------- applying presets ----------

    void ApplyPreset(Preset p, bool notify)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(DesktopKey))
            {
                k.SetValue("FontSmoothing", "2", RegistryValueKind.String);
                k.SetValue("FontSmoothingType", p.SmoothingType, RegistryValueKind.DWord);
            }
            foreach (string d in AvalonDisplays())
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(AvalonKey + "\\" + d))
                    k.SetValue("ClearTypeLevel", p.Level, RegistryValueKind.DWord);
            }
            Broadcast(p.SmoothingType);
            RefreshChecks();
            if (notify)
                tray.ShowBalloonTip(6000, "FringeFree: " + p.Name, RestartHint(), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(5000, "FringeFree", "Could not apply: " + ex.Message, ToolTipIcon.Error);
        }
    }

    static void Broadcast(int smoothingType)
    {
        // Updates the live system parameters and notifies running windows.
        SystemParametersInfo(SPI_SETFONTSMOOTHING, 1, IntPtr.Zero, SPIF_UPDATE_AND_BROADCAST);
        SystemParametersInfo(SPI_SETFONTSMOOTHINGTYPE, 0, (IntPtr)smoothingType, SPIF_UPDATE_AND_BROADCAST);
    }

    static string RestartHint()
    {
        List<string> running = new List<string>();
        string[,] browsers = {
            { "brave", "Brave" }, { "chrome", "Chrome" }, { "msedge", "Edge" },
            { "firefox", "Firefox" }, { "vivaldi", "Vivaldi" }, { "opera", "Opera" }
        };
        for (int i = 0; i < browsers.GetLength(0); i++)
        {
            try { if (Process.GetProcessesByName(browsers[i, 0]).Length > 0) running.Add(browsers[i, 1]); }
            catch { }
        }
        if (running.Count == 0) return "Applied. Some apps need a restart to pick it up.";
        return "Applied. Fully close and reopen " + string.Join(", ", running.ToArray()) +
               " (and any app that still looks fringed) to pick it up.";
    }

    // ---------- UI state ----------

    void RefreshChecks()
    {
        int type = ReadSmoothingType();
        int level = ReadLevel();
        Preset active = null;
        for (int i = 0; i < presets.Length; i++)
        {
            bool match = presets[i].SmoothingType == type && presets[i].Level == level;
            presetItems[i].Checked = match;
            if (match) active = presets[i];
        }
        startupItem.Checked = IsStartupEnabled();
        braveItem.Checked = BravePref();
        SetIcon(active == null ? Color.FromArgb(150, 60, 160) : active.IconColor);
        tray.Text = "FringeFree - " + (active == null ? "custom settings" : ShortName(active));
    }

    static string ShortName(Preset p)
    {
        int i = p.Name.IndexOf(" (");
        return i > 0 ? p.Name.Substring(0, i) : p.Name;
    }

    void SetIcon(Color c)
    {
        using (Bitmap bmp = new Bitmap(32, 32))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (GraphicsPath path = RoundRect(new Rectangle(1, 1, 30, 30), 7))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, path);
            using (Font f = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString("Aa", f, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
            }
            IntPtr h = bmp.GetHicon();
            Icon ic = Icon.FromHandle(h);
            tray.Icon = ic;
            if (currentHIcon != IntPtr.Zero) DestroyIcon(currentHIcon);
            currentHIcon = h;
        }
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- startup ----------

    static bool IsStartupEnabled()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }
        catch { return false; }
    }

    void ToggleStartup()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (IsStartupEnabled()) k.DeleteValue(RunName, false);
                else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
            }
            RefreshChecks();
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(4000, "FringeFree", ex.Message, ToolTipIcon.Error);
        }
    }

    void ExitApp()
    {
        tray.Visible = false;
        tray.Dispose();
        if (currentHIcon != IntPtr.Zero) DestroyIcon(currentHIcon);
        ExitThread();
    }
}
