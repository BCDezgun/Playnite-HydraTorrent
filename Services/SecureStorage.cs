using System;
using System.Security.Cryptography;
using System.Text;

namespace HydraTorrent.Services
{
    public static class SecureStorage
    {
        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                var plainBytes = Encoding.UTF8.GetBytes(plainText);
                var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protectedBytes);
            }
            catch
            {
                return plainText;
            }
        }

        public static string Unprotect(string protectedText)
        {
            if (string.IsNullOrEmpty(protectedText))
                return string.Empty;

            try
            {
                var protectedBytes = Convert.FromBase64String(protectedText);
                var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return protectedText;
            }
        }

        public static bool IsProtected(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            try
            {
                var bytes = Convert.FromBase64String(value);
                ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
