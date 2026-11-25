// 本地 UI 预览工具：把某个页面画到位图存成 PNG，用来核排版。
//   preview.exe            → 历史管理页（深色）
//   preview.exe light      → 历史管理页（浅色）
//   preview.exe about      → 整窗（左导航 + 关于页，深色）
//   preview.exe about light→ 整窗（浅色）
// 不参与正式编译（正式编译走根目录的 编译并启动.cmd）。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

static class Preview
{
    static bool _light;
    static string _root = @"C:\Projects\battery-float\";

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();

        string what = "data";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "light") _light = true;
            else what = args[i];
        }
        BatteryMonitor.Ui.ApplyTheme(!_light);

        // 拷一份真实 csv 到 exe 目录再 Init：Init 里可能按保留天数裁剪并回写，
        // 直接指真实文件等于拿用户数据做实验。拷到 exe 目录顺带让「数据库大小」显示真实体积。
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        CopyOrEmpty(_root + "battery-monitor-history.csv",
            Path.Combine(dir, "battery-monitor-history.csv"));
        CopyOrEmpty(_root + "battery-monitor-history-bt.csv",
            Path.Combine(dir, "battery-monitor-history-bt.csv"));
        BatteryMonitor.History.Init(
            Path.Combine(dir, "battery-monitor-history.csv"),
            Path.Combine(dir, "battery-monitor-history-bt.csv"), 3650);

        if (what == "about") RenderAbout();
        else if (what == "settings") RenderSettings();
        else RenderData();
    }

    // 单页：历史管理
    static void RenderData()
    {
        BatteryMonitor.Config cfg = BatteryMonitor.Config.Load();
        BatteryMonitor.DataPage page = new BatteryMonitor.DataPage(null, cfg);

        Form f = new Form();
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.ClientSize = new Size(980, 700);
        f.BackColor = _light ? Color.FromArgb(245, 246, 248) : Color.FromArgb(32, 32, 32);
        page.Bounds = new Rectangle(28, 24, 924, 652);
        f.Controls.Add(page);
        Shot(f, page, _light ? "datapage-light.png" : "datapage.png");
    }

    // 整窗：左导航 + 关于页（导航最后一项贴底，正好一起验）
    static void RenderAbout()
    {
        Form f = new Form();
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.ClientSize = new Size(940, 660);
        f.BackColor = BatteryMonitor.Ui.Bg;

        BatteryMonitor.NavBar nav = new BatteryMonitor.NavBar();
        nav.Bounds = new Rectangle(0, 0, BatteryMonitor.NavBar.ExpandedW, 660);
        nav.SelectedIndex = 4;

        BatteryMonitor.AboutPage page = new BatteryMonitor.AboutPage();
        page.Bounds = new Rectangle(nav.Width + 23, 18, 940 - nav.Width - 45, 660 - 36);

        f.Controls.Add(page);
        f.Controls.Add(nav);
        Shot(f, f, _light ? "about-light.png" : "about.png");
    }

    // 整窗：左导航 + 设置页（验强调色换掉之后开关/选中态的样子）
    static void RenderSettings()
    {
        Form f = new Form();
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.ClientSize = new Size(940, 660);
        f.BackColor = BatteryMonitor.Ui.Bg;

        BatteryMonitor.NavBar nav = new BatteryMonitor.NavBar();
        nav.Bounds = new Rectangle(0, 0, BatteryMonitor.NavBar.ExpandedW, 660);
        nav.SelectedIndex = 3;

        BatteryMonitor.Config cfg = BatteryMonitor.Config.Load();
        BatteryMonitor.SettingsPage page = new BatteryMonitor.SettingsPage(null, cfg);
        page.Bounds = new Rectangle(nav.Width + 23, 18, 940 - nav.Width - 45, 660 - 36);

        f.Controls.Add(page);
        f.Controls.Add(nav);
        Shot(f, f, _light ? "settings-light.png" : "settings.png");
    }

    static void Shot(Form f, Control target, string name)
    {
        f.Show();
        Application.DoEvents();
        System.Threading.Thread.Sleep(1000);
        Application.DoEvents();

        using (Bitmap bmp = new Bitmap(target.Width, target.Height))
        {
            target.DrawToBitmap(bmp, new Rectangle(0, 0, target.Width, target.Height));
            bmp.Save(Path.Combine(_root, "dev", "shots", name), ImageFormat.Png);
        }
        Console.WriteLine("done " + name + " " + target.Width + "x" + target.Height);
        f.Close();
    }

    static void CopyOrEmpty(string src, string dst)
    {
        try
        {
            if (File.Exists(src)) File.Copy(src, dst, true);
            else File.WriteAllText(dst, "");
        }
        catch { }
    }
}
