// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace CopyexFTPServer.Models;

/// <summary>
/// Cuenta FTP. Los nombres de propiedad son los mismos que en 2.x para que el config.json
/// existente se pueda reutilizar sin migraciones manuales.
/// </summary>
public sealed class FtpUser
{
    /// <summary>Iteraciones que usaban las versiones 2.x (se siguen aceptando).</summary>
    public const int LegacyIterations = 100_000;

    /// <summary>Iteraciones para contraseñas nuevas o cambiadas en 3.x.</summary>
    public const int CurrentIterations = 210_000;

    public const int MaxUsernameLength = 64;
    public const int MaxPasswordLength = 256;

    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;

    /// <summary>0 = configuración de 2.x (100.000 iteraciones).</summary>
    public int PasswordIterations { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Vacío = usa la raíz general configurada en el servidor.</summary>
    public string RootFolder { get; set; } = string.Empty;

    public bool CanBrowse { get; set; } = true;
    public bool CanDownload { get; set; } = true;
    public bool CanUpload { get; set; } = true;
    public bool CanDelete { get; set; } = true;
    public bool CanCreateDirectories { get; set; } = true;
    public bool CanRename { get; set; } = true;

    [JsonIgnore]
    public bool UsesDefaultRoot => string.IsNullOrWhiteSpace(RootFolder);

    public void SetPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, CurrentIterations, HashAlgorithmName.SHA256, 32);
        PasswordSalt = Convert.ToBase64String(salt);
        PasswordHash = Convert.ToBase64String(hash);
        PasswordIterations = CurrentIterations;
    }

    public bool VerifyPassword(string? password)
    {
        if (password == null || password.Length > MaxPasswordLength)
            return false;
        if (string.IsNullOrWhiteSpace(PasswordSalt) || string.IsNullOrWhiteSpace(PasswordHash))
            return false;

        try
        {
            byte[] salt = Convert.FromBase64String(PasswordSalt);
            byte[] expected = Convert.FromBase64String(PasswordHash);
            if (expected.Length is < 16 or > 64)
                return false;

            int iterations = PasswordIterations > 0 ? PasswordIterations : LegacyIterations;
            iterations = Math.Clamp(iterations, 10_000, 2_000_000);

            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    public string GetPermissionsSummary()
    {
        var p = new List<string>(6);
        if (CanBrowse) p.Add("listar");
        if (CanDownload) p.Add("descargar");
        if (CanUpload) p.Add("subir");
        if (CanCreateDirectories) p.Add("crear carpetas");
        if (CanRename) p.Add("renombrar");
        if (CanDelete) p.Add("borrar");
        return p.Count == 0 ? "Sin permisos" : string.Join(", ", p);
    }

    /// <summary>Devuelve null si el nombre es válido o el motivo si no lo es.</summary>
    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "El nombre de usuario no puede estar vacío.";
        if (username.Length > MaxUsernameLength)
            return $"El nombre de usuario no puede superar {MaxUsernameLength} caracteres.";
        if (!string.Equals(username, username.Trim(), StringComparison.Ordinal))
            return "El nombre de usuario no puede empezar ni terminar con espacios.";
        foreach (char c in username)
        {
            if (char.IsControl(c) || "/\\:*?\"<>|".Contains(c))
                return "El nombre de usuario contiene caracteres no permitidos ( / \\ : * ? \" < > | ).";
        }
        return null;
    }

    public FtpUser Clone() => new()
    {
        Username = Username,
        PasswordHash = PasswordHash,
        PasswordSalt = PasswordSalt,
        PasswordIterations = PasswordIterations,
        Enabled = Enabled,
        RootFolder = RootFolder,
        CanBrowse = CanBrowse,
        CanDownload = CanDownload,
        CanUpload = CanUpload,
        CanDelete = CanDelete,
        CanCreateDirectories = CanCreateDirectories,
        CanRename = CanRename
    };
}
