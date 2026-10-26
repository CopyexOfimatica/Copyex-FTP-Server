// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Globalization;
using CopyexFTPServer.Core;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Services;

/// <summary>
/// Perfil de red de Windows (Pública / Privada / Dominio). Las consultas son asíncronas:
/// en 2.x PowerShell se ejecutaba en el hilo de la ventana y la congelaba al arrancar.
/// </summary>
public static class NetworkProfileService
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);

    public static async Task<NetworkCategory> GetCategoryAsync(int interfaceIndex, CancellationToken cancellationToken = default)
    {
        string script =
            "$p = Get-NetConnectionProfile -InterfaceIndex " + interfaceIndex.ToString(CultureInfo.InvariantCulture) +
            " -ErrorAction Stop | Select-Object -First 1; " +
            "if ($null -eq $p) { exit 3 }; " +
            "Write-Output ([string]$p.NetworkCategory)";

        try
        {
            PowerShellResult result = await PowerShellRunner.RunAsync(script, QueryTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                Logger.Warning($"RED: no se pudo consultar el perfil de red (código {result.ExitCode}) {result.Error.Trim()}");
                return NetworkCategory.Unknown;
            }
            return Parse(result.Output.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning($"RED: no se pudo consultar el perfil de red: {ex.Message}");
            return NetworkCategory.Unknown;
        }
    }

    /// <summary>Cambia el adaptador a red Privada (pide UAC). Devuelve true si queda Privada o Dominio.</summary>
    public static async Task<bool> SetPrivateAsync(int interfaceIndex)
    {
        Logger.Info("RED: solicitando cambio de perfil Público a Privado");
        string script =
            "$ErrorActionPreference='Stop'; " +
            "Set-NetConnectionProfile -InterfaceIndex " + interfaceIndex.ToString(CultureInfo.InvariantCulture) +
            " -NetworkCategory Private";

        int exit = await PowerShellRunner.RunElevatedAsync(script, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (exit != 0)
        {
            if (exit != PowerShellRunner.UacCancelledExitCode)
                Logger.Error($"RED ERROR: PowerShell devolvió el código {exit} al cambiar el perfil");
            return false;
        }

        // Windows tarda un instante en reflejar el cambio.
        for (int i = 0; i < 10; i++)
        {
            NetworkCategory now = await GetCategoryAsync(interfaceIndex).ConfigureAwait(false);
            if (now is NetworkCategory.Private or NetworkCategory.Domain)
            {
                Logger.Success($"RED OK: perfil cambiado a {ToDisplayName(now)}");
                return true;
            }
            await Task.Delay(300).ConfigureAwait(false);
        }

        Logger.Warning("RED: Windows aceptó el cambio pero el perfil todavía no se ha actualizado");
        return false;
    }

    public static NetworkCategory Parse(string value) => value switch
    {
        "Public" => NetworkCategory.Public,
        "Private" => NetworkCategory.Private,
        "DomainAuthenticated" => NetworkCategory.Domain,
        _ => NetworkCategory.Unknown
    };

    public static string ToDisplayName(NetworkCategory category) => category switch
    {
        NetworkCategory.Public => "Pública",
        NetworkCategory.Private => "Privada",
        NetworkCategory.Domain => "Dominio",
        _ => "No disponible"
    };
}
