// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Buffers.Binary;
using System.Globalization;
using CopyexFTPServer.Core;
using CopyexFTPServer.Ftp;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.Sftp;

/// <summary>
/// Subsistema SFTP versión 3 (draft-ietf-secsh-filexfer-02), la que usan OpenSSH, WinSCP y los
/// escáneres. Reutiliza la protección de rutas y los permisos del servidor FTP.
/// </summary>
internal sealed class SftpHandler : IDisposable
{
    private const int MaxRequestLength = 256 * 1024 + 1024;
    private const int MaxReadLength = 64 * 1024;
    private const int MaxHandles = 32;
    private const int DirBatch = 100;

    // Tipos de paquete
    private const byte Init = 1, Version = 2, Open = 3, Close = 4, Read = 5, Write = 6, Lstat = 7, Fstat = 8,
        Setstat = 9, Fsetstat = 10, Opendir = 11, Readdir = 12, Remove = 13, Mkdir = 14, Rmdir = 15,
        Realpath = 16, Stat = 17, Rename = 18, Readlink = 19, Symlink = 20;
    private const byte Status = 101, Handle = 102, Data = 103, Name = 104, Attrs = 105;

    // Códigos de estado
    private const uint Ok = 0, Eof = 1, NoSuchFile = 2, PermissionDenied = 3, Failure = 4, BadMessage = 5, OpUnsupported = 8;

    // Atributos
    private const uint AttrSize = 0x1, AttrUidGid = 0x2, AttrPermissions = 0x4, AttrAcModTime = 0x8, AttrExtended = 0x80000000;

    private readonly SshSession _session;
    private readonly SshChannel _channel;
    private readonly Dictionary<string, HandleState> _handles = new(StringComparer.Ordinal);
    private byte[] _input = new byte[64 * 1024];
    private int _inputLength;
    private uint _nextHandle;
    private bool _disposed;

    public SftpHandler(SshSession session, SshChannel channel)
    {
        _session = session;
        _channel = channel;
    }

    public bool HasBufferedInput => _inputLength > 0;

    /// <summary>Bytes recibidos aún sin procesar (incluye peticiones a medio llegar).</summary>
    public int BufferedBytes => _inputLength;

    private FtpUser User => _session.User!;
    private FtpFileSystem Fs => _session.FileSystem!;
    private string Remote => _session.Remote;

    /// <summary>Añade datos recibidos y procesa las peticiones completas. False = cerrar la sesión.</summary>
    public async Task<bool> FeedAsync(ReadOnlyMemory<byte> data)
    {
        if (_inputLength + data.Length > _input.Length)
        {
            int size = Math.Max(_input.Length * 2, _inputLength + data.Length);
            if (size > 8 * 1024 * 1024)
                throw new SshProtocolException("Demasiados datos SFTP pendientes");
            Array.Resize(ref _input, size);
        }
        data.Span.CopyTo(_input.AsSpan(_inputLength));
        _inputLength += data.Length;
        return await ProcessAsync().ConfigureAwait(false);
    }

    public async Task<bool> ProcessAsync()
    {
        int offset = 0;
        try
        {
            while (_inputLength - offset >= 4 && _session.CanSend(_channel))
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset));
                if (length < 1 || length > MaxRequestLength)
                    throw new SshProtocolException("Petición SFTP de tamaño no válido");
                if (_inputLength - offset - 4 < length)
                    break;

                byte[] request = _input.AsSpan(offset + 4, (int)length).ToArray();
                offset += 4 + (int)length;
                if (!await HandleAsync(request).ConfigureAwait(false))
                    return false;
            }
        }
        finally
        {
            if (offset > 0)
            {
                Buffer.BlockCopy(_input, offset, _input, 0, _inputLength - offset);
                _inputLength -= offset;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ peticiones

    private async Task<bool> HandleAsync(byte[] request)
    {
        var r = new SshReader(request);
        byte type = r.Byte();

        if (type == Init)
        {
            await SendAsync(new SshWriter(16).Byte(Version).UInt32(3)).ConfigureAwait(false);
            return true;
        }

        uint id = r.UInt32();
        if (!_session.RefreshUser())
            return false;

        try
        {
            switch (type)
            {
                case Realpath: await DoRealpathAsync(id, r).ConfigureAwait(false); break;
                case Stat:
                case Lstat: await DoStatAsync(id, r).ConfigureAwait(false); break;
                case Fstat: await DoFstatAsync(id, r).ConfigureAwait(false); break;
                case Open: await DoOpenAsync(id, r).ConfigureAwait(false); break;
                case Read: await DoReadAsync(id, r).ConfigureAwait(false); break;
                case Write: await DoWriteAsync(id, r).ConfigureAwait(false); break;
                case Close: await DoCloseAsync(id, r).ConfigureAwait(false); break;
                case Opendir: await DoOpendirAsync(id, r).ConfigureAwait(false); break;
                case Readdir: await DoReaddirAsync(id, r).ConfigureAwait(false); break;
                case Remove: await DoRemoveAsync(id, r).ConfigureAwait(false); break;
                case Mkdir: await DoMkdirAsync(id, r).ConfigureAwait(false); break;
                case Rmdir: await DoRmdirAsync(id, r).ConfigureAwait(false); break;
                case Rename: await DoRenameAsync(id, r).ConfigureAwait(false); break;
                case Setstat: await DoSetstatAsync(id, r, byHandle: false).ConfigureAwait(false); break;
                case Fsetstat: await DoSetstatAsync(id, r, byHandle: true).ConfigureAwait(false); break;
                case Readlink:
                case Symlink:
                default:
                    await StatusAsync(id, OpUnsupported, "Operación no soportada").ConfigureAwait(false);
                    break;
            }
        }
        catch (SshProtocolException)
        {
            await StatusAsync(id, BadMessage, "Petición mal formada").ConfigureAwait(false);
        }
        catch (DeniedException ex)
        {
            Logger.Warning($"SFTP PERMISO DENEGADO {Remote} usuario={User.Username} acción={ex.Message}");
            await StatusAsync(id, PermissionDenied, "Permiso denegado: " + ex.Message).ConfigureAwait(false);
        }
        catch (FtpPathException ex)
        {
            Logger.Warning($"SFTP RUTA RECHAZADA {Remote}: {ex.Message}");
            await StatusAsync(id, PermissionDenied, "Nombre no permitido: " + ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            await StatusAsync(id, NoSuchFile, "No existe el archivo o la carpeta").ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            await StatusAsync(id, PermissionDenied, "Acceso denegado por Windows").ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            int win32 = ex.HResult & 0xFFFF;
            string message = win32 switch
            {
                112 or 39 => "Espacio en disco insuficiente",
                32 or 33 => "El archivo está en uso por otro programa",
                145 => "La carpeta no está vacía",
                80 or 183 => "Ya existe",
                _ => "Error de lectura/escritura"
            };
            if (win32 is not (145 or 80 or 183))
                Logger.Error($"SFTP ERROR {Remote}: {ex.Message}");
            await StatusAsync(id, Failure, message).ConfigureAwait(false);
        }
        return true;
    }

    private async Task DoRealpathAsync(uint id, SshReader r)
    {
        string path = r.String(4096);
        string vpath = FtpFileSystem.CombineVirtual("/", path);
        var w = Begin(Name, id).UInt32(1).String(vpath).String(vpath).UInt32(0);
        await SendAsync(w).ConfigureAwait(false);
    }

    private async Task DoStatAsync(uint id, SshReader r)
    {
        Require(User.CanBrowse || User.CanDownload || User.CanUpload, "consultar");
        string path = Resolve(r, out _);
        FileSystemInfo info = Info(path) ?? throw new FileNotFoundException();
        var w = Begin(Attrs, id);
        WriteAttrs(w, info);
        await SendAsync(w).ConfigureAwait(false);
    }

    private async Task DoFstatAsync(uint id, SshReader r)
    {
        HandleState h = GetHandle(r);
        FileSystemInfo info = Info(h.Path) ?? throw new FileNotFoundException();
        var w = Begin(Attrs, id);
        WriteAttrs(w, info);
        await SendAsync(w).ConfigureAwait(false);
    }

    private async Task DoOpenAsync(uint id, SshReader r)
    {
        string path = Resolve(r, out string vpath);
        uint flags = r.UInt32();
        SkipAttrs(r);

        bool read = (flags & 0x01) != 0;
        bool write = (flags & 0x02) != 0;
        bool append = (flags & 0x04) != 0;
        bool create = (flags & 0x08) != 0;
        bool truncate = (flags & 0x10) != 0;
        bool exclusive = (flags & 0x20) != 0;

        if (!read && !write)
        {
            await StatusAsync(id, Failure, "Modo de apertura no válido").ConfigureAwait(false);
            return;
        }
        if (read) Require(User.CanDownload, "descargar/leer");
        if (write || create || truncate) Require(User.CanUpload, "subir/escribir");

        if (FtpFileSystem.IsRoot(vpath) || Directory.Exists(path))
        {
            await StatusAsync(id, Failure, "Es una carpeta").ConfigureAwait(false);
            return;
        }
        if (_handles.Count >= MaxHandles)
        {
            await StatusAsync(id, Failure, "Demasiados archivos abiertos").ConfigureAwait(false);
            return;
        }

        FileMode mode = create
            ? (exclusive ? FileMode.CreateNew : truncate ? FileMode.Create : FileMode.OpenOrCreate)
            : (truncate ? FileMode.Truncate : FileMode.Open);
        FileAccess access = read && write ? FileAccess.ReadWrite : write ? FileAccess.Write : FileAccess.Read;
        FileShare share = write ? FileShare.Read : FileShare.ReadWrite;
        if (mode == FileMode.Truncate && !write)
            access = FileAccess.ReadWrite;

        var stream = new FileStream(path, mode, access, share, 64 * 1024, FileOptions.SequentialScan);
        string handle = NewHandle(new HandleState { Path = path, Stream = stream, Write = write, Append = append });

        if (_session.Trace)
            Logger.Write(LogKind.ClientCommand, $"SFTP {Remote} C> OPEN {vpath} ({(write ? "escritura" : "lectura")})");
        await SendAsync(Begin(Handle, id).String(handle)).ConfigureAwait(false);
    }

    private async Task DoReadAsync(uint id, SshReader r)
    {
        HandleState h = GetHandle(r);
        ulong offset = r.UInt64();
        int length = (int)Math.Min(r.UInt32(), (uint)MaxReadLength);
        if (h.Stream == null || !h.Stream.CanRead)
        {
            await StatusAsync(id, PermissionDenied, "Archivo no abierto para lectura").ConfigureAwait(false);
            return;
        }
        if (offset >= (ulong)h.Stream.Length)
        {
            await StatusAsync(id, Eof, "Fin de archivo").ConfigureAwait(false);
            return;
        }

        h.Stream.Seek((long)offset, SeekOrigin.Begin);
        byte[] buffer = new byte[length];
        int total = 0;
        while (total < length)
        {
            int n = h.Stream.Read(buffer, total, length - total);
            if (n == 0) break;
            total += n;
        }
        h.BytesRead += total;
        await SendAsync(Begin(Data, id, total + 16).String(buffer.AsSpan(0, total))).ConfigureAwait(false);
    }

    private async Task DoWriteAsync(uint id, SshReader r)
    {
        HandleState h = GetHandle(r);
        ulong offset = r.UInt64();
        ReadOnlyMemory<byte> data = r.StringBytes(MaxRequestLength);
        if (h.Stream == null || !h.Stream.CanWrite)
        {
            await StatusAsync(id, PermissionDenied, "Archivo no abierto para escritura").ConfigureAwait(false);
            return;
        }
        if (offset > long.MaxValue / 2)
            throw new SshProtocolException("Posición no válida");

        h.Stream.Seek(h.Append ? 0 : (long)offset, h.Append ? SeekOrigin.End : SeekOrigin.Begin);
        h.Stream.Write(data.Span);
        h.BytesWritten += data.Length;
        await StatusAsync(id, Ok, "OK").ConfigureAwait(false);
    }

    private async Task DoCloseAsync(uint id, SshReader r)
    {
        string handle = r.String(256);
        if (!_handles.Remove(handle, out HandleState? h))
        {
            await StatusAsync(id, Failure, "Identificador no válido").ConfigureAwait(false);
            return;
        }

        h.Dispose();
        if (h.Write && h.BytesWritten > 0)
        {
            long size = File.Exists(h.Path) ? new FileInfo(h.Path).Length : h.BytesWritten;
            Logger.Success($"SFTP SUBIDA OK {Remote} usuario={User.Username} archivo={h.Path} bytes={size.ToString(CultureInfo.InvariantCulture)}");
            _session.Server.NotifyFileReceived(new FtpFileReceivedInfo(h.Path, User.Username, size, DateTime.Now));
        }
        else if (h.BytesRead > 0)
        {
            Logger.Success($"SFTP DESCARGA OK {Remote} usuario={User.Username} archivo={h.Path} bytes={h.BytesRead.ToString(CultureInfo.InvariantCulture)}");
        }
        await StatusAsync(id, Ok, "OK").ConfigureAwait(false);
    }

    private async Task DoOpendirAsync(uint id, SshReader r)
    {
        Require(User.CanBrowse, "listar");
        string path = Resolve(r, out string vpath);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException();
        if (_handles.Count >= MaxHandles)
        {
            await StatusAsync(id, Failure, "Demasiados identificadores abiertos").ConfigureAwait(false);
            return;
        }

        var entries = new List<FileSystemInfo>();
        var dir = new DirectoryInfo(path);
        entries.Add(dir);
        entries.Add(FtpFileSystem.IsRoot(vpath) ? dir : dir.Parent ?? dir);
        entries.AddRange(dir.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        }));

        string handle = NewHandle(new HandleState { Path = path, Entries = entries });
        await SendAsync(Begin(Handle, id).String(handle)).ConfigureAwait(false);
    }

    private async Task DoReaddirAsync(uint id, SshReader r)
    {
        HandleState h = GetHandle(r);
        if (h.Entries == null)
        {
            await StatusAsync(id, Failure, "No es una carpeta").ConfigureAwait(false);
            return;
        }
        if (h.EntryIndex >= h.Entries.Count)
        {
            await StatusAsync(id, Eof, "Fin del listado").ConfigureAwait(false);
            return;
        }

        int count = Math.Min(DirBatch, h.Entries.Count - h.EntryIndex);
        var w = Begin(Name, id, 16 * 1024).UInt32((uint)count);
        DateTime now = DateTime.Now;
        for (int i = 0; i < count; i++)
        {
            int index = h.EntryIndex + i;
            FileSystemInfo e = h.Entries[index];
            string name = index == 0 ? "." : index == 1 ? ".." : e.Name;
            try
            {
                e.Refresh();
                string longName = FtpSession.FormatUnix(e, now);
                if (index < 2)
                    longName = longName[..^e.Name.Length] + name;
                w.String(name).String(longName);
                WriteAttrs(w, e);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Borrado mientras se listaba: se envía sin atributos.
                w.String(name).String(name).UInt32(0);
            }
        }
        h.EntryIndex += count;
        await SendAsync(w).ConfigureAwait(false);
    }

    private async Task DoRemoveAsync(uint id, SshReader r)
    {
        Require(User.CanDelete, "borrar");
        string path = Resolve(r, out string vpath);
        if (FtpFileSystem.IsRoot(vpath) || Directory.Exists(path))
        {
            await StatusAsync(id, Failure, "No es un archivo").ConfigureAwait(false);
            return;
        }
        if (!File.Exists(path))
            throw new FileNotFoundException();
        File.Delete(path);
        Logger.Success($"SFTP BORRADO OK {Remote} usuario={User.Username} archivo={path}");
        await StatusAsync(id, Ok, "Archivo eliminado").ConfigureAwait(false);
    }

    private async Task DoMkdirAsync(uint id, SshReader r)
    {
        Require(User.CanCreateDirectories, "crear carpetas");
        string path = Resolve(r, out string vpath);
        SkipAttrs(r);
        if (FtpFileSystem.IsRoot(vpath) || Directory.Exists(path) || File.Exists(path))
        {
            await StatusAsync(id, Failure, "Ya existe").ConfigureAwait(false);
            return;
        }
        string? parent = Path.GetDirectoryName(path);
        if (parent != null && !Directory.Exists(parent))
            throw new DirectoryNotFoundException();
        Directory.CreateDirectory(path);
        Logger.Success($"SFTP CARPETA CREADA {Remote} usuario={User.Username} carpeta={path}");
        await StatusAsync(id, Ok, "Carpeta creada").ConfigureAwait(false);
    }

    private async Task DoRmdirAsync(uint id, SshReader r)
    {
        Require(User.CanDelete, "borrar");
        string path = Resolve(r, out string vpath);
        if (FtpFileSystem.IsRoot(vpath))
        {
            await StatusAsync(id, Failure, "No se puede eliminar la carpeta raíz").ConfigureAwait(false);
            return;
        }
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException();
        Directory.Delete(path, recursive: false);
        Logger.Success($"SFTP CARPETA ELIMINADA {Remote} usuario={User.Username} carpeta={path}");
        await StatusAsync(id, Ok, "Carpeta eliminada").ConfigureAwait(false);
    }

    private async Task DoRenameAsync(uint id, SshReader r)
    {
        Require(User.CanRename, "renombrar");
        string from = Resolve(r, out string fromVirtual);
        string to = Resolve(r, out string toVirtual);
        if (FtpFileSystem.IsRoot(fromVirtual) || FtpFileSystem.IsRoot(toVirtual))
        {
            await StatusAsync(id, Failure, "No se puede renombrar la carpeta raíz").ConfigureAwait(false);
            return;
        }
        bool sameName = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        if (!sameName && (File.Exists(to) || Directory.Exists(to)))
        {
            await StatusAsync(id, Failure, "Ya existe un archivo o carpeta con ese nombre").ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(from))
        {
            if (toVirtual.StartsWith(fromVirtual + "/", StringComparison.OrdinalIgnoreCase))
            {
                await StatusAsync(id, Failure, "No se puede mover una carpeta dentro de sí misma").ConfigureAwait(false);
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
        Logger.Success($"SFTP RENOMBRADO OK {Remote} usuario={User.Username} de={from} a={to}");
        await StatusAsync(id, Ok, "Renombrado").ConfigureAwait(false);
    }

    /// <summary>Cambiar permisos/fechas no aplica en Windows: se acepta sin cambios (los escáneres lo piden a veces).</summary>
    private async Task DoSetstatAsync(uint id, SshReader r, bool byHandle)
    {
        string path = byHandle ? GetHandle(r).Path : Resolve(r, out _);
        if (Info(path) == null)
            throw new FileNotFoundException();
        await StatusAsync(id, Ok, "OK").ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ utilidades

    private string Resolve(SshReader r, out string vpath) => Fs.Resolve("/", r.String(4096), out vpath);

    private static FileSystemInfo? Info(string path)
    {
        if (Directory.Exists(path)) return new DirectoryInfo(path);
        if (File.Exists(path)) return new FileInfo(path);
        return null;
    }

    private static void Require(bool allowed, string action)
    {
        if (!allowed)
            throw new DeniedException(action);
    }

    private string NewHandle(HandleState state)
    {
        string handle = (++_nextHandle).ToString("x8", CultureInfo.InvariantCulture);
        _handles[handle] = state;
        return handle;
    }

    private HandleState GetHandle(SshReader r)
    {
        string handle = r.String(256);
        return _handles.TryGetValue(handle, out HandleState? h) ? h : throw new SshProtocolException("Identificador no válido");
    }

    private static void SkipAttrs(SshReader r)
    {
        uint flags = r.UInt32();
        if ((flags & AttrSize) != 0) r.UInt64();
        if ((flags & AttrUidGid) != 0) { r.UInt32(); r.UInt32(); }
        if ((flags & AttrPermissions) != 0) r.UInt32();
        if ((flags & AttrAcModTime) != 0) { r.UInt32(); r.UInt32(); }
        if ((flags & AttrExtended) != 0)
        {
            uint count = r.UInt32();
            if (count > 64) throw new SshProtocolException("Demasiados atributos extendidos");
            for (uint i = 0; i < count; i++) { r.StringBytes(4096); r.StringBytes(64 * 1024); }
        }
    }

    private static void WriteAttrs(SshWriter w, FileSystemInfo info)
    {
        bool dir = info is DirectoryInfo;
        w.UInt32(AttrSize | AttrPermissions | AttrAcModTime);
        w.UInt64(dir ? 0UL : (ulong)((FileInfo)info).Length);
        w.UInt32(dir ? 0x41EDu : 0x81A4u); // drwxr-xr-x / -rw-r--r--
        w.UInt32(ToUnix(info.LastAccessTimeUtc));
        w.UInt32(ToUnix(info.LastWriteTimeUtc));
    }

    private static uint ToUnix(DateTime utc)
    {
        long seconds = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return (uint)Math.Clamp(seconds, 0, uint.MaxValue);
    }

    private static SshWriter Begin(byte type, uint id, int capacity = 256) => new SshWriter(capacity).Byte(type).UInt32(id);

    private Task StatusAsync(uint id, uint code, string message) =>
        SendAsync(Begin(Status, id).UInt32(code).String(message).String("es"));

    private Task SendAsync(SshWriter body)
    {
        byte[] packet = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)body.Length);
        body.AsSpan().CopyTo(packet.AsSpan(4));
        return _session.SendChannelDataAsync(_channel, packet);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (HandleState h in _handles.Values)
        {
            if (h.Write && h.BytesWritten > 0)
                Logger.Warning($"SFTP SUBIDA INCOMPLETA {Remote} archivo={h.Path} (conexión cerrada sin terminar)");
            h.Dispose();
        }
        _handles.Clear();
    }

    private sealed class DeniedException : Exception
    {
        public DeniedException(string action) : base(action) { }
    }

    private sealed class HandleState : IDisposable
    {
        public string Path = string.Empty;
        public FileStream? Stream;
        public bool Write;
        public bool Append;
        public long BytesWritten;
        public long BytesRead;
        public List<FileSystemInfo>? Entries;
        public int EntryIndex;

        public void Dispose()
        {
            try { Stream?.Dispose(); } catch { }
            Stream = null;
        }
    }
}
