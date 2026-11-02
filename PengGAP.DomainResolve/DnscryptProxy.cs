using PengGAP.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static PInvoke.AdvApi32;

namespace PengGAP.DomainResolve
{
    /// <summary>
    /// DnscryptProxy服务
    /// </summary>
    sealed class DnscryptProxy : IDisposable
    {
        /// <summary>
        /// 结束子进程后的等待上限
        /// </summary>
        private static readonly TimeSpan killWaitTimeout = TimeSpan.FromSeconds(3d);

        private readonly ILogger<DnscryptProxy> logger;
        private readonly string processName;
        private readonly string serviceName;
        private readonly string exeFilePath;
        private readonly string tomlFilePath;

        /// <summary>
        /// 停止操作的同步锁与状态，保证 Stop 可重入且只生效一次
        /// </summary>
        private readonly object stopLocker = new();
        private bool stopped;

        /// <summary>
        /// 相关进程
        /// </summary>
        private Process? process;

        /// <summary>
        /// 承载子进程的作业对象
        /// 父进程无论以何种方式退出，系统都会结束其中的进程
        /// </summary>
        private JobObject? jobObject;

        /// <summary>
        /// 获取监听的节点
        /// </summary>
        public IPEndPoint? LocalEndPoint { get; private set; }

        /// <summary>
        /// DnscryptProxy服务
        /// </summary>
        /// <param name="logger"></param>
        public DnscryptProxy(ILogger<DnscryptProxy> logger)
        {
            const string PATH = "dnscrypt-proxy";
            const string NAME = "dnscrypt-proxy";

            this.logger = logger;
            this.processName = NAME;
            this.serviceName = $"{nameof(PengGAP)}.{NAME}";
            this.exeFilePath = Path.Combine(PATH, OperatingSystem.IsWindows() ? $"{NAME}.exe" : NAME);
            this.tomlFilePath = Path.Combine(PATH, $"{NAME}.toml");

            // 兜底：宿主进程退出时确保子进程被清理
            AppDomain.CurrentDomain.ProcessExit += (s, e) => this.Stop();
        }

        /// <summary>
        /// 启动dnscrypt-proxy
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await this.StartCoreAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogError($"{this.processName}启动失败：{ex.Message}；纯净DNS不可用，已回退到普通DNS");
            }
        }

        /// <summary>
        /// 启动dnscrypt-proxy
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task StartCoreAsync(CancellationToken cancellationToken)
        {
            // 上次异常退出（崩溃 / 强杀）可能留下同名进程，先清理
            this.KillStaleProcesses();

            var port = GlobalListener.GetAvailablePort(5533);
            var localEndPoint = new IPEndPoint(IPAddress.Loopback, port);

            // 一次读写完成全部改写，避免反复重写配置文件
            await TomlUtil.SetRuntimeAsync(this.tomlFilePath, localEndPoint, cancellationToken);

            if (OperatingSystem.IsWindows() && Environment.UserInteractive == false)
            {
                ServiceInstallUtil.StopAndDeleteService(this.serviceName);
                if (ServiceInstallUtil.InstallAndStartService(this.serviceName, this.exeFilePath, ServiceStartType.SERVICE_DEMAND_START) == false)
                {
                    this.logger.LogError($"无法把{this.processName}安装为Windows服务，纯净DNS不可用");
                }
                this.process = Process.GetProcessesByName(this.processName).FirstOrDefault(item => item.SessionId == 0);
            }
            else
            {
                this.process = StartDnscryptProxy();
            }

            if (this.process == null)
            {
                this.logger.LogError($"{this.processName}未能启动，纯净DNS不可用，已回退到普通DNS");
                return;
            }

            this.LocalEndPoint = localEndPoint;
            this.process.EnableRaisingEvents = true;
            this.process.Exited += (s, e) =>
            {
                this.LocalEndPoint = null;
                this.logger.LogError($"{this.processName}已退出，纯净DNS不可用，已回退到普通DNS");
            };
        }

        /// <summary>
        /// 清理上次运行残留的同名进程
        /// 只结束镜像路径与本程序 dnscrypt-proxy 完全一致的进程，避免误杀
        /// </summary>
        private void KillStaleProcesses()
        {
            string expectedPath;
            try
            {
                expectedPath = Path.GetFullPath(this.exeFilePath);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var item in Process.GetProcessesByName(this.processName))
            {
                try
                {
                    var mainModule = item.MainModule;
                    if (mainModule == null || string.Equals(mainModule.FileName, expectedPath, StringComparison.OrdinalIgnoreCase) == false)
                    {
                        continue;
                    }

                    this.logger.LogWarning($"清理上次运行残留的 {this.processName} 进程（PID {item.Id}）");
                    item.Kill(entireProcessTree: true);
                    item.WaitForExit((int)killWaitTimeout.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning($"清理残留进程失败：{ex.Message}");
                }
                finally
                {
                    item.Dispose();
                }
            }
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        public void Stop()
        {
            lock (this.stopLocker)
            {
                if (this.stopped == true)
                {
                    return;
                }
                this.stopped = true;
            }

            try
            {
                if (OperatingSystem.IsWindows() && Environment.UserInteractive == false)
                {
                    ServiceInstallUtil.StopAndDeleteService(this.serviceName);
                }

                var item = this.process;
                this.process = null;

                if (item != null && item.HasExited == false)
                {
                    // entireProcessTree 一并结束 dnscrypt-proxy 可能派生的子进程
                    item.Kill(entireProcessTree: true);
                    if (item.WaitForExit((int)killWaitTimeout.TotalMilliseconds) == false)
                    {
                        this.logger.LogWarning($"{this.processName}（PID {item.Id}）未能在 {killWaitTimeout.TotalSeconds} 秒内退出");
                    }
                }
                item?.Dispose();
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"{this.processName}停止失败：{ex.Message}");
            }
            finally
            {
                // 关闭作业句柄会连带结束其中残余的进程
                if (OperatingSystem.IsWindows())
                {
                    this.jobObject?.Dispose();
                    this.jobObject = null;
                }
                this.LocalEndPoint = null;
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            this.Stop();
        }

        /// <summary>
        /// 启动DnscryptProxy进程
        /// </summary> 
        /// <returns></returns>
        private Process? StartDnscryptProxy()
        {
            var item = Process.Start(new ProcessStartInfo
            {
                FileName = this.exeFilePath,
                WorkingDirectory = Path.GetDirectoryName(this.exeFilePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (item != null && OperatingSystem.IsWindows())
            {
                try
                {
                    this.jobObject = new JobObject();
                    if (this.jobObject.TryAssign(item) == false)
                    {
                        this.logger.LogWarning($"无法把{this.processName}加入作业对象，父进程被强杀时可能残留子进程");
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning($"创建作业对象失败：{ex.Message}");
                }
            }

            return item;
        }

        /// <summary>
        /// 转换为字符串
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return this.processName;
        }
    }
}
