// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.Diagnostics;
using System.Runtime.InteropServices;
using CopyexFTPServer.Core;

namespace CopyexFTPServer.UI;

/// <summary>Icono y logo de la aplicación (embebidos en el EXE, cargados una sola vez).</summary>
internal static class AppResources
{
    private static readonly Lazy<Icon> AppIconLazy = new(() => LoadIcon(null));
    private static readonly Lazy<Icon> TrayIconLazy = new(() => LoadIcon(SystemInformation.SmallIconSize));
    private static readonly Lazy<Image?> LogoLazy = new(LoadLogo);

    public static Icon AppIcon => AppIconLazy.Value;
    public static Icon TrayIcon => TrayIconLazy.Value;
    public static Image? Logo => LogoLazy.Value;

    /// <summary>Logotipo corporativo Copyex ("Si copias, ¡copia bien!"), fondo transparente.</summary>
    public static Image? Brand => BrandLazy.Value;

    private static readonly Lazy<Image?> BrandLazy = new(() =>
    {
        try
        {
            using Stream? stream = typeof(AppResources).Assembly.GetManifestResourceStream("CopyexFTPServer.Brand.png");
            if (stream == null)
                return null;
            using Image original = Image.FromStream(stream);
            return new Bitmap(original);
        }
        catch
        {
            return null;
        }
    });

    private static Icon LoadIcon(Size? size)
    {
        try
        {
            using Stream? stream = typeof(AppResources).Assembly.GetManifestResourceStream("CopyexFTPServer.App.ico");
            if (stream != null)
                return size.HasValue ? new Icon(stream, size.Value) : new Icon(stream);
        }
        catch
        {
            // Se usa el icono genérico.
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    private static Image? LoadLogo()
    {
        try
        {
            using Stream? stream = typeof(AppResources).Assembly.GetManifestResourceStream("CopyexFTPServer.Logo.png");
            if (stream == null)
                return null;
            using var original = Image.FromStream(stream);
            // Se reduce una vez a 96 px: el PNG original pesa mucho para pintarlo escalado cada vez.
            var bitmap = new Bitmap(96, 96);
            using Graphics g = Graphics.FromImage(bitmap);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawImage(original, 0, 0, 96, 96);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}

internal static class Ui
{
    public const string Title = AppInfo.Name;

    public static Label MakeLabel(string text, Font? font = null, Color? color = null, bool autoSize = true)
    {
        return new Label
        {
            Text = text,
            Font = font ?? Theme.Base,
            ForeColor = color ?? Theme.Text,
            AutoSize = autoSize,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 0),
            UseMnemonic = false
        };
    }

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        Font = Theme.Base,
        ForeColor = Theme.Text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.Transparent,
        UseMnemonic = false
    };

    public static Label Hint(string text) => new()
    {
        Text = text,
        Font = Theme.Small,
        ForeColor = Theme.TextMuted,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.Transparent,
        UseMnemonic = false
    };

    /// <summary>Apila controles de arriba a abajo (Dock=Top) con separación.</summary>
    public static void StackTop(Control parent, int gap, params Control[] items)
    {
        parent.SuspendLayout();
        for (int i = items.Length - 1; i >= 0; i--)
        {
            Control item = items[i];
            item.Dock = DockStyle.Top;
            parent.Controls.Add(item);
            if (i > 0 && gap > 0)
            {
                var spacer = new Panel { Dock = DockStyle.Top, Height = gap, BackColor = Color.Transparent };
                parent.Controls.Add(spacer);
                item.Tag ??= spacer; // permite ocultar el separador junto al elemento
            }
        }
        parent.ResumeLayout(true);
    }

    /// <summary>Muestra/oculta un control apilado junto con su separador.</summary>
    public static void SetStackedVisible(Control item, bool visible)
    {
        item.Visible = visible;
        if (item.Tag is Panel spacer)
            spacer.Visible = visible;
    }

    public static TableLayoutPanel Grid(int columns, params float[] widths)
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = columns,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (int i = 0; i < columns; i++)
        {
            float w = i < widths.Length ? widths[i] : -1;
            grid.ColumnStyles.Add(w switch
            {
                < 0 => new ColumnStyle(SizeType.Percent, 100),
                0 => new ColumnStyle(SizeType.AutoSize),
                _ => new ColumnStyle(SizeType.Absolute, w)
            });
        }
        return grid;
    }

    public static void AddRow(TableLayoutPanel grid, float height, params Control?[] cells)
    {
        int row = grid.RowCount;
        grid.RowCount = row + 1;
        grid.RowStyles.Add(height > 0 ? new RowStyle(SizeType.Absolute, height) : new RowStyle(SizeType.AutoSize));
        for (int col = 0; col < cells.Length; col++)
        {
            if (cells[col] != null)
                grid.Controls.Add(cells[col]!, col, row);
        }
    }

    public static void Info(IWin32Window? owner, string text) =>
        MessageBox.Show(owner, text, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Warn(IWin32Window? owner, string text) =>
        MessageBox.Show(owner, text, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static void Error(IWin32Window? owner, string text) =>
        MessageBox.Show(owner, text, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static bool Confirm(IWin32Window? owner, string text, string? title = null, MessageBoxIcon icon = MessageBoxIcon.Question) =>
        MessageBox.Show(owner, text, title ?? Title, MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public static bool TryCopyToClipboard(string text)
    {
        // El portapapeles puede estar bloqueado por otro programa: se reintenta brevemente.
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            string full = Path.GetFullPath(folder);
            if (!Directory.Exists(full))
                return;
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                ArgumentList = { full },
                UseShellExecute = false
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error($"ERROR abriendo la carpeta {folder}: {ex.Message}");
        }
    }

    public static void RevealFile(string file)
    {
        try
        {
            string full = Path.GetFullPath(file);
            if (!File.Exists(full))
            {
                OpenFolder(Path.GetDirectoryName(full) ?? full);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = "/select,\"" + full + "\"",
                UseShellExecute = false
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error($"ERROR mostrando el archivo {file}: {ex.Message}");
        }
    }

    // Congela el repintado durante actualizaciones masivas (registro).
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WmSetRedraw = 0x000B;

    public static void SuspendDrawing(Control control)
    {
        if (control.IsHandleCreated)
            SendMessage(control.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
    }

    public static void ResumeDrawing(Control control)
    {
        if (!control.IsHandleCreated)
            return;
        SendMessage(control.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
        control.Invalidate(true);
    }
}
