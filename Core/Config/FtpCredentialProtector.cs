using System;
using System.Security.Cryptography;
using System.Text;

namespace FolderSync.Core.Config
{
    public static class FtpCredentialProtector
    {
        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return string.Empty;
            }

            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public static string Unprotect(string encryptedText)
        {
            if (string.IsNullOrWhiteSpace(encryptedText))
            {
                return string.Empty;
            }

            try
            {
                var protectedBytes = Convert.FromBase64String(encryptedText);
                var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                throw new InvalidOperationException("已保存的 FTP 密码无法解密。该任务可能来自其他 Windows 用户、其他机器，或保存数据已损坏。", ex);
            }
        }
    }
}
