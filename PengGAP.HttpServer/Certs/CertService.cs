using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using PengGAP.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PengGAP.HttpServer.Certs
{
    /// <summary>
    /// 证书服务
    /// 根CA短期轮换、私钥加密存储、叶证书短命，降低根私钥泄漏的影响窗口
    /// </summary>
    public sealed class CertService
    {
        private const string CACERT_PATH = "cacert";

        /// <summary>
        /// 根CA有效期：到期即轮换，把根私钥泄漏的影响窗口从十年压缩到一周
        /// </summary>
        private static readonly TimeSpan caLifetime = TimeSpan.FromDays(7d);

        /// <summary>
        /// 剩余有效期不足该值时就提前轮换，避免运行中恰好过期
        /// </summary>
        private static readonly TimeSpan caRotateThreshold = TimeSpan.FromDays(1d);

        /// <summary>
        /// 叶证书有效期：短命，单张证书泄漏的影响被限制在一天内
        /// </summary>
        private static readonly TimeSpan serverCertLifetime = TimeSpan.FromHours(24d);

        /// <summary>
        /// 私钥文件的加密标记，用于区分加密存储与历史明文
        /// </summary>
        private static readonly byte[] keyFileMagic = Encoding.ASCII.GetBytes("PGAPDK1");

        private readonly IMemoryCache serverCertCache;
        private readonly IEnumerable<ICaCertInstaller> certInstallers;
        private readonly PengGAPConfig pengGapConfig;
        private readonly ILogger<CertService> logger;

        /// <summary>
        /// 已签发叶证书的历史记录
        /// 用于「证书」页展示，过期的也保留（除非用户清空）
        /// </summary>
        private readonly ConcurrentDictionary<string, CertRecord> leafRecords = new();

        /// <summary>
        /// 叶证书记录上限，避免被随机域名撑爆
        /// </summary>
        private const int MAX_LEAF_RECORDS = 512;
        private readonly object caCertLock = new object();
        private volatile X509Certificate2? caCert;
        private int initialized;
        private int gitConfigApplied;

        /// <summary>
        /// 获取证书文件路径
        /// </summary>
        public string CaCerFilePath { get; } = OperatingSystem.IsLinux() ? $"{CACERT_PATH}/penggap.crt" : $"{CACERT_PATH}/penggap.cer";

        /// <summary>
        /// 获取私钥文件路径
        /// </summary>
        public string CaKeyFilePath { get; } = $"{CACERT_PATH}/penggap.key";

        /// <summary>
        /// 证书服务
        /// </summary>
        /// <param name="serverCertCache"></param>
        /// <param name="certInstallers"></param>
        /// <param name="pengGapConfig"></param>
        /// <param name="logger"></param>
        public CertService(
            IMemoryCache serverCertCache,
            IEnumerable<ICaCertInstaller> certInstallers,
            PengGAPConfig pengGapConfig,
            ILogger<CertService> logger)
        {
            this.serverCertCache = serverCertCache;
            this.certInstallers = certInstallers;
            this.pengGapConfig = pengGapConfig;
            this.logger = logger;
            Directory.CreateDirectory(CACERT_PATH);

            // 先把证书目录收紧到仅当前用户：目录里新建的文件会继承该权限，
            // 私钥从"创建"到"显式设权"之间不存在可被别人打开句柄的窗口
            RestrictDirectoryOrThrow(CACERT_PATH);

            // 根CA会在运行期间轮换，所以用可替换字段而不是 Lazy：读写由 caCertLock 保护
        }

        /// <summary>
        /// 初始化根CA：必要时轮换，然后确保已被系统信任
        /// 幂等，进程内只真正执行一次
        /// </summary>
        public void EnsureCaCert()
        {
            if (Interlocked.Exchange(ref this.initialized, 1) == 1)
            {
                return;
            }

            this.CheckAndRotate();
        }

        /// <summary>
        /// 根CA是否已被系统信任（最近一次校验结果）
        /// </summary>
        public bool IsTrusted { get; private set; }

        /// <summary>
        /// 当前根CA的到期时间
        /// </summary>
        public DateTimeOffset? CaNotAfter { get; private set; }

        /// <summary>
        /// 检查并在必要时轮换根CA，然后重新安装信任并回读校验
        /// 启动时调用一次，运行期间由 CertGuardHostedService 定期调用
        /// </summary>
        public void CheckAndRotate()
        {
            try
            {
                lock (this.caCertLock)
                {
                    if (this.CreateCaCertIfNotExists() == true)
                    {
                        // 保守策略：不清理叶证书缓存，旧叶证书随旧根自然到期，
                        // 正在进行的连接不会被中断
                        this.caCert = null;
                    }
                    this.CaNotAfter = this.ReadCaNotAfter();
                }

                this.InstallAndTrustCaCert();
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "根CA检查或轮换失败");
            }
        }

        /// <summary>
        /// 延迟释放证书
        /// 淘汰可能与正在使用该证书的 TLS 握手重叠，立即 Dispose 会让握手失败，
        /// 因此推迟一段时间再释放
        /// </summary>
        /// <param name="cert"></param>
        private static async void DisposeCertLater(X509Certificate2 cert)
        {
            await Task.Delay(TimeSpan.FromSeconds(60d)).ConfigureAwait(false);
            try
            {
                cert.Dispose();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 覆写后再删除，降低磁盘残留被恢复的可能
        /// 只用于私钥这类敏感文件
        /// </summary>
        /// <param name="filePath"></param>
        private static void DeleteFileSecurely(string filePath)
        {
            try
            {
                if (File.Exists(filePath) == false)
                {
                    return;
                }

                var length = new FileInfo(filePath).Length;
                if (length > 0)
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                    var remaining = length;
                    while (remaining > 0)
                    {
                        var count = (int)Math.Min(4096L, remaining);
                        stream.Write(RandomNumberGenerator.GetBytes(count));
                        remaining -= count;
                    }
                    stream.Flush(true);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 读取当前根CA的到期时间
        /// </summary>
        /// <returns></returns>
        private DateTimeOffset? ReadCaNotAfter()
        {
            try
            {
                using var cert = new X509Certificate2(this.CaCerFilePath);
                return new DateTimeOffset(cert.NotAfter.ToUniversalTime());
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 获取当前的CA证书实例
        /// </summary>
        /// <returns></returns>
        private X509Certificate2 GetCaCert()
        {
            var cert = this.caCert;
            if (cert != null)
            {
                return cert;
            }

            lock (this.caCertLock)
            {
                if (this.caCert == null)
                {
                    this.caCert = this.LoadCaCert();
                }
                return this.caCert;
            }
        }

        /// <summary>
        /// 加载CA证书
        /// </summary>
        /// <returns></returns>
        private X509Certificate2 LoadCaCert()
        {
            var raw = File.ReadAllBytes(this.CaKeyFilePath);
            var pem = this.DecryptPrivateKey(raw);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return new X509Certificate2(this.CaCerFilePath).CopyWithPrivateKey(rsa);
        }

        /// <summary>
        /// 解密私钥（Windows 为 DPAPI，其它平台为受限权限的明文 PEM）
        /// </summary>
        /// <param name="raw"></param>
        /// <returns></returns>
        private string DecryptPrivateKey(byte[] raw)
        {
            if (OperatingSystem.IsWindows() && IsProtectedPayload(raw))
            {
                var payload = new byte[raw.Length - keyFileMagic.Length];
                Array.Copy(raw, keyFileMagic.Length, payload, 0, payload.Length);

                var plain = UnprotectWithDpapi(payload);
                try
                {
                    return Encoding.ASCII.GetString(plain);
                }
                finally
                {
                    Array.Clear(plain, 0, plain.Length);
                }
            }

            // 兼容历史明文 PEM，下次轮换时会自动转为加密存储
            return Encoding.ASCII.GetString(raw);
        }

        /// <summary>
        /// 生成CA证书，到期或缺失时轮换
        /// </summary>
        /// <returns></returns>
        public bool CreateCaCertIfNotExists()
        {
            if (this.IsCaCertFresh())
            {
                return false;
            }

            // 私钥先随机覆写再删除，降低磁盘残留被恢复的可能
            DeleteFileSecurely(this.CaKeyFilePath);
            File.Delete(this.CaCerFilePath);

            var notBefore = DateTimeOffset.Now.AddMinutes(-5d);
            var notAfter = notBefore + caLifetime;

            var subjectName = new X500DistinguishedName($"CN={nameof(PengGAP)}");
            var caCert = CertGenerator.CreateCACertificate(subjectName, notBefore, notAfter);

            var privateKeyPem = caCert.GetRSAPrivateKey()?.ExportRSAPrivateKeyPem();
            if (privateKeyPem == null)
            {
                throw new InvalidOperationException("无法导出根CA的私钥");
            }

            this.SavePrivateKey(privateKeyPem);

            var certPem = caCert.ExportCertificatePem();
            File.WriteAllText(this.CaCerFilePath, new string(certPem), Encoding.ASCII);

            this.logger.LogInformation($"已生成根CA证书，有效期至{notAfter:yyyy-MM-dd HH:mm}");
            return true;
        }

        /// <summary>
        /// 判断现有根CA是否仍然新鲜
        /// </summary>
        /// <returns></returns>
        private bool IsCaCertFresh()
        {
            try
            {
                if (File.Exists(this.CaCerFilePath) == false || File.Exists(this.CaKeyFilePath) == false)
                {
                    return false;
                }

                using var cert = new X509Certificate2(this.CaCerFilePath);

                // 历史版本签发的是十年有效期的根，视为遗留CA，强制轮换一次
                var lifetime = cert.NotAfter - cert.NotBefore;
                if (lifetime > caLifetime + caLifetime)
                {
                    return false;
                }

                return cert.NotAfter.ToUniversalTime() - DateTime.UtcNow > caRotateThreshold;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 保存私钥
        /// Windows 用 DPAPI 加密，其它平台写明文 PEM 并把权限收到 0600
        /// </summary>
        /// <param name="privateKeyPem"></param>
        private void SavePrivateKey(ReadOnlySpan<char> privateKeyPem)
        {
            var plain = Encoding.ASCII.GetBytes(privateKeyPem.ToString());
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var protectedBytes = ProtectWithDpapi(plain);
                    var payload = new byte[keyFileMagic.Length + protectedBytes.Length];
                    keyFileMagic.CopyTo(payload, 0);
                    protectedBytes.CopyTo(payload, keyFileMagic.Length);
                    this.WritePrivateKeyFile(payload);
                }
                else
                {
                    this.WritePrivateKeyFile(plain);
                }
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        /// <summary>
        /// 写入私钥文件
        /// 证书目录已在构造时收紧到仅当前用户，新建文件会继承该权限，
        /// 因此不存在"先建空文件、后收紧权限"的窗口
        /// </summary>
        /// <param name="content"></param>
        private void WritePrivateKeyFile(byte[] content)
        {
            using (var stream = new FileStream(this.CaKeyFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(true);
            }

            // 双保险：万一目录权限将来被外部改动，文件本身也再收紧一次
            this.RestrictPrivateKeyFileOrThrow(this.CaKeyFilePath);
        }

        /// <summary>
        /// 把证书目录收紧到仅当前用户可访问
        /// 该目录存放根CA私钥，目录级限制让目录内新建文件自动继承最小权限
        /// </summary>
        /// <param name="path"></param>
        private static void RestrictDirectoryOrThrow(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var directory = new DirectoryInfo(path);
                    var security = directory.GetAccessControl();
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                    var currentUser = WindowsIdentity.GetCurrent().User;
                    if (currentUser != null)
                    {
                        security.AddAccessRule(new FileSystemAccessRule(
                            currentUser,
                            FileSystemRights.FullControl,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));
                    }
                    directory.SetAccessControl(security);
                }
                else
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch (Exception ex)
            {
                throw new IOException($"无法收紧证书目录{path}的访问权限，出于安全考虑已中止启动：{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 收紧CA私钥的访问权限
        /// 该私钥可以签发任意站点的证书，收紧失败必须中止，否则等于默许暴露
        /// </summary>
        /// <param name="filePath"></param>
        private void RestrictPrivateKeyFileOrThrow(string filePath)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    RestrictPrivateKeyFileAtWindows(filePath);
                }
                else
                {
                    File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (Exception ex)
            {
                throw new IOException($"无法收紧私钥{filePath}的访问权限，出于安全考虑已中止启动：{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 关闭继承，仅授权当前用户完全控制
        /// </summary>
        /// <param name="filePath"></param>
        [SupportedOSPlatform("windows")]
        private static void RestrictPrivateKeyFileAtWindows(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            }
            fileInfo.SetAccessControl(security);
        }

        /// <summary>
        /// 安装和信任CA证书
        /// </summary>
        public void InstallAndTrustCaCert()
        {
            var installer = this.certInstallers.FirstOrDefault(item => item.IsSupported());
            if (installer == null)
            {
                this.IsTrusted = false;
                this.logger.LogWarning($"找不到当前平台的证书安装器，请手动安装CA证书{this.CaCerFilePath}");
                return;
            }

            installer.Install(this.CaCerFilePath);

            // 回读校验：不能假设 Install 一定成功，企业环境里可能被组策略挡掉
            var trusted = installer.IsInstalled(this.CaCerFilePath);
            if (trusted != this.IsTrusted)
            {
                if (trusted == true)
                {
                    this.logger.LogInformation($"根CA已受信任，有效期至{this.CaNotAfter:yyyy-MM-dd HH:mm}");
                }
                else
                {
                    this.logger.LogError($"根CA未被系统信任，浏览器会报证书错误；请手动安装{this.CaCerFilePath}到“受信任的根证书颁发机构”");
                }
            }
            this.IsTrusted = trusted;

            // git 的 sslbackend 是全局设置，成功后进程内不再重复执行；失败则留待下次重试
            if (Volatile.Read(ref this.gitConfigApplied) == 0)
            {
                if (this.GitConfigSSLChannel("schannel") == true)
                {
                    Interlocked.Exchange(ref this.gitConfigApplied, 1);
                }
            }
        }

        /// <summary>
        /// 设置 git 的 ssl 后端
        /// UseShellExecute=true 时拿不到进程句柄，git 失败会完全静默，因此改为等待退出码
        /// </summary>
        /// <param name="value">后端名，例如 schannel</param>
        /// <returns></returns>
        private bool GitConfigSSLChannel(string value)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"config --global http.sslbackend {value.ToLower()}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true
                });

                if (process == null)
                {
                    return false;
                }

                if (process.WaitForExit(5000) == false)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                    }

                    this.logger.LogWarning($"设置 git 的 http.sslbackend 超时");
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    var error = process.StandardError.ReadToEnd().Trim();
                    this.logger.LogWarning($"设置 git 的 http.sslbackend 失败（退出码{process.ExitCode}）：{error}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // 未安装 git 时会走到这里，属于正常情况
                this.logger.LogDebug($"未能设置 git 的 http.sslbackend：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取颁发给指定域名的证书
        /// </summary>
        /// <param name="domain"></param> 
        /// <returns></returns>
        public X509Certificate2 GetOrCreateServerCert(string? domain)
        {
            var caCert = this.GetCaCert();

            // 缓存键带上CA指纹：根CA轮换后旧叶证书自然失效并按新键重新签发，
            // 不会出现"旧根已从信任库删除、旧叶证书却还继续被服务出去"导致浏览器报证书错误
            var key = $"{caCert.Thumbprint}:{domain}";
            var endCert = this.serverCertCache.GetOrCreate(key, entry => this.GetOrCreateCert(caCert, domain, entry));
            return endCert!;
        }

        /// <summary>
        /// 生成域名的短期证书
        /// </summary>
        /// <param name="caCert"></param>
        /// <param name="domain"></param>
        /// <param name="entry"></param>
        /// <returns></returns>
        private X509Certificate2 GetOrCreateCert(X509Certificate2 caCert, string? domain, ICacheEntry entry)
        {
            entry.SetSize(1);

            // 淘汰时显式释放证书，否则 native 加密句柄要等终结器回收
            entry.RegisterPostEvictionCallback(static (key, value, reason, state) =>
            {
                if (value is X509Certificate2 cert)
                {
                    DisposeCertLater(cert);
                }
            });

            var notBefore = DateTimeOffset.Now.AddHours(-1d);
            var notAfter = DateTimeOffset.Now.Add(serverCertLifetime);
            entry.SetAbsoluteExpiration(notAfter);

            // 只对配置规则内的域名签发由本CA签名的证书；
            // 其它域名一律给自签名证书，避免本进程被当成任意域名的签发服务
            if (string.IsNullOrEmpty(domain) || this.pengGapConfig.IsMatch(domain) == false)
            {
                this.logger.LogWarning($"域名{domain}不在加速规则内，返回自签名证书而不由根CA签发");
                this.RecordLeaf(domain, notBefore, notAfter);
                return CreateSelfSignedCert(domain, notBefore, notAfter);
            }

            var extraDomains = GetExtraDomains();

            var subjectName = new X500DistinguishedName($"CN={domain}");
            var endCert = CertGenerator.CreateEndCertificate(caCert, subjectName, extraDomains, notBefore, notAfter);
            this.RecordLeaf(domain, notBefore, notAfter);

            // 重新初始化证书，以兼容win平台不能使用内存证书
            return new X509Certificate2(endCert.Export(X509ContentType.Pfx));
        }

        /// <summary>
        /// 记录一张已签发的叶证书
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="notBefore"></param>
        /// <param name="notAfter"></param>
        private void RecordLeaf(string? domain, DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            var name = string.IsNullOrEmpty(domain) ? "(未知域名)" : domain;
            this.leafRecords[name] = new CertRecord
            {
                Name = name,
                Kind = "leaf",
                NotBefore = notBefore,
                NotAfter = notAfter
            };

            if (this.leafRecords.Count <= MAX_LEAF_RECORDS)
            {
                return;
            }

            var overflow = this.leafRecords
                .OrderBy(item => item.Value.NotBefore)
                .Take(this.leafRecords.Count - MAX_LEAF_RECORDS)
                .ToArray();

            foreach (var item in overflow)
            {
                this.leafRecords.TryRemove(item.Key, out _);
            }
        }

        /// <summary>
        /// 获取全部证书记录
        /// 根证书以磁盘文件为准，用户删掉文件后就不再显示
        /// </summary>
        /// <returns></returns>
        public CertRecord[] GetRecords()
        {
            var now = DateTimeOffset.Now;
            var records = new List<CertRecord>();

            var root = this.ReadRootRecord();
            if (root != null)
            {
                records.Add(root with { Expired = root.NotAfter <= now });
            }

            foreach (var item in this.leafRecords.Values)
            {
                records.Add(item with { Expired = item.NotAfter <= now });
            }

            // 根证书排最前，其余按到期时间倒序
            return records
                .OrderBy(item => item.Kind == "root" ? 0 : 1)
                .ThenByDescending(item => item.NotAfter)
                .ToArray();
        }

        /// <summary>
        /// 读取根证书记录
        /// </summary>
        /// <returns></returns>
        private CertRecord? ReadRootRecord()
        {
            try
            {
                if (File.Exists(this.CaCerFilePath) == false)
                {
                    return null;
                }

                using var cert = new X509Certificate2(this.CaCerFilePath);
                return new CertRecord
                {
                    Name = cert.GetNameInfo(X509NameType.SimpleName, false),
                    Kind = "root",
                    NotBefore = new DateTimeOffset(cert.NotBefore),
                    NotAfter = new DateTimeOffset(cert.NotAfter)
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 生成自签名证书，用于不在规则内的域名
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="notBefore"></param>
        /// <param name="notAfter"></param>
        /// <returns></returns>
        private static X509Certificate2 CreateSelfSignedCert(string? domain, DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            using var rsa = RSA.Create(2048);
            var subjectName = new X500DistinguishedName($"CN={domain}");
            var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

            var oids = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, true));

            var dnsBuilder = new SubjectAlternativeNameBuilder();
            if (string.IsNullOrEmpty(domain) == false && IPAddress.TryParse(domain, out var address) == true)
            {
                dnsBuilder.AddIpAddress(address);
            }
            else if (string.IsNullOrEmpty(domain) == false)
            {
                dnsBuilder.AddDnsName(domain);
            }
            request.CertificateExtensions.Add(dnsBuilder.Build());

            using var cert = request.CreateSelfSigned(notBefore, notAfter);
            return new X509Certificate2(cert.Export(X509ContentType.Pfx));
        }

        /// <summary>
        /// 获取域名
        /// </summary>
        /// <returns></returns>
        private static IEnumerable<string> GetExtraDomains()
        {
            yield return Environment.MachineName;
            yield return IPAddress.Loopback.ToString();
            yield return IPAddress.IPv6Loopback.ToString();
        }

        /// <summary>
        /// 判断密钥文件是否是加密载荷
        /// </summary>
        /// <param name="raw"></param>
        /// <returns></returns>
        private static bool IsProtectedPayload(byte[] raw)
        {
            if (raw.Length <= keyFileMagic.Length)
            {
                return false;
            }

            for (var i = 0; i < keyFileMagic.Length; i++)
            {
                if (raw[i] != keyFileMagic[i])
                {
                    return false;
                }
            }
            return true;
        }

        #region DPAPI

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn,
            string? szDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn,
            IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>
        /// 用当前用户的 DPAPI 加密
        /// </summary>
        /// <param name="plain"></param>
        /// <returns></returns>
        [SupportedOSPlatform("windows")]
        private static byte[] ProtectWithDpapi(byte[] plain)
        {
            var input = new DataBlob();
            var output = new DataBlob();
            try
            {
                input.Size = plain.Length;
                input.Data = Marshal.AllocHGlobal(plain.Length);
                Marshal.Copy(plain, 0, input.Data, plain.Length);

                if (CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output) == false)
                {
                    throw new CryptographicException($"DPAPI 加密失败，错误码 {Marshal.GetLastWin32Error()}");
                }

                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    LocalFree(output.Data);
                }
                if (input.Data != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(input.Data);
                }
            }
        }

        /// <summary>
        /// 用当前用户的 DPAPI 解密
        /// </summary>
        /// <param name="payload"></param>
        /// <returns></returns>
        [SupportedOSPlatform("windows")]
        private static byte[] UnprotectWithDpapi(byte[] payload)
        {
            var input = new DataBlob();
            var output = new DataBlob();
            try
            {
                input.Size = payload.Length;
                input.Data = Marshal.AllocHGlobal(payload.Length);
                Marshal.Copy(payload, 0, input.Data, payload.Length);

                if (CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output) == false)
                {
                    throw new CryptographicException($"DPAPI 解密失败，错误码 {Marshal.GetLastWin32Error()}");
                }

                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    LocalFree(output.Data);
                }
                if (input.Data != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(input.Data);
                }
            }
        }

        #endregion
    }
}
