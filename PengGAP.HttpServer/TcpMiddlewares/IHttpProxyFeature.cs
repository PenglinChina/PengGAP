using Microsoft.AspNetCore.Http;

namespace PengGAP.HttpServer.TcpMiddlewares
{
    interface IHttpProxyFeature
    {
        HostString ProxyHost { get; }

        ProxyProtocol ProxyProtocol { get; }
    }
}
