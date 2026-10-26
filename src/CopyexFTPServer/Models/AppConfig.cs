// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

namespace CopyexFTPServer.Models;

/// <summary>
/// Configuración persistida en config.json. Compatible con el formato 2.x: las propiedades
/// antiguas mantienen su nombre y las nuevas toman su valor por defecto si no existen.
/// </summary>
public sealed class AppConfig
{
    public const int DefaultPassivePortMin = 50000;
    public const int DefaultPassivePortMax = 50100;

    // --- Servidor (2.x) ---
    public int Port { get; set; } = 21;
    public string RootFolder { get; set; } = @"C:\escaner";
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool AutoStartServer { get; set; } = true;
    public bool DefaultScannerUserInitialized { get; set; }
    public bool DefaultsV20Initialized { get; set; }
    public bool? InitialSetupCompleted { get; set; }
    public List<FtpUser> Users { get; set; } = new();

    // --- Nuevas en 3.0 ---
    /// <summary>0 en ficheros de 2.x (no existía); 3 en configuraciones creadas o migradas por 3.x.</summary>
    public int ConfigVersion { get; set; }

    /// <summary>Rango de puertos para FTP pasivo (permite abrirlos en el firewall si hace falta).</summary>
    public int PassivePortMin { get; set; } = DefaultPassivePortMin;
    public int PassivePortMax { get; set; } = DefaultPassivePortMax;

    /// <summary>Rechaza conexiones que no procedan de la red local (IPs privadas o subredes del propio equipo).</summary>
    public bool AllowOnlyLocalNetworks { get; set; } = true;

    public int MaxConnections { get; set; } = 50;
    public int MaxConnectionsPerIp { get; set; } = 10;

    /// <summary>SFTP (FTP sobre SSH, cifrado). Desactivado salvo que se elija en Configuración.</summary>
    public bool SftpEnabled { get; set; }
    public int SftpPort { get; set; } = 22;

    /// <summary>Segundos sin recibir comandos antes de cerrar la sesión FTP.</summary>
    public int IdleTimeoutSeconds { get; set; } = 300;

    public bool NotifyOnUpload { get; set; } = true;

    /// <summary>Registra cada comando y respuesta (Server Trace completo). El registro vive solo en memoria.</summary>
    public bool DetailedTrace { get; set; } = true;

    public AppConfig CloneSettings()
    {
        var copy = (AppConfig)MemberwiseClone();
        copy.Users = Users.Select(u => u.Clone()).ToList();
        return copy;
    }
}
