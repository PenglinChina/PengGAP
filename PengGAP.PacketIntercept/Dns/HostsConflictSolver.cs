using PengGAP.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PengGAP.PacketIntercept.Dns
{
    /// <summary>
    /// host文件冲解决者
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class HostsConflictSolver : IDnsConflictSolver
    {
        /// <summary>
        /// 被注释行的专用前缀
        /// 用它而不是普通的 "# "，退出时才能准确还原，且不会误动用户自己的注释
        /// </summary>
        private const string COMMENT_PREFIX = "#penggap# ";

        private readonly PengGAPConfig pengGapConfig;
        private readonly ILogger<HostsConflictSolver> logger;

        /// <summary>
        /// host文件冲解决者
        /// </summary>
        /// <param name="pengGapConfig"></param>
        /// <param name="logger"></param>
        public HostsConflictSolver(
            PengGAPConfig pengGapConfig,
            ILogger<HostsConflictSolver> logger)
        {
            this.pengGapConfig = pengGapConfig;
            this.logger = logger;
        }

        /// <summary>
        /// hosts 文件路径
        /// </summary>
        private static string HostsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers/etc/hosts");

        /// <summary>
        /// 解决冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task SolveAsync(CancellationToken cancellationToken)
        {
            var hostsPath = HostsPath;
            if (File.Exists(hostsPath) == false)
            {
                return;
            }

            Encoding hostsEncoding;
            var hasConflicting = false;
            var hostsBuilder = new StringBuilder();
            using (var fileStream = new FileStream(hostsPath, FileMode.Open, FileAccess.Read))
            {
                using var streamReader = new StreamReader(fileStream);
                while (streamReader.EndOfStream == false)
                {
                    var line = await streamReader.ReadLineAsync(cancellationToken);
                    if (this.IsConflictingLine(line) == true)
                    {
                        hasConflicting = true;
                        hostsBuilder.AppendLine(COMMENT_PREFIX + line);
                    }
                    else
                    {
                        hostsBuilder.AppendLine(line);
                    }
                }
                hostsEncoding = streamReader.CurrentEncoding;
            }


            if (hasConflicting == true)
            {
                try
                {
                    await File.WriteAllTextAsync(hostsPath, hostsBuilder.ToString(), hostsEncoding, cancellationToken);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning($"无法解决hosts文件冲突：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 恢复被注释的行
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task RestoreAsync(CancellationToken cancellationToken)
        {
            var hostsPath = HostsPath;
            if (File.Exists(hostsPath) == false)
            {
                return;
            }

            try
            {
                Encoding hostsEncoding;
                var hasRestored = false;
                var hostsBuilder = new StringBuilder();

                using (var fileStream = new FileStream(hostsPath, FileMode.Open, FileAccess.Read))
                {
                    using var streamReader = new StreamReader(fileStream);
                    while (streamReader.EndOfStream == false)
                    {
                        var line = await streamReader.ReadLineAsync(cancellationToken);
                        if (line != null && line.StartsWith(COMMENT_PREFIX, StringComparison.Ordinal) == true)
                        {
                            hasRestored = true;
                            hostsBuilder.AppendLine(line.Substring(COMMENT_PREFIX.Length));
                        }
                        else
                        {
                            hostsBuilder.AppendLine(line);
                        }
                    }
                    hostsEncoding = streamReader.CurrentEncoding;
                }

                if (hasRestored == true)
                {
                    await File.WriteAllTextAsync(hostsPath, hostsBuilder.ToString(), hostsEncoding, cancellationToken);
                    this.logger.LogInformation("已还原 hosts 文件中被注释的条目");
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"无法还原hosts文件：{ex.Message}");
            }
        }

        /// <summary>
        /// 是否为冲突的行
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
        private bool IsConflictingLine(string? line)
        {
            if (line == null || line.TrimStart().StartsWith("#") == true)
            {
                return false;
            }

            var items = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length < 2)
            {
                return false;
            }

            var domain = items[1];
            return this.pengGapConfig.IsMatch(domain);
        }
    }
}
