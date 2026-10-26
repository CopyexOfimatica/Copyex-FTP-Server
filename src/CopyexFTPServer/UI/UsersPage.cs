// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using CopyexFTPServer.Models;

namespace CopyexFTPServer.UI;

/// <summary>Página "Usuarios": lista de cuentas FTP con alta, edición y baja.</summary>
internal sealed class UsersPage : UserControl
{
    private readonly DataGridView _grid = new();
    private readonly ModernButton _btnAdd = ModernButton.Create("Nuevo usuario", ButtonKind.Primary, Glyphs.Add);
    private readonly ModernButton _btnEdit = ModernButton.Create("Editar", ButtonKind.Secondary, Glyphs.Edit);
    private readonly ModernButton _btnDelete = ModernButton.Create("Eliminar", ButtonKind.Secondary, Glyphs.Delete);
    private readonly Label _summary = Ui.MakeLabel("", Theme.Small, Theme.TextMuted);

    public event EventHandler? AddClicked;
    public event EventHandler<int>? EditClicked;
    public event EventHandler<int>? DeleteClicked;

    public UsersPage()
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
        _summary.Margin = new Padding(8, 11, 0, 0);
        toolbar.Controls.AddRange(new Control[] { _btnAdd, _btnEdit, _btnDelete, _summary });

        var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(2, 2, 2, 2) };
        ConfigureGrid();
        card.Controls.Add(_grid);

        Controls.Add(card);
        Controls.Add(toolbar);

        _btnAdd.Click += (_, _) => AddClicked?.Invoke(this, EventArgs.Empty);
        _btnEdit.Click += (_, _) => RaiseForSelection(EditClicked);
        _btnDelete.Click += (_, _) => RaiseForSelection(DeleteClicked);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditClicked?.Invoke(this, e.RowIndex); };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { RaiseForSelection(EditClicked); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { RaiseForSelection(DeleteClicked); e.Handled = true; }
        };
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        UpdateButtons();
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Theme.Surface;
        _grid.BorderStyle = BorderStyle.None;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.GridColor = Theme.Border;
        _grid.EnableHeadersVisualStyles = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AllowUserToOrderColumns = false;
        _grid.MultiSelect = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 40;
        _grid.RowTemplate.Height = 38;
        _grid.StandardTab = true;

        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextMuted,
            SelectionBackColor = Theme.SurfaceAlt,
            SelectionForeColor = Theme.TextMuted,
            Font = Theme.Caption,
            Padding = new Padding(10, 0, 0, 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            SelectionBackColor = Theme.AccentSoft,
            SelectionForeColor = Theme.Text,
            Font = Theme.Base,
            Padding = new Padding(10, 0, 6, 0),
            WrapMode = DataGridViewTriState.False
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "User", HeaderText = "USUARIO", FillWeight = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "ESTADO", FillWeight = 45 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Root", HeaderText = "CARPETA RAÍZ", FillWeight = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Perms", HeaderText = "PERMISOS", FillWeight = 170 });
        foreach (DataGridViewColumn column in _grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        _grid.Columns["User"]!.DefaultCellStyle.Font = Theme.Bold;

        _grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == _grid.Columns["State"]!.Index && e.Value is string s && e.CellStyle != null)
            {
                Color c = s == "Habilitado" ? Theme.Success : Theme.Danger;
                e.CellStyle.ForeColor = c;
                e.CellStyle.SelectionForeColor = c;
                e.CellStyle.Font = Theme.Bold;
            }
        };
    }

    public void SetUsers(IReadOnlyList<FtpUser> users, string defaultRoot)
    {
        int selected = _grid.CurrentRow?.Index ?? -1;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (FtpUser user in users)
        {
            string root = user.UsesDefaultRoot ? $"Raíz general ({defaultRoot})" : user.RootFolder;
            _grid.Rows.Add(user.Username, user.Enabled ? "Habilitado" : "Deshabilitado", root, user.GetPermissionsSummary());
        }
        _grid.ResumeLayout();

        if (_grid.Rows.Count > 0)
        {
            int index = Math.Clamp(selected, 0, _grid.Rows.Count - 1);
            _grid.CurrentCell = _grid.Rows[index].Cells[0];
            _grid.Rows[index].Selected = true;
        }

        int enabled = users.Count(u => u.Enabled);
        _summary.Text = users.Count == 0
            ? "No hay usuarios: nadie podrá iniciar sesión"
            : $"{users.Count} {(users.Count == 1 ? "usuario" : "usuarios")}, {enabled} {(enabled == 1 ? "habilitado" : "habilitados")} · los cambios se aplican al momento";
        UpdateButtons();
    }

    public void SelectRow(int index)
    {
        if (index >= 0 && index < _grid.Rows.Count)
        {
            _grid.CurrentCell = _grid.Rows[index].Cells[0];
            _grid.Rows[index].Selected = true;
        }
    }

    private void RaiseForSelection(EventHandler<int>? handler)
    {
        int index = _grid.CurrentRow?.Index ?? -1;
        if (index < 0)
        {
            Ui.Info(FindForm(), "Selecciona un usuario.");
            return;
        }
        handler?.Invoke(this, index);
    }

    private void UpdateButtons()
    {
        bool hasSelection = _grid.CurrentRow != null;
        _btnEdit.Enabled = hasSelection;
        _btnDelete.Enabled = hasSelection;
    }
}
