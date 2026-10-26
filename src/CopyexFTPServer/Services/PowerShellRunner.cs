// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CopyexFTPServer.Core;

namespace CopyexFTPServer.Services;

public sealed record PowerShellResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Ejecuta PowerShell de forma asíncrona y con tiempo máximo.
/// - Ruta absoluta a powershell.exe (en 2.x se buscaba en el PATH: un powershell.exe
///   malicioso en la carpeta de trabajo podía ejecutarse, incluso con UAC).
/// - El script viaja codificado (-EncodedCommand): no hay problemas de comillas ni inyección.
/// - Lectura de stdout/stderr en paralelo (en 2.x podía bloquearse si stderr se llenaba).
/// </summary>
public static class PowerShellRunner
{
    public const int UacCancelledExitCode = 1223;

    private static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static async Task<PowerShellResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory
        };
        AddCommonArguments(psi, "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException("No se pudo iniciar PowerShell.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException("PowerShell no respondió a tiempo.");
        }

        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        return new PowerShellResult(process.ExitCode, output, error);
    }

    /// <summary>Ejecuta un script con elevación UAC. Devuelve el código de salida (1223 = UAC cancelado).</summary>
    public static async Task<int> RunElevatedAsync(string script, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory
        };
        AddCommonArguments(psi, script);

        try
        {
            using Process? process = Process.Start(psi);
            if (process == null)
                return -1;

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                Logger.Error("RED ERROR: el cambio de configuración no terminó a tiempo");
                return -2;
            }
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UacCancelledExitCode)
        {
            Logger.Warning("RED: operación cancelada en el aviso de Control de cuentas (UAC)");
            return UacCancelledExitCode;
        }
        catch (Exception ex)
        {
            Logger.Error($"RED ERROR: no se pudo ejecutar PowerShell elevado: {ex.Message}");
            return -1;
        }
    }

    private static void AddCommonArguments(ProcessStartInfo psi, string script)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        if (psi.UseShellExecute)
        {
            psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
        }
        else
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encoded);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ya terminó o no hay permisos (proceso elevado): nada más que hacer.
        }
    }

    /// <summary>Entrecomillado seguro para literales de PowerShell.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
