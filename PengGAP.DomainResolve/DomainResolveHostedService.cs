using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PengGAP.DomainResolve
{
    /// <summary>
    /// 域名解析后台服务
    /// </summary>
    sealed class DomainResolveHostedService : BackgroundService
    {
        /// <summary>
        /// 巡检周期：保持每秒一轮，让「最快 IP」始终跟随当前网络状况
        /// </summary>
        private static readonly TimeSpan testPeriodTimeSpan = TimeSpan.FromSeconds(1d);

        private readonly DnscryptProxy dnscryptProxy;
        private readonly IDomainResolver domainResolver;
        private readonly ILogger<DomainResolveHostedService> logger;
        private readonly TimeSpan dnscryptProxyInitDelay = TimeSpan.FromSeconds(5d);

        /// <summary>
        /// 域名解析后台服务
        /// </summary>
        /// <param name="dnscryptProxy"></param>
        /// <param name="domainResolver"></param>
        /// <param name="logger"></param>
        public DomainResolveHostedService(
            DnscryptProxy dnscryptProxy,
            IDomainResolver domainResolver,
            ILogger<DomainResolveHostedService> logger)
        {
            this.dnscryptProxy = dnscryptProxy;
            this.domainResolver = domainResolver;
            this.logger = logger;
        }

        /// <summary>
        /// 后台任务
        /// 上一轮测速结束后再补足 1 秒间隔：既不重叠执行，也不会因耗时长而停摆
        /// </summary>
        /// <param name="stoppingToken"></param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await this.dnscryptProxy.StartAsync(stoppingToken);
                await Task.Delay(this.dnscryptProxyInitDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (stoppingToken.IsCancellationRequested == false)
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    await this.domainResolver.TestSpeedAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "域名解析异常");
                }
                finally
                {
                    stopwatch.Stop();
                }

                var delay = testPeriodTimeSpan - stopwatch.Elapsed;
                if (delay <= TimeSpan.Zero)
                {
                    continue;
                }

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        /// <param name="cancellationToken"></param>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            this.dnscryptProxy.Stop();
            return base.StopAsync(cancellationToken);
        }
    }
}
