// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Diagnostics;
using CopyexFTPServer.Core;
using CopyexFTPServer.UI;

namespace CopyexFTPServer;

internal static class Program
{
    // Mismo nombre que 2.x: impide que convivan dos versiones peleando por el puerto 21.
#if UI_PREVIEW
    // Solo para capturas de desarrollo: permite abrirla junto a otra instancia.
    private const string MutexName = @"Local\CopyexFTPServer_Preview";
    private const string ShowEventName = @"Local\CopyexFTPServer_PreviewShow";
#else
    private const string MutexName = @"Local\CopyexFTPServer_SingleInstance";
    private const string ShowEventName = @"Local\CopyexFTPServer_Show";
#endif

    private static volatile bool _restartRequested;

    internal static void RequestRestart() => _restartRequested = true;

    [STAThread]
    private static int Main(string[] args)
    {
        var mutex = new Mutex(true, MutexName, out bool firstInstance);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out bool createdEvent);

        if (!firstInstance)
        {
            if (!createdEvent)
            {
                // Ya hay una 3.x abierta (quizá en la bandeja): se le pide que muestre su ventana.
                showSignal.Set();
            }
            else
            {
                MessageBox.Show("Copyex FTP Server ya está en ejecución (posiblemente una versión anterior).\r\n\r\n" +
                                "Ciérrala desde el icono junto al reloj antes de abrir esta versión.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            showSignal.Dispose();
            mutex.Dispose();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUnhandled(e.Exception, showDialog: true);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportUnhandled(e.ExceptionObject as Exception, showDialog: false);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error($"ERROR no observado: {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };

        Logger.Info($"APLICACIÓN INICIADA {AppInfo.Name}");

        bool startup = args.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));
        bool forceShow = args.Any(a => a.Equals("--restart", StringComparison.OrdinalIgnoreCase) ||
                                        a.Equals("--show", StringComparison.OrdinalIgnoreCase));
        try
        {
            using var form = new MainForm(startup, forceShow, showSignal);
            Application.Run(form);
        }
        catch (Exception ex)
        {
            ReportUnhandled(ex, showDialog: true);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
            // Se cierran los identificadores ANTES de relanzar: si no, la nueva instancia
            // creería que esta sigue abierta y se cerraría.
            showSignal.Dispose();
            mutex.Dispose();
        }

        if (_restartRequested)
        {
            try
            {
                string exe = Environment.ProcessPath ?? Application.ExecutablePath;
                Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false })?.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo reiniciar {AppInfo.Name}:\r\n{ex.Message}", AppInfo.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        return 0;
    }

    private static void ReportUnhandled(Exception? ex, bool showDialog)
    {
        if (ex == null)
            return;
        Logger.Error($"ERROR inesperado: {ex}");
        if (!showDialog)
            return;
        try
        {
            MessageBox.Show($"Se ha producido un error inesperado:\r\n\r\n{ex.Message}\r\n\r\nEl detalle está en la pestaña Registro.",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Sin interfaz disponible.
        }
    }
}
