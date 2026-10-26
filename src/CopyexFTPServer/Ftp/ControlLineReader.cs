// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Text;

namespace CopyexFTPServer.Ftp;

internal sealed class LineTooLongException : Exception
{
    public LineTooLongException() : base("Línea de comando demasiado larga") { }
}

/// <summary>
/// Lee líneas del canal de control con un tamaño máximo fijo. StreamReader.ReadLine (2.x)
/// acumulaba líneas sin límite en memoria antes de comprobar la longitud: un cliente podía
/// enviar gigas sin salto de línea y agotar la RAM del equipo.
/// </summary>
internal sealed class ControlLineReader
{
    public const int MaxLineBytes = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[MaxLineBytes];
    private int _start;
    private int _end;

    public ControlLineReader(Stream stream) => _stream = stream;

    /// <summary>Devuelve la siguiente línea (sin CR/LF) o null si el cliente cerró la conexión.</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        bool discarding = false;
        while (true)
        {
            int newline = _end > _start ? Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start) : -1;
            if (newline >= 0)
            {
                int length = newline - _start;
                string? line = discarding ? null : Decode(_buffer.AsSpan(_start, length));
                _start = newline + 1;
                if (_start == _end) _start = _end = 0;
                if (discarding)
                    throw new LineTooLongException();
                return line;
            }

            if (_end - _start >= _buffer.Length)
            {
                // Búfer lleno sin salto de línea: se descarta hasta el siguiente '\n'.
                discarding = true;
                _start = _end = 0;
            }
            else if (_end == _buffer.Length)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            int read = await _stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return null;
            _end += read;
        }
    }

    private static string Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > 0 && raw[^1] == (byte)'\r')
            raw = raw[..^1];

        // Algunos clientes envían secuencias Telnet (IAC = 0xFF) antes de ABOR: se eliminan.
        Span<byte> clean = raw.Length <= 512 ? stackalloc byte[raw.Length] : new byte[raw.Length];
        int n = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == 0xFF)
            {
                i++; // salta el byte de comando Telnet
                continue;
            }
            clean[n++] = raw[i];
        }
        ReadOnlySpan<byte> bytes = clean[..n];

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Escáneres antiguos envían nombres en Latin-1 en lugar de UTF-8.
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
