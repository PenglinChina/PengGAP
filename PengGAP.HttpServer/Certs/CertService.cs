using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PengGAP.HttpServer.Certs
{
    /// <summary>
    /// 证书服务
    /// </summary>
    sealed class CertService
    {
        private const string CACERT_PATH = "cacert";
        private readonly IMemoryCache serverCertCache;
        private readonly IEnumerable<ICaCertInstaller> certInstallers;
        private readonly ILogger<CertService> logger;
        private readonly Lazy<X509Certificate2> caCert;


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
        /// <param name="logger"></param>
        public CertService(
            IMemoryCache serverCertCache,
            IEnumerable<ICaCertInstaller> certInstallers,
            ILogger<CertService> logger)
        {
            this.serverCertCache = serverCertCache;
            this.certInstallers = certInstallers;
            this.logger = logger;
            Directory.CreateDirectory(CACERT_PATH);

            // Kestrel 在 TLS 回调里并发取证书，CA 的懒加载必须是线程安全的
            this.caCert = new Lazy<X509Certificate2>(this.LoadCaCert, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// 加载CA证书
        /// </summary>
        /// <returns></returns>
        private X509Certificate2 LoadCaCert()
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(this.CaKeyFilePath));
            return new X509Certificate2(this.CaCerFilePath).CopyWithPrivateKey(rsa);
        }

        /// <summary>
        /// 生成CA证书
        /// </summary> 
        public bool CreateCaCertIfNotExists()
        {
            if (File.Exists(this.CaCerFilePath) && File.Exists(this.CaKeyFilePath))
            {
                return false;
            }

            File.Delete(this.CaCerFilePath);
            File.Delete(this.CaKeyFilePath);

            var notBefore = DateTimeOffset.Now.AddDays(-1);
            var notAfter = DateTimeOffset.Now.AddYears(10);

            var subjectName = new X500DistinguishedName($"CN={nameof(PengGAP)}");
            var caCert = CertGenerator.CreateCACertificate(subjectName, notBefore, notAfter);

            var privateKeyPem = caCert.GetRSAPrivateKey()?.ExportRSAPrivateKeyPem();
            File.WriteAllText(this.CaKeyFilePath, new string(privateKeyPem), Encoding.ASCII);
            this.RestrictPrivateKeyFile(this.CaKeyFilePath);

            var certPem = caCert.ExportCertificatePem();
            File.WriteAllText(this.CaCerFilePath, new string(certPem), Encoding.ASCII);

            return true;
        }

        /// <summary>
        /// 收紧CA私钥的访问权限
        /// 该私钥可以签发任意站点的证书，不应让同机的其它账户读取
        /// </summary>
        /// <param name="filePath"></param>
        private void RestrictPrivateKeyFile(string filePath)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    this.RestrictPrivateKeyFileAtWindows(filePath);
                }
                else
                {
                    File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"无法收紧私钥{filePath}的访问权限：{ex.Message}");
            }
        }

        /// <summary>
        /// 关闭继承，仅授权当前用户完全控制
        /// </summary>
        /// <param name="filePath"></param>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void RestrictPrivateKeyFileAtWindows(string filePath)
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
            if (installer != null)
            {
                installer.Install(this.CaCerFilePath);
            }
            else
            {
                this.logger.LogWarning($"请根据你的系统平台手动安装和信任CA证书{this.CaCerFilePath}");
            }

            GitConfigSSLChannel("schannel");
        }

        /// <summary>
        /// 设置ssl验证
        /// </summary>
        /// <param name="value">是否验证</param>
        public static bool GitConfigSSLChannel(string value)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"config --global http.sslbackend {value.ToLower()}",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                return true;
            }
            catch (Exception)
            {
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
            var caCert = this.caCert.Value;
            var key = $"{nameof(CertService)}:{domain}";
            var endCert = this.serverCertCache.GetOrCreate(key, entry => GetOrCreateCert(caCert, domain, entry));
            return endCert!;
        }

        /// <summary>
        /// 生成域名的1年证书
        /// </summary>
        /// <param name="caCert"></param>
        /// <param name="domain"></param>
        /// <param name="entry"></param>
        /// <returns></returns>
        private static X509Certificate2 GetOrCreateCert(X509Certificate2 caCert, string? domain, ICacheEntry entry)
        {
            var notBefore = DateTimeOffset.Now.AddDays(-1);
            var notAfter = DateTimeOffset.Now.AddYears(1);
            entry.SetAbsoluteExpiration(notAfter);

            var extraDomains = GetExtraDomains();

            var subjectName = new X500DistinguishedName($"CN={domain}");
            var endCert = CertGenerator.CreateEndCertificate(caCert, subjectName, extraDomains, notBefore, notAfter);

            // 重新初始化证书，以兼容win平台不能使用内存证书
            return new X509Certificate2(endCert.Export(X509ContentType.Pfx));
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
    }
}
