using System.Text.Json.Serialization;

namespace PengGAP.HttpServer.HttpMiddlewares
{
    /// <summary>
    /// 转发错误响应体
    /// 必须使用具名类型：裁剪发布下只能使用源生成的 JSON 元数据，
    /// 匿名类型没有 JsonTypeInfo，会在序列化时抛 NotSupportedException
    /// </summary>
    internal sealed class ForwarderErrorResponse
    {
        /// <summary>
        /// 错误码
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// 转发错误响应的序列化上下文
    /// 属性名保持 camelCase，与原来的匿名类型输出一致（error / message）
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ForwarderErrorResponse))]
    internal sealed partial class ForwarderErrorContext : JsonSerializerContext
    {
    }
}
