using System;
using System.Text.Json.Serialization;

namespace PengGAP.HttpServer.Certs
{
    /// <summary>
    /// 证书信息
    /// </summary>
    public record CertRecord
    {
        /// <summary>
        /// 获取证书名（根证书为主题名，叶证书为域名）
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// 获取证书类型：root=根证书，leaf=叶证书
        /// </summary>
        public string Kind { get; init; } = "leaf";

        /// <summary>
        /// 获取签发时间
        /// </summary>
        public DateTimeOffset NotBefore { get; init; }

        /// <summary>
        /// 获取到期时间
        /// </summary>
        public DateTimeOffset NotAfter { get; init; }

        /// <summary>
        /// 获取是否已过期
        /// </summary>
        public bool Expired { get; init; }
    }

    /// <summary>
    /// 证书信息的序列化上下文
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(CertRecord[]))]
    public sealed partial class CertRecordContext : JsonSerializerContext
    {
    }
}
