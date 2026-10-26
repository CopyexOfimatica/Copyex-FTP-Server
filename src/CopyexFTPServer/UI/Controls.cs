// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CopyexFTPServer.UI;

internal enum ButtonKind
{
    Primary,
    Secondary,
    Danger,
    Success,
    Ghost,
    Warning
}

/// <summary>Botón plano con esquinas redondeadas e icono opcional. Mantiene teclado, foco y DialogResult.</summary>
internal sealed class ModernButton : Button
{
    private bool _hover;
    private bool _pressed;
    private ButtonKind _kind = ButtonKind.Secondary;
    private string? _glyph;

    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = Theme.Bold;
        Height = 36;
        Padding = new Padding(14, 0, 14, 0);
        Margin = new Padding(0, 0, 8, 0);
        UseVisualStyleBackColor = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Glyph
    {
        get => _glyph;
        set { _glyph = value; Invalidate(); }
    }

    public static ModernButton Create(string text, ButtonKind kind, string? glyph = null, int width = 0)
    {
        var b = new ModernButton { Text = text, Kind = kind, Glyph = glyph };
        if (width > 0)
            b.Width = width;
        else
        {
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }
        return b;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        int glyphWidth = string.IsNullOrEmpty(_glyph) ? 0 : TextRenderer.MeasureText(_glyph, Theme.IconSmall).Width + 2;
        int width = Padding.Horizontal + text.Width + glyphWidth;
        return new Size(Math.Max(width, 40), Math.Max(Height, 32));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) PerformLayout(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.BackOf(Parent));

        (Color back, Color fore, Color border) = GetColors();
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (GraphicsPath path = Theme.RoundedRect(rect, 6))
        {
            if (back.A > 0)
            {
                using var brush = new SolidBrush(back);
                g.FillPath(brush, path);
            }
            if (border.A > 0)
            {
                using var pen = new Pen(border);
                g.DrawPath(pen, path);
            }
        }

        Size textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        int glyphWidth = 0;
        Size glyphSize = Size.Empty;
        if (!string.IsNullOrEmpty(_glyph))
        {
            glyphSize = TextRenderer.MeasureText(g, _glyph, Theme.IconSmall, Size.Empty, TextFormatFlags.NoPadding);
            glyphWidth = glyphSize.Width + (string.IsNullOrEmpty(Text) ? 0 : 7);
        }

        int total = glyphWidth + textSize.Width;
        int x = (Width - total) / 2;
        if (!string.IsNullOrEmpty(_glyph))
        {
            var glyphRect = new Rectangle(x, (Height - glyphSize.Height) / 2 + 1, glyphSize.Width, glyphSize.Height);
            TextRenderer.DrawText(g, _glyph, Theme.IconSmall, glyphRect, fore, TextFormatFlags.NoPadding);
            x += glyphWidth;
        }
        var textRect = new Rectangle(x, 0, textSize.Width + 2, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        if (Focused && ShowFocusCues)
        {
            using var focusPen = new Pen(Theme.Mix(back.A > 0 ? back : Theme.Accent, Color.Black, 0.35f)) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focusPen, 3, 3, Width - 7, Height - 7);
        }
    }

    private (Color Back, Color Fore, Color Border) GetColors()
    {
        if (!Enabled)
        {
            return _kind == ButtonKind.Ghost
                ? (Theme.BackOf(Parent), Color.FromArgb(170, 175, 182), Color.Transparent)
                : (Color.FromArgb(232, 234, 238), Color.FromArgb(160, 165, 172), Color.Transparent);
        }

        float t = _pressed ? 0.18f : _hover ? 0.09f : 0f;
        return _kind switch
        {
            ButtonKind.Primary => (_pressed ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent, Color.White, Color.Transparent),
            ButtonKind.Danger => (Theme.Mix(Theme.Danger, Color.Black, t), Color.White, Color.Transparent),
            ButtonKind.Success => (Theme.Mix(Theme.Success, Color.Black, t), Color.White, Color.Transparent),
            ButtonKind.Warning => (Theme.Mix(Theme.Orange, Color.Black, t), Color.White, Color.Transparent),
            // Nunca transparente: se pinta con el fondo real del contenedor (evita cuadros negros).
            ButtonKind.Ghost => (Theme.Mix(Theme.BackOf(Parent), Color.Black, t * 0.6f), Theme.Text, Color.Transparent),
            _ => (Theme.Mix(Theme.Surface, Color.Black, t * 0.5f), Theme.Text, Theme.Border)
        };
    }
}

/// <summary>Panel blanco con borde redondeado.</summary>
internal class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Surface;
        Padding = new Padding(18, 14, 18, 14);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using GraphicsPath path = Theme.RoundedRect(rect, 8);
        using var brush = new SolidBrush(BackColor);
        using var pen = new Pen(Theme.Border);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }
}

internal enum BadgeKind
{
    Neutral,
    Success,
    Danger,
    Warning,
    Info
}

/// <summary>Etiqueta tipo "píldora" con icono, para estados (OK, aviso, error).</summary>
internal sealed class Badge : Control
{
    private BadgeKind _kind;
    private string? _glyph;

    public Badge()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Font = Theme.Bold;
        Height = 28;
        Margin = new Padding(0, 4, 10, 4);
        TabStop = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BadgeKind Kind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Glyph
    {
        get => _glyph;
        set { _glyph = value; UpdateWidth(); Invalidate(); }
    }

    public void Set(BadgeKind kind, string text, string? glyph)
    {
        _kind = kind;
        _glyph = glyph;
        Text = text;
        UpdateWidth();
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        UpdateWidth();
        Invalidate();
    }

    private void UpdateWidth()
    {
        Size text = TextRenderer.MeasureText(Text, Font);
        int glyph = string.IsNullOrEmpty(_glyph) ? 0 : TextRenderer.MeasureText(_glyph, Theme.IconSmall).Width;
        Width = text.Width + glyph + 22;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.BackOf(Parent));

        (Color back, Color fore) = _kind switch
        {
            BadgeKind.Success => (Theme.SuccessSoft, Theme.Success),
            BadgeKind.Danger => (Theme.DangerSoft, Theme.Danger),
            BadgeKind.Warning => (Theme.WarningSoft, Theme.Warning),
            BadgeKind.Info => (Theme.InfoSoft, Theme.Info),
            _ => (Color.FromArgb(236, 238, 242), Theme.TextMuted)
        };

        using (GraphicsPath path = Theme.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
        using (var brush = new SolidBrush(back))
            g.FillPath(brush, path);

        int x = 10;
        if (!string.IsNullOrEmpty(_glyph))
        {
            Size gs = TextRenderer.MeasureText(g, _glyph, Theme.IconSmall, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _glyph, Theme.IconSmall, new Rectangle(x, (Height - gs.Height) / 2 + 1, gs.Width, gs.Height), fore, TextFormatFlags.NoPadding);
            x += gs.Width + 5;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

/// <summary>Elemento de navegación de la barra lateral.</summary>
internal sealed class NavItem : Control
{
    private bool _hover;
    private bool _selected;

    public NavItem(string text, string glyph)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Text = text;
        Glyph = glyph;
        Font = Theme.Bold;
        Height = 44;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTab;
        AccessibleName = text;
    }

    public string Glyph { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Sidebar);

        var rect = new RectangleF(10, 2, Width - 20, Height - 4);
        if (_selected || _hover)
        {
            using GraphicsPath path = Theme.RoundedRect(rect, 7);
            using var brush = new SolidBrush(_selected ? Theme.SidebarActive : Theme.SidebarHover);
            g.FillPath(brush, path);
        }
        if (_selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            g.FillRectangle(accent, 10, 12, 3, Height - 24);
        }

        Color fore = _selected ? Color.White : Theme.SidebarText;
        Color iconColor = _selected ? Theme.Accent : Theme.SidebarMuted;
        TextRenderer.DrawText(g, Glyph, Theme.IconMedium, new Rectangle(24, 0, 28, Height), iconColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(58, 0, Width - 62, Height), fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, 12, 4, Width - 25, Height - 9);
        }
    }
}

/// <summary>Círculo de estado (verde/rojo) con halo.</summary>
internal sealed class StatusDot : Control
{
    private Color _color = Theme.Danger;

    public StatusDot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Size = new Size(22, 22);
        TabStop = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DotColor
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.BackOf(Parent));
        float size = Math.Min(Width, Height) - 1;
        using (var halo = new SolidBrush(Color.FromArgb(50, _color)))
            g.FillEllipse(halo, 0, 0, size, size);
        float inner = size * 0.55f;
        float offset = (size - inner) / 2;
        using var brush = new SolidBrush(_color);
        g.FillEllipse(brush, offset, offset, inner, inner);
    }
}

/// <summary>Aviso destacado con icono, texto y botón opcional.</summary>
internal sealed class Banner : Panel
{
    private readonly Label _icon = new();
    private readonly Label _text = new();
    private BadgeKind _kind = BadgeKind.Warning;

    public Banner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Height = 56;
        Padding = new Padding(14, 8, 12, 8);

        _icon.Font = Theme.IconMedium;
        _icon.Dock = DockStyle.Left;
        _icon.Width = 30;
        _icon.TextAlign = ContentAlignment.MiddleLeft;
        _icon.BackColor = Color.Transparent;

        _text.Dock = DockStyle.Fill;
        _text.TextAlign = ContentAlignment.MiddleLeft;
        _text.Font = Theme.Base;
        _text.BackColor = Color.Transparent;

        Action = ModernButton.Create("", ButtonKind.Secondary);
        Action.Dock = DockStyle.Right;
        Action.Visible = false;

        Controls.Add(_text);
        Controls.Add(Action);
        Controls.Add(_icon);
        ApplyKind();
    }

    public ModernButton Action { get; }

    public void Set(BadgeKind kind, string glyph, string text, string? actionText = null)
    {
        _kind = kind;
        _icon.Text = glyph;
        _text.Text = text;
        Action.Visible = !string.IsNullOrEmpty(actionText);
        if (actionText != null)
            Action.Text = actionText;
        ApplyKind();
    }

    private void ApplyKind()
    {
        (Color back, Color fore) = _kind switch
        {
            BadgeKind.Danger => (Theme.DangerSoft, Theme.Danger),
            BadgeKind.Success => (Theme.SuccessSoft, Theme.Success),
            BadgeKind.Info => (Theme.InfoSoft, Theme.Info),
            _ => (Theme.WarningSoft, Theme.Warning)
        };
        BackColor = back;
        _icon.ForeColor = fore;
        _text.ForeColor = Theme.Text;
        Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8);
        using var brush = new SolidBrush(BackColor);
        g.FillPath(brush, path);
    }
}
