using PengGAP.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PengGAP
{
    /// <summary>
    /// app后台服务
    /// </summary>
    sealed class AppHostedService : BackgroundService
    {
        private readonly IHost host;
        private readonly IOptions<AppOptions> appOptions;
        private readonly IOptions<PengGAPOptions> pengGapOptions;
        private readonly ILogger<AppHostedService> logger;

        public AppHostedService(
            IHost host,
            IOptions<AppOptions> appOptions,
            IOptions<PengGAPOptions> pengGapOptions,
            ILogger<AppHostedService> logger)
        {
            this.host = host;
            this.appOptions = appOptions;
            this.pengGapOptions = pengGapOptions;
            this.logger = logger;
        }

        /// <summary>
        /// 启动完成
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            var version = ProductionVersion.RawText;
            this.logger.LogInformation($"{nameof(PengGAP)}启动完成，当前版本为v{version}");
            this.LogConfigurationSummary();
            return base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// 输出配置自检信息
        /// 裁剪发布下若配置绑定被破坏，这里的数字会一起退化，从而变成可观测的失败
        /// </summary>
        private void LogConfigurationSummary()
        {
            try
            {
                var options = this.pengGapOptions.Value;
                var configs = options.DomainConfigs.Values.ToArray();
                var fallbackDnsCount = options.FallbackDns == null ? 0 : options.FallbackDns.Length;

                var ignoreNameMismatchCount = configs.Count(item => item.TlsIgnoreNameMismatch);
                var tlsSniCount = configs.Count(item => item.TlsSni);
                var destinationCount = configs.Count(item => item.Destination != null);
                var responseCount = configs.Count(item => item.Response != null);

                this.logger.LogInformation(
                    $"配置自检：规则{configs.Length}条 | TlsIgnoreNameMismatch={ignoreNameMismatchCount}条 | " +
                    $"TlsSni={tlsSniCount}条 | Destination={destinationCount}条 | Response={responseCount}条 | " +
                    $"FallbackDns={fallbackDnsCount}条");

                if (configs.Length > 0 && ignoreNameMismatchCount == 0 && tlsSniCount == 0 && destinationCount == 0)
                {
                    this.logger.LogError(
                        "配置自检失败：域名规则全部为默认值，疑似裁剪删除了配置属性 setter，" +
                        "请确认 PengGAP.csproj 中的 TrimmerRootAssembly 仍保留 PengGAP.Configuration");
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"配置自检异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 后台任务
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1d), stoppingToken);
            await this.CheckPengGAPProxyAsync(stoppingToken);
            await this.WaitForParentProcessExitAsync(stoppingToken);
        }


        /// <summary>
        /// 检测penggap代理设置
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task CheckPengGAPProxyAsync(CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows() == false)
            {
                try
                {
                    if (await this.UsePengGAPProxyAsync() == false)
                    {
                        var httpProxyPort = this.pengGapOptions.Value.HttpProxyPort;
                        this.logger.LogWarning($"请设置系统自动代理为http://{IPAddress.Loopback}:{httpProxyPort}，或手动代理http/https为{IPAddress.Loopback}:{httpProxyPort}");
                    }
                }
                catch (Exception)
                {
                    this.logger.LogWarning("尝试获取代理信息失败");
                }
            }
        }


        /// <summary>
        /// 应用penggap代理
        /// </summary>
        /// <param name="proxyServer"></param>
        /// <param name="httpProxyPort"></param>
        /// <returns></returns>
        private async Task<bool> UsePengGAPProxyAsync()
        {
            var systemProxy = HttpClient.DefaultProxy;
            if (systemProxy == null)
            {
                return false;
            }

            var domain = this.pengGapOptions.Value.DomainConfigs.Keys.FirstOrDefault();
            if (domain == null)
            {
                return true;
            }

            var destination = new Uri($"https://{domain.Replace('*', 'a')}");
            var proxyServer = systemProxy.GetProxy(destination);
            if (proxyServer == null)
            {
                return false;
            }

            var httpProxyPort = this.pengGapOptions.Value.HttpProxyPort;
            if (proxyServer.Port != httpProxyPort)
            {
                return false;
            }

            if (IPAddress.TryParse(proxyServer.Host, out var address))
            {
                return IPAddress.IsLoopback(address);
            }

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(proxyServer.Host);
                return addresses.Any(item => IPAddress.IsLoopback(item));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 等待父进程退出
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task WaitForParentProcessExitAsync(CancellationToken cancellationToken)
        {
            var parentId = this.appOptions.Value.ParentProcessId;
            if (parentId <= 0)
            {
                return;
            }

            try
            {
                Process.GetProcessById(parentId).WaitForExit();
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, $"获取进程{parentId}异常");
            }
            finally
            {
                this.logger.LogInformation($"正在主动关闭，因为父进程已退出");
                await this.host.StopAsync(cancellationToken);
            }
        }

    }
}
