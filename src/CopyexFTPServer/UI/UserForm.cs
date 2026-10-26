// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

using CopyexFTPServer.Core;
using CopyexFTPServer.Models;

namespace CopyexFTPServer.UI;

/// <summary>Alta / edición de un usuario FTP.</summary>
internal sealed class UserForm : Form
{
    private readonly TextBox _user = Field();
    private readonly TextBox _password = Field(password: true);
    private readonly TextBox _password2 = Field(password: true);
    private readonly CheckBox _showPassword = Check("Mostrar");
    private readonly CheckBox _enabled = Check("Usuario habilitado");
    private readonly CheckBox _useDefaultRoot = Check("Usar la carpeta raíz general del servidor");
    private readonly TextBox _root = Field();
    private readonly ModernButton _btnBrowse = ModernButton.Create("Examinar…", ButtonKind.Secondary, Glyphs.FolderOpen);
    private readonly CheckBox _browse = Check("Listar / navegar");
    private readonly CheckBox _download = Check("Descargar / leer");
    private readonly CheckBox _upload = Check("Subir / escribir");
    private readonly CheckBox _mkdir = Check("Crear carpetas");
    private readonly CheckBox _rename = Check("Renombrar");
    private readonly CheckBox _delete = Check("Borrar");
    private readonly ModernButton _btnAll = ModernButton.Create("Todos", ButtonKind.Ghost, Glyphs.Check);
    private readonly ModernButton _btnNone = ModernButton.Create("Ninguno", ButtonKind.Ghost, Glyphs.Clear);
    private readonly ModernButton _btnOk = ModernButton.Create("Guardar usuario", ButtonKind.Primary, Glyphs.Save);
    private readonly ModernButton _btnCancel = ModernButton.Create("Cancelar", ButtonKind.Secondary);
    private readonly Label _passwordHint = Ui.Hint("");

    private readonly bool _editing;
    private readonly string _defaultRoot;
    private readonly IReadOnlyCollection<string> _otherUsernames;

    public FtpUser ResultUser { get; }

    public UserForm(string defaultRoot, IReadOnlyCollection<string> otherUsernames, FtpUser? existing = null)
    {
        _editing = existing != null;
        _defaultRoot = defaultRoot;
        _otherUsernames = otherUsernames;
        ResultUser = existing?.Clone() ?? new FtpUser();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = _editing ? "Editar usuario FTP" : "Nuevo usuario FTP";
        Icon = AppResources.AppIcon;
        Font = Theme.Base;
        BackColor = Theme.Background;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 640);
        Padding = new Padding(18);
        BuildUi();
        ResumeLayout(true);

        if (existing != null)
        {
            _user.Text = existing.Username;
            _enabled.Checked = existing.Enabled;
            _browse.Checked = existing.CanBrowse;
            _download.Checked = existing.CanDownload;
            _upload.Checked = existing.CanUpload;
            _mkdir.Checked = existing.CanCreateDirectories;
            _rename.Checked = existing.CanRename;
            _delete.Checked = existing.CanDelete;
            _useDefaultRoot.Checked = existing.UsesDefaultRoot;
            _root.Text = existing.UsesDefaultRoot ? _defaultRoot : existing.RootFolder;
            _passwordHint.Text = "Deja la contraseña vacía para conservar la actual.";
        }
        else
        {
            _enabled.Checked = true;
            _useDefaultRoot.Checked = true;
            _root.Text = _defaultRoot;
            SetAllPermissions(true);
            _passwordHint.Text = "Usa la misma contraseña que configures en el escáner.";
        }
        UpdateRootControls();
    }

    private void BuildUi()
    {
        var account = new Card { Height = 206 };
        TableLayoutPanel g1 = Ui.Grid(3, 150, -1, 0);
        Ui.AddRow(g1, 34, Ui.MakeLabel("Cuenta", Theme.CardTitle));
        Ui.AddRow(g1, 38, Ui.FieldLabel("Usuario"), _user, null);
        Ui.AddRow(g1, 38, Ui.FieldLabel("Contraseña"), _password, _showPassword);
        Ui.AddRow(g1, 38, Ui.FieldLabel("Repetir contraseña"), _password2, null);
        Ui.AddRow(g1, 30, _enabled, _passwordHint);
        g1.SetColumnSpan(_passwordHint, 2);
        _showPassword.Margin = new Padding(8, 8, 0, 0);
        account.Controls.Add(g1);

        var rootCard = new Card { Height = 132 };
        TableLayoutPanel g2 = Ui.Grid(2, -1, 0);
        Ui.AddRow(g2, 34, Ui.MakeLabel("Carpeta raíz de este usuario", Theme.CardTitle));
        Ui.AddRow(g2, 32, _useDefaultRoot);
        g2.SetColumnSpan(_useDefaultRoot, 2);
        _btnBrowse.Height = 32;
        _btnBrowse.Margin = new Padding(8, 3, 0, 0);
        Ui.AddRow(g2, 40, _root, _btnBrowse);
        rootCard.Controls.Add(g2);

        var permsCard = new Card { Height = 168 };
        TableLayoutPanel g3 = Ui.Grid(3, -1, -1, -1);
        g3.ColumnStyles.Clear();
        for (int i = 0; i < 3; i++) g3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        Label permsTitle = Ui.MakeLabel("Permisos FTP", Theme.CardTitle);
        permsTitle.Margin = new Padding(0, 6, 16, 0);
        _btnAll.Height = 30;
        _btnNone.Height = 30;
        header.Controls.AddRange(new Control[] { permsTitle, _btnAll, _btnNone });
        Ui.AddRow(g3, 38, header);
        g3.SetColumnSpan(header, 3);
        Ui.AddRow(g3, 34, _browse, _download, _upload);
        Ui.AddRow(g3, 34, _mkdir, _rename, _delete);
        Label permHint = Ui.Hint("Para un escáner basta con «Subir»; «Listar» y «Crear carpetas» los usan algunos modelos.");
        Ui.AddRow(g3, 28, permHint);
        g3.SetColumnSpan(permHint, 3);
        permsCard.Controls.Add(g3);

        var buttons = new FlowLayoutPanel
        {
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 8, 0, 0)
        };
        _btnCancel.Margin = Padding.Empty;
        buttons.Controls.Add(_btnCancel);
        buttons.Controls.Add(_btnOk);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        Ui.StackTop(body, 12, account, rootCard, permsCard, buttons);
        Controls.Add(body);

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;
        _btnCancel.DialogResult = DialogResult.Cancel;

        _btnOk.Click += (_, _) => Save();
        _btnBrowse.Click += (_, _) => BrowseRoot();
        _btnAll.Click += (_, _) => SetAllPermissions(true);
        _btnNone.Click += (_, _) => SetAllPermissions(false);
        _useDefaultRoot.CheckedChanged += (_, _) => UpdateRootControls();
        _showPassword.CheckedChanged += (_, _) =>
        {
            _password.UseSystemPasswordChar = !_showPassword.Checked;
            _password2.UseSystemPasswordChar = !_showPassword.Checked;
        };
        _user.MaxLength = FtpUser.MaxUsernameLength;
        _password.MaxLength = FtpUser.MaxPasswordLength;
        _password2.MaxLength = FtpUser.MaxPasswordLength;
    }

    private void SetAllPermissions(bool value)
    {
        foreach (CheckBox c in new[] { _browse, _download, _upload, _mkdir, _rename, _delete })
            c.Checked = value;
    }

    private void UpdateRootControls()
    {
        bool custom = !_useDefaultRoot.Checked;
        _root.Enabled = custom;
        _btnBrowse.Enabled = custom;
        if (!custom)
            _root.Text = _defaultRoot;
    }

    private void BrowseRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Selecciona la carpeta raíz FTP para este usuario",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_root.Text) ? _root.Text : string.Empty
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _root.Text = dialog.SelectedPath;
    }

    private void Save()
    {
        string username = _user.Text.Trim();
        string? userError = FtpUser.ValidateUsername(username);
        if (userError != null)
        {
            Ui.Warn(this, userError);
            _user.Focus();
            return;
        }
        if (_otherUsernames.Contains(username, StringComparer.OrdinalIgnoreCase))
        {
            Ui.Warn(this, "Ya existe otro usuario con ese nombre.");
            _user.Focus();
            return;
        }

        string p1 = _password.Text;
        string p2 = _password2.Text;
        bool changePassword = !_editing || p1.Length > 0 || p2.Length > 0;
        if (changePassword)
        {
            if (p1.Length == 0)
            {
                Ui.Warn(this, "Introduce una contraseña.");
                _password.Focus();
                return;
            }
            if (p1 != p2)
            {
                Ui.Warn(this, "Las contraseñas no coinciden.");
                _password2.Focus();
                return;
            }
            if (p1.Length < 6 || p1.Equals(username, StringComparison.OrdinalIgnoreCase))
            {
                if (!Ui.Confirm(this,
                        "La contraseña es débil (menos de 6 caracteres o igual al usuario).\r\n\r\n" +
                        "Algunos escáneres no admiten contraseñas largas, pero cualquiera en la red podría adivinarla.\r\n\r\n¿Usarla de todos modos?",
                        "Contraseña débil", MessageBoxIcon.Warning))
                    return;
            }
        }

        string customRoot = string.Empty;
        if (!_useDefaultRoot.Checked)
        {
            string? rootError = RootFolderPolicy.Validate(_root.Text, out string full);
            if (rootError != null)
            {
                Ui.Warn(this, rootError);
                _root.Focus();
                return;
            }
            try
            {
                Directory.CreateDirectory(full);
            }
            catch (Exception ex)
            {
                Ui.Error(this, $"No se puede usar esa carpeta:\r\n{ex.Message}");
                return;
            }
            customRoot = full;
        }

        if (changePassword)
        {
            UseWaitCursor = true;
            try { ResultUser.SetPassword(p1); }
            finally { UseWaitCursor = false; }
        }

        ResultUser.Username = username;
        ResultUser.Enabled = _enabled.Checked;
        ResultUser.RootFolder = customRoot;
        ResultUser.CanBrowse = _browse.Checked;
        ResultUser.CanDownload = _download.Checked;
        ResultUser.CanUpload = _upload.Checked;
        ResultUser.CanCreateDirectories = _mkdir.Checked;
        ResultUser.CanRename = _rename.Checked;
        ResultUser.CanDelete = _delete.Checked;

        _password.Clear();
        _password2.Clear();
        DialogResult = DialogResult.OK;
        Close();
    }

    private static TextBox Field(bool password = false) => new()
    {
        Font = Theme.Base,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 6, 0, 0),
        UseSystemPasswordChar = password
    };

    private static CheckBox Check(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.Base,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 6, 0, 0),
        UseMnemonic = false
    };
}
