// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Net;
using System.Net.Sockets;
using CopyexFTPServer.Core;
using CopyexFTPServer.Ftp;

namespace CopyexFTPServer.Sftp;

/// <summary>Servidor SFTP (SSH-2, solo subsistema sftp, autenticación por contraseña).</summary>
public sealed class SftpServer : TcpServerBase
{
    private SshHostKey? _hostKey;

    protected override string Protocol => "SFTP";

    /// <summary>Clave de identidad del servidor. Obligatoria antes de iniciar.</summary>
    public SshHostKey? HostKey
    {
        get => _hostKey;
        set
        {
            if (IsRunning)
                throw new InvalidOperationException("No se puede cambiar la clave con el servidor en marcha.");
            _hostKey = value;
        }
    }

    protected override void ValidateOptions(FtpServerOptions options)
    {
        if (options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "Puerto SFTP fuera de rango (1-65535).");
        if (string.IsNullOrWhiteSpace(options.RootFolder))
            throw new ArgumentException("La carpeta raíz no puede estar vacía.", nameof(options));
        if (_hostKey == null)
            throw new InvalidOperationException("Falta la clave del servidor SFTP.");
    }

    protected override void OnStarted(FtpServerOptions options) =>
        Logger.Info($"SFTP: huella de la clave del servidor {_hostKey!.Fingerprint}");

    // SSH no tiene un mensaje de rechazo previo al saludo: se cierra la conexión.
    protected override string? RejectMessage(string reason) => null;

    protected override async Task RunSessionAsync(long id, TcpClient client, IPAddress remoteIp, FtpServerOptions options, CancellationToken serverToken)
    {
        using var session = new SshSession(id, client, remoteIp, options, this, _hostKey!, serverToken);
        await session.RunAsync().ConfigureAwait(false);
    }
}
