// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Globalization;
using System.Security.Cryptography;
using CopyexFTPServer.Core;

namespace CopyexFTPServer.Sftp;

/// <summary>AES en modo contador (aes128/192/256-ctr, RFC 4344). Mantiene el contador entre paquetes.</summary>
internal sealed class AesCtr : IDisposable
{
    private const int Chunk = 4096;
    private readonly Aes _aes;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _counters = new byte[Chunk];
    private readonly byte[] _keystream = new byte[Chunk];
    private int _available; // bytes de keystream sin usar al final de _keystream

    public AesCtr(byte[] key, byte[] iv)
    {
        _aes = Aes.Create();
        _aes.Key = key;
        iv.AsSpan(0, 16).CopyTo(_counter);
    }

    public void Transform(Span<byte> data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            if (_available == 0)
                Refill();
            int start = Chunk - _available;
            int n = Math.Min(_available, data.Length - offset);
            Span<byte> d = data.Slice(offset, n);
            ReadOnlySpan<byte> k = _keystream.AsSpan(start, n);
            for (int i = 0; i < n; i++)
                d[i] ^= k[i];
            _available -= n;
            offset += n;
        }
    }

    private void Refill()
    {
        for (int b = 0; b < Chunk; b += 16)
        {
            _counter.CopyTo(_counters, b);
            for (int i = 15; i >= 0; i--)
            {
                if (++_counter[i] != 0)
                    break;
            }
        }
        _aes.EncryptEcb(_counters, _keystream, PaddingMode.None);
        _available = Chunk;
    }

    public void Dispose()
    {
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_keystream);
    }
}

/// <summary>Algoritmos negociables y sus parámetros.</summary>
internal static class SshAlgorithms
{
    // Orden = preferencia del servidor. SHA-1 solo como último recurso para escáneres antiguos.
    public static readonly string[] Kex = { "ecdh-sha2-nistp256", "diffie-hellman-group14-sha256", "diffie-hellman-group14-sha1" };
    public static readonly string[] HostKey = { "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa" };
    public static readonly string[] Ciphers = { "aes256-ctr", "aes192-ctr", "aes128-ctr" };
    public static readonly string[] Macs = { "hmac-sha2-256", "hmac-sha2-512", "hmac-sha1" };
    public static readonly string[] Compression = { "none" };

    public static int CipherKeyLength(string cipher) => cipher switch
    {
        "aes128-ctr" => 16,
        "aes192-ctr" => 24,
        "aes256-ctr" => 32,
        _ => throw new SshProtocolException("Cifrado no soportado")
    };

    public static (HashAlgorithmName Hash, int Length) Mac(string mac) => mac switch
    {
        "hmac-sha2-256" => (HashAlgorithmName.SHA256, 32),
        "hmac-sha2-512" => (HashAlgorithmName.SHA512, 64),
        "hmac-sha1" => (HashAlgorithmName.SHA1, 20),
        _ => throw new SshProtocolException("MAC no soportado")
    };

    public static HashAlgorithmName KexHash(string kex) => kex switch
    {
        "ecdh-sha2-nistp256" or "diffie-hellman-group14-sha256" => HashAlgorithmName.SHA256,
        "diffie-hellman-group14-sha1" => HashAlgorithmName.SHA1,
        _ => throw new SshProtocolException("Intercambio de claves no soportado")
    };

    /// <summary>Primer algoritmo de la lista del cliente que también admite el servidor (RFC 4253 §7.1).</summary>
    public static string Negotiate(string[] client, string[] server, string what)
    {
        foreach (string c in client)
        {
            if (Array.IndexOf(server, c) >= 0)
                return c;
        }
        throw new SshProtocolException($"Sin algoritmo común de {what} (cliente: {string.Join(",", client)})");
    }

    /// <summary>Grupo 14 de RFC 3526 (2048 bits).</summary>
    public static readonly System.Numerics.BigInteger Group14Prime = System.Numerics.BigInteger.Parse(
        "00FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3D" +
        "C2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D" +
        "670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9" +
        "DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
        "15728E5A8AACAA68FFFFFFFFFFFFFFFF",
        NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

/// <summary>
/// Clave de identidad del servidor SFTP (RSA 3072). Se crea la primera vez y se guarda junto a
/// config.json; su huella SHA256 permite al técnico comprobar que el escáner habla con este equipo.
/// </summary>
public sealed class SshHostKey : IDisposable
{
    private readonly RSA _rsa;

    private SshHostKey(RSA rsa)
    {
        _rsa = rsa;
        RSAParameters p = rsa.ExportParameters(false);
        PublicKeyBlob = new SshWriter(512).String("ssh-rsa").MpintUnsigned(p.Exponent).MpintUnsigned(p.Modulus).ToArray();
        Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(PublicKeyBlob)).TrimEnd('=');
    }

    internal byte[] PublicKeyBlob { get; }

    /// <summary>Huella en el mismo formato que muestra OpenSSH (SHA256:…).</summary>
    public string Fingerprint { get; }

    public static SshHostKey LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(File.ReadAllText(path));
                if (rsa.KeySize < 2048)
                    throw new CryptographicException("Clave demasiado corta");
                return new SshHostKey(rsa);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                rsa.Dispose();
                string backup = path + ".danada-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                try { File.Move(path, backup); } catch { }
                Logger.Error("SFTP: la clave del servidor estaba dañada; se genera una nueva (el escáner avisará de que la huella ha cambiado)");
            }
        }

        var created = RSA.Create(3072);
        string pem = created.ExportPkcs8PrivateKeyPem();
        string temp = path + ".tmp";
        File.WriteAllText(temp, pem);
        File.Move(temp, path, overwrite: true);
        Logger.Info("SFTP: generada la clave de identidad del servidor");
        return new SshHostKey(created);
    }

    /// <summary>Huella de la clave guardada, sin crearla. Null si aún no existe.</summary>
    public static string? TryGetFingerprint(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using SshHostKey key = LoadOrCreate(path);
            return key.Fingerprint;
        }
        catch
        {
            return null;
        }
    }

    internal byte[] Sign(byte[] data, string algorithm)
    {
        HashAlgorithmName hash = algorithm switch
        {
            "rsa-sha2-512" => HashAlgorithmName.SHA512,
            "rsa-sha2-256" => HashAlgorithmName.SHA256,
            "ssh-rsa" => HashAlgorithmName.SHA1,
            _ => throw new SshProtocolException("Algoritmo de firma no soportado")
        };
        byte[] signature = _rsa.SignData(data, hash, RSASignaturePadding.Pkcs1);
        return new SshWriter(signature.Length + 32).String(algorithm).String(signature).ToArray();
    }

    public void Dispose() => _rsa.Dispose();
}
