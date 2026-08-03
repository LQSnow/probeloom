using System.Text.Json;
using ProbeLoom.Core;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace ProbeLoom.Services;

public sealed class WindowsDataProtectionSecretStore : ISecureValueStore
{
    private readonly string _filePath;
    private readonly TransactionalSecureValueStore _store;

    public WindowsDataProtectionSecretStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProbeLoom");
        _filePath = Path.Combine(directory, "secure-values.dat");
        _store = new TransactionalSecureValueStore(LoadAsync, SaveAsync);
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        _store.GetAsync(key, cancellationToken);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        _store.SetAsync(key, value, cancellationToken);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        _store.RemoveAsync(key, cancellationToken);

    private async Task<IReadOnlyDictionary<string, string>> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(_filePath, cancellationToken);
            var encryptedBuffer = CryptographicBuffer.CreateFromByteArray(encryptedBytes);
            var provider = new DataProtectionProvider();
            var clearBuffer = await provider.UnprotectAsync(encryptedBuffer);
            var json = CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, clearBuffer);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return values is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(values, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Runtime.InteropServices.COMException or JsonException)
        {
            throw new SecureValueStoreException(
                "无法读取 Windows 用户范围的安全存储；Secret 和 Token 未加载。",
                exception);
        }
    }

    private async Task SaveAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(values);
            var clearBuffer = CryptographicBuffer.ConvertStringToBinary(json, BinaryStringEncoding.Utf8);
            var provider = new DataProtectionProvider("LOCAL=user");
            var encryptedBuffer = await provider.ProtectAsync(clearBuffer);
            CryptographicBuffer.CopyToByteArray(encryptedBuffer, out var encryptedBytes);

            var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, encryptedBytes, cancellationToken);
                File.Move(temporaryPath, _filePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Runtime.InteropServices.COMException)
        {
            throw new SecureValueStoreException(
                "无法写入 Windows 用户范围的安全存储；Secret 或 Token 未保存。",
                exception);
        }
    }
}
