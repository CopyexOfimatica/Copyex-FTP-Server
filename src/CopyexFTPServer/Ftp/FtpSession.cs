// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CopyexFTPServer.Core;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Ftp;

/// <summary>Una conexión de control FTP. Procesa los comandos de forma secuencial.</summary>
internal sealed class FtpSession : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly EnumerationOptions ListOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.System
    };

    private readonly long _id;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ControlLineReader _reader;
    private readonly IPAddress _remoteIp;
    private readonly string _remote;
    private readonly FtpServerOptions _options;
    private readonly FtpServer _server;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenRegistration _closeOnCancel;

    private string? _pendingUser;
    private volatile FtpUser? _user;
    private FtpFileSystem? _fs;
    private string _cwd = "/";
    private TcpListener? _passiveListener;
    private IPEndPoint? _activeEndpoint;
    private string? _renameFromPhysical;
    private string? _renameFromVirtual;
    private long _restartOffset;
    private int _failedLogins;
    private bool _epsvAll;
    private int _disposed;

    public FtpSession(long id, TcpClient client, IPAddress remoteIp, FtpServerOptions options, FtpServer server, CancellationToken serverToken)
    {
        _id = id;
        _client = client;
        _stream = client.GetStream();
        _reader = new ControlLineReader(_stream);
        _remoteIp = remoteIp;
        int port = (client.Client.RemoteEndPoint as IPEndPoint)?.Port ?? 0;
        _remote = remoteIp + ":" + port.ToString(Inv);
        _options = options;
        _server = server;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        // Al detener el servidor se cierra el socket: cualquier lectura/escritura pendiente termina.
        _closeOnCancel = _cts.Token.Register(static state =>
        {
            try { ((TcpClient)state!).Dispose(); } catch { }
        }, client);
    }

    public bool IsAuthenticated => _user != null;

    private bool Trace => _options.DetailedTrace;

    public async Task RunAsync()
    {
        Logger.Info($"CONEXIÓN {_remote} (sesión #{_id})");
        if (!await TryReplyAsync(220, $"{AppInfo.Name} listo").ConfigureAwait(false))
            return;

        while (!_cts.IsCancellationRequested)
        {
            string? line;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                idle.CancelAfter(_options.IdleTimeout);
                try
                {
                    line = await _reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (LineTooLongException)
                {
                    Logger.Warning($"SEGURIDAD {_remote}: comando demasiado largo descartado");
                    if (!await TryReplyAsync(500, "Comando demasiado largo").ConfigureAwait(false)) break;
                    continue;
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    Logger.Info($"INACTIVIDAD {_remote}: sesión cerrada tras {_options.IdleTimeout.TotalSeconds:0} s sin comandos");
                    await TryReplyAsync(421, "Tiempo de inactividad agotado, cerrando conexión").ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
                {
                    break;
                }
            }

            if (line == null)
                break;

            line = line.Trim();
            if (line.Length == 0)
                continue;

            int space = line.IndexOf(' ');
            string command = (space < 0 ? line : line[..space]).ToUpperInvariant();
            string argument = space < 0 ? string.Empty : line[(space + 1)..].Trim();

            if (Trace)
            {
                string shown = command == "PASS" ? "PASS ********" : (argument.Length > 0 ? command + " " + argument : command);
                Logger.Write(LogKind.ClientCommand, $"{_remote} C> {shown}");
            }

            if (command.Length > 8)
            {
                if (!await TryReplyAsync(500, "Comando no reconocido").ConfigureAwait(false)) break;
                continue;
            }

            bool keepOpen;
            try
            {
                keepOpen = await HandleCommandAsync(command, argument).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsControlChannelFailure(ex))
            {
                break;
            }
            catch (Exception ex)
            {
                CloseDataChannel();
                (int code, string text) = MapException(ex, command);
                if (code is 451 or 426)
                    Logger.Error($"ERROR {_remote} {command}: {ex.Message}");
                keepOpen = await TryReplyAsync(code, text).ConfigureAwait(false);
            }

            if (!keepOpen)
                break;
        }

        Logger.Info($"DESCONEXIÓN {_remote} usuario={_user?.Username ?? "-"}");
    }

    // ------------------------------------------------------------------ comandos

    private async Task<bool> HandleCommandAsync(string cmd, string arg)
    {
        // RNTO debe seguir inmediatamente a RNFR; REST solo afecta a la siguiente transferencia.
        if (cmd is not ("RNFR" or "RNTO"))
        {
            _renameFromPhysical = null;
            _renameFromVirtual = null;
        }
        if (cmd is not ("REST" or "RETR" or "STOR" or "APPE" or "PASV" or "EPSV" or "PORT" or "EPRT" or "TYPE" or "MODE" or "STRU" or "NOOP"))
            _restartOffset = 0;

        switch (cmd)
        {
            case "USER": return await CmdUserAsync(arg).ConfigureAwait(false);
            case "PASS": return await CmdPassAsync(arg).ConfigureAwait(false);
            case "QUIT":
                await ReplyAsync(221, "Hasta luego").ConfigureAwait(false);
                return false;
            case "SYST":
                await ReplyAsync(215, "UNIX Type: L8").ConfigureAwait(false);
                return true;
            case "FEAT":
                await ReplyMultiAsync(211, "Extensiones soportadas:",
                    new[] { "UTF8", "SIZE", "MDTM", "REST STREAM", "MLST type*;size*;modify*;", "EPSV", "EPRT", "PASV" },
                    "Fin").ConfigureAwait(false);
                return true;
            case "OPTS":
                await ReplyAsync(200, arg.StartsWith("UTF8", StringComparison.OrdinalIgnoreCase) ? "UTF8 activado" : "OPTS aceptado").ConfigureAwait(false);
                return true;
            case "NOOP":
                await ReplyAsync(200, "OK").ConfigureAwait(false);
                return true;
            case "CLNT":
                await ReplyAsync(200, "OK").ConfigureAwait(false);
                return true;
            case "HELP":
                await ReplyAsync(214, "Comandos: USER PASS QUIT PWD CWD CDUP LIST NLST MLSD MLST RETR STOR APPE REST SIZE MDTM DELE MKD RMD RNFR RNTO PASV EPSV PORT EPRT TYPE NOOP").ConfigureAwait(false);
                return true;
            case "AUTH":
            case "PBSZ":
            case "PROT":
            case "CCC":
                await ReplyAsync(502, "TLS no disponible en este servidor").ConfigureAwait(false);
                return true;
        }

        if (_user == null || _fs == null)
        {
            await ReplyAsync(530, "Inicie sesión con USER y PASS").ConfigureAwait(false);
            return true;
        }

        if (!await RefreshUserAsync().ConfigureAwait(false))
            return false;

        FtpUser user = _user!;
        switch (cmd)
        {
            case "PWD":
            case "XPWD":
                await ReplyAsync(257, $"\"{_cwd.Replace("\"", "\"\"")}\" es el directorio actual").ConfigureAwait(false);
                return true;

            case "CWD":
            case "XCWD":
                if (!await RequireAsync(user.CanBrowse, "navegar").ConfigureAwait(false)) return true;
                await ChangeDirectoryAsync(arg, 250).ConfigureAwait(false);
                return true;

            case "CDUP":
            case "XCUP":
                if (!await RequireAsync(user.CanBrowse, "navegar").ConfigureAwait(false)) return true;
                await ChangeDirectoryAsync("..", 200).ConfigureAwait(false);
                return true;

            case "TYPE":
                {
                    string t = arg.ToUpperInvariant();
                    bool ok = t is "A" or "A N" or "I" or "L 8" or "L8";
                    await ReplyAsync(ok ? 200 : 504, ok ? $"Tipo establecido a {t}" : "Tipo no soportado").ConfigureAwait(false);
                    return true;
                }

            case "MODE":
                {
                    bool ok = arg.Equals("S", StringComparison.OrdinalIgnoreCase);
                    await ReplyAsync(ok ? 200 : 504, ok ? "Modo S" : "Solo se admite MODE S").ConfigureAwait(false);
                    return true;
                }

            case "STRU":
                {
                    bool ok = arg.Equals("F", StringComparison.OrdinalIgnoreCase);
                    await ReplyAsync(ok ? 200 : 504, ok ? "Estructura F" : "Solo se admite STRU F").ConfigureAwait(false);
                    return true;
                }

            case "PASV":
                await EnterPassiveAsync(extended: false).ConfigureAwait(false);
                return true;

            case "EPSV":
                if (arg.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                {
                    _epsvAll = true;
                    await ReplyAsync(200, "EPSV ALL aceptado").ConfigureAwait(false);
                    return true;
                }
                await EnterPassiveAsync(extended: true).ConfigureAwait(false);
                return true;

            case "PORT":
                await CmdPortAsync(arg).ConfigureAwait(false);
                return true;

            case "EPRT":
                await CmdEprtAsync(arg).ConfigureAwait(false);
                return true;

            case "LIST":
                if (!await RequireAsync(user.CanBrowse, "listar").ConfigureAwait(false)) return true;
                await SendListingAsync(arg, ListingFormat.Unix).ConfigureAwait(false);
                return true;

            case "NLST":
                if (!await RequireAsync(user.CanBrowse, "listar").ConfigureAwait(false)) return true;
                await SendListingAsync(arg, ListingFormat.NamesOnly).ConfigureAwait(false);
                return true;

            case "MLSD":
                if (!await RequireAsync(user.CanBrowse, "listar").ConfigureAwait(false)) return true;
                await SendListingAsync(arg, ListingFormat.Machine).ConfigureAwait(false);
                return true;

            case "MLST":
                if (!await RequireAsync(user.CanBrowse, "listar").ConfigureAwait(false)) return true;
                await CmdMlstAsync(arg).ConfigureAwait(false);
                return true;

            case "STOR":
                if (!await RequireAsync(user.CanUpload, "subir/escribir").ConfigureAwait(false)) return true;
                await ReceiveFileAsync(arg, append: false).ConfigureAwait(false);
                return true;

            case "APPE":
                if (!await RequireAsync(user.CanUpload, "subir/escribir").ConfigureAwait(false)) return true;
                await ReceiveFileAsync(arg, append: true).ConfigureAwait(false);
                return true;

            case "RETR":
                if (!await RequireAsync(user.CanDownload, "descargar/leer").ConfigureAwait(false)) return true;
                await SendFileAsync(arg).ConfigureAwait(false);
                return true;

            case "REST":
                if (long.TryParse(arg, NumberStyles.None, Inv, out long offset) && offset >= 0)
                {
                    _restartOffset = offset;
                    await ReplyAsync(350, $"Reinicio en la posición {offset.ToString(Inv)}").ConfigureAwait(false);
                }
                else
                {
                    await ReplyAsync(501, "Posición de reinicio no válida").ConfigureAwait(false);
                }
                return true;

            case "SIZE":
                if (!await RequireAsync(user.CanDownload || user.CanBrowse, "consultar archivo").ConfigureAwait(false)) return true;
                {
                    string path = _fs.Resolve(_cwd, arg, out _);
                    if (!File.Exists(path)) throw new FileNotFoundException();
                    await ReplyAsync(213, new FileInfo(path).Length.ToString(Inv)).ConfigureAwait(false);
                    return true;
                }

            case "MDTM":
                if (!await RequireAsync(user.CanDownload || user.CanBrowse, "consultar archivo").ConfigureAwait(false)) return true;
                {
                    string path = _fs.Resolve(_cwd, arg, out _);
                    if (!File.Exists(path)) throw new FileNotFoundException();
                    await ReplyAsync(213, File.GetLastWriteTimeUtc(path).ToString("yyyyMMddHHmmss", Inv)).ConfigureAwait(false);
                    return true;
                }

            case "DELE":
                if (!await RequireAsync(user.CanDelete, "borrar").ConfigureAwait(false)) return true;
                {
                    string path = _fs.Resolve(_cwd, arg, out string vpath);
                    if (FtpFileSystem.IsRoot(vpath) || Directory.Exists(path))
                    {
                        await ReplyAsync(550, "No es un archivo").ConfigureAwait(false);
                        return true;
                    }
                    if (!File.Exists(path)) throw new FileNotFoundException();
                    File.Delete(path);
                    Logger.Success($"BORRADO OK {_remote} usuario={user.Username} archivo={path}");
                    await ReplyAsync(250, "Archivo eliminado").ConfigureAwait(false);
                    return true;
                }

            case "MKD":
            case "XMKD":
                if (!await RequireAsync(user.CanCreateDirectories, "crear carpetas").ConfigureAwait(false)) return true;
                {
                    string path = _fs.Resolve(_cwd, arg, out string vpath);
                    if (FtpFileSystem.IsRoot(vpath) || Directory.Exists(path) || File.Exists(path))
                    {
                        await ReplyAsync(550, "Ya existe").ConfigureAwait(false);
                        return true;
                    }
                    Directory.CreateDirectory(path);
                    Logger.Success($"CARPETA CREADA {_remote} usuario={user.Username} carpeta={path}");
                    await ReplyAsync(257, $"\"{vpath.Replace("\"", "\"\"")}\" creado").ConfigureAwait(false);
                    return true;
                }

            case "RMD":
            case "XRMD":
                if (!await RequireAsync(user.CanDelete, "borrar").ConfigureAwait(false)) return true;
                {
                    string path = _fs.Resolve(_cwd, arg, out string vpath);
                    if (FtpFileSystem.IsRoot(vpath))
                    {
                        await ReplyAsync(550, "No se puede eliminar la carpeta raíz").ConfigureAwait(false);
                        return true;
                    }
                    if (!Directory.Exists(path)) throw new DirectoryNotFoundException();
                    Directory.Delete(path, recursive: false);
                    if (_cwd.Equals(vpath, StringComparison.OrdinalIgnoreCase) || _cwd.StartsWith(vpath + "/", StringComparison.OrdinalIgnoreCase))
                        _cwd = "/";
                    Logger.Success($"CARPETA ELIMINADA {_remote} usuario={user.Username} carpeta={path}");
                    await ReplyAsync(250, "Carpeta eliminada").ConfigureAwait(false);
                    return true;
                }

            case "RNFR":
                if (!await RequireAsync(user.CanRename, "renombrar").ConfigureAwait(false)) return true;
                {
                    string path = _fs.Resolve(_cwd, arg, out string vpath);
                    if (FtpFileSystem.IsRoot(vpath))
                    {
                        await ReplyAsync(550, "No se puede renombrar la carpeta raíz").ConfigureAwait(false);
                        return true;
                    }
                    if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException();
                    _renameFromPhysical = path;
                    _renameFromVirtual = vpath;
                    await ReplyAsync(350, "Preparado para RNTO").ConfigureAwait(false);
                    return true;
                }

            case "RNTO":
                if (!await RequireAsync(user.CanRename, "renombrar").ConfigureAwait(false)) return true;
                await CmdRenameToAsync(arg, user).ConfigureAwait(false);
                return true;

            case "ALLO":
                await ReplyAsync(202, "No es necesario reservar espacio").ConfigureAwait(false);
                return true;

            case "SITE":
                await ReplyAsync(200, "OK").ConfigureAwait(false);
                return true;

            case "ABOR":
                CloseDataChannel();
                await ReplyAsync(226, "No hay transferencia en curso").ConfigureAwait(false);
                return true;

            case "STAT":
                if (arg.Length > 0)
                {
                    await ReplyAsync(502, "STAT con argumentos no soportado").ConfigureAwait(false);
                    return true;
                }
                await ReplyMultiAsync(211, AppInfo.Name,
                    new[] { $"Conectado desde {_remote}", $"Usuario: {user.Username}", $"Directorio: {_cwd}" },
                    "Fin del estado").ConfigureAwait(false);
                return true;

            default:
                await ReplyAsync(502, "Comando no implementado").ConfigureAwait(false);
                return true;
        }
    }

    private async Task<bool> CmdUserAsync(string arg)
    {
        if (arg.Length == 0 || arg.Length > 128)
        {
            await ReplyAsync(501, "Nombre de usuario no válido").ConfigureAwait(false);
            return true;
        }
        _pendingUser = arg;
        _user = null;
        _fs = null;
        _cwd = "/";
        CloseDataChannel();
        await ReplyAsync(331, "Usuario recibido, envíe la contraseña").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> CmdPassAsync(string arg)
    {
        if (_pendingUser == null)
        {
            await ReplyAsync(503, "Envíe USER primero").ConfigureAwait(false);
            return true;
        }

        string username = _pendingUser;
        _pendingUser = null;
        FtpUser? user = _server.Users.Authenticate(username, arg);

        if (user == null)
        {
            _failedLogins++;
            Logger.Warning($"LOGIN FALLIDO {_remote} usuario={username}");

            // Retardo fijo: frena los ataques de diccionario sin bloquear a nadie.
            await Task.Delay(_options.FailedLoginDelay, _cts.Token).ConfigureAwait(false);

            if (_failedLogins >= _options.MaxFailedLoginsPerSession)
            {
                await ReplyAsync(421, "Demasiados intentos fallidos, cerrando conexión").ConfigureAwait(false);
                return false;
            }
            await ReplyAsync(530, "Usuario o contraseña incorrectos").ConfigureAwait(false);
            return true;
        }

        FtpFileSystem fs;
        try
        {
            string root = RootFor(user);
            Directory.CreateDirectory(root);
            fs = new FtpFileSystem(root);
        }
        catch (Exception ex)
        {
            Logger.Error($"LOGIN ERROR {_remote} usuario={user.Username}: la carpeta raíz no está disponible ({ex.Message})");
            await ReplyAsync(550, "La carpeta raíz del usuario no está disponible").ConfigureAwait(false);
            return true;
        }

        _user = user;
        _fs = fs;
        _cwd = "/";
        _failedLogins = 0;
        Logger.Success($"LOGIN OK {_remote} usuario={user.Username} raíz={fs.Root}");
        await ReplyAsync(230, "Sesión iniciada").ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Aplica en caliente los cambios hechos desde la interfaz: si el usuario se deshabilita o
    /// elimina se corta la sesión; si cambian permisos o raíz se aplican al siguiente comando.
    /// </summary>
    private async Task<bool> RefreshUserAsync()
    {
        FtpUser current = _user!;
        FtpUser? latest = _server.Users.Find(current.Username);
        if (latest == null || !latest.Enabled)
        {
            Logger.Warning($"SESIÓN CERRADA {_remote}: el usuario '{current.Username}' se ha deshabilitado o eliminado");
            await ReplyAsync(421, "Usuario deshabilitado").ConfigureAwait(false);
            return false;
        }

        if (!ReferenceEquals(latest, current))
        {
            if (!string.Equals(latest.PasswordHash, current.PasswordHash, StringComparison.Ordinal))
            {
                Logger.Info($"SESIÓN CERRADA {_remote}: la contraseña de '{current.Username}' ha cambiado");
                await ReplyAsync(421, "Credenciales modificadas, vuelva a iniciar sesión").ConfigureAwait(false);
                return false;
            }

            string newRoot = Path.GetFullPath(RootFor(latest));
            if (!ConfigService.PathsEquivalent(newRoot, _fs!.Root))
            {
                Directory.CreateDirectory(newRoot);
                _fs = new FtpFileSystem(newRoot);
                _cwd = "/";
            }
            _user = latest;
        }
        return true;
    }

    private string RootFor(FtpUser user) =>
        Path.GetFullPath(user.UsesDefaultRoot ? _options.RootFolder : user.RootFolder);

    private async Task ChangeDirectoryAsync(string arg, int successCode)
    {
        string path = _fs!.Resolve(_cwd, arg, out string vpath);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException();
        _cwd = vpath;
        await ReplyAsync(successCode, "Directorio cambiado a " + vpath).ConfigureAwait(false);
    }

    private async Task<bool> RequireAsync(bool allowed, string action)
    {
        if (allowed)
            return true;
        Logger.Warning($"PERMISO DENEGADO {_remote} usuario={_user?.Username ?? "-"} acción={action}");
        await ReplyAsync(550, $"Permiso denegado: {action}").ConfigureAwait(false);
        return false;
    }

    private async Task CmdRenameToAsync(string arg, FtpUser user)
    {
        if (_renameFromPhysical == null || _renameFromVirtual == null)
        {
            await ReplyAsync(503, "Use RNFR primero").ConfigureAwait(false);
            return;
        }

        string from = _renameFromPhysical;
        string fromVirtual = _renameFromVirtual;
        _renameFromPhysical = null;
        _renameFromVirtual = null;

        string to = _fs!.Resolve(_cwd, arg, out string toVirtual);
        if (FtpFileSystem.IsRoot(toVirtual))
        {
            await ReplyAsync(553, "Destino no válido").ConfigureAwait(false);
            return;
        }

        bool sameName = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        if (!sameName && (File.Exists(to) || Directory.Exists(to)))
        {
            await ReplyAsync(553, "Ya existe un archivo o carpeta con ese nombre").ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(from))
        {
            if (toVirtual.StartsWith(fromVirtual + "/", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(553, "No se puede mover una carpeta dentro de sí misma").ConfigureAwait(false);
                return;
            }
            Directory.Move(from, to);
        }
        else if (File.Exists(from))
        {
            File.Move(from, to, overwrite: false);
        }
        else
        {
            throw new FileNotFoundException();
        }

        Logger.Success($"RENOMBRADO OK {_remote} usuario={user.Username} de={from} a={to}");
        await ReplyAsync(250, "Renombrado").ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ canal de datos

    private async Task EnterPassiveAsync(bool extended)
    {
        CloseDataChannel();

        IPAddress local = (_client.Client.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        if (local.IsIPv4MappedToIPv6)
            local = local.MapToIPv4();

        if (!extended && local.AddressFamily != AddressFamily.InterNetwork)
        {
            await ReplyAsync(522, "Use EPSV para conexiones IPv6").ConfigureAwait(false);
            return;
        }

        TcpListener? listener = BindPassiveListener(local);
        if (listener == null)
        {
            Logger.Error($"PASIVO ERROR {_remote}: no hay puertos libres en {_options.PassivePortMin}-{_options.PassivePortMax}");
            await ReplyAsync(425, "No hay puertos pasivos disponibles").ConfigureAwait(false);
            return;
        }

        _passiveListener = listener;
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        if (extended)
        {
            await ReplyAsync(229, $"Modo pasivo extendido (|||{port.ToString(Inv)}|)").ConfigureAwait(false);
            return;
        }

        byte[] b = local.GetAddressBytes();
        await ReplyAsync(227, string.Create(Inv, $"Entering Passive Mode ({b[0]},{b[1]},{b[2]},{b[3]},{port >> 8},{port & 0xFF})")).ConfigureAwait(false);
    }

    private TcpListener? BindPassiveListener(IPAddress local)
    {
        int min = _options.PassivePortMin;
        int range = _options.PassivePortMax - min + 1;
        int start = Random.Shared.Next(range);

        for (int i = 0; i < range; i++)
        {
            int port = min + (start + i) % range;
            var listener = new TcpListener(local, port);
            try
            {
                listener.Server.ExclusiveAddressUse = true;
                listener.Start(4);
                return listener;
            }
            catch (SocketException)
            {
                listener.Dispose();
            }
        }
        return null;
    }

    private async Task CmdPortAsync(string arg)
    {
        if (_epsvAll)
        {
            await ReplyAsync(501, "PORT no permitido tras EPSV ALL").ConfigureAwait(false);
            return;
        }

        string[] p = arg.Split(',');
        var v = new byte[6];
        if (p.Length != 6 || !p.Select((s, i) => byte.TryParse(s.Trim(), NumberStyles.None, Inv, out v[i])).All(ok => ok))
        {
            await ReplyAsync(501, "Parámetros PORT no válidos").ConfigureAwait(false);
            return;
        }

        var ip = new IPAddress(new[] { v[0], v[1], v[2], v[3] });
        int port = (v[4] << 8) | v[5];
        await SetActiveEndpointAsync(ip, port, "PORT").ConfigureAwait(false);
    }

    private async Task CmdEprtAsync(string arg)
    {
        if (_epsvAll)
        {
            await ReplyAsync(501, "EPRT no permitido tras EPSV ALL").ConfigureAwait(false);
            return;
        }

        if (arg.Length < 7)
        {
            await ReplyAsync(501, "Parámetros EPRT no válidos").ConfigureAwait(false);
            return;
        }

        char d = arg[0];
        string[] parts = arg.Split(d);
        if (parts.Length != 5 ||
            !int.TryParse(parts[1], NumberStyles.None, Inv, out int proto) ||
            !IPAddress.TryParse(parts[2], out IPAddress? ip) ||
            !int.TryParse(parts[3], NumberStyles.None, Inv, out int port) ||
            proto is not (1 or 2))
        {
            await ReplyAsync(501, "Parámetros EPRT no válidos").ConfigureAwait(false);
            return;
        }

        await SetActiveEndpointAsync(ip, port, "EPRT").ConfigureAwait(false);
    }

    private async Task SetActiveEndpointAsync(IPAddress ip, int port, string command)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        // Anti "FTP bounce": solo se conecta a la IP del propio cliente y a puertos no privilegiados.
        if (!ip.Equals(_remoteIp) || port is < 1024 or > 65535)
        {
            Logger.Warning($"SEGURIDAD {_remote}: {command} hacia {ip}:{port} rechazado");
            await ReplyAsync(501, $"{command} rechazado").ConfigureAwait(false);
            return;
        }

        CloseDataChannel();
        _activeEndpoint = new IPEndPoint(ip, port);
        await ReplyAsync(200, $"{command} aceptado").ConfigureAwait(false);
    }

    private bool HasDataChannel => _passiveListener != null || _activeEndpoint != null;

    private async Task<TcpClient?> OpenDataConnectionAsync()
    {
        if (_passiveListener != null)
        {
            TcpListener listener = _passiveListener;
            _passiveListener = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(_options.DataConnectionTimeout);
            try
            {
                while (true)
                {
                    TcpClient candidate = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                    IPAddress? from = (candidate.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    if (from != null && from.IsIPv4MappedToIPv6)
                        from = from.MapToIPv4();

                    // Anti robo de conexión pasiva: solo la IP del cliente puede usar el puerto.
                    if (from != null && from.Equals(_remoteIp))
                    {
                        candidate.NoDelay = true;
                        return candidate;
                    }

                    Logger.Warning($"SEGURIDAD {_remote}: conexión de datos desde otra IP ({from}) rechazada");
                    candidate.Dispose();
                }
            }
            catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                listener.Stop();
            }
        }

        if (_activeEndpoint != null)
        {
            IPEndPoint endpoint = _activeEndpoint;
            _activeEndpoint = null;
            var client = new TcpClient(endpoint.AddressFamily);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                client.NoDelay = true;
                return client;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
                client.Dispose();
                return null;
            }
        }

        return null;
    }

    /// <summary>Abre la conexión de datos (tras responder 150) y ejecuta la transferencia.</summary>
    private async Task<bool> RunTransferAsync(string startMessage, Func<NetworkStream, Task> transfer)
    {
        await ReplyAsync(150, startMessage).ConfigureAwait(false);
        using TcpClient? data = await OpenDataConnectionAsync().ConfigureAwait(false);
        if (data == null)
        {
            await ReplyAsync(425, "No se pudo abrir la conexión de datos").ConfigureAwait(false);
            return false;
        }

        NetworkStream stream = data.GetStream();
        try
        {
            await transfer(stream).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException { InnerException: SocketException } or SocketException)
        {
            throw new DataChannelException(ex);
        }
        finally
        {
            try { data.Client.Shutdown(SocketShutdown.Both); } catch { }
        }
        return true;
    }

    private void CloseDataChannel()
    {
        TcpListener? listener = _passiveListener;
        _passiveListener = null;
        _activeEndpoint = null;
        if (listener != null)
        {
            try { listener.Stop(); } catch { }
        }
    }

    // ------------------------------------------------------------------ listados

    private enum ListingFormat { Unix, NamesOnly, Machine }

    private async Task SendListingAsync(string arg, ListingFormat format)
    {
        string target = format == ListingFormat.Machine ? arg : StripListOptions(arg);
        string path = _fs!.Resolve(_cwd, target, out _);

        bool isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            throw new FileNotFoundException();
        if (format == ListingFormat.Machine && !isDirectory)
        {
            await ReplyAsync(501, "No es un directorio").ConfigureAwait(false);
            return;
        }
        if (!HasDataChannel)
        {
            await ReplyAsync(425, "Use PASV o PORT primero").ConfigureAwait(false);
            return;
        }

        int count = 0;
        bool ok = await RunTransferAsync("Enviando listado", async stream =>
        {
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            await using var writer = new StreamWriter(stream, Utf8, 16 * 1024, leaveOpen: true) { NewLine = "\r\n" };

            IEnumerable<FileSystemInfo> entries = isDirectory
                ? new DirectoryInfo(path).EnumerateFileSystemInfos("*", ListOptions)
                : new FileSystemInfo[] { new FileInfo(path) };

            DateTime now = DateTime.Now;
            foreach (FileSystemInfo entry in entries)
            {
                string? line;
                try
                {
                    line = format switch
                    {
                        ListingFormat.NamesOnly => entry.Name,
                        ListingFormat.Machine => FormatMachine(entry),
                        _ => FormatUnix(entry, now)
                    };
                }
                catch (IOException) { line = null; }          // borrado mientras se listaba
                catch (UnauthorizedAccessException) { line = null; }
                if (line == null)
                    continue;

                watchdog.CancelAfter(_options.TransferStallTimeout);
                await writer.WriteLineAsync(line.AsMemory(), watchdog.Token).ConfigureAwait(false);
                count++;
            }
            watchdog.CancelAfter(_options.TransferStallTimeout);
            await writer.FlushAsync(watchdog.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (ok)
            await ReplyAsync(226, $"Listado enviado ({count.ToString(Inv)} elementos)").ConfigureAwait(false);
    }

    private async Task CmdMlstAsync(string arg)
    {
        string path = _fs!.Resolve(_cwd, arg, out string vpath);
        FileSystemInfo info;
        if (Directory.Exists(path)) info = new DirectoryInfo(path);
        else if (File.Exists(path)) info = new FileInfo(path);
        else throw new FileNotFoundException();

        string facts = FormatMachine(info);
        int sep = facts.IndexOf("; ", StringComparison.Ordinal);
        string line = facts[..(sep + 2)] + vpath;
        await ReplyMultiAsync(250, "Información", new[] { line }, "Fin").ConfigureAwait(false);
    }

    internal static string FormatUnix(FileSystemInfo entry, DateTime now)
    {
        bool dir = entry is DirectoryInfo;
        long size = dir ? 0 : ((FileInfo)entry).Length;
        DateTime t = entry.LastWriteTime;
        // Formato "ls -l" estándar con meses en inglés: en 2.x se usaban meses en español
        // ("oct.") que algunos clientes y escáneres no sabían interpretar.
        string date = Math.Abs((now - t).TotalDays) > 180
            ? t.ToString("MMM dd  yyyy", Inv)
            : t.ToString("MMM dd HH:mm", Inv);
        return string.Create(Inv, $"{(dir ? "drwxr-xr-x" : "-rw-r--r--")} 1 ftp ftp {size,13} {date} {entry.Name}");
    }

    private static string FormatMachine(FileSystemInfo entry)
    {
        string modify = entry.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", Inv);
        return entry is FileInfo file
            ? string.Create(Inv, $"type=file;size={file.Length};modify={modify}; {entry.Name}")
            : string.Create(Inv, $"type=dir;modify={modify}; {entry.Name}");
    }

    private static string StripListOptions(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return string.Empty;
        // "LIST -la carpeta" -> "carpeta". Solo se quitan opciones al principio.
        string rest = arg;
        while (rest.StartsWith('-'))
        {
            int space = rest.IndexOf(' ');
            if (space < 0) return string.Empty;
            rest = rest[(space + 1)..].TrimStart();
        }
        return rest;
    }

    // ------------------------------------------------------------------ transferencias

    private async Task ReceiveFileAsync(string arg, bool append)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await ReplyAsync(501, "Falta el nombre del archivo").ConfigureAwait(false);
            return;
        }

        FtpUser user = _user!;
        string path = _fs!.Resolve(_cwd, arg, out string vpath);
        if (FtpFileSystem.IsRoot(vpath) || Directory.Exists(path))
        {
            await ReplyAsync(553, "El destino es una carpeta").ConfigureAwait(false);
            return;
        }

        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            if (!user.CanCreateDirectories)
            {
                Logger.Warning($"PERMISO DENEGADO {_remote} usuario={user.Username} acción=crear carpeta para subida");
                await ReplyAsync(550, "Permiso denegado: crear carpetas").ConfigureAwait(false);
                return;
            }
            Directory.CreateDirectory(parent);
        }

        long offset = append ? 0 : _restartOffset;
        _restartOffset = 0;
        if (offset > 0 && (!File.Exists(path) || new FileInfo(path).Length < offset))
        {
            await ReplyAsync(554, "Posición de reinicio no válida").ConfigureAwait(false);
            return;
        }

        if (!HasDataChannel)
        {
            await ReplyAsync(425, "Use PASV o PORT primero").ConfigureAwait(false);
            return;
        }

        FileMode mode = append ? FileMode.Append : offset > 0 ? FileMode.Open : FileMode.Create;
        long received = 0;
        bool completed;
        await using (var file = new FileStream(path, mode, FileAccess.Write, FileShare.Read, 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (offset > 0)
            {
                // REST + STOR: reanudar subida. En 2.x se ignoraba y el archivo quedaba corrupto.
                file.SetLength(offset);
                file.Seek(offset, SeekOrigin.Begin);
            }

            try
            {
                completed = await RunTransferAsync("Preparado para recibir datos", async stream =>
                {
                    received = await StreamCopy.CopyAsync(stream, file, _options.TransferStallTimeout, _cts.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch
            {
                Logger.Warning($"SUBIDA INCOMPLETA {_remote} usuario={user.Username} archivo={path} bytes={received.ToString(Inv)}");
                throw;
            }
        }

        if (!completed)
            return;

        long size = new FileInfo(path).Length;
        Logger.Success($"SUBIDA OK {_remote} usuario={user.Username} archivo={path} bytes={size.ToString(Inv)}");
        _server.NotifyFileReceived(new FtpFileReceivedInfo(path, user.Username, size, DateTime.Now));
        await ReplyAsync(226, "Transferencia completada").ConfigureAwait(false);
    }

    private async Task SendFileAsync(string arg)
    {
        FtpUser user = _user!;
        string path = _fs!.Resolve(_cwd, arg, out _);
        if (Directory.Exists(path))
        {
            await ReplyAsync(550, "Es una carpeta").ConfigureAwait(false);
            return;
        }
        if (!File.Exists(path))
            throw new FileNotFoundException();

        long offset = _restartOffset;
        _restartOffset = 0;

        if (!HasDataChannel)
        {
            await ReplyAsync(425, "Use PASV o PORT primero").ConfigureAwait(false);
            return;
        }

        long sent = 0;
        bool completed;
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (offset > file.Length)
            {
                CloseDataChannel();
                await ReplyAsync(554, "Posición de reinicio no válida").ConfigureAwait(false);
                return;
            }
            if (offset > 0)
                file.Seek(offset, SeekOrigin.Begin);

            completed = await RunTransferAsync("Enviando archivo", async stream =>
            {
                sent = await StreamCopy.CopyAsync(file, stream, _options.TransferStallTimeout, _cts.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        if (!completed)
            return;

        Logger.Success($"DESCARGA OK {_remote} usuario={user.Username} archivo={path} bytes={sent.ToString(Inv)}");
        await ReplyAsync(226, "Transferencia completada").ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ respuestas

    private async Task ReplyAsync(int code, string text)
    {
        string line = code.ToString(Inv) + " " + text;
        if (Trace)
            Logger.Write(LogKind.ServerReply, $"{_remote} S> {line}");
        await WriteRawAsync(line + "\r\n").ConfigureAwait(false);
    }

    private async Task ReplyMultiAsync(int code, string first, IEnumerable<string> lines, string last)
    {
        var sb = new StringBuilder();
        string c = code.ToString(Inv);
        sb.Append(c).Append('-').Append(first).Append("\r\n");
        foreach (string l in lines)
            sb.Append(' ').Append(l).Append("\r\n");
        sb.Append(c).Append(' ').Append(last).Append("\r\n");
        string text = sb.ToString();

        if (Trace)
        {
            foreach (string l in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                Logger.Write(LogKind.ServerReply, $"{_remote} S> {l}");
        }
        await WriteRawAsync(text).ConfigureAwait(false);
    }

    private async Task WriteRawAsync(string text)
    {
        byte[] bytes = Utf8.GetBytes(text);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(_options.ReplyTimeout);
        try
        {
            await _stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            throw new ControlChannelException(ex);
        }
    }

    private async Task<bool> TryReplyAsync(int code, string text)
    {
        try
        {
            await ReplyAsync(code, text).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ errores

    private sealed class ControlChannelException : Exception
    {
        public ControlChannelException(Exception inner) : base("Canal de control cerrado", inner) { }
    }

    private sealed class DataChannelException : Exception
    {
        public DataChannelException(Exception inner) : base("Conexión de datos interrumpida: " + inner.Message, inner) { }
    }

    private bool IsControlChannelFailure(Exception ex) =>
        ex is ControlChannelException ||
        (ex is OperationCanceledException && _cts.IsCancellationRequested);

    private (int Code, string Text) MapException(Exception ex, string command)
    {
        switch (ex)
        {
            case FtpPathException p:
                Logger.Warning($"RUTA RECHAZADA {_remote} {command}: {p.Message}");
                return (553, "Nombre no permitido: " + p.Message);
            case DataChannelException:
                return (426, "Conexión de datos cerrada; transferencia abortada");
            case TimeoutException:
                return (426, "Transferencia abortada por inactividad");
            case FileNotFoundException:
            case DirectoryNotFoundException:
                return (550, "No existe el archivo o directorio");
            case UnauthorizedAccessException:
                return (550, "Acceso denegado por Windows");
            case PathTooLongException:
                return (553, "Ruta demasiado larga");
            case IOException io:
                int win32 = io.HResult & 0xFFFF;
                return win32 switch
                {
                    112 or 39 => (452, "Espacio en disco insuficiente"),
                    32 or 33 => (450, "El archivo está en uso por otro programa"),
                    145 => (550, "La carpeta no está vacía"),
                    80 or 183 => (553, "Ya existe"),
                    _ => (451, "Error de lectura/escritura local")
                };
            default:
                return (451, "Error local procesando la operación");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CloseDataChannel();
        _closeOnCancel.Dispose();
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
