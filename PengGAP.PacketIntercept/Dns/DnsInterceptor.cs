using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using PengGAP.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using WindivertDotnet;

namespace PengGAP.PacketIntercept.Dns
{
    /// <summary>
    /// dns拦截器
    /// </summary>   
    [SupportedOSPlatform("windows")]
    sealed class DnsInterceptor : IDnsInterceptor
    {
        private const int HEADER_LENGTH = 12;
        private const ushort FLAG_RESPONSE = 0x8000;
        private const int OPCODE_QUERY = 0;

        private static readonly Filter filter = Filter.True.And(f => f.Udp.DstPort == 53);

        private readonly PengGAPConfig pengGapConfig;
        private readonly ILogger<DnsInterceptor> logger;

        private readonly TimeSpan ttl = TimeSpan.FromMinutes(5d);

        /// <summary>
        /// 刷新DNS缓存
        /// </summary>    
        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
        private static extern void DnsFlushResolverCache();

        /// <summary>
        /// dns拦截器
        /// </summary>
        /// <param name="pengGapConfig"></param>
        /// <param name="logger"></param>
        /// <param name="options"></param>
        public DnsInterceptor(
            PengGAPConfig pengGapConfig,
            ILogger<DnsInterceptor> logger,
            IOptionsMonitor<PengGAPOptions> options)
        {
            this.pengGapConfig = pengGapConfig;
            this.logger = logger;

            options.OnChange(_ => DnsFlushResolverCache());
        }

        /// <summary>
        /// DNS拦截
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <exception cref="Win32Exception"></exception>
        /// <returns></returns>
        public async Task InterceptAsync(CancellationToken cancellationToken)
        {
            using var divert = new WinDivert(filter, WinDivertLayer.Network);
            using var packet = new WinDivertPacket();
            using var addr = new WinDivertAddress();

            DnsFlushResolverCache();
            cancellationToken.Register(DnsFlushResolverCache);

            while (cancellationToken.IsCancellationRequested == false)
            {
                await divert.RecvAsync(packet, addr, cancellationToken);
                try
                {
                    this.ModifyDnsPacket(packet, addr);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex.Message);
                }
                finally
                {
                    await divert.SendAsync(packet, addr, cancellationToken);
                }
            }
        }

        /// <summary>
        /// 修改DNS数据包
        /// </summary>
        /// <param name="packet"></param>
        /// <param name="addr"></param>
        unsafe private void ModifyDnsPacket(WinDivertPacket packet, WinDivertAddress addr)
        {
            var result = packet.GetParseResult();
            var dataSpan = result.DataSpan;

            // 先做零分配的报文头校验，非"标准查询"的包直接放回，避免无谓的托管分配
            if (IsStandardQuery(dataSpan) == false)
            {
                return;
            }

            var requestPayload = dataSpan.ToArray();
            if (TryParseRequest(requestPayload, out var request) == false ||
                request.Questions.Count == 0)
            {
                return;
            }

            var question = request.Questions.First();
            if (question.Type != RecordType.A && question.Type != RecordType.AAAA)
            {
                return;
            }

            var domain = question.Name;
            if (this.pengGapConfig.IsMatch(question.Name.ToString()) == false)
            {
                return;
            }

            // dns响应数据
            var response = Response.FromRequest(request);
            var loopback = question.Type == RecordType.A ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            var record = new IPAddressResourceRecord(domain, loopback, this.ttl);
            response.AnswerRecords.Add(record);

            // 修改payload
            var writer = packet.GetWriter(packet.Length - result.DataLength);
            writer.Write(response.ToArray());

            packet.ReverseEndPoint();
            packet.ApplyLengthToHeaders();
            packet.CalcChecksums(addr);
            packet.CalcOutboundFlag(addr);

            addr.Flags |= WinDivertAddressFlag.Impostor;
            this.logger.LogInformation($"{domain}->{loopback}");
        }

        /// <summary>
        /// 是否为标准的DNS查询请求
        /// 只在报文头层面判断，不解析域名，因此没有任何托管分配
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private static bool IsStandardQuery(ReadOnlySpan<byte> data)
        {
            if (data.Length < HEADER_LENGTH)
            {
                return false;
            }

            var flags = (ushort)((data[2] << 8) | data[3]);
            if ((flags & FLAG_RESPONSE) != 0)
            {
                return false;
            }

            var operationCode = (flags >> 11) & 0x0F;
            if (operationCode != OPCODE_QUERY)
            {
                return false;
            }

            var questionCount = (ushort)((data[4] << 8) | data[5]);
            return questionCount > 0;
        }

        /// <summary>
        /// 尝试解析请求
        /// </summary>
        /// <param name="payload"></param>
        /// <param name="request"></param>
        /// <returns></returns>
        static bool TryParseRequest(byte[] payload, [MaybeNullWhen(false)] out Request request)
        {
            try
            {
                request = Request.FromArray(payload);
                return true;
            }
            catch (Exception)
            {
                request = null;
                return false;
            }
        }
    }
}
