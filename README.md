# Copyex FTP Server

Free, portable FTP and SFTP server for Windows that receives scans from network printers and multifunction devices.

Servidor FTP y SFTP gratuito y portable para Windows, pensado para recibir los escaneos de impresoras y multifunciones.

Desarrollado y mantenido por **Copyex Ofimática S.L.** (España).

## Características

- Servidor FTP con usuarios, carpeta raíz general o propia por usuario y permisos (listar, descargar, subir, crear carpetas, renombrar, borrar).
- SFTP opcional y cifrado, con los mismos usuarios, carpetas y permisos.
- Detección del perfil de red (Pública / Privada / Dominio) con botón para cambiarla a Privada.
- Gestión de IP: DHCP o IP fija, con búsqueda de IPs libres.
- Arranque con Windows, inicio automático del servidor y funcionamiento en la bandeja.
- Aviso junto al reloj al recibir escaneos.
- Un único `.exe` autocontenido: no necesita instalar .NET en el equipo.
- Windows 10 y 11.

## Valores iniciales

FTP en el puerto 21 · carpeta `C:\escaner` · usuario `escaner` / contraseña `escaner`.
**Cambie la contraseña tras la instalación**: el programa avisa mientras se use la de fábrica.

FTP no cifra las contraseñas: no abra el puerto a Internet. Si el escáner lo admite, use SFTP.

## Descarga

Las versiones publicadas están en [Releases](../../releases). Copie `CopyexFTPServer.exe` a una carpeta del equipo y ejecútelo.

## Compilar

```bat
COMPILAR_PORTABLE_X64.bat   rem bin\Portable\win-x64\CopyexFTPServer.exe
COMPILAR_PORTABLE_X86.bat   rem bin\Portable\win-x86\CopyexFTPServer.exe
EJECUTAR_PRUEBAS.bat        rem pruebas automáticas (FTP, SFTP y seguridad)
```

La primera vez se descarga un SDK de .NET 10 portable en `.tools\` (no se instala nada en Windows).
Las versiones oficiales se compilan automáticamente en GitHub Actions (`.github/workflows/build.yml`).

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io),
certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [Copyex team members](../../graphs/contributors)
- Approvers: Copyex Ofimática S.L. (informatica@copyex.es)

Every signed release is built from this repository's source code by GitHub Actions
and manually approved before signing.

## Privacy policy

This program will not transfer any information to other networked systems unless
specifically requested by the user or the person installing or operating it. It only
serves files to the FTP/SFTP clients (scanners) configured by the user on the local network.

## License / Licencia

Copyright © 2026 Copyex Ofimática S.L.

This program is free software: you can redistribute it and/or modify it under the terms
of the GNU Affero General Public License version 3, as published by the Free Software
Foundation. See [LICENSE](LICENSE).

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
See the GNU Affero General Public License for more details.

Este programa es software libre: puede redistribuirlo y/o modificarlo bajo los términos de
la Licencia Pública General Affero de GNU, versión 3. Se distribuye **sin ninguna garantía**.
El texto legal válido es el original en inglés del fichero [LICENSE](LICENSE).

## Trademarks

"Copyex" and the Copyex logo are trademarks of Copyex Ofimática S.L. The AGPL-3.0
license covers the source code and assets only and does not grant permission to use
these trademarks. Forks and redistributions must remove the Copyex name and logo.

"Copyex" y el logotipo de Copyex son marcas de Copyex Ofimática S.L. La licencia AGPL-3.0
no concede ningún derecho sobre ellas: las versiones modificadas o redistribuidas deben
quitar el nombre y el logotipo de Copyex.
