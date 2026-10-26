// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using CopyexFTPServer.Core;

namespace CopyexFTPServer.UI;

/// <summary>
/// Página "Registro" (Server Trace). Lee el buffer del Logger cada 300 ms solo mientras está
/// visible; oculta o en la bandeja no consume nada.
/// </summary>
internal sealed class LogPage : UserControl
{
    private const int MaxLines = 5000;
    private const int TrimLines = 1000;

    private readonly RichTextBox _text = new();
    private readonly ComboBox _filter = new();
    private readonly CheckBox _follow = new();
    private readonly ModernButton _btnClear = ModernButton.Create("Limpiar pantalla", ButtonKind.Secondary, Glyphs.Clear);
    private readonly Label _note = Ui.MakeLabel("Solo en memoria: se borra al cerrar la aplicación", Theme.Small, Theme.TextMuted);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 300 };
    private readonly List<LogEntry> _buffer = new(512);
    private long _lastSequence;
    private int _lineCount;

    public LogPage()
    {
        BackColor = Theme.Background;
        Font = Theme.Base;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            WrapContents = false,
            BackColor = Theme.Background,
            Padding = new Padding(0, 0, 0, 12)
        };

        Label filterLabel = Ui.MakeLabel("Mostrar:");
        filterLabel.Margin = new Padding(0, 9, 6, 0);
        _filter.DropDownStyle = ComboBoxStyle.DropDownList;
        _filter.Font = Theme.Base;
        _filter.Width = 200;
        _filter.Margin = new Padding(0, 5, 14, 0);
        _filter.Items.AddRange(new object[] { "Todo (con comandos FTP)", "Solo eventos", "Solo avisos y errores" });
        _filter.SelectedIndex = 0;

        _follow.Text = "Seguir al final";
        _follow.Checked = true;
        _follow.AutoSize = true;
        _follow.Margin = new Padding(0, 8, 16, 0);
        _follow.BackColor = Color.Transparent;

        _note.Margin = new Padding(14, 11, 0, 0);
        toolbar.Controls.AddRange(new Control[] { filterLabel, _filter, _follow, _btnClear, _note });

        var frame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.TraceBack, Padding = new Padding(12, 10, 4, 6) };
        _text.Dock = DockStyle.Fill;
        _text.ReadOnly = true;
        _text.BorderStyle = BorderStyle.None;
        _text.BackColor = Theme.TraceBack;
        _text.ForeColor = Theme.TraceText;
        _text.Font = Theme.Mono;
        _text.WordWrap = false;
        _text.HideSelection = false;
        _text.DetectUrls = false;
        _text.ScrollBars = RichTextBoxScrollBars.Both;
        frame.Controls.Add(_text);

        Controls.Add(frame);
        Controls.Add(toolbar);

        _timer.Tick += (_, _) => Poll();
        _filter.SelectedIndexChanged += (_, _) => Reload();
        _btnClear.Click += (_, _) => { _text.Clear(); _lineCount = 0; };
        _follow.CheckedChanged += (_, _) => { if (_follow.Checked) ScrollToEnd(); };
    }

    /// <summary>Activa o detiene la lectura del registro (cuando la página o la ventana se ocultan).</summary>
    public void SetActive(bool active)
    {
        if (active == _timer.Enabled)
            return;
        if (active)
        {
            Reload();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Reload()
    {
        _text.Clear();
        _lineCount = 0;
        _lastSequence = 0;
        Poll();
    }

    private void Poll()
    {
        _buffer.Clear();
        _lastSequence = Logger.CopySince(_lastSequence, _buffer);
        if (_buffer.Count == 0)
            return;

        int filter = _filter.SelectedIndex;
        Ui.SuspendDrawing(_text);
        try
        {
            int selStart = _text.SelectionStart;
            int selLength = _text.SelectionLength;

            foreach (LogEntry entry in _buffer)
            {
                if (filter == 1 && entry.IsTrace) continue;
                if (filter == 2 && entry.Kind is not (LogKind.Warning or LogKind.Error)) continue;

                _text.SelectionStart = _text.TextLength;
                _text.SelectionLength = 0;
                _text.SelectionColor = ColorFor(entry.Kind);
                _text.AppendText(entry.Format() + "\n");
                _lineCount++;
            }

            if (_lineCount > MaxLines)
                TrimOldLines();

            if (_follow.Checked)
                ScrollToEnd();
            else if (selStart <= _text.TextLength)
                _text.Select(selStart, Math.Min(selLength, _text.TextLength - selStart));
        }
        finally
        {
            Ui.ResumeDrawing(_text);
        }
    }

    private void TrimOldLines()
    {
        int cut = _text.GetFirstCharIndexFromLine(TrimLines);
        if (cut <= 0)
            return;
        _text.ReadOnly = false;
        _text.Select(0, cut);
        _text.SelectedText = string.Empty;
        _text.ReadOnly = true;
        _lineCount -= TrimLines;
    }

    private void ScrollToEnd()
    {
        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.ScrollToCaret();
    }

    private static Color ColorFor(LogKind kind) => kind switch
    {
        LogKind.Success => Theme.TraceSuccess,
        LogKind.Warning => Theme.TraceWarning,
        LogKind.Error => Theme.TraceError,
        LogKind.ClientCommand => Theme.TraceClient,
        LogKind.ServerReply => Theme.TraceServer,
        _ => Theme.TraceText
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
