// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Security.Cryptography;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Ftp;

/// <summary>
/// Copia inmutable de los usuarios que usa el servidor. Al editar usuarios en la interfaz se
/// sustituye entera, así los cambios se aplican en caliente sin bloqueos ni condiciones de carrera.
/// </summary>
public sealed class UserDirectory
{
    private readonly Dictionary<string, FtpUser> _users;

    public static UserDirectory Empty { get; } = new(Array.Empty<FtpUser>());

    // Usuario ficticio: si el nombre no existe se calcula igualmente un hash para que el tiempo
    // de respuesta no delate qué usuarios existen.
    private static readonly Lazy<FtpUser> Dummy = new(() =>
    {
        var u = new FtpUser { Username = "\u0000dummy" };
        u.SetPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));
        return u;
    });

    public UserDirectory(IEnumerable<FtpUser> users)
    {
        _users = new Dictionary<string, FtpUser>(StringComparer.OrdinalIgnoreCase);
        foreach (FtpUser user in users)
        {
            if (!string.IsNullOrWhiteSpace(user.Username))
                _users[user.Username] = user.Clone();
        }
    }

    public int Count => _users.Count;

    public FtpUser? Find(string? username) =>
        username != null && _users.TryGetValue(username, out FtpUser? user) ? user : null;

    /// <summary>Devuelve el usuario si existe, está habilitado y la contraseña es correcta.</summary>
    public FtpUser? Authenticate(string? username, string? password)
    {
        FtpUser? user = Find(username);
        if (user == null || !user.Enabled)
        {
            Dummy.Value.VerifyPassword(password ?? string.Empty);
            return null;
        }
        return user.VerifyPassword(password) ? user : null;
    }
}
