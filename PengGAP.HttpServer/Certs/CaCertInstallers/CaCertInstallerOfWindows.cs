using Microsoft.Extensions.Logging;
using System;
using System.Security.Cryptography.X509Certificates;

namespace PengGAP.HttpServer.Certs.CaCertInstallers
{
    sealed class CaCertInstallerOfWindows : ICaCertInstaller
    {
        private readonly ILogger<CaCertInstallerOfWindows> logger;

        public CaCertInstallerOfWindows(ILogger<CaCertInstallerOfWindows> logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// 是否支持
        /// </summary>
        /// <returns></returns>
        public bool IsSupported()
        {
            return OperatingSystem.IsWindows();
        }

        /// <summary>
        /// 安装ca证书
        /// </summary>
        /// <param name="caCertFilePath">证书文件路径</param>
        public void Install(string caCertFilePath)
        {
            try
            {
                using var caCert = new X509Certificate2(caCertFilePath);

                // 新根装到 CurrentUser 而不是 LocalMachine：只影响当前用户，缩小根CA的影响面
                using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    if (store.Certificates.Find(X509FindType.FindByThumbprint, caCert.Thumbprint, true).Count == 0)
                    {
                        store.Add(caCert);
                        logger.LogInformation($"已把根CA装进当前用户的受信任根，指纹{caCert.Thumbprint}，有效期至{caCert.NotAfter:yyyy-MM-dd HH:mm}");
                    }
                    store.Close();
                }

                // 新根已经生效，清理旧根属于收尾工作：
                // 现代 Windows 会保护 CurrentUser\Root 的删除操作（Access is denied），
                // 绝不能因为清理失败而让上面的安装白做
                this.TryRemoveOldCerts(caCert);
            }
            catch (Exception ex)
            {
                logger.LogWarning($"安装CA证书失败：{ex.Message}；请手动安装{caCertFilePath}到“受信任的根证书颁发机构”");
            }
        }

        /// <summary>
        /// 尽力清理同主题的旧CA，失败不影响新根已生效
        /// </summary>
        /// <param name="caCert"></param>
        private void TryRemoveOldCerts(X509Certificate2 caCert)
        {
            var subjectName = caCert.Subject[3..];

            try
            {
                using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    this.RemoveSameSubjectCerts(store, subjectName, caCert.Thumbprint);
                    store.Close();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"清理当前用户根存储里的旧CA失败（不影响新根）：{ex.Message}");
            }

            this.RemoveLegacyMachineRootCerts(subjectName, caCert.Thumbprint);
        }

        /// <summary>
        /// 回读当前用户受信任根，确认证书真的在里面
        /// </summary>
        /// <param name="caCertFilePath"></param>
        /// <returns></returns>
        public bool IsInstalled(string caCertFilePath)
        {
            try
            {
                using var caCert = new X509Certificate2(caCertFilePath);
                using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, caCert.Thumbprint, true).Count > 0;
                store.Close();
                return found;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 删除同主题但指纹不同的旧证书
        /// </summary>
        /// <param name="store"></param>
        /// <param name="subjectName"></param>
        /// <param name="thumbprint"></param>
        private void RemoveSameSubjectCerts(X509Store store, string subjectName, string thumbprint)
        {
            foreach (var item in store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, false))
            {
                if (item.Thumbprint != thumbprint)
                {
                    store.Remove(item);
                }
            }
        }

        /// <summary>
        /// 清理本机受信任根里的历史CA
        /// 需要管理员权限，失败时忽略：CurrentUser 里的新根已经生效
        /// </summary>
        /// <param name="subjectName"></param>
        /// <param name="thumbprint"></param>
        private void RemoveLegacyMachineRootCerts(string subjectName, string thumbprint)
        {
            try
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                foreach (var item in store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, false))
                {
                    if (item.Thumbprint != thumbprint)
                    {
                        store.Remove(item);
                        logger.LogInformation($"已从本机受信任根移除历史CA证书{item.Thumbprint}");
                    }
                }
                store.Close();
            }
            catch (Exception)
            {
                // 非管理员或证书存储受策略保护，忽略
            }
        }
    }
}
