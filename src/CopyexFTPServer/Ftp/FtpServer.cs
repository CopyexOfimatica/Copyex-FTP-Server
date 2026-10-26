// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Net;
using System.Net.Sockets;
using CopyexFTPServer.Core;

namespace CopyexFTPServer.Ftp;

/// <summary>Servidor FTP (RFC 959 + EPSV/EPRT, MLSD/MLST, SIZE, MDTM, REST).</summary>
public sealed class FtpServer : TcpServerBase
{
    protected override string Protocol => "FTP";

    protected override void OnStarted(FtpServerOptions options)
    {
        Logger.Info($"FTP: pasivo {options.PassivePortMin}-{options.PassivePortMax}, " +
                    $"solo red local: {(options.AllowOnlyLocalNetworks ? "sí" : "no")}, " +
                    $"máx. conexiones {options.MaxConnections} ({options.MaxConnectionsPerIp} por IP)");
    }

    protected override string? RejectMessage(string reason) => "421 Demasiadas conexiones. Inténtelo más tarde.";

    protected override async Task RunSessionAsync(long id, TcpClient client, IPAddress remoteIp, FtpServerOptions options, CancellationToken serverToken)
    {
        using var session = new FtpSession(id, client, remoteIp, options, this, serverToken);
        await session.RunAsync().ConfigureAwait(false);
    }
}
