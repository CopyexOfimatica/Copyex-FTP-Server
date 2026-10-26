// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using Microsoft.Win32;

namespace CopyexFTPServer.Services;

/// <summary>Arranque con Windows mediante HKCU\...\Run (no requiere administrador).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CopyexFTPServer";

    public static void Apply(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("No se pudo abrir la clave de inicio de Windows.");

        if (enabled)
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            string command = $"\"{exe}\" --startup";
            // Solo se escribe si cambia (p. ej. el EXE se ha movido de carpeta).
            if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
