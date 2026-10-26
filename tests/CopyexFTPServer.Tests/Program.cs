// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CopyexFTPServer.Core;
using CopyexFTPServer.Ftp;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Tests;

internal static partial class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    private static async Task<int> Main()
    {
        // Modo "askpass": OpenSSH nos pide la contraseña durante las pruebas SFTP.
        if (Environment.GetEnvironmentVariable("CX_ASKPASS_PASSWORD") is string askpass)
        {
            Console.Out.Write(askpass + "\n");
            return 0;
        }

        Console.OutputEncoding = Encoding.UTF8;

        // Modo depuración: servidor SFTP en marcha (CX_SFTP_SERVE=puerto;carpeta) hasta Ctrl+C.
        if (Environment.GetEnvironmentVariable("CX_SFTP_SERVE") is string serve)
            return await ServeSftpAsync(serve);
        string work = Path.Combine(Path.GetTempPath(), "copyexftp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        Console.WriteLine($"Carpeta de pruebas: {work}\n");

        try
        {
            Section("Rutas virtuales y nombres peligrosos");
            PathTests(work);

            Section("Configuración (compatibilidad 2.x, fichero dañado)");
            ConfigTests();

            Section("Política de carpetas raíz");
            RootPolicyTests();

            Section("Utilidades de seguridad");
            MiscTests();

            Section("Servidor FTP: protocolo, transferencias y ataques");
            await ServerTests(work);

            Section("Servidor FTP: límites, inactividad y contraseñas erróneas");
            await LimitTests(work);

            Section("Compatibilidad con cliente real (curl.exe)");
            await CurlTests(work);

            Section("SFTP con cliente real (OpenSSH sftp.exe)");
            await SftpTests(work);
        }
        catch (Exception ex)
        {
            Fail("Excepción no controlada en las pruebas", ex.ToString());
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"RESULTADO: {_passed} correctas, {_failed} fallidas");
        foreach (string f in Failures)
            Console.WriteLine("  FALLO: " + f);
        return _failed == 0 ? 0 : 1;
    }

    // ================================================================== infraestructura

    private static void Section(string name) => Console.WriteLine($"\n--- {name} ---");

    private static void Check(bool condition, string name, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("  OK    " + name);
        }
        else
        {
            Fail(name, detail);
        }
    }

    private static void Fail(string name, string? detail)
    {
        _failed++;
        Failures.Add(name);
        Console.WriteLine("  FALLO " + name + (detail != null ? "  -> " + detail : ""));
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
            Fail(name, "no lanzó " + typeof(T).Name);
        }
        catch (T)
        {
            Check(true, name);
        }
        catch (Exception ex)
        {
            Fail(name, "lanzó " + ex.GetType().Name);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static FtpUser User(string name, string password, Action<FtpUser>? configure = null)
    {
        var u = new FtpUser { Username = name };
        u.SetPassword(password);
        configure?.Invoke(u);
        return u;
    }

    // ================================================================== pruebas unitarias

    private static void PathTests(string work)
    {
        Check(FtpFileSystem.CombineVirtual("/", "../../..") == "/", "'..' en la raíz se queda en la raíz");
        Check(FtpFileSystem.CombineVirtual("/a/b", "../c") == "/a/c", "ruta relativa con '..'");
        Check(FtpFileSystem.CombineVirtual("/x", "a\\..\\..\\..\\b") == "/b", "barras invertidas normalizadas");
        Check(FtpFileSystem.CombineVirtual("/x", "/y/./z") == "/y/z", "ruta absoluta con '.'");

        foreach (string bad in new[] { "a.pdf:oculto", "C:/Windows", "CON", "con.txt", "nul", "LPT1.log", "COM3", "fichero.", "fichero ", "a*b", "a?b", "x\u0001y", "..." })
            Throws<FtpPathException>(() => FtpFileSystem.CombineVirtual("/", bad), $"rechaza '{bad.Replace("\u0001", "\\x01")}'");

        string root = Path.Combine(work, "fsroot");
        Directory.CreateDirectory(root);
        var fs = new FtpFileSystem(root);
        string p = fs.Resolve("/", "../../../Windows/win.ini", out string v);
        Check(p.StartsWith(fs.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && v == "/Windows/win.ini",
            "'../../../Windows' queda dentro de la raíz", p);
        Check(fs.ToPhysical("/") == fs.Root, "raíz virtual = raíz física");
    }

    private static void ConfigTests()
    {
        string configPath = AppPaths.ConfigFile;
        string? backup = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
        try
        {
            // config.json tal y como lo guardaba la versión 2.1.1 (hash de 100.000 iteraciones, sin campos nuevos)
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2("clave-antigua", salt, 100_000, HashAlgorithmName.SHA256, 32);
            string legacy = $$"""
            {
              "Port": 2121,
              "RootFolder": "C:\\escaner_test",
              "LogRetentionDays": 30,
              "StartWithWindows": true,
              "StartMinimized": true,
              "AutoStartServer": true,
              "DefaultScannerUserInitialized": true,
              "DefaultsV20Initialized": true,
              "Users": [
                {
                  "Username": "antiguo",
                  "PasswordHash": "{{Convert.ToBase64String(hash)}}",
                  "PasswordSalt": "{{Convert.ToBase64String(salt)}}",
                  "Enabled": true,
                  "RootFolder": "",
                  "CanBrowse": true, "CanDownload": true, "CanUpload": true,
                  "CanDelete": false, "CanCreateDirectories": true, "CanRename": false
                }
              ]
            }
            """;
            File.WriteAllText(configPath, legacy);
            AppConfig c = ConfigService.Load();
            Check(c.Port == 2121 && c.RootFolder == @"C:\escaner_test", "conserva puerto y raíz de 2.x");
            FtpUser? u = c.Users.FirstOrDefault(x => x.Username == "antiguo");
            Check(u != null && u.VerifyPassword("clave-antigua"), "contraseña de 2.x sigue siendo válida");
            Check(u != null && !u.VerifyPassword("otra"), "contraseña incorrecta rechazada");
            Check(u != null && !u.CanDelete && !u.CanRename, "conserva permisos de 2.x");
            Check(c.InitialSetupCompleted == true, "instalación 2.x se considera ya configurada");
            Check(c.ConfigVersion == 3 && c.PassivePortMin == 50000 && c.AllowOnlyLocalNetworks, "campos nuevos con valores por defecto");

            File.WriteAllText(configPath, "{ esto no es json");
            AppConfig broken = ConfigService.Load();
            string dir = Path.GetDirectoryName(configPath)!;
            bool savedCopy = Directory.EnumerateFiles(dir, "config.json.danado-*").Any();
            Check(broken.Users.Any(x => x.Username == "escaner") && savedCopy, "config dañado: se aparta una copia y se usa la configuración por defecto");
            foreach (string f in Directory.EnumerateFiles(dir, "config.json.danado-*")) File.Delete(f);

            AppConfig d = ConfigService.CreateDefault();
            Check(ConfigService.HasDefaultCredentials(d), "detecta contraseña de fábrica escaner/escaner");
            d.Users[0].SetPassword("nueva-clave");
            Check(!ConfigService.HasDefaultCredentials(d), "aviso desaparece al cambiar la contraseña");
        }
        finally
        {
            if (backup != null) File.WriteAllText(configPath, backup);
            else File.Delete(configPath);
        }
    }

    private static void RootPolicyTests()
    {
        string sys = Path.GetPathRoot(Environment.SystemDirectory)!;
        Check(RootFolderPolicy.Validate(sys, out _) != null, $"rechaza la unidad del sistema ({sys})");
        Check(RootFolderPolicy.Validate(Environment.GetFolderPath(Environment.SpecialFolder.Windows), out _) != null, "rechaza C:\\Windows");
        Check(RootFolderPolicy.Validate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), out _) != null, "rechaza subcarpetas de Windows");
        Check(RootFolderPolicy.Validate(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), out _) != null, "rechaza Archivos de programa");
        Check(RootFolderPolicy.Validate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out _) != null, "rechaza el perfil de usuario completo");
        Check(RootFolderPolicy.Validate(Path.Combine(sys, "escaner"), out string full) == null && full.EndsWith("escaner"), "acepta C:\\escaner");
        Check(RootFolderPolicy.Validate(@"D:\", out _) == null, "acepta la raíz de otro disco (RAID)");
        Check(RootFolderPolicy.Validate("", out _) != null, "rechaza ruta vacía");
    }

    private static void MiscTests()
    {
        string s = Logger.Sanitize("LOGIN OK\r\n[2026-01-01] FALSO");
        Check(!s.Contains('\r') && !s.Contains('\n'), "el registro no admite saltos de línea inyectados");


        Check(FtpUser.ValidateUsername("escaner") == null, "usuario válido");
        Check(FtpUser.ValidateUsername("a/b") != null && FtpUser.ValidateUsername(" a") != null && FtpUser.ValidateUsername(new string('x', 65)) != null,
            "usuarios con caracteres o longitud no válidos rechazados");
        Check(LocalNetworkFilter.IsLocal(IPAddress.Parse("192.168.1.50")) && LocalNetworkFilter.IsLocal(IPAddress.Loopback),
            "IPs privadas y loopback son red local");
        Check(!LocalNetworkFilter.IsLocal(IPAddress.Parse("8.8.8.8")) && !LocalNetworkFilter.IsLocal(IPAddress.Parse("1.2.3.4")),
            "IPs públicas no son red local");
    }

    // ================================================================== servidor

    private static async Task ServerTests(string work)
    {
        string root = Path.Combine(work, "root");
        string otherRoot = Path.Combine(work, "otra-raiz");
        int port = FreePort();
        var users = new List<FtpUser>
        {
            User("test", "Secreto-123"),
            User("subir", "Subir-123", u => { u.CanBrowse = false; u.CanDownload = false; u.CanDelete = false; u.CanRename = false; u.CanCreateDirectories = false; }),
            User("otro", "Otro-123", u => u.RootFolder = otherRoot)
        };

        var server = new FtpServer();
        var received = new List<FtpFileReceivedInfo>();
        server.FileReceived += info => { lock (received) received.Add(info); };
        server.Start(new FtpServerOptions
        {
            Port = port,
            RootFolder = root,
            ListenAddress = IPAddress.Loopback,
            PassivePortMin = 52000,
            PassivePortMax = 52200,
            MaxConnectionsPerIp = 50,
            FailedLoginDelay = TimeSpan.FromMilliseconds(50)
        }, users);

        try
        {
            (RawFtp ftp, int greet, _) = await RawFtp.ConnectAsync(port);
            using (ftp)
            {
                Check(greet == 220, "saludo 220");
                Check((await ftp.CmdAsync("LIST")).Code == 530, "LIST sin iniciar sesión -> 530");
                Check(await ftp.LoginAsync("test", "mala") == 530, "contraseña incorrecta -> 530");
                Check(await ftp.LoginAsync("noexiste", "x") == 530, "usuario inexistente -> 530 (misma respuesta)");
                Check(await ftp.LoginAsync("test", "Secreto-123") == 230, "login correcto -> 230");
                Check((await ftp.CmdAsync("AUTH TLS")).Code == 502, "AUTH TLS -> 502 (cliente puede seguir sin TLS)");
                Check((await ftp.CmdAsync("PWD")).Text.Contains("\"/\""), "PWD inicial '/'");
                Check((await ftp.CmdAsync("MKD docs")).Code == 257, "MKD docs");
                Check((await ftp.CmdAsync("MKD docs")).Code == 550, "MKD repetido -> 550");
                Check((await ftp.CmdAsync("CWD docs")).Code == 250 && (await ftp.CmdAsync("PWD")).Text.Contains("\"/docs\""), "CWD docs");
                Check((await ftp.CmdAsync("CDUP")).Code == 200 && (await ftp.CmdAsync("PWD")).Text.Contains("\"/\""), "CDUP vuelve a '/'");
                Check((await ftp.CmdAsync("CWD ../../..")).Code == 250 && (await ftp.CmdAsync("PWD")).Text.Contains("\"/\""), "CWD ../../.. no sale de la raíz");

                // --- transferencias ---
                byte[] scan = RandomNumberGenerator.GetBytes(1024 * 1024);
                (int sc, string st) = await ftp.StorAsync("docs/scan1.pdf", scan);
                Check(sc == 226, "STOR 1 MB -> 226", st);
                string physical = Path.Combine(root, "docs", "scan1.pdf");
                Check(File.Exists(physical) && File.ReadAllBytes(physical).AsSpan().SequenceEqual(scan), "archivo subido íntegro en disco");
                await Task.Delay(100);
                lock (received) Check(received.Any(r => r.FilePath == physical && r.Bytes == scan.Length && r.Username == "test"), "evento de archivo recibido (notificación)");

                Check((await ftp.CmdAsync("SIZE docs/scan1.pdf")).Text == "213 1048576", "SIZE");
                Check((await ftp.CmdAsync("MDTM docs/scan1.pdf")).Code == 213, "MDTM");

                (int rc, _, byte[] down) = await ftp.RetrAsync("docs/scan1.pdf");
                Check(rc == 226 && down.AsSpan().SequenceEqual(scan), "RETR descarga íntegra");
                (rc, _, down) = await ftp.RetrAsync("docs/scan1.pdf", rest: 1000);
                Check(rc == 226 && down.AsSpan().SequenceEqual(scan.AsSpan(1000)), "REST + RETR (reanudar descarga)");

                byte[] big = RandomNumberGenerator.GetBytes(800_000);
                await ftp.StorAsync("big.bin", big[..300_000]);
                (int rs, string rt) = await ftp.StorAsync("big.bin", big[300_000..], rest: 300_000);
                Check(rs == 226 && File.ReadAllBytes(Path.Combine(root, "big.bin")).AsSpan().SequenceEqual(big),
                    "REST + STOR reanuda la subida sin corromper (fallaba en 2.x)", rt);
                Check((await ftp.CmdAsync("REST 99999999")).Code == 350 && (await ftp.StorAsync("big.bin", new byte[] { 1 }, null)).Code is 226 or 554,
                    "REST obsoleto no afecta a otros comandos");
                File.WriteAllBytes(Path.Combine(root, "big.bin"), big);

                await ftp.StorAsync("small.txt", Encoding.ASCII.GetBytes("xyz"));
                await ftp.StorAsync("small.txt", Encoding.ASCII.GetBytes("abc"), verb: "APPE");
                Check(File.ReadAllText(Path.Combine(root, "small.txt")) == "xyzabc", "APPE añade al final");

                string unicodeName = "escaneo ñ ü €.pdf";
                Check((await ftp.StorAsync(unicodeName, new byte[] { 1, 2, 3 })).Code == 226, "STOR con nombre UTF-8 (ñ, €)");

                // --- listados ---
                (int lc, _, string listing) = await ftp.ListAsync("LIST -la docs");
                Check(lc == 226 && listing.Contains("scan1.pdf"), "LIST -la docs");
                Check(Regex.IsMatch(listing, @"\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) \d\d "), "LIST usa meses en inglés (formato estándar)", listing);
                (lc, _, listing) = await ftp.ListAsync("LIST");
                Check(listing.Contains(unicodeName), "LIST muestra el nombre UTF-8", listing);
                (lc, _, listing) = await ftp.ListAsync("MLSD docs");
                Check(lc == 226 && listing.Contains("type=file;size=1048576;"), "MLSD");
                (lc, _, listing) = await ftp.ListAsync("NLST");
                Check(lc == 226 && listing.Contains("small.txt") && listing.Contains("docs"), "NLST");
                Check((await ftp.CmdAsync("MLST docs/scan1.pdf")).Text.Contains("/docs/scan1.pdf"), "MLST");

                // --- ataques de ruta ---
                Check((await ftp.RetrAsync("../../../../Windows/win.ini")).Code == 550, "RETR ../../Windows/win.ini -> no existe dentro de la raíz");
                Check((await ftp.CmdAsync("SIZE C:/Windows/win.ini")).Code == 553, "ruta con letra de unidad -> 553");
                Check((await ftp.CmdAsync("CWD C:\\Windows")).Code == 553, "CWD C:\\Windows -> 553");
                Check((await ftp.CmdAsync("SIZE \\..\\..\\Windows\\win.ini")).Code == 550, "barras invertidas no escapan de la raíz");
                Check((await ftp.StorAsync("a.txt:oculto", new byte[] { 1 })).Code == 553, "flujo alternativo NTFS (a.txt:oculto) -> 553");
                Check((await ftp.StorAsync("CON", new byte[] { 1 })).Code == 553, "nombre de dispositivo CON -> 553");
                Check((await ftp.StorAsync("nul.txt", new byte[] { 1 })).Code == 553, "nombre de dispositivo nul.txt -> 553");
                Check((await ftp.CmdAsync("MKD a/../../fuera")).Code == 257 && Directory.Exists(Path.Combine(root, "fuera")) &&
                      !Directory.Exists(Path.Combine(work, "fuera")), "MKD a/../../fuera se crea dentro de la raíz");

                // --- protección de la raíz ---
                Check((await ftp.CmdAsync("RMD /")).Code == 550, "RMD / -> 550");
                Check((await ftp.CmdAsync("RMD ..")).Code == 550, "RMD .. -> 550");
                Check((await ftp.CmdAsync("RNFR /")).Code == 550, "RNFR / -> 550");
                Check((await ftp.CmdAsync("DELE /")).Code == 550, "DELE / -> 550");
                Check(Directory.Exists(root), "la carpeta raíz sigue existiendo");

                // --- renombrar / borrar ---
                Check((await ftp.CmdAsync("RNTO x")).Code == 503, "RNTO sin RNFR -> 503");
                Check((await ftp.CmdAsync("RNFR docs/scan1.pdf")).Code == 350 && (await ftp.CmdAsync("RNTO docs/scan2.pdf")).Code == 250, "RNFR/RNTO");
                Check((await ftp.CmdAsync("RNFR small.txt")).Code == 350 && (await ftp.CmdAsync("RNTO big.bin")).Code == 553, "RNTO sobre archivo existente -> 553");
                Check((await ftp.CmdAsync("RNFR docs")).Code == 350 && (await ftp.CmdAsync("RNTO docs/sub")).Code == 553, "mover carpeta dentro de sí misma -> 553");
                Check((await ftp.CmdAsync("RMD docs")).Code == 550, "RMD carpeta no vacía -> 550");
                Check((await ftp.CmdAsync("DELE docs/scan2.pdf")).Code == 250 && (await ftp.CmdAsync("RMD docs")).Code == 250, "DELE y RMD");

                // --- modo activo y anti-bounce ---
                Check((await ftp.CmdAsync("PORT 10,0,0,1,200,10")).Code == 501, "PORT hacia otra IP -> 501 (anti FTP bounce)");
                Check((await ftp.CmdAsync("PORT 127,0,0,1,0,80")).Code == 501, "PORT a puerto privilegiado -> 501");
                Check((await ftp.CmdAsync("EPRT |1|8.8.8.8|5000|")).Code == 501, "EPRT hacia otra IP -> 501");
                var active = new TcpListener(IPAddress.Loopback, 0);
                active.Start();
                int ap = ((IPEndPoint)active.LocalEndpoint).Port;
                Check((await ftp.CmdAsync($"PORT 127,0,0,1,{ap / 256},{ap % 256}")).Code == 200, "PORT al propio cliente -> 200");
                await ftp.SendAsync("NLST");
                using (TcpClient activeData = await active.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10)))
                {
                    (int c150, _) = await ftp.ReadReplyAsync();
                    string names = await new StreamReader(activeData.GetStream()).ReadToEndAsync();
                    (int c226, _) = await ftp.ReadReplyAsync();
                    Check(c150 == 150 && c226 == 226 && names.Contains("small.txt"), "modo activo (PORT) funciona");
                }
                active.Stop();

                // --- robo de conexión pasiva ---
                IPEndPoint pasv = await ftp.PasvAsync();
                using var attacker = new TcpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                await attacker.ConnectAsync(pasv);
                using var legit = new TcpClient();
                await legit.ConnectAsync(pasv);
                await ftp.SendAsync("NLST");
                (int h150, _) = await ftp.ReadReplyAsync();
                string legitData = await new StreamReader(legit.GetStream()).ReadToEndAsync();
                (int h226, _) = await ftp.ReadReplyAsync();
                int stolen;
                try { stolen = await attacker.GetStream().ReadAsync(new byte[1024]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { stolen = 0; }
                Check(h150 == 150 && h226 == 226 && legitData.Contains("small.txt") && stolen == 0,
                    "conexión de datos desde otra IP rechazada; el cliente legítimo recibe los datos (fallaba en 2.x)");

                // --- robustez del canal de control ---
                await ftp.SendRawAsync(Encoding.ASCII.GetBytes(new string('A', 20000) + "\r\n"));
                Check((await ftp.ReadReplyAsync()).Code == 500, "línea de 20 KB -> 500 sin agotar memoria");
                Check((await ftp.CmdAsync("NOOP")).Code == 200, "la sesión sigue operativa tras la línea larga");
                await ftp.SendRawAsync(new byte[] { 0xFF, 0xF4, 0xFF, 0xF2 }.Concat(Encoding.ASCII.GetBytes("ABOR\r\n")).ToArray());
                Check((await ftp.ReadReplyAsync()).Code == 226, "ABOR con secuencia Telnet IAC");
                Check((await ftp.CmdAsync("XYZZY")).Code == 502, "comando desconocido -> 502");
                Check((await ftp.CmdAsync("LIST")).Code == 425, "LIST sin PASV/PORT -> 425");

                // --- cambios de usuario en caliente ---
                users[0].SetPassword("Cambiada-456");
                server.UpdateUsers(users);
                Check((await ftp.CmdAsync("PWD")).Code == 421, "cambio de contraseña cierra la sesión abierta");
            }

            // permisos limitados
            (RawFtp up, _, _) = await RawFtp.ConnectAsync(port);
            using (up)
            {
                Check(await up.LoginAsync("subir", "Subir-123") == 230, "login usuario solo-subida");
                Check((await up.CmdAsync("CWD /")).Code == 550, "sin permiso de navegar -> 550");
                Check((await up.StorAsync("subida.pdf", new byte[] { 9, 9 })).Code == 226, "puede subir");
                Check((await up.RetrAsync("subida.pdf")).Code == 550, "no puede descargar");
                Check((await up.CmdAsync("DELE subida.pdf")).Code == 550, "no puede borrar");
                Check((await up.CmdAsync("MKD nueva")).Code == 550, "no puede crear carpetas");
                Check((await up.StorAsync("sub/carpeta/x.pdf", new byte[] { 1 })).Code == 550, "no puede crear carpetas al subir");

                users[1].Enabled = false;
                server.UpdateUsers(users);
                Check((await up.CmdAsync("PWD")).Code == 421, "deshabilitar el usuario corta su sesión al momento");
            }

            // raíz propia
            (RawFtp other, _, _) = await RawFtp.ConnectAsync(port);
            using (other)
            {
                Check(await other.LoginAsync("otro", "Otro-123") == 230, "login usuario con raíz propia");
                await other.StorAsync("propio.pdf", new byte[] { 7 });
                Check(File.Exists(Path.Combine(otherRoot, "propio.pdf")) && !File.Exists(Path.Combine(root, "propio.pdf")), "sube a su raíz propia");
                Check((await other.CmdAsync("SIZE ../root/small.txt")).Code == 550, "no accede a la raíz general");
            }

            // varios escáneres a la vez
            var tasks = Enumerable.Range(0, 20).Select(async i =>
            {
                (RawFtp c, _, _) = await RawFtp.ConnectAsync(port);
                using (c)
                {
                    if (await c.LoginAsync("otro", "Otro-123") != 230) return false;
                    byte[] data = RandomNumberGenerator.GetBytes(200_000);
                    (int code, _) = await c.StorAsync($"paralelo-{i}.pdf", data);
                    await c.CmdAsync("QUIT");
                    return code == 226 && File.ReadAllBytes(Path.Combine(otherRoot, $"paralelo-{i}.pdf")).AsSpan().SequenceEqual(data);
                }
            }).ToArray();
            bool[] results = await Task.WhenAll(tasks);
            Check(results.All(r => r), "20 subidas simultáneas correctas");

            // detener con sesión abierta
            (RawFtp idle, _, _) = await RawFtp.ConnectAsync(port);
            using (idle)
            {
                await idle.LoginAsync("otro", "Otro-123");
                var sw = Stopwatch.StartNew();
                await server.StopAsync();
                sw.Stop();
                await Task.Delay(200);
                Check(sw.Elapsed < TimeSpan.FromSeconds(6) && !server.IsRunning, $"detener el servidor con sesiones abiertas ({sw.ElapsedMilliseconds} ms)");
                Check((await idle.ReadReplyAsync()).Code == -1, "las sesiones abiertas se cierran al detener");
                Check(server.ActiveConnections == 0, "contador de conexiones vuelve a 0");
            }

            // reinicio en el mismo puerto
            server.Start(new FtpServerOptions { Port = port, RootFolder = root, ListenAddress = IPAddress.Loopback, PassivePortMin = 52000, PassivePortMax = 52200 }, users);
            (RawFtp again, int g2, _) = await RawFtp.ConnectAsync(port);
            using (again) Check(g2 == 220, "reinicio del servidor en el mismo puerto");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task LimitTests(string work)
    {
        string root = Path.Combine(work, "limits");
        var users = new List<FtpUser> { User("test", "Secreto-123") };

        // conexiones por IP + inactividad
        int port = FreePort();
        var server = new FtpServer();
        server.Start(new FtpServerOptions
        {
            Port = port, RootFolder = root, ListenAddress = IPAddress.Loopback,
            PassivePortMin = 52300, PassivePortMax = 52400,
            MaxConnectionsPerIp = 2, IdleTimeout = TimeSpan.FromSeconds(2)
        }, users);
        try
        {
            (RawFtp a, int ga, _) = await RawFtp.ConnectAsync(port);
            (RawFtp b, int gb, _) = await RawFtp.ConnectAsync(port);
            (RawFtp c, int gc, _) = await RawFtp.ConnectAsync(port);
            Check(ga == 220 && gb == 220 && gc == 421, "3ª conexión desde la misma IP rechazada (límite 2)");
            c.Dispose();
            await Task.Delay(3000);
            Check((await a.ReadReplyAsync()).Code == 421, "sesión inactiva cerrada con 421 tras el tiempo límite");
            a.Dispose();
            b.Dispose();
            await Task.Delay(300);
            Check(server.ActiveConnections == 0, "las conexiones cerradas liberan su hueco");
        }
        finally
        {
            await server.StopAsync();
        }

        // contraseñas erróneas: sin bloqueo de IP
        port = FreePort();
        server = new FtpServer();
        server.Start(new FtpServerOptions
        {
            Port = port, RootFolder = root, ListenAddress = IPAddress.Loopback,
            PassivePortMin = 52300, PassivePortMax = 52400,
            FailedLoginDelay = TimeSpan.FromMilliseconds(50)
        }, users);
        try
        {
            for (int round = 0; round < 3; round++)
            {
                (RawFtp s, _, _) = await RawFtp.ConnectAsync(port);
                using (s)
                {
                    int r1 = await s.LoginAsync("test", "a");
                    int r2 = await s.LoginAsync("test", "b");
                    int r3 = await s.LoginAsync("test", "c");
                    if (round == 0)
                        Check(r1 == 530 && r2 == 530 && r3 == 421, "3 contraseñas fallidas en una conexión -> se corta esa conexión");
                }
            }
            (RawFtp ok, int g, _) = await RawFtp.ConnectAsync(port);
            using (ok)
                Check(g == 220 && await ok.LoginAsync("test", "Secreto-123") == 230,
                    "sin bloqueo de IP: tras 9 fallos el escáner puede conectar con la contraseña correcta");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task CurlTests(string work)
    {
        string curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (!File.Exists(curl))
        {
            Console.WriteLine("  (curl.exe no disponible: se omite)");
            return;
        }

        string root = Path.Combine(work, "curlroot");
        int port = FreePort();
        var server = new FtpServer();
        server.Start(new FtpServerOptions { Port = port, RootFolder = root, ListenAddress = IPAddress.Loopback, PassivePortMin = 52500, PassivePortMax = 52600 },
            new[] { User("scan", "Scan-2026") });
        try
        {
            string local = Path.Combine(work, "curl-upload.pdf");
            byte[] content = RandomNumberGenerator.GetBytes(300_000);
            File.WriteAllBytes(local, content);
            string baseUrl = $"ftp://127.0.0.1:{port}";

            (int e1, string o1) = await RunAsync(curl, "-sS", "--ftp-create-dirs", "-u", "scan:Scan-2026", "-T", local, baseUrl + "/escaneos/2026/");
            Check(e1 == 0 && File.Exists(Path.Combine(root, "escaneos", "2026", "curl-upload.pdf")), "curl: subida (EPSV) creando carpetas", o1);

            string downloaded = Path.Combine(work, "curl-download.pdf");
            (int e2, string o2) = await RunAsync(curl, "-sS", "-u", "scan:Scan-2026", "-o", downloaded, baseUrl + "/escaneos/2026/curl-upload.pdf");
            Check(e2 == 0 && File.ReadAllBytes(downloaded).AsSpan().SequenceEqual(content), "curl: descarga íntegra", o2);

            (int e3, string o3) = await RunAsync(curl, "-sS", "-u", "scan:Scan-2026", baseUrl + "/escaneos/2026/");
            Check(e3 == 0 && o3.Contains("curl-upload.pdf"), "curl: listado", o3);

            (int e4, string o4) = await RunAsync(curl, "-sS", "--disable-epsv", "-u", "scan:Scan-2026", "--list-only", baseUrl + "/escaneos/2026/");
            Check(e4 == 0 && o4.Contains("curl-upload.pdf"), "curl: modo PASV clásico", o4);

            (int e5, string o5) = await RunAsync(curl, "-sS", "-P", "127.0.0.1", "-u", "scan:Scan-2026", "--list-only", baseUrl + "/escaneos/2026/");
            Check(e5 == 0 && o5.Contains("curl-upload.pdf"), "curl: modo activo (EPRT/PORT)", o5);

            (int e6, _) = await RunAsync(curl, "-sS", "-u", "scan:incorrecta", "--list-only", baseUrl + "/");
            Check(e6 != 0, "curl: contraseña incorrecta rechazada");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        Task<string> o = p.StandardOutput.ReadToEndAsync();
        Task<string> e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await o + await e);
    }
}
