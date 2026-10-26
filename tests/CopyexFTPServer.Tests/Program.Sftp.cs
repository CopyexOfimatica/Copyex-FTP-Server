// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CopyexFTPServer.Ftp;
using CopyexFTPServer.Models;
using CopyexFTPServer.Sftp;

namespace CopyexFTPServer.Tests;

internal static partial class Program
{
    private static readonly string OpenSshDir = Path.Combine(Environment.SystemDirectory, "OpenSSH");

    private static async Task SftpTests(string work)
    {
        string sftpExe = Path.Combine(OpenSshDir, "sftp.exe");
        if (!File.Exists(sftpExe))
        {
            Console.WriteLine("  (OpenSSH no disponible: se omite)");
            return;
        }

        string root = Path.Combine(work, "sftproot");
        string upOnlyRoot = Path.Combine(work, "sftp-subida");
        string keyPath = Path.Combine(work, "sftp_host_key.pem");
        int port = FreePort();

        var users = new List<FtpUser>
        {
            User("scan", "Scan-2026"),
            User("solosubir", "Sube-2026", u =>
            {
                u.RootFolder = upOnlyRoot;
                u.CanBrowse = false; u.CanDownload = false; u.CanDelete = false; u.CanRename = false; u.CanCreateDirectories = false;
            })
        };

        using SshHostKey key = SshHostKey.LoadOrCreate(keyPath);
        using (SshHostKey again = SshHostKey.LoadOrCreate(keyPath))
            Check(again.Fingerprint == key.Fingerprint && key.Fingerprint.StartsWith("SHA256:"), "la clave del servidor se guarda y se reutiliza (misma huella)");

        var server = new SftpServer { HostKey = key };
        var received = new List<FtpFileReceivedInfo>();
        server.FileReceived += i => { lock (received) received.Add(i); };
        server.Start(new FtpServerOptions
        {
            Port = port, RootFolder = root, ListenAddress = IPAddress.Loopback,
            FailedLoginDelay = TimeSpan.FromMilliseconds(50)
        }, users);

        try
        {
            string small = Path.Combine(work, "scan-local.pdf");
            byte[] smallData = RandomNumberGenerator.GetBytes(150_000);
            File.WriteAllBytes(small, smallData);
            string big = Path.Combine(work, "grande-local.bin");
            byte[] bigData = RandomNumberGenerator.GetBytes(6 * 1024 * 1024);
            File.WriteAllBytes(big, bigData);
            string down = Path.Combine(work, "descargado.pdf");
            string bigDown = Path.Combine(work, "grande-descargado.bin");

            (int exit, string output) = await RunSftpAsync(port, "scan", "Scan-2026", new[]
            {
                "mkdir escaneos",
                "cd escaneos",
                $"put {Q(small)} scan.pdf",
                "ls -l",
                $"get scan.pdf {Q(down)}",
                "rename scan.pdf scan2.pdf",
                $"put {Q(small)} borrar.pdf",
                "rm borrar.pdf",
                $"put {Q(big)} grande.bin",
                $"get grande.bin {Q(bigDown)}",
                "pwd",
                "bye"
            });
            string remoteDir = Path.Combine(root, "escaneos");
            Check(exit == 0, "sftp: conexión, cifrado e inicio de sesión con contraseña", Tail(output));
            Check(File.Exists(Path.Combine(remoteDir, "scan2.pdf")) && File.ReadAllBytes(Path.Combine(remoteDir, "scan2.pdf")).AsSpan().SequenceEqual(smallData),
                "sftp: subida íntegra + renombrar", Tail(output));
            Check(output.Contains("scan.pdf"), "sftp: listado (ls -l)", Tail(output));
            Check(File.Exists(down) && File.ReadAllBytes(down).AsSpan().SequenceEqual(smallData), "sftp: descarga íntegra");
            Check(!File.Exists(Path.Combine(remoteDir, "borrar.pdf")), "sftp: borrar archivo");
            Check(File.Exists(bigDown) && File.ReadAllBytes(bigDown).AsSpan().SequenceEqual(bigData) &&
                  File.ReadAllBytes(Path.Combine(remoteDir, "grande.bin")).AsSpan().SequenceEqual(bigData),
                "sftp: archivo de 6 MB subido y descargado íntegro (control de flujo)");
            Check(output.Contains("/escaneos"), "sftp: ruta virtual (pwd)", Tail(output));
            await Task.Delay(200);
            lock (received)
                Check(received.Any(r => r.FilePath.EndsWith("scan.pdf") && r.Username == "scan"), "sftp: notificación de archivo recibido");

            // Algoritmos alternativos (escáneres antiguos o configuraciones estrictas)
            var variants = new (string Name, string[] Options)[]
            {
                ("DH group14-sha256 + rsa-sha2-256 + aes256-ctr + hmac-sha2-512",
                    new[] { "KexAlgorithms=diffie-hellman-group14-sha256", "HostKeyAlgorithms=rsa-sha2-256", "Ciphers=aes256-ctr", "MACs=hmac-sha2-512" }),
                ("DH group14-sha1 + ssh-rsa + aes128-ctr + hmac-sha1 (equipos antiguos)",
                    new[] { "KexAlgorithms=diffie-hellman-group14-sha1", "HostKeyAlgorithms=ssh-rsa", "Ciphers=aes128-ctr", "MACs=hmac-sha1" }),
                ("ECDH nistp256 + aes192-ctr",
                    new[] { "KexAlgorithms=ecdh-sha2-nistp256", "Ciphers=aes192-ctr" }),
                ("renegociación de claves durante la transferencia (RekeyLimit 1M)",
                    new[] { "RekeyLimit=1M" })
            };
            foreach ((string name, string[] options) in variants)
            {
                string target = "v-" + Math.Abs(name.GetHashCode()) + ".bin";
                (int e, string o) = await RunSftpAsync(port, "scan", "Scan-2026", new[] { $"put {Q(big)} {target}", "bye" }, options);
                bool same = File.Exists(Path.Combine(root, target)) && File.ReadAllBytes(Path.Combine(root, target)).AsSpan().SequenceEqual(bigData);
                Check(e == 0 && same, "sftp: " + name, Tail(o));
            }

            // Seguridad
            (int bad, string badOut) = await RunSftpAsync(port, "scan", "incorrecta", new[] { "ls", "bye" });
            Check(bad != 0, "sftp: contraseña incorrecta rechazada", Tail(badOut));

            (int sub, string subOut) = await RunSftpAsync(port, "solosubir", "Sube-2026", new[]
            {
                $"put {Q(small)} subida.pdf",
                "ls",
                "get subida.pdf NUL",
                "rm subida.pdf",
                "mkdir nueva",
                "bye"
            });
            Check(File.Exists(Path.Combine(upOnlyRoot, "subida.pdf")), "sftp: usuario solo-subida puede subir", Tail(subOut));
            Check(subOut.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(Path.Combine(upOnlyRoot, "nueva")),
                "sftp: usuario solo-subida no puede listar, descargar, borrar ni crear carpetas", Tail(subOut));

            (int esc, string escOut) = await RunSftpAsync(port, "scan", "Scan-2026", new[]
            {
                "cd ../../..",
                "pwd",
                $"get /../../Windows/win.ini {Q(Path.Combine(work, "robado.ini"))}",
                "bye"
            });
            Check(!File.Exists(Path.Combine(work, "robado.ini")) && escOut.Contains("Remote working directory: /"),
                "sftp: no se puede salir de la carpeta raíz", Tail(escOut));

            string sshExe = Path.Combine(OpenSshDir, "ssh.exe");
            (int sh, string shOut) = await RunOpenSshAsync(sshExe, port, "scan", "Scan-2026", new[] { "whoami" }, Array.Empty<string>(), null);
            Check(sh != 0 && !shOut.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase),
                "ssh: ejecutar comandos está prohibido (solo SFTP)", Tail(shOut));

            var sw = Stopwatch.StartNew();
            await server.StopAsync();
            Check(!server.IsRunning && sw.Elapsed < TimeSpan.FromSeconds(6) && server.ActiveConnections == 0, "sftp: detener el servidor");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task<int> ServeSftpAsync(string spec)
    {
        string[] parts = spec.Split(';');
        int port = int.Parse(parts[0]);
        string root = parts[1];
        using SshHostKey key = SshHostKey.LoadOrCreate(Path.Combine(Path.GetTempPath(), "cx-debug-host.pem"));
        var server = new SftpServer { HostKey = key };
        server.Start(new FtpServerOptions { Port = port, RootFolder = root, ListenAddress = IPAddress.Loopback, DetailedTrace = true },
            new[] { User("scan", "Scan-2026") });
        long seq = 0;
        var buffer = new List<CopyexFTPServer.Core.LogEntry>();
        int seconds = int.Parse(parts.Length > 2 ? parts[2] : "60");
        for (int i = 0; i < seconds * 4; i++)
        {
            await Task.Delay(250);
            buffer.Clear();
            seq = CopyexFTPServer.Core.Logger.CopySince(seq, buffer);
            foreach (var e in buffer) Console.WriteLine(e.Format());
        }
        await server.StopAsync();
        return 0;
    }

    private static Task<(int ExitCode, string Output)> RunSftpAsync(int port, string user, string password, string[] commands, params string[] extraOptions) =>
        RunOpenSshAsync(Path.Combine(OpenSshDir, "sftp.exe"), port, user, password, Array.Empty<string>(), extraOptions, string.Join("\n", commands) + "\n");

    private static async Task<(int ExitCode, string Output)> RunOpenSshAsync(string exe, int port, string user, string password,
        string[] remoteCommand, string[] extraOptions, string? stdin)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(exe.EndsWith("sftp.exe", StringComparison.OrdinalIgnoreCase) ? "-P" : "-p");
        psi.ArgumentList.Add(port.ToString());
        foreach (string o in new[]
                 {
                     "StrictHostKeyChecking=no", "UserKnownHostsFile=NUL", "GlobalKnownHostsFile=NUL", "LogLevel=ERROR",
                     "PubkeyAuthentication=no", "PreferredAuthentications=password", "NumberOfPasswordPrompts=3", "ConnectTimeout=15"
                 }.Concat(extraOptions))
        {
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(o);
        }
        psi.ArgumentList.Add($"{user}@127.0.0.1");
        foreach (string c in remoteCommand)
            psi.ArgumentList.Add(c);

        psi.Environment["SSH_ASKPASS"] = Environment.ProcessPath!;
        psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        psi.Environment["DISPLAY"] = "localhost:0";
        psi.Environment["CX_ASKPASS_PASSWORD"] = password;

        using Process p = Process.Start(psi)!;
        Task<string> o1 = p.StandardOutput.ReadToEndAsync();
        Task<string> o2 = p.StandardError.ReadToEndAsync();
        if (stdin != null)
            await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-999, "TIEMPO AGOTADO\n" + await o1 + await o2);
        }
        return (p.ExitCode, await o1 + "\n" + await o2);
    }

    /// <summary>Ruta local para sftp.exe (con barras / y entre comillas).</summary>
    private static string Q(string path) => "\"" + path.Replace('\\', '/') + "\"";

    private static string Tail(string text)
    {
        string t = text.Replace("\r", "").Trim();
        return t.Length > 600 ? "…" + t[^600..] : t;
    }
}
