using System.Text.Json.Serialization;

namespace PengGAP.FlowAnalyze
{
    [JsonSerializable(typeof(FlowStatistics))]
    public partial class FlowStatisticsContext : JsonSerializerContext
    {
    }
}
