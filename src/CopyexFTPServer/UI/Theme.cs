// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Drawing.Drawing2D;

namespace CopyexFTPServer.UI;

/// <summary>Colores, fuentes y recursos gráficos compartidos (se crean una sola vez).</summary>
internal static class Theme
{
    // Marca: rojo corporativo Copyex (#E30613)
    public static readonly Color Accent = Color.FromArgb(227, 6, 19);
    public static readonly Color AccentHover = Color.FromArgb(240, 40, 50);
    public static readonly Color AccentPressed = Color.FromArgb(190, 0, 12);
    public static readonly Color AccentSoft = Color.FromArgb(253, 228, 230);

    // Botón de reinicio
    public static readonly Color Orange = Color.FromArgb(235, 125, 20);

    // Barra lateral
    public static readonly Color Sidebar = Color.FromArgb(22, 23, 27);
    public static readonly Color SidebarHover = Color.FromArgb(34, 36, 42);
    public static readonly Color SidebarActive = Color.FromArgb(44, 46, 54);
    public static readonly Color SidebarText = Color.FromArgb(205, 208, 215);
    public static readonly Color SidebarMuted = Color.FromArgb(125, 130, 140);

    // Contenido
    public static readonly Color Background = Color.FromArgb(243, 244, 246);
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceAlt = Color.FromArgb(249, 250, 251);
    public static readonly Color Border = Color.FromArgb(224, 227, 232);
    public static readonly Color Text = Color.FromArgb(28, 32, 38);
    public static readonly Color TextMuted = Color.FromArgb(104, 112, 125);

    // Estados
    public static readonly Color Success = Color.FromArgb(21, 145, 66);
    public static readonly Color SuccessSoft = Color.FromArgb(220, 247, 229);
    public static readonly Color Danger = Color.FromArgb(205, 38, 38);
    public static readonly Color DangerSoft = Color.FromArgb(253, 228, 228);
    public static readonly Color Warning = Color.FromArgb(190, 105, 0);
    public static readonly Color WarningSoft = Color.FromArgb(255, 243, 205);
    public static readonly Color Info = Color.FromArgb(37, 99, 235);
    public static readonly Color InfoSoft = Color.FromArgb(222, 234, 255);

    // Registro (fondo oscuro tipo consola)
    public static readonly Color TraceBack = Color.FromArgb(24, 25, 30);
    public static readonly Color TraceText = Color.FromArgb(212, 215, 222);
    public static readonly Color TraceClient = Color.FromArgb(130, 175, 255);
    public static readonly Color TraceServer = Color.FromArgb(140, 145, 155);
    public static readonly Color TraceSuccess = Color.FromArgb(105, 212, 130);
    public static readonly Color TraceWarning = Color.FromArgb(255, 192, 90);
    public static readonly Color TraceError = Color.FromArgb(255, 115, 105);

    // Fuentes
    public static readonly Font Base = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Caption = new("Segoe UI Semibold", 8.25f);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 11.5f);
    public static readonly Font PageTitle = new("Segoe UI Semibold", 17f);
    public static readonly Font Hero = new("Segoe UI Semibold", 15f);
    public static readonly Font Value = new("Segoe UI Semibold", 15f);
    public static readonly Font Mono = CreateFont(new[] { "Cascadia Mono", "Consolas" }, 9.5f, FontStyle.Regular);

    /// <summary>Fuente de iconos de Windows (Fluent en Windows 11, MDL2 en Windows 10).</summary>
    public static readonly string IconFontName = FirstInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets") ?? "Segoe UI Symbol";
    public static readonly Font IconSmall = new(IconFontName, 10f);
    public static readonly Font IconMedium = new(IconFontName, 12f);
    public static readonly Font IconLarge = new(IconFontName, 20f);

    /// <summary>Color de fondo visible detrás de un control (salta los contenedores transparentes).</summary>
    public static Color BackOf(Control? control)
    {
        while (control != null)
        {
            if (control.BackColor.A == 255)
                return control.BackColor;
            control = control.Parent;
        }
        return Background;
    }

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string? FirstInstalled(params string[] names)
    {
        foreach (string name in names)
        {
            try
            {
                using var family = new FontFamily(name);
                return family.Name;
            }
            catch (ArgumentException)
            {
                // No instalada
            }
        }
        return null;
    }

    private static Font CreateFont(string[] names, float size, FontStyle style) =>
        new(FirstInstalled(names) ?? FontFamily.GenericMonospace.Name, size, style);
}

/// <summary>Códigos de iconos (comunes a Segoe Fluent Icons y Segoe MDL2 Assets).</summary>
internal static class Glyphs
{
    public const string Home = "";
    public const string People = "";
    public const string Settings = "";
    public const string Document = "";
    public const string Play = "";
    public const string Stop = "";
    public const string Refresh = "";
    public const string Save = "";
    public const string FolderOpen = "";
    public const string Copy = "";
    public const string Add = "";
    public const string Edit = "";
    public const string Delete = "";
    public const string Globe = "";
    public const string Warning = "";
    public const string Check = "";
    public const string Shield = "";
    public const string Info = "";
    public const string Lock = "";
    public const string Clear = "";
    public const string Network = "";
    public const string Download = "";
    public const string Undo = "";
    public const string View = "";
    public const string Search = "";
    public const string Shortcut = ""; // Link
}
