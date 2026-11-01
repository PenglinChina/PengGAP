using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Threading;

namespace PengGAP.Configuration
{
    /// <summary>
    /// PengGAP配置
    /// </summary>
    public class PengGAPConfig
    {
        /// <summary>
        /// 匹配缓存的最大条目数
        /// 伪造子域名（如 CDN 的随机前缀）会让缓存无限增长，因此必须有上限
        /// </summary>
        private const int MAX_CACHE_COUNT = 4096;

        /// <summary>
        /// 不可变的配置快照
        /// 域名表达式字典与匹配缓存必须成对替换，否则热更新瞬间会读到"新字典 + 旧缓存"的组合
        /// </summary>
        private sealed record ConfigSnapshot(
            int HttpProxyPort,
            IPEndPoint[] FallbackDns,
            SortedDictionary<DomainPattern, DomainConfig> DomainConfigs,
            ConcurrentDictionary<string, DomainConfig?> DomainConfigCache);

        private ConfigSnapshot snapshot;

        /// <summary>
        /// http代理端口
        /// </summary>
        public int HttpProxyPort => this.snapshot.HttpProxyPort;

        /// <summary>
        /// 回退的dns
        /// </summary>
        public IPEndPoint[] FallbackDns => this.snapshot.FallbackDns;

        /// <summary>
        /// PengGAP配置
        /// </summary>
        /// <param name="options"></param>
        public PengGAPConfig(IOptionsMonitor<PengGAPOptions> options)
        {
            this.snapshot = CreateSnapshot(options.CurrentValue);
            options.OnChange(opt => Volatile.Write(ref this.snapshot, CreateSnapshot(opt)));
        }

        /// <summary>
        /// 由选项创建快照
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static ConfigSnapshot CreateSnapshot(PengGAPOptions options)
        {
            var domainConfigs = new SortedDictionary<DomainPattern, DomainConfig>();
            foreach (var kv in options.DomainConfigs)
            {
                domainConfigs.Add(new DomainPattern(kv.Key), kv.Value);
            }

            return new ConfigSnapshot(
                options.HttpProxyPort,
                options.FallbackDns,
                domainConfigs,
                new ConcurrentDictionary<string, DomainConfig?>());
        }

        /// <summary>
        /// 是否匹配指定的域名
        /// </summary>
        /// <param name="domain"></param>
        /// <returns></returns>
        public bool IsMatch(string domain)
        {
            return this.TryGetDomainConfig(domain, out _);
        }

        /// <summary>
        /// 尝试获取域名配置
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public bool TryGetDomainConfig(string domain, [MaybeNullWhen(false)] out DomainConfig value)
        {
            var current = Volatile.Read(ref this.snapshot);
            var cache = current.DomainConfigCache;

            if (cache.TryGetValue(domain, out var cached) == true)
            {
                value = cached;
                return cached != null;
            }

            value = FindDomainConfig(current.DomainConfigs, domain);
            if (cache.Count < MAX_CACHE_COUNT)
            {
                cache[domain] = value;
            }
            return value != null;
        }

        /// <summary>
        /// 在字典中查找域名配置
        /// </summary>
        /// <param name="domainConfigs"></param>
        /// <param name="domain"></param>
        /// <returns></returns>
        private static DomainConfig? FindDomainConfig(SortedDictionary<DomainPattern, DomainConfig> domainConfigs, string domain)
        {
            foreach (var kv in domainConfigs)
            {
                if (kv.Key.IsMatch(domain) == true)
                {
                    return kv.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// 获取所有域名表达式
        /// </summary>
        /// <returns></returns>
        public DomainPattern[] GetDomainPatterns()
        {
            return Volatile.Read(ref this.snapshot).DomainConfigs.Keys.ToArray();
        }
    }
}
