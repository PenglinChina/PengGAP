using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace PengGAP.DomainResolve
{
    /// <summary>
    /// 域名解析器
    /// </summary> 
    sealed class DomainResolver : IDomainResolver
    {
        private const int MAX_IP_COUNT = 3;

        /// <summary>
        /// 测速并发上限，避免一次性向所有节点发起连接
        /// </summary>
        private const int MAX_DEGREE_OF_PARALLELISM = 8;

        private readonly DnsClient dnsClient;
        private readonly PersistenceService persistence;
        private readonly IPAddressService addressService;
        private readonly ILogger<DomainResolver> logger;
        private readonly ConcurrentDictionary<DnsEndPoint, IPAddress[]> dnsEndPointAddress = new();

        /// <summary>
        /// 域名解析器
        /// </summary>
        /// <param name="dnsClient"></param>
        /// <param name="persistence"></param>
        /// <param name="addressService"></param>
        /// <param name="logger"></param>
        public DomainResolver(
            DnsClient dnsClient,
            PersistenceService persistence,
            IPAddressService addressService,
            ILogger<DomainResolver> logger)
        {
            this.dnsClient = dnsClient;
            this.persistence = persistence;
            this.addressService = addressService;
            this.logger = logger;

            foreach (var endPoint in persistence.ReadDnsEndPoints())
            {
                this.dnsEndPointAddress.TryAdd(endPoint, Array.Empty<IPAddress>());
            }
        }

        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="cancellationToken"></param>
        public async IAsyncEnumerable<IPAddress> ResolveAsync(DnsEndPoint endPoint, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (this.dnsEndPointAddress.TryGetValue(endPoint, out var addresses) && addresses.Length > 0)
            {
                foreach (var address in addresses)
                {
                    yield return address;
                }
            }
            else
            {
                if (this.dnsEndPointAddress.TryAdd(endPoint, Array.Empty<IPAddress>()))
                {
                    await this.persistence.WriteDnsEndPointsAsync(this.dnsEndPointAddress.Keys, cancellationToken);
                }

                await foreach (var adddress in this.dnsClient.ResolveAsync(endPoint, fastSort: true, cancellationToken))
                {
                    yield return adddress;
                }
            }
        }

        /// <summary>
        /// 对所有节点进行测速
        /// 每一轮都会重新测量全部节点，保证「最快 IP」始终是当前时刻的真实结果
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task TestSpeedAsync(CancellationToken cancellationToken)
        {
            // 取快照，避免遍历时字典被 ResolveAsync 修改
            var endPoints = this.dnsEndPointAddress
                .OrderBy(item => item.Value.Length)
                .ToArray();

            if (endPoints.Length == 0)
            {
                return;
            }

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = MAX_DEGREE_OF_PARALLELISM,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(endPoints, options, async (item, token) =>
            {
                var dnsEndPoint = item.Key;
                var oldAddresses = item.Value;
                var newAddresses = await this.addressService.GetAddressesAsync(dnsEndPoint, oldAddresses, token);
                this.dnsEndPointAddress[dnsEndPoint] = newAddresses;

                var oldSegmentums = oldAddresses.Take(MAX_IP_COUNT);
                var newSegmentums = newAddresses.Take(MAX_IP_COUNT);
                if (oldSegmentums.SequenceEqual(newSegmentums) == false)
                {
                    var addressArray = string.Join(", ", newSegmentums.Select(address => address.ToString()));
                    this.logger.LogInformation($"{dnsEndPoint.Host}:{dnsEndPoint.Port}->[{addressArray}]");
                }
            });
        }
    }
}
