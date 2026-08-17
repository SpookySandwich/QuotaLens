using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class ElectronSafeStorageTests
{
    [TestMethod]
    public void TryDecryptString_DecryptsChromiumV10Envelope()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quotelens-electron-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var masterKey = RandomNumberGenerator.GetBytes(32);
            var protectedKey = RandomNumberGenerator.GetBytes(24);
            var localStatePath = WriteLocalState(directory, protectedKey);
            var payload = EncryptV10("{\"session\":\"fixture\"}", masterKey);

            var result = ElectronSafeStorage.TryDecryptString(
                payload,
                localStatePath,
                wrapped =>
                {
                    CollectionAssert.AreEqual(protectedKey, wrapped);
                    return masterKey.ToArray();
                });

            Assert.AreEqual("{\"session\":\"fixture\"}", result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void TryDecryptString_UnknownEnvelopeReturnsNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quotelens-electron-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var localStatePath = WriteLocalState(directory, RandomNumberGenerator.GetBytes(24));
            var payload = Convert.ToBase64String(Encoding.ASCII.GetBytes("v99-not-supported"));

            Assert.IsNull(ElectronSafeStorage.TryDecryptString(
                payload,
                localStatePath,
                _ => RandomNumberGenerator.GetBytes(32)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static string WriteLocalState(string directory, byte[] protectedKey)
    {
        var wrapped = Encoding.ASCII.GetBytes("DPAPI").Concat(protectedKey).ToArray();
        var path = Path.Combine(directory, "Local State");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            os_crypt = new { encrypted_key = Convert.ToBase64String(wrapped) },
        }));
        return path;
    }

    internal static string EncryptV10(string plaintext, byte[] masterKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(masterKey, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(
            Encoding.ASCII.GetBytes("v10").Concat(nonce).Concat(cipher).Concat(tag).ToArray());
    }
}
