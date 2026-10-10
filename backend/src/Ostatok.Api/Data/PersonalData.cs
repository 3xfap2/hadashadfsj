using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ostatok.Api.Data;

/// <summary>
/// Шифрование личных данных в базе: имена, контакты и переписка посетителей лендинга хранятся зашифрованными
/// (AES-256-GCM, свой случайный вектор на каждое значение). Тот, кто получил копию базы без ключа, их не прочитает.
/// Ключ задаётся настройкой Crypto:Key; если её нет, выводится из Auth:Key.
/// </summary>
public static class PersonalData
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static byte[]? _key;

    public static void Configure(string secret)
    {
        if (string.IsNullOrEmpty(secret)) throw new InvalidOperationException("Задайте Crypto:Key или Auth:Key: без ключа личные данные не шифруются.");
        // Отдельная метка, чтобы ключ шифрования не совпадал с ключом подписи токенов, даже если секрет один.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("ostatok.personal-data:" + secret));
    }

    private static byte[] Key => _key ?? throw new InvalidOperationException("Ключ шифрования личных данных не задан.");

    public static string Encrypt(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var packed = new byte[NonceSize + TagSize + plain.Length];
        var nonce = packed.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(Key, TagSize);
        aes.Encrypt(nonce, plain, packed.AsSpan(NonceSize + TagSize), packed.AsSpan(NonceSize, TagSize));
        return Prefix + Convert.ToBase64String(packed);
    }

    public static string Decrypt(string stored)
    {
        // Значение без метки записано до включения шифрования — оно читается как есть.
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        var packed = Convert.FromBase64String(stored[Prefix.Length..]);
        var plain = new byte[packed.Length - NonceSize - TagSize];
        using var aes = new AesGcm(Key, TagSize);
        aes.Decrypt(packed.AsSpan(0, NonceSize), packed.AsSpan(NonceSize + TagSize), packed.AsSpan(NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Преобразователь для столбца с личными данными: в базе — шифртекст, в коде — обычная строка.</summary>
    public static readonly ValueConverter<string, string> Converter = new(v => Encrypt(v), v => Decrypt(v));
    public static readonly ValueConverter<string?, string?> NullableConverter = new(v => v == null ? null : Encrypt(v), v => v == null ? null : Decrypt(v));
}
