// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Buffers;

namespace CopyexFTPServer.Ftp;

internal static class StreamCopy
{
    private const int BufferSize = 128 * 1024;

    /// <summary>
    /// Copia con vigilancia de bloqueo: si durante <paramref name="stallTimeout"/> no se
    /// mueve ningún byte, se aborta. En 2.x una conexión de datos colgada dejaba la sesión
    /// (y el archivo abierto) bloqueados para siempre.
    /// </summary>
    public static async Task<long> CopyAsync(Stream source, Stream destination, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long total = 0;
        try
        {
            while (true)
            {
                watchdog.CancelAfter(stallTimeout);
                int read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), watchdog.Token).ConfigureAwait(false);
                if (read == 0)
                    break;

                watchdog.CancelAfter(stallTimeout);
                await destination.WriteAsync(buffer.AsMemory(0, read), watchdog.Token).ConfigureAwait(false);
                total += read;
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return total;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Transferencia detenida: sin datos durante {stallTimeout.TotalSeconds:0} s");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
