using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace PengGAP.UI
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly System.Windows.Forms.NotifyIcon notifyIcon;
        private const string PENGGAP_UI = "PengGAP.UI";
        private const string RELEASES_URI = "https://github.com/PenglinChina/PengGAP/releases";
        private const string FLOW_STATISTICS_URI = "http://localhost/flowStatistics";

        /// <summary>
        /// 证书状态轮询周期
        /// </summary>
        private static readonly TimeSpan certCheckPeriod = TimeSpan.FromMinutes(5d);

        /// <summary>
        /// 剩余有效期低于该值时提醒用户
        /// </summary>
        private static readonly TimeSpan certWarnThreshold = TimeSpan.FromDays(1d);

        private string? lastCertNoticeKey;

        public MainWindow()
        {
            InitializeComponent();

            var upgrade = new System.Windows.Forms.MenuItem("检测更新(&U)");
            upgrade.Click += (s, e) => Process.Start(RELEASES_URI);

            var exit = new System.Windows.Forms.MenuItem("关闭应用(&C)");
            exit.Click += (s, e) => this.Close();

            var version = this.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            this.Title = $"{PENGGAP_UI} v{version}";
            this.notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Visible = true,
                Text = PENGGAP_UI,
                ContextMenu = new System.Windows.Forms.ContextMenu(new[] { upgrade, exit }),
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath)
            };

            this.notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    this.Show();
                    this.Activate();
                    this.WindowState = WindowState.Normal;
                }
            };

            this.MonitorCertificateAsync();
        }

        /// <summary>
        /// 监控根证书状态
        /// 与界面是否可见无关：隐藏到托盘时也要能提示用户
        /// </summary>
        private async void MonitorCertificateAsync()
        {
            using var httpClient = new HttpClient();
            while (this.Dispatcher.HasShutdownStarted == false)
            {
                try
                {
                    var json = await httpClient.GetStringAsync(FLOW_STATISTICS_URI);
                    var statistics = JsonConvert.DeserializeObject<FlowStatistics>(json);
                    if (statistics != null)
                    {
                        this.CheckCertificate(statistics);
                    }
                }
                catch (Exception)
                {
                }

                await Task.Delay(certCheckPeriod);
            }
        }

        /// <summary>
        /// 根证书状态异常时用托盘气泡提醒
        /// </summary>
        /// <param name="statistics"></param>
        private void CheckCertificate(FlowStatistics statistics)
        {
            var notAfter = statistics.CertNotAfter;
            var expiring = notAfter.HasValue && (notAfter.Value - DateTimeOffset.Now) < certWarnThreshold;

            if (statistics.CertTrusted == false)
            {
                this.ShowCertNotice(
                    "untrusted",
                    "根证书未被系统信任",
                    "浏览器会提示证书错误。请手动安装 cacert\\penggap.cer 到“受信任的根证书颁发机构”。");
            }
            else if (expiring == true)
            {
                this.ShowCertNotice(
                    "expiring",
                    "根证书即将到期",
                    $"根证书将于 {notAfter:yyyy-MM-dd HH:mm} 到期，程序会自动换新。");
            }
            else
            {
                this.lastCertNoticeKey = null;
            }
        }

        /// <summary>
        /// 同一种状态只提示一次，避免反复打扰
        /// </summary>
        /// <param name="key"></param>
        /// <param name="title"></param>
        /// <param name="text"></param>
        private void ShowCertNotice(string key, string title, string text)
        {
            if (this.lastCertNoticeKey == key)
            {
                return;
            }

            this.lastCertNoticeKey = key;
            this.notifyIcon.ShowBalloonTip(10000, title, text, System.Windows.Forms.ToolTipIcon.Warning);
        }


        /// <summary>
        /// 拦截最小化事件
        /// </summary>
        /// <param name="e"></param>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwndSource = (HwndSource)PresentationSource.FromVisual(this);
            hwndSource.AddHook(WndProc);

            IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                const int WM_SYSCOMMAND = 0x112;
                const int SC_MINIMIZE = 0xf020;
                const int SC_CLOSE = 0xf060;

                if (msg == WM_SYSCOMMAND)
                {
                    if (wParam.ToInt32() == SC_MINIMIZE || wParam.ToInt32() == SC_CLOSE)
                    {
                        this.Hide();
                        handled = true;
                    }
                }
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// 关闭时
        /// </summary>
        /// <param name="e"></param>
        protected override void OnClosed(EventArgs e)
        {
            this.notifyIcon.Icon = null;
            this.notifyIcon.Dispose();
            base.OnClosed(e);
        }
    }
}
