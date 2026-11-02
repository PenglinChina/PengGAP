using PengGAP.Configuration;
using PengGAP.Http;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading.Tasks;
using Yarp.ReverseProxy.Forwarder;

namespace PengGAP.HttpServer.HttpMiddlewares
{
    /// <summary>
    /// 反向代理中间件
    /// </summary>
    sealed class HttpReverseProxyMiddleware
    {
        private static readonly DomainConfig defaultDomainConfig = new() { TlsSni = true };

        /// <summary>
        /// 已记录过配置的域名
        /// </summary>
        private static readonly ConcurrentDictionary<string, bool> loggedHosts = new();

        private readonly IHttpForwarder httpForwarder;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly PengGAPConfig pengGapConfig;
        private readonly ILogger<HttpReverseProxyMiddleware> logger;

        public HttpReverseProxyMiddleware(
            IHttpForwarder httpForwarder,
            IHttpClientFactory httpClientFactory,
            PengGAPConfig pengGapConfig,
            ILogger<HttpReverseProxyMiddleware> logger)
        {
            this.httpForwarder = httpForwarder;
            this.httpClientFactory = httpClientFactory;
            this.pengGapConfig = pengGapConfig;
            this.logger = logger;
        }

        /// <summary>
        /// 处理请求
        /// </summary>
        /// <param name="context"></param>
        /// <param name="next"?
        /// <returns></returns>
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            var host = context.Request.Host;
            if (TryGetDomainConfig(host, out var domainConfig) == false)
            {
                await next(context);
            }
            else if (domainConfig.Response == null)
            {
                var scheme = context.Request.Scheme;
                var destinationPrefix = GetDestinationPrefix(scheme, host, domainConfig.Destination);
                LogDomainConfigOnce(host.Host, domainConfig);
                var httpClient = httpClientFactory.CreateHttpClient(host.Host, domainConfig);
                var error = await httpForwarder.SendAsync(context, destinationPrefix, httpClient, ForwarderRequestConfig.Empty, HttpTransformer.Empty);
                await HandleErrorAsync(context, error);
            }
            else
            {
                context.Response.StatusCode = domainConfig.Response.StatusCode;
                context.Response.ContentType = domainConfig.Response.ContentType;
                if (domainConfig.Response.ContentValue != null)
                {
                    await context.Response.WriteAsync(domainConfig.Response.ContentValue);
                }
            }
        }

        /// <summary>
        /// 获取域名的DomainConfig
        /// </summary>
        /// <param name="host"></param>
        /// <param name="domainConfig"></param>
        /// <returns></returns>
        private bool TryGetDomainConfig(HostString host, [MaybeNullWhen(false)] out DomainConfig domainConfig)
        {
            if (pengGapConfig.TryGetDomainConfig(host.Host, out domainConfig) == true)
            {
                return true;
            }

            // 未配置的域名，但仍然被解析到本机ip的域名
            if (OperatingSystem.IsWindows() && IsDomain(host.Host))
            {
                logger.LogWarning($"域名{host.Host}可能已经被DNS污染，如果域名为本机域名，请解析为非回环IP");
                domainConfig = defaultDomainConfig;
                return true;
            }

            return false;

            // 是否为域名
            static bool IsDomain(string host)
            {
                return IPAddress.TryParse(host, out _) == false && host.Contains('.');
            }
        }

        /// <summary>
        /// 获取目标前缀
        /// </summary>
        /// <param name="scheme"></param>
        /// <param name="host"></param>
        /// <param name="destination"></param>
        /// <returns></returns>
        private string GetDestinationPrefix(string scheme, HostString host, Uri? destination)
        {
            var defaultValue = $"{scheme}://{host}/";
            if (destination == null)
            {
                return defaultValue;
            }

            var baseUri = new Uri(defaultValue);
            var result = new Uri(baseUri, destination).ToString();
            logger.LogInformation($"{defaultValue} => {result}");
            return result;
        }

        /// <summary>
        /// 每个域名只记录一次解析到的域名配置，用于定位证书校验相关的问题
        /// </summary>
        /// <param name="host"></param>
        /// <param name="domainConfig"></param>
        private void LogDomainConfigOnce(string host, DomainConfig domainConfig)
        {
            if (loggedHosts.TryAdd(host, true) == false)
            {
                return;
            }
            this.logger.LogInformation($"域名配置 host={host} TlsSni={domainConfig.TlsSni} TlsIgnoreNameMismatch={domainConfig.TlsIgnoreNameMismatch} TlsSniPattern=[{domainConfig.TlsSniPattern}]");
        }

        /// <summary>
        /// 处理错误信息
        /// </summary>
        /// <param name="context"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        private async Task HandleErrorAsync(HttpContext context, ForwarderError error)
        {
            if (error == ForwarderError.None || context.Response.HasStarted)
            {
                return;
            }

            var exception = context.GetForwarderErrorFeature()?.Exception;
            var message = $"转发失败 host={context.Request.Host.Value} path={context.Request.Path} error={error} message={exception?.Message}";

            // 客户端主动取消（页面跳转、组件卸载）属于正常行为，
            // 记成 Error 会把真正的故障淹没在噪音里
            if (error == ForwarderError.RequestCanceled)
            {
                this.logger.LogDebug(message);
            }
            else
            {
                this.logger.LogError(message);
                if (exception != null)
                {
                    this.logger.LogDebug(exception, $"转发失败的异常详情 host={context.Request.Host.Value}");
                }
            }

            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            var response = new ForwarderErrorResponse
            {
                Error = error.ToString(),
                Message = exception?.Message
            };
            await context.Response.WriteAsJsonAsync(response, ForwarderErrorContext.Default.ForwarderErrorResponse);
        }
    }
}
