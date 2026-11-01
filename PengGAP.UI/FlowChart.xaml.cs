using LiveCharts;
using LiveCharts.Configurations;
using LiveCharts.Wpf;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace PengGAP.UI
{
    /// <summary>
    /// 流量图表的交互逻辑
    /// 横纵坐标固定：X 为 60 秒滑动窗口，Y 为阶梯式上限，曲线整块右移而非跟随缩放；
    /// 两个轴的刻度步长都取整数，保证节点值不出现小数；
    /// 图表不可见时（隐藏到托盘、或停留在其它标签页）不做任何轮询与重绘
    /// </summary>
    public partial class FlowChart : UserControl
    {
        /// <summary>
        /// 窗口内的点数，也等于窗口的秒数
        /// </summary>
        private const double WINDOW_SECONDS = 60d;

        /// <summary>
        /// X 轴刻度步长（秒），60 / 12 正好 5 个间隔
        /// </summary>
        private const double X_SEPARATOR_STEP = 12d;

        /// <summary>
        /// Y 轴刻度分段数
        /// </summary>
        private const double Y_SEPARATOR_COUNT = 4d;

        /// <summary>
        /// Y 轴上限的最小值（1KB/s）
        /// </summary>
        private const double MIN_Y_MAX = 1024d;

        private readonly LineSeries readSeries = new LineSeries
        {
            Title = "上行速率",
            PointGeometry = null,
            LineSmoothness = 1D,
            Values = new ChartValues<RateTick>()
        };

        private readonly LineSeries writeSeries = new LineSeries()
        {
            Title = "下行速率",
            PointGeometry = null,
            LineSmoothness = 1D,
            Values = new ChartValues<RateTick>()
        };

        /// <summary>
        /// 图表起点时间，用于把刻度值还原成时钟标签
        /// </summary>
        private readonly DateTime startTime = DateTime.Now;

        /// <summary>
        /// 当前刻度值（距起点的秒数）
        /// </summary>
        private double tick;

        /// <summary>
        /// 当前 Y 轴上限，只增不减
        /// </summary>
        private double yMax = MIN_Y_MAX;

        /// <summary>
        /// 当前 Y 轴刻度步长，保证为整数
        /// </summary>
        private double ySeparatorStep;

        public SeriesCollection Series { get; }

        public Func<double, string> XFormatter { get; }

        public Func<double, string> YFormatter { get; }

        public FlowChart()
        {
            InitializeComponent();

            this.Series = new SeriesCollection(Mappers.Xy<RateTick>().X(item => item.Tick).Y(item => item.Rate));
            this.Series.Add(this.readSeries);
            this.Series.Add(this.writeSeries);

            this.XFormatter = value => this.startTime.AddSeconds(value).ToString("HH:mm:ss");
            this.YFormatter = value => $"{FlowStatistics.ToNetworkSizeString((long)value)}/s";

            // 刻度步长显式指定为整数，避免自动计算落到 7.5、256.5 这类值
            this.flowChart.AxisX[0].Separator = new LiveCharts.Wpf.Separator { Step = X_SEPARATOR_STEP };
            this.UpdateYSeparator();

            this.DataContext = this;
            this.InitFlowChartAsync();
        }

        private async void InitFlowChartAsync()
        {
            using var httpClient = new HttpClient();
            while (this.Dispatcher.HasShutdownStarted == false)
            {
                try
                {
                    // 图表不可见时不轮询、不重绘：隐藏到托盘或停在其它标签页时几乎不占资源
                    if (this.IsVisible == true)
                    {
                        await this.FlushFlowStatisticsAsync(httpClient);
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    await Task.Delay(TimeSpan.FromSeconds(1d));
                }
            }
        }

        private async Task FlushFlowStatisticsAsync(HttpClient httpClient)
        {
            var response = await httpClient.GetAsync("http://localhost/flowStatistics");
            var json = await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
            var flowStatistics = JsonConvert.DeserializeObject<FlowStatistics>(json);
            if (flowStatistics == null)
            {
                return;
            }

            this.textBlockRead.Text = FlowStatistics.ToNetworkSizeString(flowStatistics.TotalRead);
            this.textBlockWrite.Text = FlowStatistics.ToNetworkSizeString(flowStatistics.TotalWrite);

            // 刻度直接取墙上时钟，保证长时间不可见后恢复时标签仍然准确
            var now = (DateTime.Now - this.startTime).TotalSeconds;
            if ((now - this.tick) > WINDOW_SECONDS)
            {
                // 中断超过一个窗口，旧数据已落在可视范围之外，直接丢弃
                this.readSeries.Values.Clear();
                this.writeSeries.Values.Clear();
            }
            this.tick = now;

            this.readSeries.Values.Add(new RateTick(flowStatistics.ReadRate, this.tick));
            this.writeSeries.Values.Add(new RateTick(flowStatistics.WriteRate, this.tick));

            while (this.readSeries.Values.Count > WINDOW_SECONDS)
            {
                this.readSeries.Values.RemoveAt(0);
                this.writeSeries.Values.RemoveAt(0);
            }

            this.ApplyFixedAxes(flowStatistics.ReadRate, flowStatistics.WriteRate);
        }

        /// <summary>
        /// 应用固定的坐标范围
        /// X 始终是 60 秒宽度的窗口，Y 只在峰值超出上限时阶梯式上调
        /// </summary>
        /// <param name="readRate"></param>
        /// <param name="writeRate"></param>
        private void ApplyFixedAxes(double readRate, double writeRate)
        {
            var axisX = this.flowChart.AxisX[0];
            axisX.MinValue = Math.Max(0d, this.tick - WINDOW_SECONDS);
            axisX.MaxValue = Math.Max(WINDOW_SECONDS, this.tick);

            var peak = Math.Max(readRate, writeRate);
            if (peak > this.yMax)
            {
                this.yMax = GetNiceCeiling(peak);
                this.flowChart.AxisY[0].MaxValue = this.yMax;
                this.UpdateYSeparator();
            }
        }

        /// <summary>
        /// 更新 Y 轴刻度步长，确保为整数
        /// </summary>
        private void UpdateYSeparator()
        {
            this.ySeparatorStep = Math.Max(1d, Math.Round(this.yMax / Y_SEPARATOR_COUNT));
            this.flowChart.AxisY[0].Separator = new LiveCharts.Wpf.Separator { Step = this.ySeparatorStep };
        }

        /// <summary>
        /// 取不小于该值的"整数"上限：1/2/5/10 × 10^n，且能被分段数整除
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private static double GetNiceCeiling(double value)
        {
            if (value <= MIN_Y_MAX)
            {
                return MIN_Y_MAX;
            }

            var exponent = Math.Pow(10d, Math.Floor(Math.Log10(value)));
            var ceiling = 10d * exponent;
            foreach (var multiplier in new[] { 1d, 2d, 5d, 10d })
            {
                if (value <= multiplier * exponent)
                {
                    ceiling = multiplier * exponent;
                    break;
                }
            }

            // 让上限能被分段数整除，保证每格步长都是整数
            var remainder = ceiling % Y_SEPARATOR_COUNT;
            if (remainder != 0d)
            {
                ceiling += Y_SEPARATOR_COUNT - remainder;
            }
            return ceiling;
        }

        /// <summary>
        /// 坐标点
        /// </summary>
        private class RateTick
        {
            public double Rate { get; }

            public double Tick { get; }

            public RateTick(double rate, double tick)
            {
                this.Rate = rate;
                this.Tick = tick;
            }
        }
    }
}
