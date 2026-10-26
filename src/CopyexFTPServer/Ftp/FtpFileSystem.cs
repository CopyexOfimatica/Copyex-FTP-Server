// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

namespace CopyexFTPServer.Ftp;

/// <summary>Ruta FTP rechazada por seguridad (fuera de la raíz, nombre reservado, etc.).</summary>
internal sealed class FtpPathException : Exception
{
    public FtpPathException(string message) : base(message) { }
}

/// <summary>
/// Traduce rutas FTP virtuales ("/carpeta/archivo.pdf") a rutas físicas dentro de la raíz
/// del usuario, garantizando que nunca se sale de ella.
///
/// Mejoras respecto a 2.x:
/// - La ruta se normaliza primero en espacio virtual ("..", "." y barras) y luego se comprueba
///   de nuevo la ruta física final (doble control).
/// - Se rechazan flujos alternativos NTFS ("a.pdf:oculto"), letras de unidad ("C:"),
///   nombres de dispositivo de Windows (CON, NUL, COM1...), caracteres de control y comodines.
/// </summary>
internal sealed class FtpFileSystem
{
    public const int MaxVirtualPathLength = 1024;
    public const int MaxSegmentLength = 255;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3"
    };

    private readonly string _rootWithSeparator;

    public FtpFileSystem(string root)
    {
        string full = Path.GetFullPath(root);
        Root = Path.TrimEndingDirectorySeparator(full);
        _rootWithSeparator = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
    }

    /// <summary>Ruta física de la raíz (sin separador final, salvo raíz de unidad "D:\").</summary>
    public string Root { get; }

    public static bool IsRoot(string virtualPath) => virtualPath == "/";

    /// <summary>Combina el directorio actual con la ruta recibida y devuelve la ruta virtual absoluta normalizada.</summary>
    public static string CombineVirtual(string currentDirectory, string? input)
    {
        string s = string.IsNullOrEmpty(input) ? string.Empty : input.Replace('\\', '/');
        if (s.Length > MaxVirtualPathLength)
            throw new FtpPathException("Ruta demasiado larga");

        var segments = new List<string>();
        if (!s.StartsWith('/'))
        {
            foreach (string seg in currentDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
                segments.Add(seg);
        }

        foreach (string seg in s.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
                continue;
            if (seg == "..")
            {
                // Igual que en Unix: ".." en la raíz se queda en la raíz.
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }
            ValidateSegment(seg);
            segments.Add(seg);
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>Convierte una ruta virtual normalizada en ruta física dentro de la raíz.</summary>
    public string ToPhysical(string virtualPath)
    {
        if (IsRoot(virtualPath))
            return Root;

        string relative = virtualPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(_rootWithSeparator, relative));

        if (!IsInsideRoot(full))
            throw new FtpPathException("Ruta fuera de la carpeta raíz");
        return full;
    }

    public string Resolve(string currentDirectory, string? input, out string virtualPath)
    {
        virtualPath = CombineVirtual(currentDirectory, input);
        return ToPhysical(virtualPath);
    }

    public bool IsInsideRoot(string fullPath)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        return trimmed.Equals(Root, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidateSegment(string segment)
    {
        if (segment.Length > MaxSegmentLength)
            throw new FtpPathException("Nombre demasiado largo");

        foreach (char c in segment)
        {
            if (char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                throw new FtpPathException("El nombre contiene caracteres no permitidos");
        }

        // Windows elimina puntos y espacios finales: "a.pdf." y "a.pdf" serían el mismo archivo.
        char last = segment[^1];
        if (last is '.' or ' ')
            throw new FtpPathException("El nombre no puede terminar en punto o espacio");

        int dot = segment.IndexOf('.');
        string baseName = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
        if (ReservedNames.Contains(baseName))
            throw new FtpPathException("Nombre reservado por Windows");
    }
}
