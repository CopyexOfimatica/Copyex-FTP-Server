// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace CopyexFTPServer.Sftp;

/// <summary>Mensajes SSH usados por el servidor (RFC 4250 §4.1).</summary>
internal static class SshMsg
{
    public const byte Disconnect = 1, Ignore = 2, Unimplemented = 3, Debug = 4;
    public const byte ServiceRequest = 5, ServiceAccept = 6;
    public const byte KexInit = 20, NewKeys = 21, KexDhInit = 30, KexDhReply = 31;
    public const byte UserauthRequest = 50, UserauthFailure = 51, UserauthSuccess = 52;
    public const byte GlobalRequest = 80, RequestSuccess = 81, RequestFailure = 82;
    public const byte ChannelOpen = 90, ChannelOpenConfirmation = 91, ChannelOpenFailure = 92;
    public const byte ChannelWindowAdjust = 93, ChannelData = 94, ChannelExtendedData = 95;
    public const byte ChannelEof = 96, ChannelClose = 97, ChannelRequest = 98;
    public const byte ChannelSuccess = 99, ChannelFailure = 100;
}

internal sealed class SshDisconnectedException : Exception
{
    public SshDisconnectedException(string message) : base(message) { }
}

/// <summary>
/// Capa de transporte SSH-2 (RFC 4253): versiones, paquetes binarios cifrados y autenticados,
/// e intercambio de claves (inicial y renegociaciones que pida el cliente).
/// </summary>
internal sealed class SshTransport : IDisposable
{
    public const string ServerVersion = "SSH-2.0-CopyexFTPServer";
    private const int MaxPacketLength = 256 * 1024;

    private readonly Stream _stream;
    private readonly SshHostKey _hostKey;
    private readonly Direction _in = new();
    private readonly Direction _out = new();
    private byte[]? _pendingServerKexInit;

    public SshTransport(Stream stream, SshHostKey hostKey)
    {
        _stream = stream;
        _hostKey = hostKey;
    }

    public string ClientVersion { get; private set; } = string.Empty;
    public byte[]? SessionId { get; private set; }
    public uint LastReceivedSequence { get; private set; }
    public string NegotiatedSummary { get; private set; } = string.Empty;

    // ------------------------------------------------------------------ versiones

    public async Task ExchangeVersionsAsync(CancellationToken ct)
    {
        byte[] ours = Encoding.ASCII.GetBytes(ServerVersion + "\r\n");
        await _stream.WriteAsync(ours, ct).ConfigureAwait(false);

        for (int lines = 0; lines < 20; lines++)
        {
            string line = await ReadVersionLineAsync(ct).ConfigureAwait(false);
            if (line.StartsWith("SSH-", StringComparison.Ordinal))
            {
                if (!line.StartsWith("SSH-2.0-", StringComparison.Ordinal) && !line.StartsWith("SSH-1.99-", StringComparison.Ordinal))
                    throw new SshProtocolException("Versión de protocolo no soportada");
                ClientVersion = line;
                return;
            }
        }
        throw new SshProtocolException("El cliente no envió su versión SSH");
    }

    private async Task<string> ReadVersionLineAsync(CancellationToken ct)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];
        while (true)
        {
            int n = await _stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0)
                throw new SshDisconnectedException("Conexión cerrada durante el saludo");
            if (one[0] == (byte)'\n')
                break;
            if (bytes.Count >= 255)
                throw new SshProtocolException("Línea de versión demasiado larga");
            bytes.Add(one[0]);
        }
        if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
            bytes.RemoveAt(bytes.Count - 1);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    // ------------------------------------------------------------------ paquetes

    public async Task<byte[]> ReadPacketAsync(CancellationToken ct)
    {
        Direction st = _in;
        int bs = st.BlockSize;

        byte[] first = new byte[bs];
        await ReadExactAsync(first, ct).ConfigureAwait(false);
        st.Cipher?.Transform(first);

        uint length = BinaryPrimitives.ReadUInt32BigEndian(first);
        if (length < 5 || length > MaxPacketLength || (length + 4) % (uint)bs != 0)
            throw new SshProtocolException("Longitud de paquete no válida");

        byte[] packet = new byte[length + 4];
        first.CopyTo(packet, 0);
        if (packet.Length > bs)
        {
            await ReadExactAsync(packet.AsMemory(bs), ct).ConfigureAwait(false);
            st.Cipher?.Transform(packet.AsSpan(bs));
        }

        if (st.Mac != null)
        {
            byte[] received = new byte[st.MacLength];
            await ReadExactAsync(received, ct).ConfigureAwait(false);
            byte[] expected = ComputeMac(st, packet);
            if (!CryptographicOperations.FixedTimeEquals(received, expected))
                throw new SshProtocolException("MAC incorrecto");
        }

        int padding = packet[4];
        int payloadLength = (int)length - padding - 1;
        if (padding < 4 || payloadLength < 1)
            throw new SshProtocolException("Relleno de paquete no válido");

        LastReceivedSequence = st.Sequence;
        st.Sequence++;
        return packet.AsSpan(5, payloadLength).ToArray();
    }

    public async Task WritePacketAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        Direction st = _out;
        int bs = st.BlockSize;
        int padding = bs - ((payload.Length + 5) % bs);
        if (padding < 4)
            padding += bs;
        int length = 1 + payload.Length + padding;

        byte[] packet = new byte[4 + length + st.MacLength];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)length);
        packet[4] = (byte)padding;
        payload.Span.CopyTo(packet.AsSpan(5));
        RandomNumberGenerator.Fill(packet.AsSpan(5 + payload.Length, padding));

        if (st.Mac != null)
            ComputeMac(st, packet.AsSpan(0, 4 + length)).CopyTo(packet, 4 + length);
        st.Cipher?.Transform(packet.AsSpan(0, 4 + length));

        await _stream.WriteAsync(packet, ct).ConfigureAwait(false);
        st.Sequence++;
    }

    public Task WriteAsync(SshWriter message, CancellationToken ct) => WritePacketAsync(message.ToArray(), ct);

    private static byte[] ComputeMac(Direction st, ReadOnlySpan<byte> packet)
    {
        Span<byte> seq = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(seq, st.Sequence);
        st.Mac!.AppendData(seq);
        st.Mac.AppendData(packet);
        return st.Mac.GetHashAndReset();
    }

    private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0)
                throw new SshDisconnectedException("Conexión cerrada por el cliente");
            read += n;
        }
    }

    // ------------------------------------------------------------------ intercambio de claves

    public async Task SendKexInitAsync(CancellationToken ct)
    {
        byte[] cookie = RandomNumberGenerator.GetBytes(16);
        var w = new SshWriter(512)
            .Byte(SshMsg.KexInit).Raw(cookie)
            .NameList(SshAlgorithms.Kex)
            .NameList(SshAlgorithms.HostKey)
            .NameList(SshAlgorithms.Ciphers).NameList(SshAlgorithms.Ciphers)
            .NameList(SshAlgorithms.Macs).NameList(SshAlgorithms.Macs)
            .NameList(SshAlgorithms.Compression).NameList(SshAlgorithms.Compression)
            .String(string.Empty).String(string.Empty)
            .Bool(false).UInt32(0);
        _pendingServerKexInit = w.ToArray();
        await WritePacketAsync(_pendingServerKexInit, ct).ConfigureAwait(false);
    }

    /// <summary>Completa un intercambio de claves a partir del KEXINIT recibido del cliente.</summary>
    public async Task RunKeyExchangeAsync(byte[] clientKexInit, CancellationToken ct)
    {
        if (_pendingServerKexInit == null)
            await SendKexInitAsync(ct).ConfigureAwait(false);
        byte[] serverKexInit = _pendingServerKexInit!;

        var r = new SshReader(clientKexInit);
        r.Byte();
        for (int i = 0; i < 16; i++) r.Byte();
        string[] kexAlgs = r.NameList();
        string[] hostKeyAlgs = r.NameList();
        string[] encC2S = r.NameList();
        string[] encS2C = r.NameList();
        string[] macC2S = r.NameList();
        string[] macS2C = r.NameList();
        string[] compC2S = r.NameList();
        string[] compS2C = r.NameList();
        r.NameList();
        r.NameList();
        bool firstKexFollows = r.Bool();

        string kex = SshAlgorithms.Negotiate(kexAlgs, SshAlgorithms.Kex, "intercambio de claves");
        string hostKeyAlg = SshAlgorithms.Negotiate(hostKeyAlgs, SshAlgorithms.HostKey, "clave de servidor");
        string cipherIn = SshAlgorithms.Negotiate(encC2S, SshAlgorithms.Ciphers, "cifrado");
        string cipherOut = SshAlgorithms.Negotiate(encS2C, SshAlgorithms.Ciphers, "cifrado");
        string macIn = SshAlgorithms.Negotiate(macC2S, SshAlgorithms.Macs, "MAC");
        string macOut = SshAlgorithms.Negotiate(macS2C, SshAlgorithms.Macs, "MAC");
        SshAlgorithms.Negotiate(compC2S, SshAlgorithms.Compression, "compresión");
        SshAlgorithms.Negotiate(compS2C, SshAlgorithms.Compression, "compresión");

        // Si el cliente adivinó mal y envió ya su paquete de intercambio, se descarta (RFC 4253 §7).
        bool skipGuess = firstKexFollows && (kexAlgs.Length == 0 || kexAlgs[0] != kex || hostKeyAlgs.Length == 0 || hostKeyAlgs[0] != hostKeyAlg);

        byte[] kexPayload = await ReadKexMessageAsync(ct).ConfigureAwait(false);
        if (skipGuess)
            kexPayload = await ReadKexMessageAsync(ct).ConfigureAwait(false);
        if (kexPayload[0] != SshMsg.KexDhInit)
            throw new SshProtocolException("Se esperaba el inicio del intercambio de claves");

        HashAlgorithmName hashName = SshAlgorithms.KexHash(kex);
        var init = new SshReader(kexPayload);
        init.Byte();

        byte[] exchangeHash;
        byte[] sharedSecret;
        SshWriter reply = new SshWriter(1024).Byte(SshMsg.KexDhReply).String(_hostKey.PublicKeyBlob);

        var h = new SshWriter(2048)
            .String(ClientVersion).String(ServerVersion)
            .String(clientKexInit).String(serverKexInit)
            .String(_hostKey.PublicKeyBlob);

        if (kex == "ecdh-sha2-nistp256")
        {
            byte[] qc = init.StringBytes(256).ToArray();
            if (qc.Length != 65 || qc[0] != 0x04)
                throw new SshProtocolException("Clave ECDH del cliente no válida");

            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ECParameters ours = ecdh.ExportParameters(false);
            byte[] qs = new byte[65];
            qs[0] = 0x04;
            ours.Q.X!.CopyTo(qs, 1 + 32 - ours.Q.X!.Length);
            ours.Q.Y!.CopyTo(qs, 33 + 32 - ours.Q.Y!.Length);

            ECDiffieHellman peer;
            try
            {
                peer = ECDiffieHellman.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = qc[1..33], Y = qc[33..65] }
                });
            }
            catch (CryptographicException)
            {
                throw new SshProtocolException("Punto ECDH no válido");
            }
            using (peer)
                sharedSecret = ecdh.DeriveRawSecretAgreement(peer.PublicKey);

            h.String(qc).String(qs).MpintUnsigned(sharedSecret);
            exchangeHash = Hash(hashName, h.AsSpan());
            reply.String(qs);
        }
        else
        {
            BigInteger p = SshAlgorithms.Group14Prime;
            var e = new BigInteger(init.MpintUnsigned(), isUnsigned: true, isBigEndian: true);
            if (e <= BigInteger.One || e >= p - BigInteger.One)
                throw new SshProtocolException("Valor DH del cliente no válido");

            var y = new BigInteger(RandomNumberGenerator.GetBytes(64), isUnsigned: true, isBigEndian: true);
            if (y < 2) y += 2;
            BigInteger f = BigInteger.ModPow(2, y, p);
            BigInteger k = BigInteger.ModPow(e, y, p);
            if (k <= BigInteger.One)
                throw new SshProtocolException("Secreto DH no válido");
            sharedSecret = k.ToByteArray(isUnsigned: true, isBigEndian: true);

            h.Mpint(e).Mpint(f).MpintUnsigned(sharedSecret);
            exchangeHash = Hash(hashName, h.AsSpan());
            reply.Mpint(f);
        }

        SessionId ??= exchangeHash;
        reply.String(_hostKey.Sign(exchangeHash, hostKeyAlg));
        await WriteAsync(reply, ct).ConfigureAwait(false);
        await WritePacketAsync(new[] { SshMsg.NewKeys }, ct).ConfigureAwait(false);

        byte[] kMpint = new SshWriter(300).MpintUnsigned(sharedSecret).ToArray();
        CryptographicOperations.ZeroMemory(sharedSecret);

        (HashAlgorithmName macOutHash, int macOutLen) = SshAlgorithms.Mac(macOut);
        _out.Activate(
            new AesCtr(Derive(hashName, kMpint, exchangeHash, 'D', SshAlgorithms.CipherKeyLength(cipherOut)),
                       Derive(hashName, kMpint, exchangeHash, 'B', 16)),
            IncrementalHash.CreateHMAC(macOutHash, Derive(hashName, kMpint, exchangeHash, 'F', macOutLen)),
            macOutLen);

        byte[] newKeys = await ReadKexMessageAsync(ct).ConfigureAwait(false);
        if (newKeys[0] != SshMsg.NewKeys)
            throw new SshProtocolException("Se esperaba NEWKEYS");

        (HashAlgorithmName macInHash, int macInLen) = SshAlgorithms.Mac(macIn);
        _in.Activate(
            new AesCtr(Derive(hashName, kMpint, exchangeHash, 'C', SshAlgorithms.CipherKeyLength(cipherIn)),
                       Derive(hashName, kMpint, exchangeHash, 'A', 16)),
            IncrementalHash.CreateHMAC(macInHash, Derive(hashName, kMpint, exchangeHash, 'E', macInLen)),
            macInLen);

        CryptographicOperations.ZeroMemory(kMpint);
        _pendingServerKexInit = null;
        NegotiatedSummary = $"{kex}, {hostKeyAlg}, {cipherIn}, {macIn}";
    }

    /// <summary>Lee el siguiente mensaje durante el intercambio, ignorando IGNORE/DEBUG.</summary>
    private async Task<byte[]> ReadKexMessageAsync(CancellationToken ct)
    {
        while (true)
        {
            byte[] payload = await ReadPacketAsync(ct).ConfigureAwait(false);
            switch (payload[0])
            {
                case SshMsg.Ignore:
                case SshMsg.Debug:
                case SshMsg.Unimplemented:
                    continue;
                case SshMsg.Disconnect:
                    throw new SshDisconnectedException("El cliente se desconectó");
                default:
                    return payload;
            }
        }
    }

    private byte[] Derive(HashAlgorithmName hash, byte[] kMpint, byte[] exchangeHash, char letter, int length)
    {
        var result = new List<byte>(length + 64);
        byte[] block = Hash(hash, new SshWriter(512).Raw(kMpint).Raw(exchangeHash).Byte((byte)letter).Raw(SessionId!).AsSpan());
        result.AddRange(block);
        while (result.Count < length)
        {
            block = Hash(hash, new SshWriter(512).Raw(kMpint).Raw(exchangeHash).Raw(result.ToArray()).AsSpan());
            result.AddRange(block);
        }
        return result.GetRange(0, length).ToArray();
    }

    private static byte[] Hash(HashAlgorithmName name, ReadOnlySpan<byte> data)
    {
        if (name == HashAlgorithmName.SHA256) return SHA256.HashData(data);
        if (name == HashAlgorithmName.SHA512) return SHA512.HashData(data);
        if (name == HashAlgorithmName.SHA1) return SHA1.HashData(data);
        throw new SshProtocolException("Hash no soportado");
    }

    public void Dispose()
    {
        _in.Dispose();
        _out.Dispose();
    }

    private sealed class Direction : IDisposable
    {
        public AesCtr? Cipher;
        public IncrementalHash? Mac;
        public int MacLength;
        public int BlockSize = 8;
        public uint Sequence;

        public void Activate(AesCtr cipher, IncrementalHash mac, int macLength)
        {
            Cipher?.Dispose();
            Mac?.Dispose();
            Cipher = cipher;
            Mac = mac;
            MacLength = macLength;
            BlockSize = 16;
        }

        public void Dispose()
        {
            Cipher?.Dispose();
            Mac?.Dispose();
        }
    }
}
