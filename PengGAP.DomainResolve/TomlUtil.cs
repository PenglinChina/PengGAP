using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PengGAP.DomainResolve
{
    /// <summary>
    /// toml配置工具
    /// 只改写指定的键值行，保留原文件里的注释与排版；
    /// 内容无变化时不写盘，写入采用临时文件替换，避免写到一半损坏配置
    /// </summary>
    static class TomlUtil
    {
        /// <summary>
        /// 写入 PengGAP 需要的运行期配置
        /// </summary>
        /// <param name="tomlPath"></param>
        /// <param name="localEndPoint"></param>
        /// <param name="cancellationToken"></param>
        public static Task SetRuntimeAsync(string tomlPath, IPEndPoint localEndPoint, CancellationToken cancellationToken)
        {
            return SetManyAsync(tomlPath, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["listen_addresses"] = $"['{localEndPoint}']",
                ["log_level"] = "2",
                ["lb_strategy"] = "'ph'",
                ["cache_min_ttl"] = "60",
                ["cache_neg_min_ttl"] = "60",
                ["cache_max_ttl"] = "120",
                ["cache_neg_max_ttl"] = "120",
            }, cancellationToken);
        }

        /// <summary>
        /// 批量改写键值：整文件只读一次、只写一次
        /// </summary>
        /// <param name="tomlPath"></param>
        /// <param name="values"></param>
        /// <param name="cancellationToken"></param>
        public static async Task SetManyAsync(string tomlPath, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        {
            var toml = await File.ReadAllTextAsync(tomlPath, cancellationToken);
            var updated = toml;
            foreach (var item in values)
            {
                updated = SetValue(updated, item.Key, item.Value);
            }

            if (string.Equals(updated, toml, StringComparison.Ordinal) == true)
            {
                return;
            }

            await WriteAtomicAsync(tomlPath, updated, cancellationToken);
        }

        /// <summary>
        /// 改写单个键的值，同时兼容被注释掉的键
        /// </summary>
        /// <param name="toml"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        private static string SetValue(string toml, string key, string value)
        {
            var pattern = $@"^(?<indent>[ \t]*)(?:#[ \t]*)?{Regex.Escape(key)}[ \t]*=[ \t]*.*$";
            var regex = new Regex(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (regex.IsMatch(toml) == true)
            {
                return regex.Replace(toml, match =>
                {
                    var indent = match.Groups["indent"].Value;
                    return $"{indent}{key} = {value}";
                }, 1);
            }

            return toml.TrimEnd() + Environment.NewLine + $"{key} = {value}" + Environment.NewLine;
        }

        /// <summary>
        /// 原子写入：先写临时文件再替换
        /// </summary>
        /// <param name="tomlPath"></param>
        /// <param name="content"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task WriteAtomicAsync(string tomlPath, string content, CancellationToken cancellationToken)
        {
            var tempPath = $"{tomlPath}.tmp";
            await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            File.Move(tempPath, tomlPath, overwrite: true);
        }
    }
}
