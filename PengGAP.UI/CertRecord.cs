using System;

namespace PengGAP.UI
{
    /// <summary>
    /// 证书信息（界面展示用）
    /// </summary>
    public class CertRecord
    {
        /// <summary>
        /// 获取证书名
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 获取证书类型：root=根证书，leaf=叶证书
        /// </summary>
        public string Kind { get; set; } = "leaf";

        /// <summary>
        /// 获取签发时间
        /// </summary>
        public DateTimeOffset NotBefore { get; set; }

        /// <summary>
        /// 获取到期时间
        /// </summary>
        public DateTimeOffset NotAfter { get; set; }

        /// <summary>
        /// 获取是否已过期
        /// </summary>
        public bool Expired { get; set; }

        /// <summary>
        /// 获取类型文本
        /// </summary>
        public string KindText => this.Kind == "root" ? "根证书" : "叶证书";

        /// <summary>
        /// 获取签发日期文本
        /// </summary>
        public string NotBeforeText => this.NotBefore.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

        /// <summary>
        /// 获取到期日期文本
        /// </summary>
        public string NotAfterText => this.NotAfter.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

        /// <summary>
        /// 获取状态文本
        /// </summary>
        public string StatusText => this.Expired ? "已过期" : "有效";

        /// <summary>
        /// 获取状态颜色
        /// </summary>
        public string StatusColor => this.Expired ? "IndianRed" : "#2E8B57";
    }
}
