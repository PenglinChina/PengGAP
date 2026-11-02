using PengGAP.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace PengGAP.PacketIntercept.Dns
{
    /// <summary>
    /// 代理冲突解决者
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class ProxyConflictSolver : IDnsConflictSolver
    {
        private const int INTERNET_OPTION_REFRESH = 37;
        private const int INTERNET_OPTION_PROXY_SETTINGS_CHANGED = 95;

        private const char PROXYOVERRIDE_SEPARATOR = ';';
        private const string PROXYOVERRIDE_KEY = "ProxyOverride";
        private const string INTERNET_SETTINGS = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private readonly IOptions<PengGAPOptions> options;
        private readonly ILogger<ProxyConflictSolver> logger;

        [DllImport("wininet.dll")]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);


        /// <summary>
        /// 代理冲突解决者
        /// </summary>
        /// <param name="options"></param>
        /// <param name="logger"></param>
        public ProxyConflictSolver(
            IOptions<PengGAPOptions> options,
            ILogger<ProxyConflictSolver> logger)
        {
            this.options = options;
            this.logger = logger;
        }

        /// <summary>
        /// 解决冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task SolveAsync(CancellationToken cancellationToken)
        {
            this.SetToProxyOvride();
            this.CheckProxyConflict();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 恢复冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task RestoreAsync(CancellationToken cancellationToken)
        {
            this.RemoveFromProxyOvride();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 添加到ProxyOvride
        /// </summary>
        private void SetToProxyOvride()
        {
            using var settings = Registry.CurrentUser.OpenSubKey(INTERNET_SETTINGS, writable: true);
            if (settings == null)
            {
                return;
            }

            var configKeys = new HashSet<string>(this.options.Value.DomainConfigs.Keys, StringComparer.OrdinalIgnoreCase);

            // 先剔除历史写入的条目：旧版本把 *.github.com 这类通配符也写了进去，而 ProxyOverride 不支持通配符
            var items = GetProxyOvride(settings)
                .Where(item => configKeys.Contains(item) == false)
                .ToList();

            var added = 0;
            foreach (var domain in configKeys.Where(IsLiteralHost))
            {
                if (items.Any(item => string.Equals(item, domain, StringComparison.OrdinalIgnoreCase)) == false)
                {
                    items.Add(domain);
                    added++;
                }
            }

            SetProxyOvride(settings, items);

            if (added > 0)
            {
                this.logger.LogInformation($"已把{added}个字面量域名加入代理例外列表（通配符规则无法表达，已跳过）");
            }
        }

        /// <summary>
        /// 从ProxyOvride移除
        /// </summary>
        private void RemoveFromProxyOvride()
        {
            using var settings = Registry.CurrentUser.OpenSubKey(INTERNET_SETTINGS, writable: true);
            if (settings == null)
            {
                return;
            }

            var configKeys = new HashSet<string>(this.options.Value.DomainConfigs.Keys, StringComparer.OrdinalIgnoreCase);
            var items = GetProxyOvride(settings)
                .Where(item => configKeys.Contains(item) == false)
                .ToList();

            SetProxyOvride(settings, items);
        }

        /// <summary>
        /// 是否是字面量主机名
        /// Windows 的 ProxyOverride 只接受字面量，不支持 *.example.com 这种通配符
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private static bool IsLiteralHost(string value)
        {
            return string.IsNullOrWhiteSpace(value) == false
                && value.Contains('*') == false
                && value.Contains('?') == false;
        }

        /// <summary>
        /// 检测代理冲突
        /// </summary>
        private void CheckProxyConflict()
        {
            var systemProxy = HttpClient.DefaultProxy;
            if (systemProxy == null)
            {
                return;
            }

            foreach (var domain in this.options.Value.DomainConfigs.Keys)
            {
                var destination = new Uri($"https://{domain.Replace('*', 'a')}");
                var proxyServer = systemProxy.GetProxy(destination);
                if (proxyServer != null)
                {
                    this.logger.LogError($"由于系统设置了代理{proxyServer}，{nameof(PengGAP)}无法加速{domain}");
                }
            }
        }

        /// <summary>
        /// 获取ProxyOverride
        /// </summary>
        /// <param name="registryKey"></param>
        /// <returns></returns>
        private static string[] GetProxyOvride(RegistryKey registryKey)
        {
            var value = registryKey.GetValue(PROXYOVERRIDE_KEY, null)?.ToString();
            if (value == null)
            {
                return Array.Empty<string>();
            }

            return value
                .Split(PROXYOVERRIDE_SEPARATOR, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .ToArray();
        }

        /// <summary>
        /// 设置ProxyOverride
        /// </summary>
        /// <param name="registryKey"></param>
        /// <param name="items"></param>
        private static void SetProxyOvride(RegistryKey registryKey, IReadOnlyList<string> items)
        {
            var value = string.Join(PROXYOVERRIDE_SEPARATOR, items);
            if (string.Equals(registryKey.GetValue(PROXYOVERRIDE_KEY)?.ToString(), value, StringComparison.Ordinal) == true)
            {
                return;
            }

            registryKey.SetValue(PROXYOVERRIDE_KEY, value, RegistryValueKind.String);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PROXY_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }
}
