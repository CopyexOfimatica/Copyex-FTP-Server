// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

namespace CopyexFTPServer.Core;

/// <summary>
/// Evita publicar por FTP carpetas del sistema por error (C:\, Windows, Archivos de programa,
/// el perfil completo del usuario...). Las raíces de otros discos (D:\, RAID) sí se permiten.
/// </summary>
public static class RootFolderPolicy
{
    /// <summary>Devuelve null si la carpeta es aceptable, o el motivo del rechazo.</summary>
    public static string? Validate(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return "Indica una carpeta.";

        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception ex)
        {
            return $"La ruta no es válida: {ex.Message}";
        }

        if (!Path.IsPathFullyQualified(fullPath))
            return "Indica una ruta completa (por ejemplo C:\\escaner).";

        if (fullPath.StartsWith(@"\\.\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return "No se admiten rutas de dispositivo.";

        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        if (Same(fullPath, systemDrive))
            return $"No se permite compartir la unidad del sistema completa ({systemDrive}). Usa una subcarpeta, por ejemplo {systemDrive}escaner.";

        foreach (Environment.SpecialFolder special in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.UserProfile
                 })
        {
            string dir;
            try { dir = Environment.GetFolderPath(special); }
            catch { continue; }
            if (string.IsNullOrEmpty(dir))
                continue;

            dir = Path.TrimEndingDirectorySeparator(dir);
            bool exact = Same(fullPath, dir);
            bool inside = fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            // Dentro del perfil sí se permiten subcarpetas (Documentos\Escaneos), pero no el perfil entero.
            if (special == Environment.SpecialFolder.UserProfile ? exact : exact || inside)
                return $"No se permite usar una carpeta del sistema como raíz FTP ({dir}).";
        }

        string usersRoot = Path.TrimEndingDirectorySeparator(Path.Combine(systemDrive, "Users"));
        if (Same(fullPath, usersRoot))
            return $"No se permite compartir la carpeta {usersRoot} completa.";

        return null;
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
