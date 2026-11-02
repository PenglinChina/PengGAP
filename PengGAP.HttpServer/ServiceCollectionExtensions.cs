using PengGAP.HttpServer.Certs;
using PengGAP.HttpServer.Certs.CaCertInstallers;
using PengGAP.HttpServer.HttpMiddlewares;
using PengGAP.HttpServer.TcpMiddlewares;
using PengGAP.HttpServer.TlsMiddlewares;
using Microsoft.Extensions.DependencyInjection;
namespace PengGAP
{
    /// <summary>
    /// http反向代理的服务注册扩展
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 添加http反向代理
        /// </summary>
        /// <param name="services"></param> 
        /// <returns></returns>
        public static IServiceCollection AddReverseProxy(this IServiceCollection services)
        {
            return services
                // 叶证书与PAC脚本共用这个缓存，设置容量上限，避免被随机域名或Host刷爆
                .AddMemoryCache(options => options.SizeLimit = 2048)
                .AddHttpForwarder()
                .AddSingleton<CertService>()
                // 运行期间守护根CA：到期前自动轮换并重装信任
                .AddHostedService<CertGuardHostedService>()
                .AddSingleton<ICaCertInstaller, CaCertInstallerOfMacOS>()
                .AddSingleton<ICaCertInstaller, CaCertInstallerOfWindows>()
                .AddSingleton<ICaCertInstaller, CaCertInstallerOfLinuxRedHat>()
                .AddSingleton<ICaCertInstaller, CaCertInstallerOfLinuxDebian>()

                // tcp
                .AddSingleton<HttpProxyMiddleware>()
                .AddSingleton<TunnelMiddleware>()

                // tls
                .AddSingleton<TlsInvadeMiddleware>()
                .AddSingleton<TlsRestoreMiddleware>()

                // http
                .AddSingleton<HttpProxyPacMiddleware>()
                .AddSingleton<RequestLoggingMiddleware>()
                .AddSingleton<HttpReverseProxyMiddleware>();
        }
    }
}
