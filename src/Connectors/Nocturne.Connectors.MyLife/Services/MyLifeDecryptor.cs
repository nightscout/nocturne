using System.Security.Cryptography;
using System.Text;
using Nocturne.Connectors.MyLife.Constants;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeDecryptor
{
    public static byte[] Decrypt(string encryptedBase64)
    {
        var encrypted = Convert.FromBase64String(encryptedBase64);
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
}