// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

namespace CopyexFTPServer.Core;

/// <summary>
/// Ubicación de config.json y de la clave SFTP. Igual que en 2.x se guardan junto al EXE (modo portable),
/// pero si esa carpeta no es escribible (p. ej. Archivos de programa) se usa %LOCALAPPDATA%.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> DataDir = new(ResolveDataDirectory, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DataDirectory => DataDir.Value;
    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public static string SftpHostKeyFile => Path.Combine(DataDirectory, "sftp_host_key.pem");

    private static string ResolveDataDirectory()
    {
        string exeDir = AppContext.BaseDirectory;
        if (IsWritable(exeDir))
            return exeDir;

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopyexFTPServer");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
