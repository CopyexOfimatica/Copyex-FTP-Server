// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Globalization;
using System.Text;

namespace CopyexFTPServer.Core;

public enum LogKind
{
    Info,
    Success,
    Warning,
    Error,
    /// <summary>Comando recibido del cliente (trace detallado).</summary>
    ClientCommand,
    /// <summary>Respuesta enviada por el servidor (trace detallado).</summary>
    ServerReply
}

public sealed record LogEntry(long Sequence, DateTime Time, LogKind Kind, string Message)
{
    public bool IsTrace => Kind is LogKind.ClientCommand or LogKind.ServerReply;

    public string Format() =>
        "[" + Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + Message;
}

/// <summary>
/// Registro de la aplicación, solo en memoria: no se escribe nada en disco y se pierde al
/// cerrar la aplicación. Guarda las últimas entradas en un buffer circular de tamaño fijo
/// (memoria acotada) que la ventana lee bajo demanda.
/// </summary>
public static class Logger
{
    private const int RingCapacity = 5000;
    private const int MaxMessageLength = 4000;

    private static readonly object RingLock = new();
    private static readonly LogEntry?[] Ring = new LogEntry?[RingCapacity];
    private static long _sequence;

    public static void Info(string message) => Write(LogKind.Info, message);
    public static void Success(string message) => Write(LogKind.Success, message);
    public static void Warning(string message) => Write(LogKind.Warning, message);
    public static void Error(string message) => Write(LogKind.Error, message);

    public static void Write(LogKind kind, string message)
    {
        message = Sanitize(message);
        lock (RingLock)
        {
            long seq = ++_sequence;
            Ring[seq % RingCapacity] = new LogEntry(seq, DateTime.Now, kind, message);
        }
    }

    public static long LatestSequence
    {
        get { lock (RingLock) return _sequence; }
    }

    /// <summary>Copia en <paramref name="target"/> las entradas con secuencia mayor que <paramref name="afterSequence"/>.</summary>
    public static long CopySince(long afterSequence, List<LogEntry> target)
    {
        lock (RingLock)
        {
            long first = Math.Max(afterSequence + 1, _sequence - RingCapacity + 1);
            for (long s = Math.Max(first, 1); s <= _sequence; s++)
            {
                LogEntry? e = Ring[s % RingCapacity];
                if (e != null && e.Sequence == s)
                    target.Add(e);
            }
            return _sequence;
        }
    }

    /// <summary>Evita inyección de líneas falsas en el registro (CR/LF y otros caracteres de control).</summary>
    internal static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        bool needsFix = message.Length > MaxMessageLength;
        if (!needsFix)
        {
            foreach (char c in message)
            {
                if (char.IsControl(c)) { needsFix = true; break; }
            }
        }
        if (!needsFix)
            return message;

        var sb = new StringBuilder(Math.Min(message.Length, MaxMessageLength + 3));
        foreach (char c in message)
        {
            if (sb.Length >= MaxMessageLength) { sb.Append("..."); break; }
            sb.Append(char.IsControl(c) ? '·' : c);
        }
        return sb.ToString();
    }
}
