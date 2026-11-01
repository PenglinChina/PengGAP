using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace PengGAP.UI
{
    class Program
    {
        private const string MUTEX_NAME = "Global\\PengGAP.UI";
        private const string MAIN_WINDOWS = "MainWindow.xaml";
        private const string PENGGAP_PATH = "penggap.exe";

        /// <summary>
        /// 承载后端进程的作业对象
        /// 界面进程一退出，系统就会连带结束后端，不会留下孤儿进程
        /// </summary>
        private static JobObject? backendJob;

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            using var mutex = new Mutex(true, MUTEX_NAME, out var isFirstInstance);
            if (isFirstInstance == false)
            {
                return;
            }

            StartPengGAP();

            var app = new Application();
            app.StartupUri = new Uri(MAIN_WINDOWS, UriKind.Relative);
            app.Exit += (s, e) => DisposeBackendJob();
            app.Run();

            DisposeBackendJob();
        }

        /// <summary>
        /// 释放作业对象（等于结束后端进程）
        /// </summary>
        private static void DisposeBackendJob()
        {
            var job = backendJob;
            backendJob = null;
            if (job != null)
            {
                job.Dispose();
            }
        }

        /// <summary>
        /// 程序集加载失败时
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        private static Assembly? OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (name.EndsWith(".resources"))
            {
                return default;
            }

            var stream = Application.GetResourceStream(new Uri($"Resource/{name}.dll", UriKind.Relative)).Stream;
            var buffer = new byte[stream.Length];
            stream.Read(buffer, 0, buffer.Length);
            return Assembly.Load(buffer);
        }

        /// <summary>
        /// 启动penggap
        /// 参数必须是 --key=value 形式：.NET 的命令行配置提供程序会丢弃不带前缀的裸参数
        /// </summary>
        /// <returns></returns>
        private static void StartPengGAP()
        {
            if (File.Exists(PENGGAP_PATH) == false)
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = PENGGAP_PATH,
                Arguments = $"--ParentProcessId={Process.GetCurrentProcess().Id} --UdpLoggerPort={UdpLogger.Port}",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    return;
                }

                var job = new JobObject();
                if (job.TryAssign(process) == true)
                {
                    backendJob = job;
                }
                else
                {
                    job.Dispose();
                }
            }
            catch (Exception)
            {
                backendJob = null;
            }
        }
    }
}
