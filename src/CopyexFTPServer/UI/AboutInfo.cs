// Copyex FTP Server
// Copyright (C) 2026 Copyex Ofimática S.L.
// SPDX-License-Identifier: AGPL-3.0-only
// Software libre bajo la licencia GNU AGPL v3 (ver LICENSE). Se distribuye SIN NINGUNA GARANTÍA.

namespace CopyexFTPServer.UI;

/// <summary>Aviso legal "Acerca de" (autoría, licencia AGPL y ausencia de garantía).</summary>
public static class AboutInfo
{
    public const string Developer = "Copyex Ofimática S.L.";

    public static string Text =>
        $"Copyex FTP Server {Application.ProductVersion}\r\n" +
        $"Desarrollado por {Developer}.\r\n" +
        $"Copyright © 2026 {Developer}\r\n\r\n" +
        "Este programa es software libre: puede redistribuirlo y/o modificarlo bajo los " +
        "términos de la Licencia Pública General Affero de GNU (AGPL), versión 3, " +
        "publicada por la Free Software Foundation.\r\n\r\n" +
        "Se distribuye SIN NINGUNA GARANTÍA, ni siquiera la garantía implícita de " +
        "COMERCIABILIDAD o IDONEIDAD PARA UN PROPÓSITO PARTICULAR. Consulte la licencia " +
        "completa en https://www.gnu.org/licenses/agpl-3.0.html\r\n\r\n" +
        $"\"Copyex\" y el logotipo de Copyex son marcas de {Developer}. " +
        "La licencia no concede ningún derecho sobre ellas.";

    public static void Show(IWin32Window? owner) =>
        MessageBox.Show(owner, Text, "Acerca de Copyex FTP Server", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
