using System;

namespace PengGAP.Configuration
{
    /// <summary>
    /// 表示PengGAP异常
    /// </summary>
    public class PengGAPException : Exception
    {
        /// <summary>
        /// PengGAP异常
        /// </summary>
        /// <param name="message"></param>
        public PengGAPException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// PengGAP异常
        /// </summary>
        /// <param name="message"></param>
        /// <param name="innerException"></param>
        public PengGAPException(string message, Exception? innerException)
            : base(message, innerException)
        {
        }
    }
}
