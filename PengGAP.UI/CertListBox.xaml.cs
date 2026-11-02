using Newtonsoft.Json;
using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PengGAP.UI
{
    /// <summary>
    /// 证书列表
    /// 展示根证书与已签发的叶证书，过期的也保留显示
    /// </summary>
    public partial class CertListBox : UserControl
    {
        private const string CERTIFICATES_URI = "http://localhost/certificates";

        private static readonly TimeSpan refreshPeriod = TimeSpan.FromSeconds(5d);

        private readonly HttpClient httpClient = new HttpClient();
        private string? lastJson;

        /// <summary>
        /// 获取证书记录
        /// </summary>
        public ObservableCollection<CertRecord> Records { get; } = new ObservableCollection<CertRecord>();

        public CertListBox()
        {
            InitializeComponent();

            this.DataContext = this;
            this.RefreshCertificatesAsync();
        }

        /// <summary>
        /// 周期刷新
        /// 只在页面可见时拉取，避免后台空转
        /// </summary>
        private async void RefreshCertificatesAsync()
        {
            while (this.Dispatcher.HasShutdownStarted == false)
            {
                try
                {
                    if (this.IsVisible == true)
                    {
                        await this.LoadAsync();
                    }
                }
                catch (Exception)
                {
                }

                await Task.Delay(refreshPeriod);
            }
        }

        /// <summary>
        /// 拉取并更新列表
        /// </summary>
        /// <returns></returns>
        private async Task LoadAsync()
        {
            var json = await this.httpClient.GetStringAsync(CERTIFICATES_URI);
            if (json == this.lastJson)
            {
                return;
            }

            var records = JsonConvert.DeserializeObject<CertRecord[]>(json);
            if (records == null)
            {
                return;
            }

            this.lastJson = json;
            this.Records.Clear();
            foreach (var item in records)
            {
                this.Records.Add(item);
            }

            if (records.Length == 0)
            {
                this.textBlockEmpty.Text = "暂无证书";
                this.textBlockEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                this.textBlockEmpty.Visibility = Visibility.Collapsed;
            }
        }
    }
}
