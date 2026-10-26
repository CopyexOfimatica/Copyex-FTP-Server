// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Globalization;
using System.Text.Json;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Core;

public static class ConfigService
{
    public const string DefaultScannerFolder = @"C:\escaner";
    public const string DefaultScannerUser = "escaner";
    public const string DefaultScannerPassword = "escaner";

    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfig Load()
    {
        string path = AppPaths.ConfigFile;
        lock (FileLock)
        {
            if (!File.Exists(path))
            {
                AppConfig fresh = CreateDefault();
                TrySave(fresh);
                Logger.Info("CONFIG: primera ejecución, configuración por defecto creada");
                return fresh;
            }

            AppConfig? config;
            try
            {
                string json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (config == null)
                    throw new JsonException("config.json vacío");
            }
            catch (Exception ex)
            {
                // No se pisa el fichero dañado: se aparta con otro nombre para poder recuperarlo.
                string backup = path + ".danado-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                try { File.Move(path, backup); } catch { }
                Logger.Error($"CONFIG ERROR: config.json no se pudo leer ({ex.Message}). Copia guardada como {Path.GetFileName(backup)}; se usa la configuración por defecto");
                AppConfig fallback = CreateDefault();
                TrySave(fallback);
                return fallback;
            }

            bool changed = Normalize(config);
            if (changed)
                TrySave(config);
            return config;
        }
    }

    public static void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string path = AppPaths.ConfigFile;
        string json = JsonSerializer.Serialize(config, JsonOptions);

        lock (FileLock)
        {
            // Escritura atómica: si se corta la luz a mitad, config.json sigue intacto.
            string temp = path + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
            }
            File.Move(temp, path, overwrite: true);
        }
    }

    private static void TrySave(AppConfig config)
    {
        try { Save(config); }
        catch (Exception ex) { Logger.Error($"CONFIG ERROR: no se pudo guardar config.json: {ex.Message}"); }
    }

    /// <summary>Corrige valores fuera de rango y aplica migraciones de versiones anteriores.</summary>
    internal static bool Normalize(AppConfig config)
    {
        bool changed = false;
        config.Users ??= new List<FtpUser>();

        int removed = config.Users.RemoveAll(u => u == null || string.IsNullOrWhiteSpace(u.Username));
        if (removed > 0) changed = true;

        if (config.Port is < 1 or > 65535) { config.Port = 21; changed = true; }
        if (config.SftpPort is < 1 or > 65535 || config.SftpPort == config.Port)
        {
            config.SftpPort = config.Port == 22 ? 2222 : 22;
            changed = true;
        }

        if (config.PassivePortMin is < 1024 or > 65535 || config.PassivePortMax is < 1024 or > 65535 ||
            config.PassivePortMax < config.PassivePortMin)
        {
            config.PassivePortMin = AppConfig.DefaultPassivePortMin;
            config.PassivePortMax = AppConfig.DefaultPassivePortMax;
            changed = true;
        }

        changed |= Clamp(config.MaxConnections, 1, 1000, v => config.MaxConnections = v);
        changed |= Clamp(config.MaxConnectionsPerIp, 1, 1000, v => config.MaxConnectionsPerIp = v);
        changed |= Clamp(config.IdleTimeoutSeconds, 30, 7200, v => config.IdleTimeoutSeconds = v);

        if (string.IsNullOrWhiteSpace(config.RootFolder) || IsLegacyDefaultRoot(config.RootFolder))
        {
            config.RootFolder = DefaultScannerFolder;
            changed = true;
            Logger.Info($"CONFIG: raíz FTP por defecto establecida en '{DefaultScannerFolder}'");
        }

        // 2.0: autoarranque, inicio FTP y bandeja activados por defecto (una sola vez).
        if (!config.DefaultsV20Initialized)
        {
            config.StartWithWindows = true;
            config.StartMinimized = true;
            config.AutoStartServer = true;
            config.DefaultsV20Initialized = true;
            changed = true;
        }

        // Configuraciones 2.0 anteriores no tenían este indicador: se consideran ya instaladas.
        if (config.InitialSetupCompleted is null)
        {
            config.InitialSetupCompleted = true;
            changed = true;
        }

        if (!config.DefaultScannerUserInitialized)
        {
            if (!config.Users.Any(u => u.Username.Equals(DefaultScannerUser, StringComparison.OrdinalIgnoreCase)))
            {
                config.Users.Add(CreateScannerUser());
                Logger.Info($"CONFIG: creado usuario FTP inicial '{DefaultScannerUser}'");
            }
            config.DefaultScannerUserInitialized = true;
            changed = true;
        }

        if (config.ConfigVersion < 3)
        {
            config.ConfigVersion = 3;
            changed = true;
            Logger.Info("CONFIG: configuración de la versión anterior importada (usuarios y contraseñas conservados)");
        }

        return changed;
    }

    private static bool Clamp(int value, int min, int max, Action<int> set)
    {
        int clamped = Math.Clamp(value, min, max);
        if (clamped == value) return false;
        set(clamped);
        return true;
    }

    private static bool IsLegacyDefaultRoot(string path)
    {
        try
        {
            string legacy = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "FTP_ROOT"));
            return PathsEquivalent(legacy, path);
        }
        catch
        {
            return false;
        }
    }

    public static bool PathsEquivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        try
        {
            string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.Trim()));
            string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right.Trim()));
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static AppConfig CreateDefault()
    {
        var config = new AppConfig
        {
            RootFolder = DefaultScannerFolder,
            StartWithWindows = true,
            StartMinimized = true,
            AutoStartServer = true,
            DefaultScannerUserInitialized = true,
            DefaultsV20Initialized = true,
            InitialSetupCompleted = false,
            ConfigVersion = 3
        };
        config.Users.Add(CreateScannerUser());
        return config;
    }

    private static FtpUser CreateScannerUser()
    {
        var user = new FtpUser
        {
            Username = DefaultScannerUser,
            Enabled = true,
            RootFolder = string.Empty,
            CanBrowse = true,
            CanDownload = true,
            CanUpload = true,
            CanDelete = true,
            CanCreateDirectories = true,
            CanRename = true
        };
        user.SetPassword(DefaultScannerPassword);
        return user;
    }

    /// <summary>True si el usuario inicial sigue habilitado con la contraseña de fábrica.</summary>
    public static bool HasDefaultCredentials(AppConfig config)
    {
        FtpUser? user = config.Users.FirstOrDefault(u =>
            u.Enabled && u.Username.Equals(DefaultScannerUser, StringComparison.OrdinalIgnoreCase));
        return user != null && user.VerifyPassword(DefaultScannerPassword);
    }
}
