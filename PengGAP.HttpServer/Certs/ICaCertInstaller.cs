namespace PengGAP.HttpServer.Certs
{
    /// <summary>
    /// CA证书安装器
    /// </summary>
    public interface ICaCertInstaller
    {
        /// <summary>
        /// 是否支持
        /// </summary>
        /// <returns></returns>
        bool IsSupported();

        /// <summary>
        /// 安装ca证书
        /// </summary>
        /// <param name="caCertFilePath">证书文件路径</param>
        void Install(string caCertFilePath);

        /// <summary>
        /// 是否已安装到受信任存储
        /// 用于安装后的回读校验：不能假设 Install 一定成功
        /// </summary>
        /// <param name="caCertFilePath">证书文件路径</param>
        /// <returns></returns>
        bool IsInstalled(string caCertFilePath)
        {
            return true;
        }
    }
}
