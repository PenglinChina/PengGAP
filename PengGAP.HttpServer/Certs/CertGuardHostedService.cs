using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PengGAP.HttpServer.Certs
{
    /// <summary>
    /// 根CA守护服务
    /// 运行期间定期检查剩余有效期，不足阈值时就地轮换并重新安装信任，
    /// 避免长时间不重启导致根CA在运行中过期、全站证书报错
    /// </summary>
    sealed class CertGuardHostedService : BackgroundService
    {
        /// <summary>
        /// 检查周期
        /// </summary>
        private static readonly TimeSpan checkPeriod = TimeSpan.FromMinutes(30d);

        private readonly CertService certService;
        private readonly ILogger<CertGuardHostedService> logger;

        /// <summary>
        /// 根CA守护服务
        /// </summary>
        /// <param name="certService"></param>
        /// <param name="logger"></param>
        public CertGuardHostedService(CertService certService, ILogger<CertGuardHostedService> logger)
        {
            this.certService = certService;
            this.logger = logger;
        }

        /// <summary>
        /// 周期检查
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (stoppingToken.IsCancellationRequested == false)
            {
                try
                {
                    await Task.Delay(checkPeriod, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    // 剩余有效期不足 1 天时会重新生成，并重装信任、回读校验
                    this.certService.CheckAndRotate();
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "根CA守护检查失败");
                }
            }
        }
    }
}
