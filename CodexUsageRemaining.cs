using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Codex 额度悬浮条")]
[assembly: System.Reflection.AssemblyDescription("显示当前 Codex 账号及剩余额度，支持账号切换和窗口重建")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

internal sealed class QuotaWindow
{
    public string Name;
    public double UsedPercent;
    public int WindowMinutes;
    public long ResetAtUnix;
}

internal sealed class QuotaSnapshot
{
    public readonly List<QuotaWindow> Windows = new List<QuotaWindow>();
    public string AccountLabel;
    public string CredentialFingerprint;
}

// Credentials stay in memory. Identity includes both the user and selected workspace.
internal sealed class CodexCredentials
{
    public string AccountLabel;
    public string AccountKey;
    public string Fingerprint;
    public string AccessToken;
    public string AccountId;
}

internal static class QuotaReader
{
    public static string LastDiagnostic = "尚未刷新";

    public static string ResolveCodexHome()
    {
        string value = Environment.GetEnvironmentVariable("CODEX_QUOTA_DATA_DIR");
        if (String.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable("CODEX_HOME");
        return String.IsNullOrWhiteSpace(value)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : Path.GetFullPath(value);
    }

    public static CodexCredentials ReadCredentials()
    {
        return ReadCredentialsFrom(Path.Combine(ResolveCodexHome(), "auth.json"));
    }

    internal static CodexCredentials ReadCredentialsFrom(string path)
    {
        try
        {
            string json;
            // Account switchers can atomically replace this file while it is being read.
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                json = reader.ReadToEnd();
            Dictionary<string, object> auth = Parse(json);
            Dictionary<string, object> tokens = Object(auth, "tokens");
            string access = StringValue(tokens, "access_token");
            string accountId = StringValue(tokens, "account_id");
            if (String.IsNullOrWhiteSpace(access) || String.IsNullOrWhiteSpace(accountId)) return null;

            Dictionary<string, object> claims = JwtClaims(StringValue(tokens, "id_token"));
            Dictionary<string, object> accessClaims = JwtClaims(access);
            string userId = StringValue(claims, "sub");
            if (String.IsNullOrWhiteSpace(userId)) userId = StringValue(accessClaims, "sub");
            string label = StringValue(claims, "email");
            if (String.IsNullOrWhiteSpace(label)) label = StringValue(claims, "name");
            if (String.IsNullOrWhiteSpace(label)) label = accountId;
            if (String.IsNullOrWhiteSpace(userId)) userId = label;

            return new CodexCredentials {
                AccountLabel = label, AccountId = accountId, AccessToken = access,
                AccountKey = Hash(accountId + "\n" + userId),
                Fingerprint = Hash(json)
            };
        }
        catch { return null; } // Missing/partially written auth must invalidate the previous account.
    }

    public static QuotaSnapshot ReadLatest()
    {
        CodexCredentials credentials = ReadCredentials();
        return credentials == null ? null : ReadRemote(credentials);
    }

    public static QuotaSnapshot ReadRemote(CodexCredentials credentials)
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                "https://chatgpt.com/backend-api/wham/usage");
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = "codex-quota-meter/1.1.0";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + credentials.AccessToken;
            request.Headers["ChatGPT-Account-Id"] = credentials.AccountId;
            string json;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                json = reader.ReadToEnd();
            QuotaSnapshot snapshot = ParseUsage(json, credentials);
            LastDiagnostic = snapshot == null ? "接口未返回额度窗口" : "刷新成功";
            return snapshot;
        }
        catch (WebException exception)
        {
            HttpWebResponse response = exception.Response as HttpWebResponse;
            LastDiagnostic = response == null ? "网络请求失败：" + exception.Status
                : "额度接口返回 HTTP " + (int)response.StatusCode;
            if (response != null) response.Dispose();
            return null;
        }
        catch (Exception exception)
        {
            LastDiagnostic = "额度读取失败：" + exception.GetType().Name;
            return null;
        }
    }

    internal static QuotaSnapshot ParseUsage(string json, CodexCredentials credentials)
    {
        Dictionary<string, object> rate = Object(Parse(json), "rate_limit");
        QuotaSnapshot snapshot = new QuotaSnapshot {
            AccountLabel = credentials.AccountLabel,
            CredentialFingerprint = credentials.Fingerprint
        };
        AddWindow(snapshot, rate, "primary_window");
        AddWindow(snapshot, rate, "secondary_window");
        snapshot.Windows.Sort(delegate(QuotaWindow a, QuotaWindow b) {
            return a.WindowMinutes.CompareTo(b.WindowMinutes);
        });
        return snapshot.Windows.Count == 0 ? null : snapshot;
    }

    private static void AddWindow(QuotaSnapshot snapshot, Dictionary<string, object> rate, string key)
    {
        Dictionary<string, object> data = Object(rate, key);
        if (data == null) return;
        object usedValue, durationValue, resetValue;
        if (!data.TryGetValue("used_percent", out usedValue) ||
            !data.TryGetValue("limit_window_seconds", out durationValue)) return;
        double used = Convert.ToDouble(usedValue, System.Globalization.CultureInfo.InvariantCulture);
        long seconds = Convert.ToInt64(durationValue);
        if (Double.IsNaN(used) || Double.IsInfinity(used) || seconds < 60) return;
        data.TryGetValue("reset_at", out resetValue);
        snapshot.Windows.Add(new QuotaWindow {
            Name = key, UsedPercent = Math.Max(0, Math.Min(100, used)),
            WindowMinutes = (int)Math.Min(Int32.MaxValue, seconds / 60),
            ResetAtUnix = resetValue == null ? 0 : Convert.ToInt64(resetValue)
        });
    }

    private static Dictionary<string, object> JwtClaims(string value)
    {
        try
        {
            string[] parts = value.Split('.');
            string encoded = parts[1].Replace('-', '+').Replace('_', '/');
            while (encoded.Length % 4 != 0) encoded += "=";
            return Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }
        catch { return null; }
    }

    private static Dictionary<string, object> Parse(string json)
    {
        return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
    }

    private static Dictionary<string, object> Object(Dictionary<string, object> data, string key)
    {
        object value;
        return data != null && data.TryGetValue(key, out value)
            ? value as Dictionary<string, object> : null;
    }

    private static string StringValue(Dictionary<string, object> data, string key)
    {
        object value;
        return data != null && data.TryGetValue(key, out value) && value != null
            ? Convert.ToString(value) : null;
    }

    private static string Hash(string text)
    {
        using (SHA256 sha = SHA256.Create())
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }
}

// This state machine rejects an old request even after A -> B -> A.
internal sealed class AccountQuotaState
{
    public CodexCredentials Credentials;
    public QuotaSnapshot Snapshot;
    public long Revision;
    public bool IsLive;
    public string Status = "等待 Codex 登录";
    public DateTime UpdatedAt = DateTime.MinValue;

    public bool Bind(CodexCredentials credentials)
    {
        string before = Credentials == null ? null : Credentials.Fingerprint;
        string after = credentials == null ? null : credentials.Fingerprint;
        if (before == after) return false;
        bool sameAccount = Credentials != null && credentials != null &&
            Credentials.AccountKey == credentials.AccountKey;
        Credentials = credentials;
        Revision++;
        if (!sameAccount) { Snapshot = null; UpdatedAt = DateTime.MinValue; }
        IsLive = false;
        Status = credentials == null ? "等待 Codex 登录" : "额度同步中…";
        return true;
    }

    public bool Accept(long revision, QuotaSnapshot snapshot)
    {
        if (revision != Revision || Credentials == null || snapshot == null ||
            snapshot.CredentialFingerprint != Credentials.Fingerprint) return false;
        Snapshot = snapshot;
        UpdatedAt = DateTime.Now;
        IsLive = true;
        Status = String.Empty;
        return true;
    }

    public void Failed(long revision)
    {
        if (revision != Revision) return;
        IsLive = false;
        Status = Snapshot == null ? "额度同步失败，正在重试" : "刷新失败，显示上次数据";
    }

    public string AccountLabel
    { get { return Credentials == null ? "未登录" : Credentials.AccountLabel; } }
}
internal sealed class OverlayTheme
{
    public bool IsDark;
    public Color Surface;
    public Color Ink;
    public Color Accent;
    public Color Success;
    public Color Danger;

    public string Signature
    {
        get
        {
            return String.Join(":", new string[]
            {
                IsDark ? "dark" : "light",
                Surface.ToArgb().ToString(), Ink.ToArgb().ToString(), Accent.ToArgb().ToString(),
                Success.ToArgb().ToString(), Danger.ToArgb().ToString()
            });
        }
    }
}

internal static class CodexAppearance
{
    public static OverlayTheme Read(bool systemUsesDarkApps)
    {
        string text = String.Empty;
        try
        {
            string path = Path.Combine(QuotaReader.ResolveCodexHome(), "config.toml");
            if (File.Exists(path)) text = File.ReadAllText(path);
        }
        catch { }

        string configured = ReadRootString(text, "appearanceTheme");
        bool isDark = configured.Equals("dark", StringComparison.OrdinalIgnoreCase)
            ? true
            : configured.Equals("light", StringComparison.OrdinalIgnoreCase)
                ? false
                : systemUsesDarkApps;

        OverlayTheme theme = isDark
            ? new OverlayTheme
            {
                IsDark = true,
                Surface = Color.FromArgb(43, 43, 46),
                Ink = Color.FromArgb(190, 190, 195),
                Accent = Color.FromArgb(255, 201, 92),
                Success = Color.FromArgb(183, 224, 183),
                Danger = Color.FromArgb(255, 116, 116)
            }
            : new OverlayTheme
            {
                IsDark = false,
                Surface = Color.FromArgb(232, 232, 235),
                Ink = Color.FromArgb(74, 74, 80),
                Accent = Color.FromArgb(154, 92, 0),
                Success = Color.FromArgb(25, 112, 58),
                Danger = Color.FromArgb(190, 42, 42)
            };

        string section = isDark
            ? "desktop.appearanceDarkChromeTheme"
            : "desktop.appearanceLightChromeTheme";
        theme.Surface = ReadColor(text, section, "surface", theme.Surface);
        theme.Ink = ReadColor(text, section, "ink", theme.Ink);
        theme.Accent = ReadColor(text, section, "accent", theme.Accent);
        theme.Success = ReadColor(text, section + ".semanticColors", "diffAdded", theme.Success);
        theme.Danger = ReadColor(text, section + ".semanticColors", "diffRemoved", theme.Danger);
        return theme;
    }

    private static string ReadRootString(string text, string key)
    {
        Match match = Regex.Match(text ?? String.Empty,
            "(?m)^\\s*" + Regex.Escape(key) + "\\s*=\\s*\"(?<value>[^\"]*)\"",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : String.Empty;
    }

    private static Color ReadColor(string text, string section, string key, Color fallback)
    {
        Match sectionMatch = Regex.Match(text ?? String.Empty,
            "(?ms)^\\s*\\[" + Regex.Escape(section) + "\\]\\s*$" +
            "(?<body>.*?)(?=^\\s*\\[|\\z)", RegexOptions.IgnoreCase);
        if (!sectionMatch.Success) return fallback;
        Match valueMatch = Regex.Match(sectionMatch.Groups["body"].Value,
            "(?m)^\\s*" + Regex.Escape(key) + "\\s*=\\s*\"(?<value>#[0-9a-fA-F]{6,8})\"",
            RegexOptions.IgnoreCase);
        if (!valueMatch.Success) return fallback;
        string value = valueMatch.Groups["value"].Value.Substring(1);
        try
        {
            if (value.Length == 8) value = value.Substring(0, 6);
            return Color.FromArgb(
                Convert.ToInt32(value.Substring(0, 2), 16),
                Convert.ToInt32(value.Substring(2, 2), 16),
                Convert.ToInt32(value.Substring(4, 2), 16));
        }
        catch { return fallback; }
    }
}

internal sealed class QuotaDisplayControl : Control
{
    private AccountQuotaState state = new AccountQuotaState();
    public bool Dark;
    public float DpiScale = 1f;

    public QuotaDisplayControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = new Font("Microsoft YaHei UI", 9f);
        TabStop = false;
    }

    public void SetState(AccountQuotaState value) { state = value; Invalidate(); }

    internal static Color RemainingColor(double remaining, bool dark)
    {
        if (remaining >= 60) return dark ? Color.FromArgb(81, 210, 126) : Color.FromArgb(14, 131, 57);
        if (remaining >= 20) return dark ? Color.FromArgb(255, 206, 74) : Color.FromArgb(171, 112, 0);
        return dark ? Color.FromArgb(255, 108, 108) : Color.FromArgb(207, 40, 40);
    }

    internal static string Remaining(QuotaWindow window)
    {
        return (100 - window.UsedPercent).ToString("0.#",
            System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    internal static string ResetTime(long unix)
    {
        if (unix <= 0) return "未知";
        try { return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("MM-dd HH:mm"); }
        catch { return "未知"; }
    }

    internal static string WindowLabel(int minutes)
    {
        if (minutes == 300) return "5h";
        if (minutes == 10080) return "周";
        if (minutes % 1440 == 0) return (minutes / 1440) + "天";
        if (minutes % 60 == 0) return (minutes / 60) + "h";
        return minutes + "分";
    }

    public int PreferredWidth()
    {
        using (Bitmap bitmap = new Bitmap(1, 1))
        using (Graphics graphics = Graphics.FromImage(bitmap))
            return (int)Math.Ceiling(PaintOrMeasure(graphics, false, false)) + (int)(8 * DpiScale);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        bool compact = PreferredWidth() > Width;
        PaintOrMeasure(e.Graphics, true, compact);
    }

    private float PaintOrMeasure(Graphics graphics, bool paint, bool compact)
    {
        using (Font normal = new Font(Font.FontFamily, 12f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font large = new Font(Font.FontFamily, 18f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
        {
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            List<QuotaWindow> windows = state.Snapshot == null ? new List<QuotaWindow> {
                new QuotaWindow { WindowMinutes = 300, UsedPercent = Double.NaN },
                new QuotaWindow { WindowMinutes = 10080, UsedPercent = Double.NaN }
            } : state.Snapshot.Windows;
            float groupsWidth = 0;
            foreach (QuotaWindow window in windows)
            {
                string value = Double.IsNaN(window.UsedPercent) ? "—" : Remaining(window);
                groupsWidth += 32 * DpiScale;
                groupsWidth += graphics.MeasureString(WindowLabel(window.WindowMinutes),
                    normal, Int32.MaxValue, format).Width;
                groupsWidth += graphics.MeasureString(value, large, Int32.MaxValue, format).Width;
                groupsWidth += 20 * DpiScale;
                groupsWidth += graphics.MeasureString(CompactResetTime(window.ResetAtUnix),
                    normal, Int32.MaxValue, format).Width;
            }
            string label = state.Credentials == null ? "—" : state.AccountLabel;
            float labelBudget = Math.Max(12 * DpiScale, Width - groupsWidth - 8 * DpiScale);
            if (compact && graphics.MeasureString(label, normal, Int32.MaxValue, format).Width > labelBudget)
            {
                string original = label;
                int characters = original.Length;
                do {
                    characters--;
                    int left = (characters + 1) / 2;
                    int right = characters / 2;
                    label = original.Substring(0, left) + "…" + original.Substring(original.Length - right);
                } while (characters > 1 &&
                    graphics.MeasureString(label, normal, Int32.MaxValue, format).Width > labelBudget);
            }
            float x = 4 * DpiScale;
            Draw(graphics, label, normal, ForeColor, format, paint, ref x);
            foreach (QuotaWindow window in windows)
            {
                bool unknown = Double.IsNaN(window.UsedPercent);
                float gap = 32 * DpiScale;
                if (paint)
                {
                    float middle = x + gap / 2;
                    using (Pen divider = new Pen(Color.FromArgb(100, ForeColor), DpiScale))
                        graphics.DrawLine(divider, middle, Height / 2f - 6 * DpiScale,
                            middle, Height / 2f + 6 * DpiScale);
                }
                x += gap;
                Draw(graphics, WindowLabel(window.WindowMinutes), normal, ForeColor, format, paint, ref x);
                x += 10 * DpiScale;
                Draw(graphics, unknown ? "—" : Remaining(window), large,
                    unknown ? ForeColor : RemainingColor(100 - window.UsedPercent, Dark), format, paint, ref x);
                x += 10 * DpiScale;
                Draw(graphics, CompactResetTime(window.ResetAtUnix),
                    normal, ForeColor, format, paint, ref x);
            }
            return x;
        }
    }

    internal static string CompactResetTime(long unix)
    {
        if (unix <= 0) return "--:--";
        try { return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("HH:mm"); }
        catch { return "--:--"; }
    }
    private void Draw(Graphics graphics, string text, Font font, Color color, StringFormat format,
        bool paint, ref float x)
    {
        if (paint)
            using (SolidBrush brush = new SolidBrush(color))
                graphics.DrawString(text, font, brush, x, (Height - font.GetHeight(graphics)) / 2f, format);
        x += graphics.MeasureString(text, font, Int32.MaxValue, format).Width;
    }
}

internal sealed class QuotaOverlayForm : Form
{
    internal const int RefreshIntervalMilliseconds = 60000;
    private const uint NoActivate = 0x10;
    private const uint ShowWindowFlag = 0x40;
    private const uint RootAncestor = 2;
    private readonly QuotaDisplayControl display;
    private readonly System.Windows.Forms.Timer timer;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem accountItem;
    private readonly ToolStripMenuItem quotaItem;
    private readonly ToolStripMenuItem resetsItem;
    private readonly ToolStripMenuItem updateItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly ContextMenuStrip menu;
    private readonly AccountQuotaState state = new AccountQuotaState();
    private readonly Func<CodexCredentials> readCredentials;
    private readonly Func<CodexCredentials, QuotaSnapshot> readQuota;
    private readonly Func<IntPtr, bool> isCodex;
    private readonly WinEventDelegate eventCallback;
    private IntPtr eventHook;
    private int eventPending;
    private bool allowVisible;
    private bool reading;
    
    private string themeSignature;
    internal IntPtr TrackedWindow { get; private set; }
    internal AccountQuotaState State { get { return state; } }
    internal bool IsReading { get { return reading; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate void WinEventDelegate(IntPtr hook, uint type, IntPtr window,
        int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,
        int attribute, out Rect rect, int size);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max,
        IntPtr module, WinEventDelegate callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    public QuotaOverlayForm() : this(QuotaReader.ReadCredentials, QuotaReader.ReadRemote, IsCodexWindow, true) { }

    internal QuotaOverlayForm(Func<CodexCredentials> auth, Func<CodexCredentials, QuotaSnapshot> quota,
        Func<IntPtr, bool> target, bool automatic)
    {
        readCredentials = auth;
        readQuota = quota;
        isCodex = target;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(900, 32);
        display = new QuotaDisplayControl { Dock = DockStyle.Fill };
        Controls.Add(display);

        menu = new ContextMenuStrip();
        accountItem = StatusItem();
        quotaItem = StatusItem();
        resetsItem = StatusItem();
        updateItem = StatusItem();
        menu.Items.AddRange(new ToolStripItem[] { accountItem, quotaItem, resetsItem, updateItem,
            new ToolStripSeparator() });
        ToolStripMenuItem refresh = new ToolStripMenuItem("立即刷新");
        refresh.Click += delegate { RefreshQuota(); };
        startupItem = new ToolStripMenuItem("随 Windows 启动") { Checked = StartupEnabled() };
        startupItem.Click += ToggleStartup;
        ToolStripMenuItem exit = new ToolStripMenuItem("退出");
        exit.Click += delegate { Close(); };
        menu.Items.AddRange(new ToolStripItem[] { refresh, startupItem, exit });
        tray = new NotifyIcon { Icon = SystemIcons.Information, ContextMenuStrip = menu,
            Text = "Codex 额度悬浮条", Visible = automatic };

        // A native owner would destroy this window when Codex rebuilds its window.
        // Keep the overlay independent; only place it above the active Codex window.
        IntPtr unused = Handle;
        timer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMilliseconds };
        timer.Tick += delegate { Tick(); };
        eventCallback = delegate(IntPtr hook, uint type, IntPtr window, int objectId,
            int childId, uint thread, uint time) {
            if (IsDisposed || !IsHandleCreated) return;
            if (type != 0x0003 && type != 0x000A && type != 0x000B && type != 0x800B) return;
            if (type == 0x800B && (objectId != 0 || window != TrackedWindow)) return;
            if (Interlocked.Exchange(ref eventPending, 1) != 0) return;
            try { BeginInvoke((MethodInvoker)delegate {
                Interlocked.Exchange(ref eventPending, 0);
                TrackWindow(GetForegroundWindow());
            }); }
            catch { Interlocked.Exchange(ref eventPending, 0); }
        };
        if (automatic)
        {
            eventHook = SetWinEventHook(0x0003, 0x800B, IntPtr.Zero, eventCallback, 0, 0, 0x0002);
            timer.Start();
            BeginInvoke((MethodInvoker)delegate { Tick(); });
        }
        ApplyTheme();
        RenderStatus();
    }

    private static ToolStripMenuItem StatusItem() { return new ToolStripMenuItem { Enabled = false }; }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(value && allowVisible); }
    protected override CreateParams CreateParams
    {
        get { CreateParams p = base.CreateParams; p.ExStyle |= 0x20 | 0x80 | 0x08000000; return p; }
    }

    internal void Tick()
    {
        if (IsDisposed) return;
        ApplyTheme();
        TrackWindow(GetForegroundWindow());
        RefreshQuota();
    }

    private void CheckAccount()
    {
        if (state.Bind(readCredentials()))
        {
            
            RenderStatus();
        }
    }

    internal void RefreshQuota()
    {
        CheckAccount();
        if (reading || state.Credentials == null) return;
        reading = true;
        long revision = state.Revision;
        CodexCredentials captured = state.Credentials;
        ThreadPool.QueueUserWorkItem(delegate {
            QuotaSnapshot result = null;
            try { result = readQuota(captured); } catch { }
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate {
                if (IsDisposed) return;
                reading = false;
                CheckAccount(); // Revalidate before applying a request that started under another account.
                if (revision != state.Revision) return;
                bool accepted = state.Accept(revision, result);
                if (!accepted) state.Failed(revision);
                
                RenderStatus();
                TrackWindow(GetForegroundWindow());
            }); } catch { }
        });
    }

    internal void TrackWindow(IntPtr foreground)
    {
        IntPtr root = foreground == IntPtr.Zero ? IntPtr.Zero : GetAncestor(foreground, RootAncestor);
        if (root == IntPtr.Zero) root = foreground;
        if (root == Handle) root = TrackedWindow;
        if (root == IntPtr.Zero || !IsWindow(root) || IsIconic(root) || !isCodex(root))
        {
            TrackedWindow = IntPtr.Zero;
            Hide();
            return;
        }
        TrackedWindow = root;
        PositionForWindow(root);
    }

    private static bool IsCodexWindow(IntPtr window)
    {
        uint pid;
        GetWindowThreadProcessId(window, out pid);
        try
        {
            using (Process process = Process.GetProcessById((int)pid))
            {
                string name = process.ProcessName;
                if (!name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("Codex", StringComparison.OrdinalIgnoreCase)) return false;
                string path = process.MainModule.FileName;
                return path.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (name.Equals("Codex", StringComparison.OrdinalIgnoreCase) &&
                     Path.GetDirectoryName(path).IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }
        catch { return false; }
    }

    private void PositionForWindow(IntPtr root)
    {
        Rect bounds;
        if (DwmGetWindowAttribute(root, 9, out bounds, Marshal.SizeOf(typeof(Rect))) != 0 &&
            !GetWindowRect(root, out bounds)) { Hide(); return; }
        uint dpi;
        try { dpi = GetDpiForWindow(root); } catch { dpi = 96; }
        display.DpiScale = (dpi == 0 ? 96 : dpi) / 96f;
        float scale = display.DpiScale;
        int menuWidth = (int)(330 * scale);
        int buttonsWidth = (int)(145 * scale);
        int available = bounds.Right - bounds.Left - menuWidth - buttonsWidth;
        if (available < (int)(280 * scale)) { Hide(); return; }
        int width = Math.Min(display.PreferredWidth(), available);
        int x = bounds.Right - buttonsWidth - width;
        int height = (int)(32 * scale);
        int y = bounds.Top + (int)(1 * scale);
        if (!Visible) { allowVisible = true; Show(); allowVisible = false; }
        SetWindowPos(Handle, new IntPtr(-1), x, y, width, height, NoActivate | ShowWindowFlag);
    }

    private void RenderStatus()
    {
        display.SetState(state);
        accountItem.Text = state.AccountLabel;
        string quotas = String.Empty, resets = String.Empty;
        if (state.Snapshot != null)
            foreach (QuotaWindow window in state.Snapshot.Windows)
            {
                string label = QuotaDisplayControl.WindowLabel(window.WindowMinutes);
                quotas += label + " 剩余 " + QuotaDisplayControl.Remaining(window) + "   ";
                resets += label + " 重置 " + QuotaDisplayControl.ResetTime(window.ResetAtUnix) + "   ";
            }
        quotaItem.Text = String.IsNullOrEmpty(quotas) ? state.Status : quotas.TrimEnd();
        resetsItem.Text = String.IsNullOrEmpty(resets) ? "等待获取重置时间" : resets.TrimEnd();
        updateItem.Text = state.UpdatedAt == DateTime.MinValue ? state.Status
            : "刷新时间：" + state.UpdatedAt.ToString("HH:mm:ss") +
                (state.IsLive ? "（每分钟刷新）" : "（" + state.Status + "）");
        string tooltip = "Codex " + quotas.TrimEnd();
        if (state.Snapshot == null) tooltip = "Codex " + state.Status;
        tray.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
    }

    private void ApplyTheme()
    {
        bool dark = false;
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                dark = key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
        }
        catch { }
        OverlayTheme theme = CodexAppearance.Read(dark);
        if (theme.Signature == themeSignature) return;
        themeSignature = theme.Signature;
        BackColor = theme.Surface;
        TransparencyKey = theme.Surface;
        display.ForeColor = theme.Ink;
        display.Dark = theme.IsDark;
        display.Invalidate();
    }

    private static bool StartupEnabled()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key != null && key.GetValue("CodexQuotaMeter") != null;
        }
        catch { return false; }
    }

    private void ToggleStartup(object sender, EventArgs e)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (startupItem.Checked) key.DeleteValue("CodexQuotaMeter", false);
                else key.SetValue("CodexQuotaMeter", "\"" + Application.ExecutablePath + "\"");
                startupItem.Checked = !startupItem.Checked;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show("无法修改自动启动：" + exception.Message, "Codex 额度悬浮条");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop();
            timer.Dispose();
            if (eventHook != IntPtr.Zero) { UnhookWinEvent(eventHook); eventHook = IntPtr.Zero; }
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [STAThread]
    private static void Main(string[] args)
    {
        // The installer launches this through the system service, outside MSIX redirection.
        if (args.Length > 0 && (args[0] == "--enable-startup" || args[0] == "--disable-startup"))
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (args[0] == "--disable-startup")
                {
                    key.DeleteValue("CodexQuotaMeter", false);
                    return;
                }
                key.SetValue("CodexQuotaMeter", "\"" + Application.ExecutablePath + "\"");
            }
        }
        if (args.Length > 0 && args[0] == "--snapshot")
        {
            CodexCredentials auth = QuotaReader.ReadCredentials();
            QuotaSnapshot snapshot = auth == null ? null : QuotaReader.ReadRemote(auth);
            CodexCredentials latest = QuotaReader.ReadCredentials();
            if (snapshot == null || latest == null || latest.Fingerprint != auth.Fingerprint)
            {
                Console.WriteLine("NO_DATA: " + QuotaReader.LastDiagnostic);
                Environment.ExitCode = 2;
                return;
            }
            Console.WriteLine("account=" + snapshot.AccountLabel);
            foreach (QuotaWindow window in snapshot.Windows)
                Console.WriteLine("{0}: remaining={1}; reset={2}",
                    QuotaDisplayControl.WindowLabel(window.WindowMinutes),
                    QuotaDisplayControl.Remaining(window), QuotaDisplayControl.ResetTime(window.ResetAtUnix));
            return;
        }
        bool owned;
        using (Mutex mutex = new Mutex(true, "Local\\CodexQuotaMeter", out owned))
        {
            if (!owned) return;
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new QuotaOverlayForm());
        }
    }
}




