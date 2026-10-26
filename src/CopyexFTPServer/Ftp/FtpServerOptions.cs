// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Net;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Ftp;

public sealed record FtpServerOptions
{
    public required int Port { get; init; }
    public required string RootFolder { get; init; }
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;

    public int PassivePortMin { get; init; } = AppConfig.DefaultPassivePortMin;
    public int PassivePortMax { get; init; } = AppConfig.DefaultPassivePortMax;

    public bool AllowOnlyLocalNetworks { get; init; } = true;
    public int MaxConnections { get; init; } = 50;
    public int MaxConnectionsPerIp { get; init; } = 10;

    public TimeSpan FailedLoginDelay { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Contraseñas erróneas en una misma conexión antes de cortarla.</summary>
    public int MaxFailedLoginsPerSession { get; init; } = 3;

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan DataConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan TransferStallTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool DetailedTrace { get; init; } = true;

    public static FtpServerOptions FromConfig(AppConfig config) => new()
    {
        Port = config.Port,
        RootFolder = config.RootFolder,
        PassivePortMin = config.PassivePortMin,
        PassivePortMax = config.PassivePortMax,
        AllowOnlyLocalNetworks = config.AllowOnlyLocalNetworks,
        MaxConnections = config.MaxConnections,
        MaxConnectionsPerIp = config.MaxConnectionsPerIp,
        IdleTimeout = TimeSpan.FromSeconds(config.IdleTimeoutSeconds),
        DetailedTrace = config.DetailedTrace
    };

    internal void Validate()
    {
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), "Puerto FTP fuera de rango (1-65535).");
        if (string.IsNullOrWhiteSpace(RootFolder))
            throw new ArgumentException("La carpeta raíz FTP no puede estar vacía.", nameof(RootFolder));
        if (PassivePortMin is < 1024 or > 65535 || PassivePortMax is < 1024 or > 65535 || PassivePortMax < PassivePortMin)
            throw new ArgumentOutOfRangeException(nameof(PassivePortMin), "Rango de puertos pasivos no válido (1024-65535).");
        if (Port >= PassivePortMin && Port <= PassivePortMax)
            throw new ArgumentException("El puerto FTP no puede estar dentro del rango de puertos pasivos.", nameof(Port));
        if (MaxConnections < 1 || MaxConnectionsPerIp < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections));
    }
}
