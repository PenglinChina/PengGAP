using PengGAP.DomainResolve;
using Microsoft.AspNetCore.Connections;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PengGAP.HttpServer.TcpMiddlewares
{
    /// <summary>
    /// tcp协议代理处理者
    /// </summary>
    abstract class TcpReverseProxyHandler : ConnectionHandler
    {
        private readonly IDomainResolver domainResolver;
        private readonly DnsEndPoint endPoint;
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(10d);

        /// <summary>
        /// tcp协议代理处理者
        /// </summary>
        /// <param name="domainResolver"></param>
        /// <param name="endPoint"></param>
        public TcpReverseProxyHandler(IDomainResolver domainResolver, DnsEndPoint endPoint)
        {
            this.domainResolver = domainResolver;
            this.endPoint = endPoint;
        }

        /// <summary>
        /// tcp连接后
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public override async Task OnConnectedAsync(ConnectionContext context)
        {
            var cancellationToken = context.ConnectionClosed;
            using var connection = await CreateConnectionAsync(cancellationToken);
            var task1 = connection.CopyToAsync(context.Transport.Output, cancellationToken);
            var task2 = context.Transport.Input.CopyToAsync(connection, cancellationToken);

            // 必须等两个方向都结束：单方向完成（例如客户端半关闭）时，另一方向往往还有数据要发，
            // 用 WhenAny 会立即离开 using 作用域释放连接，导致上游响应被截断
            try
            {
                await Task.WhenAll(task1, task2);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested == true)
            {
                // 连接已关闭引起的取消或重置，属于正常收尾
            }
        }

        /// <summary>
        /// 创建连接
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="AggregateException"></exception>
        private async Task<Stream> CreateConnectionAsync(CancellationToken cancellationToken)
        {
            var innerExceptions = new List<Exception>();
            await foreach (var address in domainResolver.ResolveAsync(endPoint, cancellationToken))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    using var timeoutTokenSource = new CancellationTokenSource(connectTimeout);
                    using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                    await socket.ConnectAsync(address, endPoint.Port, linkedTokenSource.Token);
                    // ownsSocket 必须为 true：同项目的 TunnelMiddleware 同样是 true，
                    // 否则 Stream 释放时不会关闭 Socket，句柄要等终结器才回收
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    innerExceptions.Add(ex);
                }
            }
            throw new AggregateException($"无法连接到{endPoint.Host}:{endPoint.Port}", innerExceptions);
        }
    }
}
