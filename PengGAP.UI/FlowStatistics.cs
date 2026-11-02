using System;

namespace PengGAP.UI
{
    /// <summary>
    /// 流量统计
    /// </summary>
    public class FlowStatistics
    {
        /// <summary>
        /// 获取总读上行
        /// </summary>
        public long TotalRead { get; set; }

        /// <summary>
        /// 获取总下行
        /// </summary>
        public long TotalWrite { get; set; }

        /// <summary>
        /// 获取上行速率
        /// </summary>
        public double ReadRate { get; set; }

        /// <summary>
        /// 获取下行速率
        /// </summary>
        public double WriteRate { get; set; }

        /// <summary>
        /// 获取根证书是否已被系统信任
        /// </summary>
        public bool CertTrusted { get; set; } = true;

        /// <summary>
        /// 获取根证书到期时间
        /// </summary>
        public DateTimeOffset? CertNotAfter { get; set; }


        /// <summary>
        /// 转换为网络尺寸文本
        /// 一律取整，不显示小数，避免坐标刻度出现 1.23KB 这类值
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string ToNetworkSizeString(long value)
        {
            if (value < 1024)
            {
                return $"{value}B";
            }
            if (value < 1024 * 1024)
            {
                return $"{value / 1024d:0}KB";
            }
            return $"{value / 1024d / 1024d:0}MB";
        }
    }
}
