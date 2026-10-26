// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Reflection;

namespace CopyexFTPServer.Core;

public static class AppInfo
{
    public const string Name = "Copyex FTP Server";

    /// <summary>Versión interna (propiedades del EXE). No se muestra en la interfaz.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        string? info = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        int plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
