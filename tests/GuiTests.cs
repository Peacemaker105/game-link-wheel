using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using WheelBridge;
using WheelBridge.Gui;

/// <summary>
/// 0.1.3: every window, page and tab of Game Link Wheel, laid out as the program lays it out, then audited. Chris (2026-11-18):
/// "the main settings page needs a scroll down to see wheel settings and stuff.. the wheel calibration window at startup needs to
/// be wider/larger to not have to side scroll and fit the whole page in the little pop up window"; (2026-11-21) "lets work to make
/// all the screens scaled nicely and no text overlaps and stuff like that".
///
/// The windows are built off screen on an STA thread, with a host on loopback ports of its own (never the dashboard's), at this
/// screen's scaling; GLW_TEST_DPI=96 runs them as on a 100 % screen (the process DPI-unaware). Each screen is audited (see
/// <see cref="Audit"/>), and with GLW_RENDER_DIR set it is also saved there as a PNG to look at. The windows take the focus for a
/// moment while they are built.
/// </summary>
static class GuiTests
{
    private static bool _init;

    private static void OnSta(Action body)
    {
        Exception? err = null;
        var t = new Thread(() => { try { body(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw err;
    }

    private static void Init()
    {
        if (_init) return;
        _init = true;
        bool at96 = Environment.GetEnvironmentVariable("GLW_TEST_DPI") == "96";
        Application.SetHighDpiMode(at96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    /// <summary>Shown off screen and out of the taskbar, then left to lay out.</summary>
    private static void ShowOffScreen(Form f)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-30000, -30000);
        f.ShowInTaskbar = false;
        f.Show();
        Settle(f);
    }

    /// <summary>A few turns of the message loop (timers, BeginInvoke, layout), then a layout of the whole window.</summary>
    private static void Settle(Form f)
    {
        for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(15); }
        f.PerformLayout();
        Application.DoEvents();
    }

    private static string Dpi(Control c) => c.DeviceDpi.ToString(CultureInfo.InvariantCulture);

    private static string Slug(string s)
    {
        var chars = s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void Render(Form f, string name)
    {
        string? dir = Environment.GetEnvironmentVariable("GLW_RENDER_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
        bmp.Save(Path.Combine(dir, $"{Slug(name)}@{Dpi(f)}dpi.png"), ImageFormat.Png);
    }

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
    }

    /// <summary>A control as a person would point at it: its type and text, or the nearest parent with a text.</summary>
    private static string Name(Control c, int depth = 0)
    {
        string text = (c.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 40) text = text[..40] + "…";
        if (text.Length > 0) return $"{c.GetType().Name} \"{text}\"";
        return depth < 3 && c.Parent is { } p ? $"{c.GetType().Name} in {Name(p, depth + 1)}" : c.GetType().Name;
    }

    /// <summary>
    /// What is wrong with a window as laid out, one line each: text that does not fit its label, button or check box (cut off, or
    /// squeezed into more lines than it has room for); two controls on top of each other; a control past the edge of a parent
    /// that does not scroll (cut off); a sideways scroll; tab titles that need the arrows. The system draws the inside of a
    /// slider, number box, drop-down, text box and the tool and status strips, so their own text is left to the eye (the PNGs).
    /// </summary>
    private static List<string> Audit(Control root)
    {
        var bad = new List<string>();
        void Walk(Control p)
        {
            if (p is ToolStrip or UpDownBase or ComboBox or TrackBar or TextBoxBase) return;
            var kids = p.Controls.Cast<Control>().Where(c => c.Visible && c.Width > 0 && c.Height > 0).ToList();
            bool scrolls = p is ScrollableControl { AutoScroll: true };
            if (scrolls && ((ScrollableControl)p).HorizontalScroll.Visible) bad.Add($"{Name(p)} scrolls sideways");
            if (p is TabControl tc && tc.TabCount > 0 && tc.GetTabRect(tc.TabCount - 1).Right > tc.ClientSize.Width)
                bad.Add($"{Name(tc)}: its tab titles need the arrows (the last ends at {tc.GetTabRect(tc.TabCount - 1).Right} of {tc.ClientSize.Width})");
            for (int i = 0; i < kids.Count; i++)
            {
                var c = kids[i];
                for (int j = i + 1; j < kids.Count; j++)
                    if (c.Bounds.IntersectsWith(kids[j].Bounds)) bad.Add($"{Name(c)} and {Name(kids[j])} overlap");
                if (!scrolls && p is not TabControl && (c.Right > p.ClientSize.Width + 1 || c.Bottom > p.ClientSize.Height + 1))
                    bad.Add($"{Name(c)} runs past the edge of {Name(p)}: it ends at {c.Right} x {c.Bottom} in {p.ClientSize.Width} x {p.ClientSize.Height}");
                if (TextMisfit(c) is { } why) bad.Add($"{Name(c)}: {why}");
                Walk(c);
            }
        }
        Walk(root);
        return bad;
    }

    /// <summary>Why a control's text does not fit it, or null: the size its text asks for against the size it was given. A label
    /// is measured at its own width, where it wraps. A button or check box does not wrap: its width is the one it asks for (the
    /// text, its padding, the box), its height the text's own and a 2-pixel border each side (Windows' standard 23-pixel button
    /// asks for 25 at 100 % and shows its text in full).</summary>
    private static string? TextMisfit(Control c)
    {
        if (string.IsNullOrEmpty(c.Text) || c is not (Label or ButtonBase)) return null;
        Size need = c switch
        {
            Label { AutoSize: false } fixedLabel => TextRenderer.MeasureText(fixedLabel.Text, fixedLabel.Font,
                new Size(fixedLabel.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl),
            Label => c.GetPreferredSize(new Size(c.Width, 0)),
            _ => new Size(c.GetPreferredSize(Size.Empty).Width, TextRenderer.MeasureText(c.Text, c.Font).Height + 4),
        };
        return need.Width > c.Width + 1 || need.Height > c.Height + 1
            ? $"its text needs {need.Width} x {need.Height} and has {c.Width} x {c.Height}"
            : null;
    }

    /// <summary>The window came up scaled to the screen: its client area is its 96-dpi size in this screen's pixels, or as much of
    /// the working area as there is.</summary>
    private static void CheckScaled(Form f, int w, int h, List<string> bad)
    {
        var wa = Screen.FromControl(f).WorkingArea;
        int wantW = Math.Min(f.LogicalToDeviceUnits(w), wa.Width - 64), wantH = Math.Min(f.LogicalToDeviceUnits(h), wa.Height - 96);
        if (f.ClientSize.Width < wantW - 2 || f.ClientSize.Height < wantH - 2)
            bad.Add($"{Name(f)} is {f.ClientSize.Width} x {f.ClientSize.Height} at {Dpi(f)} dpi: not scaled from its {w} x {h} (wanted at least {wantW} x {wantH})");
    }

    private static void Report(List<string> bad) => Check.True(bad.Count == 0, string.Join("\n      ", bad.Distinct()));

    private static readonly string[] SetupSteps = ["Wheel", "Motor", "Pedals", "Controls", "Done"];

    /// <summary>Every page of the setup at the size it opens, with no wheelbase picked and with one (the motor page's longer text):
    /// scaled with the screen, no sideways scroll, nothing cut off or on top of something else.</summary>
    public static void SetupPages() => OnSta(() =>
    {
        Init();
        using var host = new BridgeHost(new HostOptions(47131, 47132));
        var goTo = typeof(SetupWizard).GetMethod("GoTo", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var stepType = typeof(SetupWizard).GetNestedType("Step", BindingFlags.NonPublic)!;
        var bad = new List<string>();
        foreach (bool picked in new[] { false, true })
        {
            // A base picked before and not attached now is listed "(not connected)"; the motor page then asks for the test.
            host.Config.Ffb.DeviceInstance = picked ? new Guid("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0") : Guid.Empty;
            host.Config.Ffb.DeviceName = picked ? "Simagic Alpha Mini Direct Drive Wheelbase" : "";
            using var w = new SetupWizard(host, () => { }, _ => { });
            ShowOffScreen(w);
            if (!picked) CheckScaled(w, 960, 700, bad);
            foreach (string step in SetupSteps)
            {
                goTo.Invoke(w, [Enum.Parse(stepType, step)]);
                Settle(w);
                string screen = $"setup {step.ToLowerInvariant()}{(picked ? " with a base" : "")}";
                Render(w, screen);
                bad.AddRange(Audit(w).Select(x => $"{screen}: {x}"));
            }
            Console.WriteLine($"      setup{(picked ? " with a base" : "")} at {Dpi(w)} dpi: {w.ClientSize.Width} x {w.ClientSize.Height}");
            w.Close();
        }
        Report(bad);
    });

    /// <summary>Every tab of the main window at the size it opens, as a returning user sees it (no first-run setup over it): scaled
    /// with the screen, nothing cut off or on top of something else, every tab title showing.</summary>
    public static void MainTabs() => OnSta(() =>
    {
        Init();
        using var host = new BridgeHost(new HostOptions(47133, 47134));
        host.Config.SetupDone = true;
        using var f = new MainForm(host);
        ShowOffScreen(f);
        var bad = new List<string>();
        CheckScaled(f, 1500, 940, bad);
        var top = All(f).OfType<TabControl>().First(t => t.TabPages.Contains(f.FfbPage));
        var under = f.FfbPage.Controls[0].Controls.OfType<TabControl>().Single();
        var screens = new List<(TabPage Page, TabPage? Sub)>();
        foreach (TabPage page in top.TabPages)
            if (page == f.FfbPage) foreach (TabPage sub in under.TabPages) screens.Add((page, sub));
            else screens.Add((page, null));
        foreach (var (page, sub) in screens)
        {
            top.SelectedTab = page;
            if (sub is not null) under.SelectedTab = sub;
            Settle(f);
            string screen = sub is null ? page.Text : $"{page.Text}, {sub.Text}";
            Render(f, "main " + screen);
            bad.AddRange(Audit(f).Select(x => $"main window, {screen}: {x}"));
        }
        Console.WriteLine($"      main window at {Dpi(f)} dpi: {f.ClientSize.Width} x {f.ClientSize.Height}");
        f.Close();
        Report(bad);
    });

    /// <summary>On a short main window (a 1080-line screen at 150 % leaves about 560 logical pixels) the force feedback page
    /// scrolls, the tabs under its panel (steering and pedals) keep at least their minimum height, and nothing is cut off.</summary>
    public static void MainWindowShort() => OnSta(() =>
    {
        Init();
        using var host = new BridgeHost(new HostOptions(47135, 47136));
        host.Config.SetupDone = true;
        using var f = new MainForm(host);
        ShowOffScreen(f);
        f.Size = new Size(f.LogicalToDeviceUnits(1100), f.LogicalToDeviceUnits(560));
        Settle(f);
        f.FitFfbPage();
        Settle(f);
        var page = f.FfbPage;
        var tabs = page.Controls[0].Controls.OfType<TabControl>().Single();
        Render(f, "main short");
        int min = f.LogicalToDeviceUnits(MainForm.MinTabsHeight);
        Console.WriteLine($"      at {Dpi(f)} dpi: the page {page.ClientSize.Height} high showing {page.DisplayRectangle.Height}; the tabs {tabs.Height} high (at least {min})");
        Check.True(page.DisplayRectangle.Height > page.ClientSize.Height && page.VerticalScroll.Visible, "the force feedback page does not scroll on a short window");
        Check.True(tabs.Height >= min - 2, $"the tabs under the force feedback panel are {tabs.Height} px high, under {min}");
        var bad = Audit(f).Select(x => "short main window: " + x).ToList();
        f.Close();
        Report(bad);
    });

    /// <summary>The profile name prompt: scaled with the screen, nothing cut off.</summary>
    public static void PromptBox() => OnSta(() =>
    {
        Init();
        using var f = Prompt.Build("Save profile", "Profile name (e.g. Speeder Bike):", "Land Speeder", out _);
        ShowOffScreen(f);
        var bad = new List<string>();
        CheckScaled(f, 360, 110, bad);
        Render(f, "prompt");
        bad.AddRange(Audit(f).Select(x => "profile prompt: " + x));
        f.Close();
        Report(bad);
    });
}
