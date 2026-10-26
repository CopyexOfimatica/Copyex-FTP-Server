// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace CopyexFTPServer.Sftp;

/// <summary>Error de protocolo SSH: se corta la conexión sin dar detalles al cliente.</summary>
internal sealed class SshProtocolException : Exception
{
    public SshProtocolException(string message) : base(message) { }
}

/// <summary>Escritura de tipos SSH (RFC 4251 §5): uint32, string, mpint, name-list…</summary>
internal sealed class SshWriter
{
    private byte[] _buffer;
    private int _length;

    public SshWriter(int capacity = 256) => _buffer = new byte[capacity];

    public int Length => _length;

    public SshWriter Byte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
        return this;
    }

    public SshWriter Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

    public SshWriter UInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
        return this;
    }

    public SshWriter UInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_length), value);
        _length += 8;
        return this;
    }

    public SshWriter Raw(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
        return this;
    }

    public SshWriter String(ReadOnlySpan<byte> data) => UInt32((uint)data.Length).Raw(data);

    public SshWriter String(string text) => String(Encoding.UTF8.GetBytes(text));

    public SshWriter NameList(params string[] names) => String(string.Join(",", names));

    /// <summary>mpint a partir de un entero sin signo en big-endian.</summary>
    public SshWriter MpintUnsigned(ReadOnlySpan<byte> unsignedBigEndian)
    {
        int i = 0;
        while (i < unsignedBigEndian.Length && unsignedBigEndian[i] == 0) i++;
        ReadOnlySpan<byte> v = unsignedBigEndian[i..];
        if (v.Length == 0)
            return UInt32(0);
        bool pad = (v[0] & 0x80) != 0;
        UInt32((uint)(v.Length + (pad ? 1 : 0)));
        if (pad) Byte(0);
        return Raw(v);
    }

    public SshWriter Mpint(BigInteger value)
    {
        if (value.Sign < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        return MpintUnsigned(value.ToByteArray(isUnsigned: true, isBigEndian: true));
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _length);

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length)
            return;
        int size = Math.Max(_buffer.Length * 2, _length + extra);
        Array.Resize(ref _buffer, size);
    }
}

/// <summary>Lectura de tipos SSH con comprobación estricta de límites.</summary>
internal sealed class SshReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _pos;

    public SshReader(ReadOnlyMemory<byte> data) => _data = data;

    public int Remaining => _data.Length - _pos;

    public byte Byte()
    {
        Need(1);
        return _data.Span[_pos++];
    }

    public bool Bool() => Byte() != 0;

    public uint UInt32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_data.Span[_pos..]);
        _pos += 4;
        return v;
    }

    public ulong UInt64()
    {
        Need(8);
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(_data.Span[_pos..]);
        _pos += 8;
        return v;
    }

    public ReadOnlyMemory<byte> StringBytes(int maxLength = 1 << 20)
    {
        uint len = UInt32();
        if (len > (uint)maxLength)
            throw new SshProtocolException("Cadena demasiado larga");
        Need((int)len);
        ReadOnlyMemory<byte> v = _data.Slice(_pos, (int)len);
        _pos += (int)len;
        return v;
    }

    public string String(int maxLength = 64 * 1024)
    {
        ReadOnlyMemory<byte> bytes = StringBytes(maxLength);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes.Span);
        }
        catch (DecoderFallbackException)
        {
            // Algunos clientes antiguos envían Latin-1 en los nombres de archivo.
            return Encoding.Latin1.GetString(bytes.Span);
        }
    }

    public string[] NameList() => String(16 * 1024).Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>mpint positivo como entero sin signo big-endian.</summary>
    public byte[] MpintUnsigned()
    {
        ReadOnlySpan<byte> v = StringBytes(2048).Span;
        if (v.Length > 0 && (v[0] & 0x80) != 0)
            throw new SshProtocolException("mpint negativo");
        int i = 0;
        while (i < v.Length && v[i] == 0) i++;
        return v[i..].ToArray();
    }

    public ReadOnlyMemory<byte> Rest()
    {
        ReadOnlyMemory<byte> r = _data[_pos..];
        _pos = _data.Length;
        return r;
    }

    private void Need(int count)
    {
        if (count < 0 || _pos + count > _data.Length)
            throw new SshProtocolException("Mensaje truncado");
    }
}
