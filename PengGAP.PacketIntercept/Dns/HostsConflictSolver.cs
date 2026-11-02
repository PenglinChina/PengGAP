using PengGAP.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Net;
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

            var file = await HostsFileContent.ReadAsync(hostsPath, cancellationToken);
            var hasChanged = false;

            for (var i = 0; i < file.Lines.Length; i++)
            {
                var line = file.Lines[i];
                if (TryParseHostsLine(line, out var hosts) == false)
                {
                    continue;
                }

                var matched = hosts.Count(host => this.pengGapConfig.IsMatch(host));
                if (matched == 0)
                {
                    continue;
                }

                // 整行的别名全部属于要加速的域名：整行注释不会波及用户其它映射
                if (matched == hosts.Length)
                {
                    file.Lines[i] = COMMENT_PREFIX + line;
                    hasChanged = true;
                    continue;
                }

                // 混合行：整行注释会连带禁用用户自己写的其余别名，因此保持原样，只提示
                this.logger.LogWarning($"hosts 第{i + 1}行同时包含要加速与其它域名，为避免误改用户配置已跳过：{line.Trim()}");
            }

            if (hasChanged == false)
            {
                return;
            }

            try
            {
                await file.WriteAsync(hostsPath, cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"无法解决hosts文件冲突：{ex.Message}");
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
                var file = await HostsFileContent.ReadAsync(hostsPath, cancellationToken);
                var hasRestored = false;

                for (var i = 0; i < file.Lines.Length; i++)
                {
                    var line = file.Lines[i];
                    if (line.StartsWith(COMMENT_PREFIX, StringComparison.Ordinal) == false)
                    {
                        continue;
                    }

                    var body = line.Substring(COMMENT_PREFIX.Length);

                    // 只还原"去掉前缀后确实是一条 hosts 记录"的行，
                    // 用户自己写的 "#penggap# 备注" 之类不会被改动
                    if (TryParseHostsLine(body, out _) == false)
                    {
                        continue;
                    }

                    file.Lines[i] = body;
                    hasRestored = true;
                }

                if (hasRestored == true)
                {
                    await file.WriteAsync(hostsPath, cancellationToken);
                    this.logger.LogInformation("已还原 hosts 文件中被注释的条目");
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"无法还原hosts文件：{ex.Message}");
            }
        }

        /// <summary>
        /// 解析一条 hosts 记录，取出全部域名别名
        /// 形如 "127.0.0.1 a.com b.com # 备注"
        /// </summary>
        /// <param name="line"></param>
        /// <param name="hosts"></param>
        /// <returns></returns>
        private static bool TryParseHostsLine(string? line, out string[] hosts)
        {
            hosts = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(line) == true)
            {
                return false;
            }

            var content = line.Trim();
            if (content.StartsWith("#", StringComparison.Ordinal) == true)
            {
                return false;
            }

            // 去掉行尾注释
            var commentIndex = content.IndexOf('#');
            if (commentIndex >= 0)
            {
                content = content.Substring(0, commentIndex).Trim();
            }

            var items = content.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length < 2)
            {
                return false;
            }

            // 第一个字段必须是 IP，否则不是一条 hosts 记录
            if (IPAddress.TryParse(items[0], out _) == false)
            {
                return false;
            }

            hosts = items.Skip(1).ToArray();
            return hosts.Length > 0;
        }

        /// <summary>
        /// hosts 文件内容：保留原有编码与换行风格，避免改坏用户文件
        /// </summary>
        private sealed class HostsFileContent
        {
            public Encoding Encoding { get; private set; } = new UTF8Encoding(false);

            public bool HasBom { get; private set; }

            public string NewLine { get; private set; } = Environment.NewLine;

            public string[] Lines { get; private set; } = Array.Empty<string>();

            /// <summary>
            /// 读取文件并按行拆分
            /// </summary>
            /// <param name="path"></param>
            /// <param name="cancellationToken"></param>
            /// <returns></returns>
            public static async Task<HostsFileContent> ReadAsync(string path, CancellationToken cancellationToken)
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                var (encoding, hasBom) = DetectEncoding(bytes);
                var preambleLength = hasBom ? encoding.GetPreamble().Length : 0;
                var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
                var newLine = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";

                return new HostsFileContent
                {
                    Encoding = encoding,
                    HasBom = hasBom,
                    NewLine = newLine,
                    Lines = text.Split('\n').Select(item => item.TrimEnd('\r')).ToArray()
                };
            }

            /// <summary>
            /// 按原编码写回
            /// </summary>
            /// <param name="path"></param>
            /// <param name="cancellationToken"></param>
            /// <returns></returns>
            public async Task WriteAsync(string path, CancellationToken cancellationToken)
            {
                var text = string.Join(this.NewLine, this.Lines);
                var body = this.Encoding.GetBytes(text);
                if (this.HasBom == false)
                {
                    await File.WriteAllBytesAsync(path, body, cancellationToken);
                    return;
                }

                var preamble = this.Encoding.GetPreamble();
                var buffer = new byte[preamble.Length + body.Length];
                preamble.CopyTo(buffer, 0);
                body.CopyTo(buffer, preamble.Length);
                await File.WriteAllBytesAsync(path, buffer, cancellationToken);
            }

            /// <summary>
            /// 探测编码
            /// hosts 常常没有 BOM，若一律当 UTF-8 处理会破坏 GBK 编码的中文注释
            /// </summary>
            /// <param name="bytes"></param>
            /// <returns></returns>
            private static (Encoding Encoding, bool HasBom) DetectEncoding(byte[] bytes)
            {
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    // 用带前导字节的实例，GetPreamble 才有内容
                    return (new UTF8Encoding(true), true);
                }
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    return (Encoding.Unicode, true);
                }
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    return (Encoding.BigEndianUnicode, true);
                }

                try
                {
                    new UTF8Encoding(false, true).GetString(bytes);
                    return (new UTF8Encoding(false), false);
                }
                catch (Exception)
                {
                    // 非 UTF-8 的旧文件（中文 Windows 的 hosts 常为 GBK）：
                    // 这里用 Latin1 做字节保真往返——每个字节唯一映射到一个字符并原样写回，
                    // 我们只插入/移除 ASCII 前缀，不会破坏原有字节。
                    // 不能用 Encoding.GetEncoding(936)：.NET 默认不带代码页。
                    return (Encoding.Latin1, false);
                }
            }
        }
    }
}
