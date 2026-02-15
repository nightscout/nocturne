using System.Security.Cryptography;
using System.Text;
using Nocturne.Connectors.MyLife.Configurations.Constants;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeDecryptor
{
    public static byte[] Decrypt(string encryptedBase64)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
            throw new ArgumentException("Encrypted data cannot be null or empty", nameof(encryptedBase64));

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(encryptedBase64);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Invalid Base64 encoded data", ex);
        }

        if (encrypted.Length == 0) throw new CryptographicException("Encrypted data is empty");

        try
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(MyLifeConstants.Crypto.ZipAesKey);
            aes.IV = Encoding.UTF8.GetBytes(MyLifeConstants.Crypto.ZipAesIv);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var input = new MemoryStream(encrypted);
            using var crypto = new CryptoStream(input, decryptor, CryptoStreamMode.Read);
            using var output = new MemoryStream();
            crypto.CopyTo(output);
            return output.ToArray();
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CryptographicException("Failed to decrypt MyLife data", ex);
        }
    }
}