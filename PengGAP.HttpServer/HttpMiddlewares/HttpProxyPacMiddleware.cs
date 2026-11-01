using PengGAP.Configuration;
using PengGAP.HttpServer.TcpMiddlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PengGAP.HttpServer.HttpMiddlewares
{
    /// <summary>
    /// http代理策略中间件
    /// </summary>
    sealed class HttpProxyPacMiddleware
    {
        /// <summary>
        /// pac 脚本缓存时长，域名配置变更后最多 1 分钟生效
        /// </summary>
        private static readonly TimeSpan cacheExpiration = TimeSpan.FromMinutes(1d);

        private readonly PengGAPConfig pengGapConfig;
        private readonly IMemoryCache cache;

        /// <summary>
        /// http代理策略中间件
        /// </summary>
        /// <param name="pengGapConfig"></param>
        /// <param name="cache"></param>
        public HttpProxyPacMiddleware(PengGAPConfig pengGapConfig, IMemoryCache cache)
        {
            this.pengGapConfig = pengGapConfig;
            this.cache = cache;
        }

        /// <summary>
        /// 处理请求
        /// </summary>
        /// <param name="context"></param>
        /// <param name="next"></param>
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            // http请求经过了httpProxy中间件
            var proxyFeature = context.Features.Get<IHttpProxyFeature>();
            if (proxyFeature != null && proxyFeature.ProxyProtocol == ProxyProtocol.None)
            {
                var proxyPac = this.cache.GetOrCreate($"proxyPac:{context.Request.Host.Value}", entry =>
                {
                    entry.SetAbsoluteExpiration(cacheExpiration);
                    return this.CreateProxyPac(context.Request.Host);
                });

                context.Response.ContentType = "application/x-ns-proxy-autoconfig";
                context.Response.Headers["Content-Disposition"] = "attachment;filename=proxy.pac";
                await context.Response.WriteAsync(proxyPac);
            }
            else
            {
                await next(context);
            }
        }

        /// <summary>
        /// 创建proxypac脚本
        /// </summary>
        /// <param name="proxyHost"></param>
        /// <returns></returns>
        private string CreateProxyPac(HostString proxyHost)
        {
            var buidler = new StringBuilder();
            buidler.AppendLine("function FindProxyForURL(url, host){");
            buidler.AppendLine($"    var penggap = 'PROXY {proxyHost}';");
            foreach (var domain in this.pengGapConfig.GetDomainPatterns())
            {
                buidler.AppendLine($"    if (shExpMatch(host, '{domain}')) return penggap;");
            }
            buidler.AppendLine("    return 'DIRECT';");
            buidler.AppendLine("}");
            return buidler.ToString();
        }
    }
}
