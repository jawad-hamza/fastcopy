// FastCopy - fast multi-threaded file copier for Windows (a GUI for robocopy /MT).
// Copyright (c) 2026 Jawad Hamza - https://github.com/jawad-hamza/fastcopy
// Free and open source under the MIT License (see LICENSE).
//
// Build with build.bat - it uses the C# compiler that ships with Windows
// (.NET Framework 4.x), so the result is one portable FastCopy.exe.
// Written for C# 5 (the language level of that compiler).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

[assembly: AssemblyTitle("FastCopy")]
[assembly: AssemblyDescription("Fast multi-threaded file copier - a GUI for robocopy")]
[assembly: AssemblyCompany("Jawad Hamza")]
[assembly: AssemblyProduct("FastCopy")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Jawad Hamza. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace FastCopy
{
    static class App
    {
        public const string Name = "FastCopy";
        public const string Version = "1.0.0";
        public const string Author = "Jawad Hamza";
        public const string RepoUrl = "https://github.com/jawad-hamza/fastcopy";
        public const string AuthorUrl = "https://github.com/jawad-hamza";

        [STAThread]
        static void Main()
        {
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ------------------------------------------------------------------
    // robocopy process: stdout is collected on a background thread into a
    // queue that the UI timer drains, so the window never freezes.
    // ------------------------------------------------------------------
    class RoboRunner
    {
        public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
        Process proc;

        public void Start(string args)
        {
            var psi = new ProcessStartInfo("robocopy.exe", args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            // robocopy writes in the console (OEM) codepage when redirected
            var enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
            proc = new Process();
            proc.StartInfo = psi;
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) Lines.Enqueue(e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Lines.Enqueue(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }

        // True once the process has exited AND all of its output has been read.
        public bool Finished()
        {
            if (proc == null || !proc.HasExited) return false;
            proc.WaitForExit();
            return true;
        }

        public int ExitCode { get { return proc.ExitCode; } }

        public void Kill()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
        }
    }

    class Job
    {
        public string Src, Dst, Base, Preview;
        public string CopyFlags = "";   // added for the copy phase only (the replace/skip answer)
        public int Threads;
    }

    // ------------------------------------------------------------------
    // Settings: %APPDATA%\FastCopy\FastCopy.ini, or FastCopy.ini next to the
    // exe if that file exists (portable mode - e.g. on a USB stick).
    // ------------------------------------------------------------------
    class Settings
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly string FilePath;

        public Settings(bool load)
        {
            string local = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "FastCopy.ini");
            FilePath = File.Exists(local) ? local
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastCopy", "FastCopy.ini");
            if (!load || !File.Exists(FilePath)) return;
            try
            {
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) values[line.Substring(0, i).Trim()] = line.Substring(i + 1);
                }
            }
            catch { }
        }

        public string Get(string key, string def)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : def;
        }

        public int GetInt(string key, int def, int min, int max)
        {
            int v;
            if (!int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) v = def;
            return Math.Max(min, Math.Min(max, v));
        }

        public bool GetBool(string key, bool def)
        {
            string v = Get(key, "");
            return v == "" ? def : v == "1";
        }

        public void Set(string key, string value) { values[key] = (value ?? "").Replace("\r", " ").Replace("\n", " "); }
        public void Set(string key, int value) { values[key] = value.ToString(CultureInfo.InvariantCulture); }
        public void Set(string key, bool value) { values[key] = value ? "1" : "0"; }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sb = new StringBuilder("; FastCopy settings - " + App.RepoUrl + "\r\n");
                foreach (var kv in values) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // Windows integration: keep-awake, taskbar progress, modern folder picker.
    // Every call is wrapped so a missing feature never breaks the app.
    // ------------------------------------------------------------------
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

        public static void KeepAwake(bool on)
        {
            try { SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS); } catch { }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);
    }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eee84"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int state);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    class TaskbarListCom { }

    static class Taskbar
    {
        public const int None = 0, Busy = 1, Normal = 2, Error = 4, Paused = 8;
        static ITaskbarList3 list;
        static bool failed;

        static ITaskbarList3 Get()
        {
            if (list == null && !failed)
            {
                try { list = (ITaskbarList3)new TaskbarListCom(); list.HrInit(); }
                catch { failed = true; list = null; }
            }
            return list;
        }

        public static void State(Form f, int state)
        {
            try { var t = Get(); if (t != null) t.SetProgressState(f.Handle, state); } catch { }
        }

        public static void Value(Form f, double fraction)
        {
            try { var t = Get(); if (t != null) t.SetProgressValue(f.Handle, (ulong)(Math.Max(0, Math.Min(1, fraction)) * 1000), 1000); } catch { }
        }
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem psi, uint hint, out int order);
    }

    // Only the methods up to GetResult are declared; the vtable order must match.
    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr filterSpec);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem psi);
        void GetCurrentSelection(out IShellItem psi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem psi);
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7"), ClassInterface(ClassInterfaceType.None)]
    class FileOpenDialogCom { }

    static class FolderPicker
    {
        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        // Modern Explorer-style picker (address bar, paste a path); falls back to the classic one.
        public static string Pick(IWin32Window owner, string title, string start)
        {
            try
            {
                var dlg = (IFileOpenDialog)new FileOpenDialogCom();
                uint options;
                dlg.GetOptions(out options);
                dlg.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                {
                    try
                    {
                        IShellItem folder;
                        Native.SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out folder);
                        dlg.SetFolder(folder);
                    }
                    catch { }
                }
                if (dlg.Show(owner == null ? IntPtr.Zero : owner.Handle) != 0) return null;   // cancelled
                IShellItem item;
                dlg.GetResult(out item);
                string path;
                item.GetDisplayName(SIGDN_FILESYSPATH, out path);
                return path;
            }
            catch (COMException) { }
            catch (InvalidCastException) { }

            using (var fb = new FolderBrowserDialog())
            {
                fb.Description = title;
                fb.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) fb.SelectedPath = start;
                return fb.ShowDialog(owner) == DialogResult.OK ? fb.SelectedPath : null;
            }
        }
    }

    // ------------------------------------------------------------------
    // Visual bits: the app logo and the round "?" help badge.
    // ------------------------------------------------------------------
    static class Logo
    {
        // Blue rounded square with a white "fast forward" double chevron.
        public static void Draw(Graphics g, RectangleF r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(r.Width, r.Height), rad = s * 0.22f;
            using (var path = new GraphicsPath())
            {
                path.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
                path.AddArc(r.X + s - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
                path.AddArc(r.X + s - rad * 2, r.Y + s - rad * 2, rad * 2, rad * 2, 0, 90);
                path.AddArc(r.X, r.Y + s - rad * 2, rad * 2, rad * 2, 90, 90);
                path.CloseFigure();
                using (var brush = new LinearGradientBrush(new PointF(r.X, r.Y), new PointF(r.X + s, r.Y + s),
                    Color.FromArgb(30, 136, 229), Color.FromArgb(13, 71, 161)))
                    g.FillPath(brush, path);
            }
            using (var pen = new Pen(Color.White, s * 0.11f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                foreach (float dx in new[] { 0.24f, 0.46f })
                {
                    g.DrawLines(pen, new[] {
                        new PointF(r.X + s * dx, r.Y + s * 0.28f),
                        new PointF(r.X + s * (dx + 0.22f), r.Y + s * 0.50f),
                        new PointF(r.X + s * dx, r.Y + s * 0.72f) });
                }
            }
        }
    }

    class LogoBox : Control
    {
        public LogoBox(int size)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size(size, size);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Logo.Draw(e.Graphics, new RectangleF(0, 0, Width - 1, Height - 1));
        }
    }

    class HelpBadge : Control
    {
        bool hot;

        public HelpBadge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.Transparent;
            Size = new Size(17, 17);
            Margin = new Padding(1, 3, 6, 3);
            Anchor = AnchorStyles.Left;
            Cursor = Cursors.Help;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            int d = Math.Min(Width, Height) - 1;
            using (var b = new SolidBrush(hot ? Color.FromArgb(0, 84, 166) : Color.FromArgb(0, 103, 192)))
                g.FillEllipse(b, 0, 0, d, d);
            using (var f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("?", f, Brushes.White, new RectangleF(0, 0.5f, d + 1, d + 1), sf);
        }
    }

    // ------------------------------------------------------------------
    // About window - credits.
    // ------------------------------------------------------------------
    class AboutForm : Form
    {
        public AboutForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "About " + App.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = SystemColors.Window;
            Padding = new Padding(16);

            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(root);

            var logo = new LogoBox(64) { Margin = new Padding(0, 4, 16, 0), Anchor = AnchorStyles.Top };
            root.Controls.Add(logo, 0, 0);

            var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            root.Controls.Add(text, 1, 0);

            text.Controls.Add(new Label { Text = App.Name, AutoSize = true, Font = new Font("Segoe UI", 18F, FontStyle.Bold), Margin = new Padding(0) });
            text.Controls.Add(new Label { Text = "Version " + App.Version + "  -  fast multi-threaded file copier for Windows", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(2, 0, 0, 12) });
            text.Controls.Add(new Label { Text = "Created by " + App.Author, AutoSize = true, Font = new Font("Segoe UI", 10.5F, FontStyle.Bold), Margin = new Padding(2, 0, 0, 2) });
            text.Controls.Add(Link(App.AuthorUrl.Replace("https://", ""), App.AuthorUrl));
            text.Controls.Add(Link(App.RepoUrl.Replace("https://", ""), App.RepoUrl));
            text.Controls.Add(new Label
            {
                Text = "Free and open source under the MIT License.\r\n" +
                       "Free for everyone - personal or commercial use. You can share and\r\n" +
                       "modify it; please keep the credit to the author.",
                AutoSize = true, Margin = new Padding(2, 12, 0, 8)
            });
            text.Controls.Add(new Label
            {
                Text = "The copying itself is done by robocopy, which is built into Windows.",
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(2, 0, 0, 12)
            });

            var ok = new Button { Text = "OK", Size = new Size(90, 30), DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
            root.Controls.Add(ok, 0, 1);
            root.SetColumnSpan(ok, 2);
            AcceptButton = ok; CancelButton = ok;
            ResumeLayout(false);
            PerformLayout();
        }

        static LinkLabel Link(string text, string url)
        {
            var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(2, 1, 0, 1) };
            l.LinkClicked += delegate { try { Process.Start(url); } catch { } };
            return l;
        }
    }

    // ------------------------------------------------------------------
    // "Files already exist" - asked once after the scan, like Windows'
    // replace/skip dialog but for all files at once (copying is parallel).
    // ------------------------------------------------------------------
    class ConflictForm : Form
    {
        public const int Cancel = 0, ReplaceAll = 1, ReplaceOlder = 2, SkipAll = 3;
        public int Choice = Cancel;

        public static string Describe(int choice)
        {
            return choice == ReplaceAll ? "replace all"
                : choice == ReplaceOlder ? "replace only older files (newer files in the destination are kept)"
                : choice == SkipAll ? "skip all (files already in the destination are kept)"
                : "cancelled";
        }

        public ConflictForm(long[] files, long[] bytes, List<string> sample)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "Files already exist - " + App.Name;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(660, 480);
            MinimumSize = new Size(560, 400);

            var tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 150 };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 12, 14, 12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            long conflicts = files[1] + files[2] + files[3];
            var lines = new StringBuilder();
            if (files[1] > 0) lines.AppendFormat("   {0:N0}  -  your file is newer\r\n", files[1]);
            if (files[2] > 0) lines.AppendFormat("   {0:N0}  -  the file in the destination is newer\r\n", files[2]);
            if (files[3] > 0) lines.AppendFormat("   {0:N0}  -  same date, different size\r\n", files[3]);
            lines.AppendFormat("\r\n{0:N0} new file(s) will be copied either way. Identical files are always skipped.", files[0]);

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label
            {
                Text = string.Format("{0:N0} file(s) already exist in the destination and are different.", conflicts),
                AutoSize = true, Font = new Font("Segoe UI", 11F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8)
            }, 0, 0);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = lines.ToString(), AutoSize = true, Margin = new Padding(0, 0, 0, 8) }, 0, 1);

            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, Font = new Font("Consolas", 9F) };
            list.BeginUpdate();
            foreach (string s in sample) list.Items.Add(s);
            if (conflicts > sample.Count) list.Items.Add(string.Format("... and {0:N0} more", conflicts - sample.Count));
            list.EndUpdate();
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(list, 0, 2);

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label
            {
                Text = "Replace all  -  overwrite every file listed with your copy.\r\n" +
                       "Replace older only  -  keep files that are newer in the destination.\r\n" +
                       "Skip all  -  leave every existing file as it is; copy only new files.",
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 8)
            }, 0, 3);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            var replace = Choose("Replace all", ReplaceAll, "Overwrite every file in the list with your copy.", tips);
            var older = Choose("Replace older only", ReplaceOlder, "Overwrite only files where your copy is newer.\nFiles that are newer in the destination are kept.", tips);
            var skip = Choose("Skip all", SkipAll, "Don't touch any existing file. Only files that are missing get copied.", tips);
            var cancel = Choose("Cancel", Cancel, "Stop - nothing gets copied.", tips);
            older.Enabled = files[2] > 0 || files[1] > 0;
            buttons.Controls.AddRange(new Control[] { replace, older, skip, cancel });
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(buttons, 0, 4);
            CancelButton = cancel;
            ActiveControl = skip;   // the safe answer gets the focus
            ResumeLayout(false);
            PerformLayout();
        }

        Button Choose(string text, int choice, string tip, ToolTip tips)
        {
            var b = new Button { Text = text, Size = new Size(text.Length > 12 ? 150 : 110, 34), UseVisualStyleBackColor = true, DialogResult = DialogResult.OK };
            if (choice == Cancel) b.DialogResult = DialogResult.Cancel;
            b.Click += delegate { Choice = choice; };
            tips.SetToolTip(b, tip);
            return b;
        }
    }

    // ------------------------------------------------------------------
    // Main window
    // ------------------------------------------------------------------
    class MainForm : Form
    {
        // "  New File    1002  C:\path\file.txt"  -> class, size, path
        static readonly Regex FileLineRx = new Regex(@"^\s*(New File|Newer|Older|Changed|Tweaked|Modified|Same|\*EXTRA File)\s+(\d+)\s+(\S.*)$", RegexOptions.Compiled);
        static readonly Regex ErrorRx = new Regex(@"ERROR \d+ \(0x[0-9A-Fa-f]+\)\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex TokenRx = new Regex("\"([^\"]+)\"|([^\\s;,]+)", RegexOptions.Compiled);
        static readonly Color Red = Color.Firebrick, Green = Color.DarkGreen, Orange = Color.DarkOrange, Dim = Color.DimGray;

        const int Success = 0, Stopped = 1, Failed = 2;

        readonly ToolTip tips = new ToolTip();
        readonly Timer timer = new Timer();
        readonly Settings settings = new Settings(true);
        readonly List<Control> optionPanels = new List<Control>();

        TextBox txtFrom, txtTo, txtCmd, txtOnly, txtXF, txtXD, txtExtra;
        Button btnFrom, btnTo, btnCopyCmd, btnStart, btnStop, btnOpen, btnAbout, btnReset, btnSaveLog, btnClearLog;
        TabControl tabs;
        TabPage pgLog;
        TrackBar trkThreads;
        NumericUpDown numThreads, numRetry, numWait, numMin, numMax, numAge;
        Label lblThreadsNote, lblStat1, lblStat2, lblFile;
        CheckBox chkJ, chkZ, chkKeep, chkEmpty, chkXJ;
        RadioButton rbCopy, rbMirror, rbMove;
        ComboBox cmbExisting, cmbDone;
        RichTextBox log;
        ProgressBar bar;

        // job state
        RoboRunner runner;
        Job job;
        string phase;
        bool busy, stopRequested, inSummary;
        long totalFiles, totalBytes, extras, doneFiles, doneBytes;
        // scan results by robocopy class: 0 new, 1 source newer, 2 source older, 3 changed
        readonly long[] classFiles = new long[4], classBytes = new long[4];
        List<string> conflicts;
        HashSet<string> errors;
        Queue<double[]> samples;
        Stopwatch clock;

        public MainForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = App.Name;
            ClientSize = new Size(760, 600);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            tips.AutoPopDelay = 30000;
            tips.InitialDelay = 150;
            tips.ReshowDelay = 50;
            tips.ShowAlways = true;
            timer.Interval = 250;
            timer.Tick += OnTick;

            BuildUi();
            ResumeLayout(false);
            PerformLayout();

            ApplySettings(settings, true);
            WireEvents();
            UpdatePreview();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            MinimumSize = new Size((int)(620 * scale), (int)(520 * scale));
        }

        // ---------------- layout helpers ----------------

        static Label Lbl(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 7) };
        }

        static Label Note(string text)
        {
            var l = Lbl(text);
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(3, 0, 3, 8);
            return l;
        }

        static NumericUpDown Num(int min, int max, int width)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Width = width, TextAlign = HorizontalAlignment.Center, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 14, 4) };
        }

        static Button Btn(string text, int width, int height)
        {
            return new Button { Text = text, Size = new Size(width, height), Anchor = AnchorStyles.Left, UseVisualStyleBackColor = true };
        }

        static CheckBox Chk(string text)
        {
            return new CheckBox { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 3, 5) };
        }

        static TextBox Txt()
        {
            return new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 5, 3, 5) };
        }

        static FlowLayoutPanel Flow(params Control[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            f.Controls.AddRange(items);
            return f;
        }

        HelpBadge Badge(string text)
        {
            var b = new HelpBadge();
            tips.SetToolTip(b, text);
            b.Click += delegate { tips.Show(text, b, 0, b.Height + 2, 30000); };
            return b;
        }

        // Puts a blue "?" right after a control; hovering either shows the help.
        Control Help(Control c, string text)
        {
            tips.SetToolTip(c, text);
            return Flow(c, Badge(text));
        }

        TableLayoutPanel NewPage(string title, bool twoColumns)
        {
            var page = new TabPage(title) { UseVisualStyleBackColor = false, BackColor = SystemColors.Window };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 8, 10, 4), BackColor = SystemColors.Window };
            if (twoColumns)
            {
                t.ColumnCount = 2;
                t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            }
            else
            {
                t.ColumnCount = 1;
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            }
            page.Controls.Add(t);
            tabs.TabPages.Add(page);
            return t;
        }

        static void Row(TableLayoutPanel t, params Control[] cells)
        {
            int r = t.RowCount;
            t.RowCount = r + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 0; i < cells.Length; i++)
                if (cells[i] != null) t.Controls.Add(cells[i], i, r);
            if (cells.Length == 1 && t.ColumnCount > 1) t.SetColumnSpan(cells[0], t.ColumnCount);
        }

        // A last stretchy row keeps the real rows packed at the top.
        static void EndRows(TableLayoutPanel t)
        {
            t.RowCount += 1;
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        }

        // ---------------- UI ----------------

        void BuildUi()
        {
            // The window is one 3-column table (label | stretch | button); only the tabs row grows.
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(root);

            txtFrom = Txt(); txtTo = Txt();
            btnFrom = Btn("Browse...", 94, 28); btnTo = Btn("Browse...", 94, 28);
            Row(root, Lbl("From"), txtFrom, btnFrom);
            Row(root, Lbl("To"), txtTo, btnTo);

            tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 6) };
            root.RowCount += 1;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(tabs, 0, root.RowCount - 1);
            root.SetColumnSpan(tabs, 3);

            BuildSpeedPage();
            BuildModePage();
            BuildFiltersPage();
            BuildMorePage();
            BuildLogPage();

            txtCmd = Txt();
            txtCmd.ReadOnly = true;
            txtCmd.Font = new Font("Consolas", 8.5F);
            btnCopyCmd = Btn("Copy cmd", 94, 28);
            Row(root, Lbl("Command"), txtCmd, btnCopyCmd);

            btnStart = Btn("Start copy", 150, 38);
            btnStart.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            btnStop = Btn("Stop", 90, 38);
            btnStop.Enabled = false;
            btnOpen = Btn("Open destination", 140, 38);
            btnOpen.Enabled = false;
            btnAbout = Btn("About", 90, 38);
            btnAbout.Anchor = AnchorStyles.Right;
            var actions = new TableLayoutPanel { AutoSize = true, ColumnCount = 5, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            for (int i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.Controls.Add(btnStart, 0, 0);
            actions.Controls.Add(btnStop, 1, 0);
            actions.Controls.Add(btnOpen, 2, 0);
            actions.Controls.Add(btnAbout, 4, 0);
            Row(root, actions);

            bar = new ProgressBar { Maximum = 1000, Height = 22, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 4, 3, 4) };
            Row(root, bar);
            lblStat1 = StatLine(root, "Pick a source and a destination, then press Start copy.");
            lblStat1.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            lblStat2 = StatLine(root, "");
            lblFile = StatLine(root, "");
            lblFile.ForeColor = SystemColors.GrayText;

            tips.SetToolTip(txtFrom, "The folder you want to copy. You can also drag a folder here from Explorer.");
            tips.SetToolTip(txtTo, "Where the copy should go. Drag a folder here, or type a new folder name - it will be created.");
            tips.SetToolTip(btnFrom, "Pick the folder to copy.");
            tips.SetToolTip(btnTo, "Pick where to copy it.");
            tips.SetToolTip(txtCmd, "The exact robocopy command FastCopy will run. It updates live as you change options.");
            tips.SetToolTip(btnCopyCmd, "Copy the command to the clipboard, e.g. to reuse it in a script.");
            tips.SetToolTip(btnStart, "Counts the files first (so progress and time-left are accurate), then copies.");
            tips.SetToolTip(btnStop, "Stop right now. Press Start again later to continue - files already copied are skipped.");
            tips.SetToolTip(btnOpen, "Open the destination folder in Explorer.");
            tips.SetToolTip(btnAbout, "About FastCopy and its author.");
        }

        Label StatLine(TableLayoutPanel root, string text)
        {
            var l = new Label { Text = text, AutoSize = false, AutoEllipsis = true, Height = 22, Anchor = AnchorStyles.Left | AnchorStyles.Right, TextAlign = ContentAlignment.MiddleLeft };
            Row(root, l);
            return l;
        }

        void BuildSpeedPage()
        {
            var t = NewPage("Speed", false);
            optionPanels.Add(t);

            Row(t, Help(Lbl("Parallel copies (threads)"),
                "How many files are copied at the same time.\n\n" +
                "Windows Explorer copies one file at a time, so folders with lots of small\n" +
                "files (photos, documents, code) crawl. More threads = more files moving at once.\n\n" +
                "Good starting points:\n" +
                "   USB pen drive / SD card:  8 - 32\n" +
                "   External SSD:  32 - 64\n" +
                "   Internal SSD / NVMe:  64 - 128\n\n" +
                "It can never beat the speed of the cable or the drive itself.\n" +
                "If raising it stops helping, you've hit that limit."));

            trkThreads = new TrackBar { Minimum = 1, Maximum = 128, TickFrequency = 8, SmallChange = 1, LargeChange = 8, BackColor = SystemColors.Window, Anchor = AnchorStyles.Left | AnchorStyles.Right };
            numThreads = Num(1, 128, 70);
            lblThreadsNote = Lbl("");
            lblThreadsNote.ForeColor = SystemColors.GrayText;
            tips.SetToolTip(trkThreads, "Drag to choose how many files are copied at the same time.");
            tips.SetToolTip(numThreads, "Type an exact number of threads (1 - 128).");
            var line = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            line.Controls.Add(trkThreads, 0, 0);
            line.Controls.Add(numThreads, 1, 0);
            line.Controls.Add(lblThreadsNote, 2, 0);
            Row(t, line);

            var presets = Flow();
            foreach (int v in new[] { 1, 8, 16, 32, 64, 128 })
            {
                int value = v;
                var b = Btn(v == 128 ? "MAX (128)" : v.ToString(), v == 128 ? 92 : 52, 28);
                b.Click += delegate { numThreads.Value = value; };
                tips.SetToolTip(b, v == 1 ? "1 file at a time - same as Windows Explorer."
                    : v == 128 ? "128 is the most robocopy allows. Best for fast SSDs with huge numbers of small files."
                    : "Copy " + v + " files at the same time.");
                presets.Controls.Add(b);
            }
            Row(t, presets);
            Row(t, Note("How many files are copied at the same time. 1 = one by one, like Explorer.\n" +
                        "Pen drives / SD cards: 8-32.  SSD or NVMe: 32-128.  The cable speed is still the ceiling."));

            numRetry = Num(0, 99, 60);
            numWait = Num(0, 600, 60);
            Row(t, Flow(
                Help(Lbl("Retries"),
                    "If a file can't be copied (for example it's open in another program),\n" +
                    "how many times to try again before skipping it.\n\n" +
                    "robocopy's own default is 1,000,000 retries, which looks like a freeze.\n" +
                    "1 is a sensible choice."),
                numRetry,
                Help(Lbl("Wait (sec)"), "How many seconds to wait before each retry."),
                numWait));

            chkJ = Chk("Unbuffered I/O (/J) - faster for very large files");
            Row(t, Help(chkJ,
                "Copies straight to the drive, skipping Windows' memory cache.\n\n" +
                "Turn ON for big files: videos, ISO images, backups of several GB.\n" +
                "Leave OFF for lots of small files (photos, documents) - it slows those down."));
            chkZ = Chk("Restartable (/Z) - resumes mid-file after unplug, slower");
            Row(t, Help(chkZ,
                "If the drive gets unplugged halfway through a big file, the next run\n" +
                "continues that file where it stopped instead of starting it again.\n\n" +
                "Makes copying noticeably slower. Only worth it for huge files on a\n" +
                "loose cable or flaky connection. Normally leave it OFF."));
            EndRows(t);
        }

        void BuildModePage()
        {
            var t = NewPage("Mode", false);
            optionPanels.Add(t);

            // Radio buttons must share one parent to act as a group.
            var modes = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            modes.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            modes.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rbCopy = new RadioButton { Text = "Copy - add and update files, never delete anything", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 0, 5) };
            rbMirror = new RadioButton { Text = "Mirror - make the destination identical (DELETES extra files in destination)", AutoSize = true, ForeColor = Red, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 0, 5) };
            rbMove = new RadioButton { Text = "Move - copy, then DELETE the originals from the source", AutoSize = true, ForeColor = Red, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 0, 5) };
            AddMode(modes, rbCopy, "The safe choice.\n\n" +
                "Copies new and changed files into the destination.\n" +
                "Nothing is ever deleted - not in the source, not in the destination.");
            AddMode(modes, rbMirror, "Makes the destination an exact copy of the source.\n\n" +
                "Files in the destination that are NOT in the source get DELETED.\n" +
                "Great for keeping a backup drive in sync - but if you pick the wrong\n" +
                "destination folder, its files will be removed. Double-check the To box.");
            AddMode(modes, rbMove, "Like Cut and Paste.\n\n" +
                "Each file is copied, then removed from the source.\n" +
                "When it finishes, the files only exist in the destination.");
            Row(t, modes);

            cmbExisting = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, Anchor = AnchorStyles.Left, Margin = new Padding(8, 4, 3, 4) };
            cmbExisting.Items.AddRange(new object[] {
                "Ask me (like Windows: replace or skip)",
                "Replace if changed (size or date differs)",
                "Replace only if source is newer (/XO)",
                "Skip existing files, only add missing ones",
                "Always overwrite everything (/IS /IT)" });
            Row(t, Flow(Help(Lbl("Files already in destination"),
                "What to do when a file with the same name is already in the destination:\n\n" +
                "Ask me - after the scan, FastCopy shows the list of files that already\n" +
                "    exist and lets you choose: replace all, replace older ones, or skip all.\n" +
                "Replace if changed - skip identical files, replace the rest. Fastest when\n" +
                "    you copy the same folder again later (only new stuff gets copied).\n" +
                "Replace only if newer - never replace a newer file in the destination.\n" +
                "Skip existing - only add files that are missing.\n" +
                "Always overwrite - copy everything again, even identical files.\n\n" +
                "Identical files are always skipped (except with 'Always overwrite')."), cmbExisting));

            chkKeep = Chk("Create the source folder inside the destination (D:\\Photos -> E:\\Photos)");
            Row(t, Help(chkKeep,
                "ON:  copying D:\\Photos to E:\\ gives you E:\\Photos\\...\n" +
                "OFF: the files inside Photos go straight into E:\\\n\n" +
                "ON is what Windows Explorer does when you drag a folder."));
            chkEmpty = Chk("Copy empty folders too");
            Row(t, Help(chkEmpty, "Also create folders that have no files in them, so the folder structure matches exactly."));
            chkXJ = Chk("Skip junctions / linked folders (prevents endless loops)");
            Row(t, Help(chkXJ,
                "Some folders are really shortcuts pointing somewhere else (common inside\n" +
                "C:\\Users). Following them can copy the same files twice or loop forever.\n\n" +
                "Leave this ON unless you know you need those links followed."));
            EndRows(t);
        }

        void AddMode(TableLayoutPanel modes, RadioButton rb, string help)
        {
            int r = modes.RowCount;
            modes.RowCount = r + 1;
            modes.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tips.SetToolTip(rb, help);
            modes.Controls.Add(rb, 0, r);
            modes.Controls.Add(Badge(help), 1, r);
        }

        void BuildFiltersPage()
        {
            var t = NewPage("Filters", true);
            optionPanels.Add(t);

            txtOnly = Txt(); txtXF = Txt(); txtXD = Txt();
            Row(t, Help(Lbl("Only these files"),
                "Copy ONLY files whose names match. Everything else is skipped.\n\n" +
                "Examples:\n   *.jpg *.png       (only pictures)\n   *.mp4             (only videos)\n   report*.docx\n\n" +
                "* means 'anything'. Leave empty to copy all files."), txtOnly);
            Row(t, Help(Lbl("Exclude files"), "Files to skip.\n\nExamples:\n   *.tmp  Thumbs.db  desktop.ini  *.log"), txtXF);
            Row(t, Help(Lbl("Exclude folders"),
                "Folders to skip, by name, wherever they appear inside the source.\n\n" +
                "Examples:\n   node_modules  .git  Cache  \"Old Backups\""), txtXD);
            Row(t, null, Note("Separate with spaces or ;   Wildcards * ? work.   Quote names with spaces: \"My Folder\""));

            numMin = Num(0, 100000000, 90);
            numMax = Num(0, 100000000, 90);
            numAge = Num(0, 100000, 70);
            Row(t, Flow(
                Help(Lbl("Min size (KB)"), "Skip files smaller than this many KB.\n1024 KB = 1 MB.   0 = no minimum."), numMin,
                Help(Lbl("Max size (MB)"), "Skip files bigger than this many MB.\n1024 MB = 1 GB.   0 = no maximum."), numMax,
                Help(Lbl("Max age (days)"), "Only copy files changed in the last N days.\nExample: 7 = only this week's files.   0 = all files."), numAge));
            Row(t, Note("0 = no limit."));
            EndRows(t);
        }

        void BuildMorePage()
        {
            var t = NewPage("More", true);
            optionPanels.Add(t);

            txtExtra = Txt();
            Row(t, Help(Lbl("Extra robocopy flags"),
                "For advanced users only - leave empty if unsure.\n\n" +
                "Any extra robocopy switches, added to the end of the command.\n" +
                "Open a terminal and run  robocopy /?  to see them all."), txtExtra);
            Row(t, null, Note("Added to the command as-is, e.g.  /COPY:DATS  /XA:H  /IPG:50\n" +
                              "Avoid /NFL /NJS /QUIET /V - they break the progress display."));
            cmbDone = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4) };
            cmbDone.Items.AddRange(new object[] { "Do nothing", "Play a sound", "Open the destination folder" });
            Row(t, Help(Lbl("When finished"), "What FastCopy should do after a copy finishes without errors."), cmbDone);
            btnReset = Btn("Reset all options", 160, 30);
            btnReset.Margin = new Padding(3, 12, 3, 3);
            tips.SetToolTip(btnReset, "Put every option back to its default. Your From/To folders are kept.");
            Row(t, btnReset);
            EndRows(t);
        }

        void BuildLogPage()
        {
            pgLog = new TabPage("Log") { UseVisualStyleBackColor = false, BackColor = SystemColors.Window };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = SystemColors.Window };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, Font = new Font("Consolas", 9F), DetectUrls = false };
            btnSaveLog = Btn("Save log...", 110, 28);
            btnClearLog = Btn("Clear", 80, 28);
            tips.SetToolTip(btnSaveLog, "Save everything in the log to a text file.");
            t.Controls.Add(log, 0, 0);
            t.Controls.Add(Flow(btnSaveLog, btnClearLog), 0, 1);
            pgLog.Controls.Add(t);
            tabs.TabPages.Add(pgLog);
        }

        // ---------------- settings ----------------

        void ApplySettings(Settings s, bool includeFolders)
        {
            if (includeFolders)
            {
                txtFrom.Text = s.Get("From", "");
                txtTo.Text = s.Get("To", "");
            }
            numThreads.Value = s.GetInt("Threads", 16, 1, 128);
            trkThreads.Value = (int)numThreads.Value;
            numRetry.Value = s.GetInt("Retry", 1, 0, 99);
            numWait.Value = s.GetInt("Wait", 1, 0, 600);
            chkJ.Checked = s.GetBool("Unbuffered", false);
            chkZ.Checked = s.GetBool("Restartable", false);
            string mode = s.Get("Mode", "copy");
            if (mode == "mirror") rbMirror.Checked = true;
            else if (mode == "move") rbMove.Checked = true;
            else rbCopy.Checked = true;
            cmbExisting.SelectedIndex = s.GetInt("Existing", 0, 0, cmbExisting.Items.Count - 1);
            chkKeep.Checked = s.GetBool("KeepFolder", true);
            chkEmpty.Checked = s.GetBool("EmptyFolders", true);
            chkXJ.Checked = s.GetBool("SkipJunctions", true);
            txtOnly.Text = s.Get("Only", "");
            txtXF.Text = s.Get("ExcludeFiles", "");
            txtXD.Text = s.Get("ExcludeFolders", "");
            numMin.Value = s.GetInt("MinKB", 0, 0, 100000000);
            numMax.Value = s.GetInt("MaxMB", 0, 0, 100000000);
            numAge.Value = s.GetInt("MaxAgeDays", 0, 0, 100000);
            txtExtra.Text = s.Get("Extra", "");
            cmbDone.SelectedIndex = s.GetInt("WhenDone", 0, 0, cmbDone.Items.Count - 1);
        }

        void SaveSettings()
        {
            var s = settings;
            s.Set("From", txtFrom.Text);
            s.Set("To", txtTo.Text);
            s.Set("Threads", (int)numThreads.Value);
            s.Set("Retry", (int)numRetry.Value);
            s.Set("Wait", (int)numWait.Value);
            s.Set("Unbuffered", chkJ.Checked);
            s.Set("Restartable", chkZ.Checked);
            s.Set("Mode", rbMirror.Checked ? "mirror" : rbMove.Checked ? "move" : "copy");
            s.Set("Existing", cmbExisting.SelectedIndex);
            s.Set("KeepFolder", chkKeep.Checked);
            s.Set("EmptyFolders", chkEmpty.Checked);
            s.Set("SkipJunctions", chkXJ.Checked);
            s.Set("Only", txtOnly.Text);
            s.Set("ExcludeFiles", txtXF.Text);
            s.Set("ExcludeFolders", txtXD.Text);
            s.Set("MinKB", (int)numMin.Value);
            s.Set("MaxMB", (int)numMax.Value);
            s.Set("MaxAgeDays", (int)numAge.Value);
            s.Set("Extra", txtExtra.Text);
            s.Set("WhenDone", cmbDone.SelectedIndex);
            s.Save();
        }

        // ---------------- events ----------------

        void WireEvents()
        {
            foreach (var box in new[] { txtFrom, txtTo })
            {
                var target = box;
                target.AllowDrop = true;
                target.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
                target.DragDrop += (s, e) =>
                {
                    var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files == null || files.Length == 0) return;
                    string p = files[0];
                    if (File.Exists(p)) p = Path.GetDirectoryName(p);
                    target.Text = p;
                };
            }
            btnFrom.Click += delegate { Pick(txtFrom, "Copy FROM this folder"); };
            btnTo.Click += delegate { Pick(txtTo, "Copy TO this folder"); };

            trkThreads.ValueChanged += delegate { numThreads.Value = trkThreads.Value; };
            numThreads.ValueChanged += delegate { trkThreads.Value = (int)numThreads.Value; UpdatePreview(); };

            EventHandler preview = delegate { UpdatePreview(); };
            foreach (var c in new[] { txtFrom, txtTo, txtOnly, txtXF, txtXD, txtExtra }) c.TextChanged += preview;
            foreach (var n in new[] { numRetry, numWait, numMin, numMax, numAge }) n.ValueChanged += preview;
            foreach (var c in new CheckBox[] { chkJ, chkZ, chkKeep, chkEmpty, chkXJ }) c.CheckedChanged += preview;
            foreach (var r in new[] { rbCopy, rbMirror, rbMove }) r.CheckedChanged += preview;
            cmbExisting.SelectedIndexChanged += preview;

            btnReset.Click += delegate { ApplySettings(new Settings(false), false); UpdatePreview(); };
            btnCopyCmd.Click += delegate { try { Clipboard.SetText(txtCmd.Text); } catch { } };
            btnSaveLog.Click += delegate { SaveLog(); };
            btnClearLog.Click += delegate { log.Clear(); };
            btnStart.Click += delegate { StartCopy(); };
            btnStop.Click += delegate { stopRequested = true; if (runner != null) runner.Kill(); };
            btnOpen.Click += delegate { OpenFolder(job == null ? null : job.Dst); };
            btnAbout.Click += delegate { using (var a = new AboutForm()) a.ShowDialog(this); };
        }

        void Pick(TextBox box, string title)
        {
            string start = box.Text.Trim().Trim('"');
            if (start.Length == 0 && box == txtTo) start = txtFrom.Text.Trim().Trim('"');
            string path = FolderPicker.Pick(this, title, start);
            if (path != null) box.Text = path;
        }

        static void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            try { Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
        }

        void SaveLog()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "Text file (*.txt)|*.txt";
                dlg.FileName = "FastCopy-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dlg.FileName, log.Text.Replace("\n", "\r\n"), Encoding.UTF8); }
                catch (Exception ex) { Warn("Could not save the log: " + ex.Message); }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                if (!Ask("A copy is running. Stop it and close FastCopy?", "FastCopy")) { e.Cancel = true; return; }
                stopRequested = true;
                if (runner != null) runner.Kill();
                timer.Stop();
                Native.KeepAwake(false);
            }
            SaveSettings();
            base.OnFormClosing(e);
        }

        void Warn(string text) { MessageBox.Show(this, text, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        bool Ask(string text, string title)
        {
            return MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        void Log(string text, Color color)
        {
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.SelectionColor = color;
            log.AppendText(text + "\n");
            log.ScrollToCaret();
        }

        void Log(string text) { Log(text, SystemColors.WindowText); }

        // ---------------- robocopy command ----------------

        static string QuotePath(string p)
        {
            // A path ending in "\" would escape robocopy's closing quote ("E:\" -> "E:\.").
            if (p.EndsWith("\\")) p += ".";
            return "\"" + p + "\"";
        }

        // "*.jpg; *.png" or "node_modules .git" or "\"My Folder\"" -> names
        static List<string> Tokens(string s)
        {
            var list = new List<string>();
            foreach (Match m in TokenRx.Matches(s ?? ""))
                list.Add((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).TrimEnd('\\'));
            return list;
        }

        Job BuildJob()
        {
            string src = txtFrom.Text.Trim().Trim('"');
            string dst = txtTo.Text.Trim().Trim('"');
            try { if (src.Length > 0) src = Path.GetFullPath(src); } catch { }
            try { if (dst.Length > 0) dst = Path.GetFullPath(dst); } catch { }
            if (src.Length > 0 && dst.Length > 0 && chkKeep.Checked)
            {
                string leaf = Path.GetFileName(src.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(leaf)) { try { dst = Path.Combine(dst, leaf); } catch { } }
            }

            var a = new List<string>();
            a.Add(QuotePath(src.Length > 0 ? src : "<from>"));
            a.Add(QuotePath(dst.Length > 0 ? dst : "<to>"));
            foreach (string t in Tokens(txtOnly.Text)) a.Add("\"" + t + "\"");

            if (rbMirror.Checked) a.Add("/MIR");
            else a.Add(chkEmpty.Checked ? "/E" : "/S");
            if (rbMove.Checked) a.Add("/MOVE");
            switch (cmbExisting.SelectedIndex)
            {
                // 0 = ask: scan everything, the choice is added after the scan (Job.CopyFlags)
                case 2: a.Add("/XO"); break;
                case 3: a.Add("/XC"); a.Add("/XN"); a.Add("/XO"); break;
                case 4: a.Add("/IS"); a.Add("/IT"); break;
            }
            a.Add("/R:" + (int)numRetry.Value);
            a.Add("/W:" + (int)numWait.Value);
            if (chkJ.Checked) a.Add("/J");
            if (chkZ.Checked) a.Add("/Z");
            if (chkXJ.Checked) a.Add("/XJ");

            var xf = Tokens(txtXF.Text);
            if (xf.Count > 0) { a.Add("/XF"); foreach (string t in xf) a.Add("\"" + t + "\""); }
            a.Add("/XD"); a.Add("\"System Volume Information\""); a.Add("\"$RECYCLE.BIN\"");
            foreach (string t in Tokens(txtXD.Text)) a.Add("\"" + t + "\"");

            if (numMin.Value > 0) a.Add("/MIN:" + ((long)numMin.Value * 1024L));
            if (numMax.Value > 0) a.Add("/MAX:" + ((long)numMax.Value * 1024L * 1024L));
            if (numAge.Value > 0) a.Add("/MAXAGE:" + (int)numAge.Value);

            a.Add("/BYTES /FP /NDL /NP /NJH");   // output format the progress parser relies on
            if (txtExtra.Text.Trim().Length > 0) a.Add(txtExtra.Text.Trim());

            var j = new Job();
            j.Src = src; j.Dst = dst;
            j.Threads = (int)numThreads.Value;
            j.Base = string.Join(" ", a);
            j.Preview = "robocopy " + j.Base + " /MT:" + j.Threads;
            return j;
        }

        void UpdatePreview()
        {
            txtCmd.Text = BuildJob().Preview;
            chkEmpty.Enabled = !rbMirror.Checked;
            int v = (int)numThreads.Value;
            lblThreadsNote.Text = v == 1 ? "one by one" : v == 128 ? "maximum" : "";
        }

        // ---------------- copy job ----------------

        void SetBusy(bool on)
        {
            busy = on;
            foreach (Control c in new Control[] { txtFrom, txtTo, btnFrom, btnTo, btnStart }) c.Enabled = !on;
            foreach (var p in optionPanels) p.Enabled = !on;
            btnStop.Enabled = on;
            if (!on) UpdatePreview();
        }

        void StartCopy()
        {
            var j = BuildJob();
            if (j.Src.Length == 0 || !Directory.Exists(j.Src)) { Warn("The source folder does not exist."); return; }
            if (j.Dst.Length == 0) { Warn("Choose a destination folder."); return; }
            string sk = j.Src.TrimEnd('\\') + "\\", dk = j.Dst.TrimEnd('\\') + "\\";
            if (dk.StartsWith(sk, StringComparison.OrdinalIgnoreCase))
            { Warn("The destination is the same as, or inside, the source folder. Pick a different destination."); return; }
            if (rbMirror.Checked && sk.StartsWith(dk, StringComparison.OrdinalIgnoreCase))
            { Warn("Mirroring into a parent of the source would delete the source. Pick a different destination."); return; }
            if (rbMirror.Checked && !Ask("Mirror mode will DELETE files in:\n" + j.Dst + "\nthat do not exist in the source.\n\nContinue?", "Confirm mirror")) return;
            if (rbMove.Checked && !Ask("Move mode will DELETE the original files from:\n" + j.Src + "\nafter they are copied.\n\nContinue?", "Confirm move")) return;

            SaveSettings();
            job = j;
            totalFiles = totalBytes = extras = doneFiles = doneBytes = 0;
            Array.Clear(classFiles, 0, 4);
            Array.Clear(classBytes, 0, 4);
            conflicts = new List<string>();
            errors = new HashSet<string>();
            samples = new Queue<double[]>();
            stopRequested = false;
            inSummary = false;

            log.Clear();
            bar.Value = 0;
            btnOpen.Enabled = false;
            tabs.SelectedTab = pgLog;
            Log(App.Name + " " + App.Version + "  -  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), SystemColors.GrayText);
            Log("From: " + j.Src);
            Log("To:   " + j.Dst);
            Log(j.Preview, SystemColors.GrayText);
            lblStat1.Text = "Scanning...";
            lblStat2.Text = "";
            lblFile.Text = "";

            SetBusy(true);
            Native.KeepAwake(true);
            Taskbar.State(this, Taskbar.Busy);
            clock = Stopwatch.StartNew();
            if (StartPhase("scan")) timer.Start();
        }

        bool StartPhase(string name)
        {
            phase = name;
            runner = new RoboRunner();
            try
            {
                runner.Start(job.Base + (name == "scan" ? " /L" : job.CopyFlags + " /MT:" + job.Threads));
                return true;
            }
            catch (Exception ex)
            {
                Finish("Could not start robocopy: " + ex.Message, Red, Failed);
                return false;
            }
        }

        static long FreeSpace(string path)
        {
            try
            {
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\")) return -1;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return -1; }
        }

        void Finish(string msg, Color color, int outcome)
        {
            timer.Stop();
            SetBusy(false);
            Native.KeepAwake(false);
            Text = App.Name;
            lblStat1.Text = msg;
            lblFile.Text = "";
            Log(msg, color);
            btnOpen.Enabled = job != null && Directory.Exists(job.Dst);

            if (outcome == Success)
            {
                Taskbar.State(this, Taskbar.None);
                if (cmbDone.SelectedIndex == 1) System.Media.SystemSounds.Asterisk.Play();
                else if (cmbDone.SelectedIndex == 2) OpenFolder(job.Dst);
            }
            else
            {
                Taskbar.State(this, outcome == Stopped ? Taskbar.Paused : Taskbar.Error);
                Taskbar.Value(this, outcome == Stopped ? bar.Value / 1000.0 : 1);
            }
        }

        void OnTick(object sender, EventArgs e)
        {
            var r = runner;
            bool finished = r.Finished();   // check before draining so no lines are missed
            string line, lastPath = null;
            int n = 0;
            while (n < 20000 && r.Lines.TryDequeue(out line))
            {
                n++;
                var m = FileLineRx.Match(line);
                if (m.Success)
                {
                    bool isExtra = m.Groups[1].Value == "*EXTRA File";
                    long size = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (phase == "scan")
                    {
                        if (isExtra) { extras++; continue; }
                        totalFiles++; totalBytes += size;
                        string cls = m.Groups[1].Value;
                        int k = cls == "New File" || cls == "Same" ? 0 : cls == "Newer" ? 1 : cls == "Older" ? 2 : 3;
                        classFiles[k]++; classBytes[k] += size;
                        if (k > 0 && conflicts.Count < 2000) conflicts.Add(ConflictLine(k, m.Groups[3].Value));
                    }
                    else if (!isExtra)
                    {
                        doneFiles++; doneBytes += size; lastPath = m.Groups[3].Value;
                    }
                    continue;
                }
                if (phase != "copy" || line.Trim().Length == 0) continue;
                if (line.StartsWith("--------------------")) { inSummary = true; Log(""); continue; }
                var em = ErrorRx.Match(line);
                if (em.Success) { errors.Add(em.Groups[1].Value); Log(line.Trim(), Red); }
                else if (inSummary) Log(line);
                else Log(line.Trim(), Dim);
            }
            if (!r.Lines.IsEmpty) finished = false;   // backlog left for the next tick

            double elapsed = clock.Elapsed.TotalSeconds;
            if (phase == "scan")
            {
                lblStat1.Text = string.Format("Scanning... {0:N0} files to copy ({1})", totalFiles, Bytes(totalBytes));
                if (!finished) return;
                if (stopRequested) { Finish("Stopped.", Orange, Stopped); return; }
                if (totalFiles == 0 && extras == 0)
                {
                    bar.Value = 1000;
                    Finish("Nothing to copy - the destination is already up to date.", Green, Success);
                    return;
                }
                if (cmbExisting.SelectedIndex == 0 && classFiles[1] + classFiles[2] + classFiles[3] > 0)
                {
                    timer.Stop();   // a modal dialog would let the timer re-enter
                    int choice;
                    using (var dlg = new ConflictForm(classFiles, classBytes, conflicts))
                    {
                        dlg.ShowDialog(this);
                        choice = dlg.Choice;
                    }
                    if (choice == ConflictForm.Cancel) { Finish("Cancelled - nothing was copied.", Orange, Stopped); return; }
                    if (choice == ConflictForm.SkipAll) { job.CopyFlags = " /XC /XN /XO"; Keep(0); }
                    else if (choice == ConflictForm.ReplaceOlder) { job.CopyFlags = " /XO"; Keep(0, 1, 3); }
                    Log("Existing files: " + ConflictForm.Describe(choice), SystemColors.GrayText);
                    if (totalFiles == 0 && extras == 0)
                    {
                        bar.Value = 1000;
                        Finish("Nothing to copy - every file already exists in the destination.", Green, Success);
                        return;
                    }
                    timer.Start();
                }
                long free = FreeSpace(job.Dst);
                if (free >= 0 && totalBytes > free)
                {
                    timer.Stop();
                    if (!Ask(string.Format("The destination drive may not have enough free space.\n\nTo copy:  {0}\nFree:      {1}\n\nCopy anyway?",
                        Bytes(totalBytes), Bytes(free)), "Not enough space?"))
                    {
                        Finish("Cancelled - not enough free space on the destination.", Orange, Stopped);
                        return;
                    }
                    timer.Start();
                }
                Log(string.Format("Scan: {0:N0} files, {1} to copy. Copying with {2} threads...", totalFiles, Bytes(totalBytes), job.Threads));
                Taskbar.State(this, Taskbar.Normal);
                if (!StartPhase("copy")) return;
                clock.Restart();
                return;
            }

            // copy phase: speed is averaged over the last ~3 seconds
            samples.Enqueue(new double[] { elapsed, doneBytes, doneFiles });
            while (samples.Count > 1 && elapsed - samples.Peek()[0] > 3) samples.Dequeue();
            var first = samples.Peek();
            double span = elapsed - first[0], bps = 0, fps = 0;
            if (span >= 0.5) { bps = (doneBytes - first[1]) / span; fps = (doneFiles - first[2]) / span; }

            double frac = totalBytes > 0 ? (double)doneBytes / totalBytes : totalFiles > 0 ? (double)doneFiles / totalFiles : 1;
            frac = Math.Min(1, frac);
            bar.Value = (int)(frac * 1000);
            Taskbar.Value(this, frac);
            Text = string.Format("{0:N0}% - {1}", frac * 100, App.Name);
            double eta = bps > 0 ? (totalBytes - doneBytes) / bps : -1;

            lblStat1.Text = string.Format("{0:N0} / {1:N0} files   {2} / {3}   ({4:N0}%)",
                doneFiles, totalFiles, Bytes(doneBytes), Bytes(totalBytes), frac * 100);
            lblStat2.Text = string.Format("{0}/s   {1:N0} files/s   {2} threads   elapsed {3}   left {4}   errors {5}",
                Bytes(bps), fps, job.Threads, Time(elapsed), Time(eta), errors.Count);
            if (lastPath != null) lblFile.Text = lastPath;

            if (!finished) return;

            int code = r.ExitCode;
            double avg = elapsed > 0 ? doneBytes / elapsed : 0;
            lblStat2.Text = string.Format("Average {0}/s   {1:N0} files in {2}   {3} threads   errors {4}",
                Bytes(avg), doneFiles, Time(elapsed), job.Threads, errors.Count);
            if (stopRequested)
                Finish("Stopped. Press Start again to resume - finished files are skipped.", Orange, Stopped);
            else if (code >= 8)
                Finish(string.Format("Finished with errors: {0} file(s) could not be copied (see the Log tab).", Math.Max(1, errors.Count)), Red, Failed);
            else
            {
                bar.Value = 1000;
                Finish(string.Format("Done. {0:N0} files copied ({1}).", doneFiles, Bytes(doneBytes)), Green, Success);
            }
        }

        // Shrinks the scan totals to the file classes that will really be copied.
        void Keep(params int[] classes)
        {
            totalFiles = 0; totalBytes = 0;
            foreach (int k in classes) { totalFiles += classFiles[k]; totalBytes += classBytes[k]; }
        }

        string ConflictLine(int k, string path)
        {
            string rel = path.StartsWith(job.Src, StringComparison.OrdinalIgnoreCase) ? path.Substring(job.Src.Length).TrimStart('\\') : path;
            return (k == 1 ? "[yours is newer]   " : k == 2 ? "[theirs is newer]  " : "[different size]   ") + rel;
        }

        // ---------------- formatting ----------------

        static string Bytes(double b)
        {
            if (b >= 1099511627776.0) return (b / 1099511627776.0).ToString("N2") + " TB";
            if (b >= 1073741824.0) return (b / 1073741824.0).ToString("N2") + " GB";
            if (b >= 1048576.0) return (b / 1048576.0).ToString("N1") + " MB";
            if (b >= 1024.0) return (b / 1024.0).ToString("N0") + " KB";
            return b.ToString("N0") + " B";
        }

        static string Time(double sec)
        {
            if (sec < 0 || double.IsNaN(sec) || double.IsInfinity(sec)) return "--:--";
            var t = TimeSpan.FromSeconds(Math.Round(sec));
            return t.TotalHours >= 1 ? string.Format("{0}:{1:mm\\:ss}", (int)t.TotalHours, t) : t.ToString("mm\\:ss");
        }
    }
}
