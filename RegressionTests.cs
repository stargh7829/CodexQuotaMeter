using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class RegressionTests
{
    private static int assertions;
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder name);
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAILED: " + name);
        assertions++;
    }

    private static string AuthJson(string user, string email, string access, string name = null)
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            new JavaScriptSerializer().Serialize(new { sub = user, email = email, name = name })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new JavaScriptSerializer().Serialize(new { tokens = new {
            access_token = access, account_id = "test-workspace", id_token = "test." + payload + ".test"
        }});
    }

    private static QuotaSnapshot Snapshot(CodexCredentials auth, int used, string name = null,
        bool profileReadSucceeded = true)
    {
        QuotaSnapshot result = QuotaReader.ParseUsage("{\"rate_limit\":{\"primary_window\":{\"used_percent\":" + used +
            ",\"limit_window_seconds\":18000,\"reset_at\":1790863200},\"secondary_window\":{\"used_percent\":10," +
            "\"limit_window_seconds\":604800,\"reset_at\":1791457200}}}", auth);
        result.ProfileReadSucceeded = profileReadSucceeded;
        if (profileReadSucceeded)
            result.DisplayName = QuotaReader.ParseProfileDisplayName(new JavaScriptSerializer().Serialize(
                new { profile = new { display_name = name, username = "fixture-handle" } }));
        return result;
    }

    private static void PumpUntil(Func<bool> done)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < end)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Check(done(), "asynchronous request completed");
    }

    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception exception)
        {
            Console.WriteLine("FAILED_TYPE: " + exception.GetType().FullName);
            try { Console.WriteLine("FAILED_MESSAGE: " + exception.Message); } catch { }
            try { Console.WriteLine(exception.StackTrace); } catch { }
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        if (args.Length > 2 && args[0] == "--installed")
        {
            string startup;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
                startup = key == null ? null : key.GetValue("CodexQuotaMeter") as string;
            uint length = 0;
            bool independent = GetCurrentPackageFullName(ref length, null) == 15700;
            bool expectedDisabled = args.Length > 3 && args[3] == "disabled";
            bool startupMatches = expectedDisabled ? startup == null : startup == "\"" + args[1] + "\"";
            CodexCredentials auth = QuotaReader.ReadCredentials();
            QuotaSnapshot quota = expectedDisabled || auth == null ? null : QuotaReader.ReadRemote(auth);
            CodexCredentials after = QuotaReader.ReadCredentials();
            bool accountMatches = quota != null && after != null &&
                quota.CredentialFingerprint == after.Fingerprint;
            File.WriteAllText(args[2], new JavaScriptSerializer().Serialize(new {
                outside_codex_package = independent, startup_matches = startupMatches,
                account_matches = accountMatches, windows = quota == null ? 0 : quota.Windows.Count,
                display_name_available = quota != null && !String.IsNullOrEmpty(quota.DisplayName),
                display_name_from_profile = quota != null && quota.ProfileReadSucceeded,
                display_name_matches_reference = args.Length > 4 && quota != null &&
                    quota.DisplayName == args[4],
                diagnostic = QuotaReader.LastDiagnostic
            }));
            Check(independent && startupMatches && (expectedDisabled || accountMatches),
                "independent installation and startup verification");
            return;
        }
        if (args.Length > 0 && args[0] == "--live")
        {
            CodexCredentials auth = QuotaReader.ReadCredentials();
            QuotaSnapshot result = auth == null ? null : QuotaReader.ReadRemote(auth);
            CodexCredentials after = QuotaReader.ReadCredentials();
            Check(auth != null, "current credentials available");
            if (result == null)
            {
                Console.WriteLine("LIVE_UNAVAILABLE: " + QuotaReader.LastDiagnostic);
                Environment.ExitCode = 2;
                return;
            }
            Check(after != null && result.CredentialFingerprint == after.Fingerprint,
                "live quota belongs to current credentials");
            Check(result.ProfileReadSucceeded, "current profile endpoint is readable");
            Console.WriteLine("LIVE_OK: account_matches=true; profile_source_verified=true; windows=" + result.Windows.Count);
            return;
        }

        Application.EnableVisualStyles();
        Check(QuotaOverlayForm.RefreshIntervalMilliseconds == 60000, "account and quota interval is one minute");
        string folder = Path.Combine(Path.GetTempPath(), "CodexQuotaTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "auth-test.json");
        try
        {
            File.WriteAllText(path, AuthJson("user-a", "alice@example.invalid", "test-token-a", "Different login name"));
            CodexCredentials a = QuotaReader.ReadCredentialsFrom(path);
            Check(a != null && a.AccountLabel == "alice@example.invalid", "email from selected credentials");
            File.WriteAllText(path, AuthJson("user-b", "bob@example.invalid", "test-token-b", "Bob"));
            CodexCredentials b = QuotaReader.ReadCredentialsFrom(path);
            Check(a.AccountKey != b.AccountKey, "same workspace, different user remains distinct");

            string profilePayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "https://api.openai.com/profile", new { name = "小林", email = "lin@example.invalid" } }
                }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            File.WriteAllText(path, AuthJson("user-c", null, "test." + profilePayload + ".test", " "));
            CodexCredentials profile = QuotaReader.ReadCredentialsFrom(path);
            Check(profile != null && profile.AccountLabel == "lin@example.invalid",
                "access-token email supplies missing email");
            Check(QuotaReader.ParseProfileDisplayName("{\"profile\":{\"display_name\":\" 小林 \",\"username\":\"lin-handle\"}}") == "小林",
                "editable profile display name is distinct from the handle");
            Check(QuotaReader.ParseProfileDisplayName("{\"profile\":{\"display_name\":null,\"username\":\"lin-handle\"}}") == String.Empty,
                "missing display name does not substitute the handle or login claim");
            Check(QuotaReader.ParseProfileDisplayName("{\"profile\":{\"display_name\":\"  \"}}") == String.Empty,
                "empty profile display name remains empty");
            bool invalidSchemaRejected = false;
            try { QuotaReader.ParseProfileDisplayName("{\"profile\":{\"name\":\"Wrong field\"}}"); }
            catch (FormatException) { invalidSchemaRejected = true; }
            Check(invalidSchemaRejected, "unexpected profile schema is treated as a failed lookup");

            AccountQuotaState state = new AccountQuotaState();
            state.Bind(a);
            Check(state.DisplayName == String.Empty, "login claim is not displayed while awaiting profile");
            long revisionA = state.Revision;
            Check(state.Accept(revisionA, Snapshot(a, 70, "Alice")) && state.DisplayName == "Alice",
                "first account accepts profile display name with matching quotas");
            state.Bind(b);
            Check(state.Snapshot == null && state.AccountLabel == "bob@example.invalid" && state.DisplayName == String.Empty,
                "switch clears previous quotas and profile name");
            Check(!state.Accept(revisionA, Snapshot(a, 1, "Old Alice")), "reject request from previous account");
            Check(state.Accept(state.Revision, Snapshot(b, 47, null, false)) &&
                state.Snapshot != null && state.DisplayName == String.Empty,
                "profile failure still displays new-account quotas without previous-account name");
            state.Bind(a);
            Check(state.DisplayName == String.Empty, "switch-back waits for fresh profile lookup");
            Check(!state.Accept(revisionA, Snapshot(a, 1, "Old Alice")), "A-B-A rejects stale profile and quota response");
            Check(state.Accept(state.Revision, Snapshot(a, 70, "Alice")), "fresh request after switch-back");
            Check(state.Accept(state.Revision, Snapshot(a, 69, null, false)) &&
                state.DisplayName == "Alice" && state.Snapshot.Windows[0].UsedPercent == 69,
                "profile-only failure keeps verified same-account name while updating quota");
            Check(state.Accept(state.Revision, Snapshot(a, 68, "Alice Updated")) && state.DisplayName == "Alice Updated",
                "same-account profile rename updates on the next refresh");
            Check(state.Accept(state.Revision, Snapshot(a, 67)) && state.DisplayName == String.Empty,
                "successful empty profile name removes previous display name");
            state.Accept(state.Revision, Snapshot(a, 66, "Alice"));

            File.WriteAllText(path, AuthJson("user-a", "alice@example.invalid", "test-token-a-renewed", "Alice"));
            CodexCredentials renewed = QuotaReader.ReadCredentialsFrom(path);
            state.Bind(renewed);
            Check(state.Snapshot != null && !state.IsLive && state.DisplayName == "Alice",
                "token renewal keeps only same-user verified profile and stale snapshot");
            state.Failed(state.Revision);
            Check(state.Status.Contains("上次数据"), "failed refresh is visibly stale");
            File.WriteAllText(path, "{partial");
            Check(QuotaReader.ReadCredentialsFrom(path) == null, "partial auth is unavailable");
            state.Bind(null);
            Check(state.Snapshot == null && state.AccountLabel == "未登录" && state.DisplayName == String.Empty,
                "logout clears data and display name");
            File.Delete(path);
            Check(QuotaReader.ReadCredentialsFrom(path) == null, "missing auth is unavailable");

            QuotaSnapshot parsed = Snapshot(b, 47);
            Check(parsed.Windows[0].WindowMinutes == 300 && parsed.Windows[1].WindowMinutes == 10080,
                "5h then weekly order");
            Check(QuotaDisplayControl.Remaining(parsed.Windows[0]) == "53%", "remaining, not used percentage");
            Check(QuotaDisplayControl.RemainingColor(61, false).G >
                QuotaDisplayControl.RemainingColor(61, false).R, "above 60 green");
            Check(QuotaDisplayControl.RemainingColor(59, false) == QuotaDisplayControl.RemainingColor(20, false),
                "20 through below 60 yellow");
            Check(QuotaDisplayControl.RemainingColor(60, false) == QuotaDisplayControl.RemainingColor(61, false),
                "60 and above green");
            Check(QuotaDisplayControl.RemainingColor(19, false).R >
                QuotaDisplayControl.RemainingColor(19, false).G, "below 20 red");

            DateTime reset = new DateTime(2026, 10, 8, 6, 41, 0, DateTimeKind.Local);
            long resetUnix = new DateTimeOffset(reset).ToUnixTimeSeconds();
            Check(QuotaDisplayControl.TitleResetTime(new QuotaWindow {
                WindowMinutes = 300, ResetAtUnix = resetUnix }) == "06:41", "5h reset shows time only");
            Check(QuotaDisplayControl.TitleResetTime(new QuotaWindow {
                WindowMinutes = 10080, ResetAtUnix = resetUnix }) == "2026-10-08 06:41",
                "weekly reset includes year month day and time");
            Check(QuotaDisplayControl.TitleResetTime(new QuotaWindow {
                WindowMinutes = 50400, ResetAtUnix = resetUnix }) == "2026-10-08 06:41",
                "longer quota windows retain full reset date");
            Check(QuotaDisplayControl.TitleResetTime(new QuotaWindow {
                WindowMinutes = 10080, ResetAtUnix = 0 }) == "--:--", "unknown reset stays a placeholder");

            Rectangle wide = QuotaOverlayForm.CalculateOverlayBounds(new Rectangle(100, 50, 1600, 900), 1f, 650);
            Check(wide.Left + wide.Width / 2 == 900, "overlay centers on the whole Codex window");
            Rectangle compact = QuotaOverlayForm.CalculateOverlayBounds(new Rectangle(100, 50, 900, 700), 1f, 650);
            Check(compact.Left >= 430 && compact.Right <= 855, "compact overlay clears menu and window buttons");
            Rectangle scaled = QuotaOverlayForm.CalculateOverlayBounds(new Rectangle(-2400, -200, 2400, 1350), 1.5f, 975);
            Check(Math.Abs(scaled.Left + scaled.Width / 2 + 1200) <= 1 && scaled.Height == 48,
                "centering follows DPI and negative monitor coordinates");
            Check(QuotaOverlayForm.CalculateOverlayBounds(new Rectangle(0, 0, 600, 400), 1f, 650).IsEmpty,
                "too-small title bars preserve menu access");

            Console.WriteLine("PASS: identity, request generation, parsing and colors");
            using (ManualResetEvent gate = new ManualResetEvent(false))
            {
                CodexCredentials current = a;
                int calls = 0;
                using (QuotaOverlayForm overlay = new QuotaOverlayForm(delegate { return current; },
                    delegate(CodexCredentials auth) {
                        if (Interlocked.Increment(ref calls) == 1) gate.WaitOne(3000);
                        return Snapshot(auth, auth == a ? 80 : 47, auth == a ? "Alice" : "Bob");
                    }, delegate { return false; }, false))
                {
                    overlay.Tick();
                    PumpUntil(delegate { return calls == 1; });
                    current = b;
                    overlay.Tick();
                    Check(overlay.State.Snapshot == null, "UI clears previous account during pending request");
                    gate.Set();
                    PumpUntil(delegate { return !overlay.IsReading; });
                    Check(overlay.State.Snapshot == null && calls == 1,
                        "stale completion waits for next minute rather than requesting again");
                    overlay.Tick(); // Simulate the next one-minute timer event.
                    PumpUntil(delegate { return overlay.State.Snapshot != null; });
                    Check(overlay.State.AccountLabel == "bob@example.invalid" &&
                        overlay.State.DisplayName == "Bob" && overlay.State.Snapshot.Windows[0].UsedPercent == 47,
                        "UI rejects in-flight A result and retains the new user's name");
                }
            }

            IntPtr target = IntPtr.Zero;
            using (QuotaOverlayForm overlay = new QuotaOverlayForm(delegate { return b; },
                delegate(CodexCredentials auth) { return Snapshot(auth, 47, "Bob"); },
                delegate(IntPtr window) { return window == target; }, false))
            {
                using (Form first = new Form { StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(-20000, -20000, 1500, 800) })
                {
                    target = first.Handle;
                    overlay.TrackWindow(target);
                    Check(overlay.Visible && overlay.TrackedWindow == target, "attach initial window");
                    Check(GetWindow(overlay.Handle, 4) != target && overlay.Owner == null,
                        "Codex window is not the overlay owner");
                    first.Close();
                    Application.DoEvents();
                    Check(!overlay.IsDisposed, "Codex window close preserves overlay process/form");
                }
                overlay.TrackWindow(IntPtr.Zero);
                Check(!overlay.Visible, "hide while target is closed");
                using (Form second = new Form { StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(-20000, -20000, 1500, 800) })
                {
                    target = second.Handle;
                    overlay.TrackWindow(target);
                    Check(overlay.Visible && overlay.TrackedWindow == target, "reattach recreated window");
                    overlay.TrackWindow(IntPtr.Zero);
                    Check(!overlay.Visible, "hide when another application is active");
                }
            }

            if (args.Length > 1 && args[0] == "--preview")
            {
                Directory.CreateDirectory(args[1]);
                AccountQuotaState preview = new AccountQuotaState();
                preview.Bind(a);
                preview.Accept(preview.Revision, Snapshot(a, 47, "Alice"));
                foreach (bool dark in new bool[] { false, true })
                using (Form host = new Form { FormBorderStyle = FormBorderStyle.None,
                    ClientSize = new Size(1440, 64) })
                using (QuotaDisplayControl control = new QuotaDisplayControl {
                    Dark = dark,
                    ForeColor = dark ? Color.FromArgb(210, 210, 215) : Color.FromArgb(65, 65, 70) })
                {
                    host.BackColor = dark ? Color.FromArgb(43, 43, 46) : Color.FromArgb(245, 245, 242);
                    control.BackColor = host.BackColor;
                    host.Controls.Add(control);
                    control.SetState(preview);
                    control.Bounds = QuotaOverlayForm.CalculateOverlayBounds(
                        new Rectangle(0, 0, 1440, 64), 1f, control.PreferredWidth());
                    using (Bitmap bitmap = new Bitmap(1440, 64))
                    using (Bitmap panel = new Bitmap(control.Width, control.Height))
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(host.BackColor);
                        control.DrawToBitmap(panel, new Rectangle(Point.Empty, control.Size));
                        graphics.DrawImageUnscaled(panel, control.Location);
                        bitmap.Save(Path.Combine(args[1], dark ? "quota-dark.png" : "quota-light.png"));
                    }
                }
            }
            Console.WriteLine("PASS: " + assertions + " regression assertions");
        }
        finally { File.Delete(path); Directory.Delete(folder); }
    }
}
