# Copyex FTP Server

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

Los ejecutables no están firmados digitalmente: Windows puede mostrar un aviso al abrirlos (ver [Firma de código](#firma-de-código)).

## Compilar

```bat
COMPILAR_PORTABLE_X64.bat   rem bin\Portable\win-x64\CopyexFTPServer.exe
COMPILAR_PORTABLE_X86.bat   rem bin\Portable\win-x86\CopyexFTPServer.exe
EJECUTAR_PRUEBAS.bat        rem pruebas automáticas (FTP, SFTP y seguridad)
```

La primera vez se descarga un SDK de .NET 10 portable en `.tools\` (no se instala nada en Windows).
Las versiones oficiales se compilan automáticamente en GitHub Actions (`.github/workflows/build.yml`).

## Firma de código

Los ejecutables **no están firmados** con un certificado de firma de código. Por eso Windows
SmartScreen o el antivirus pueden avisar de que el editor es desconocido al descargarlos o ejecutarlos.
Para continuar, pulse *Más información* → *Ejecutar de todas formas*.

Cada versión se compila automáticamente a partir del código fuente de este repositorio con
GitHub Actions. El registro de la compilación ([Actions](../../actions)) muestra el hash SHA-256
de los ejecutables, para comprobar que el archivo descargado es el original:

```powershell
Get-FileHash .\CopyexFTPServer-1.0.0-win-x64.exe -Algorithm SHA256
```

## Política de privacidad

Este programa no transmite ninguna información a otros sistemas de la red salvo que lo solicite
expresamente el usuario o la persona que lo instala o lo utiliza. Solo sirve ficheros a los
clientes FTP/SFTP (escáneres) que el usuario configure en la red local.

## Licencia

Copyright © 2026 Copyex Ofimática S.L.

Este programa es software libre: puede redistribuirlo y/o modificarlo bajo los términos de
la Licencia Pública General Affero de GNU, versión 3, publicada por la Free Software Foundation.
Consulte el fichero [LICENSE](LICENSE).

Este programa se distribuye con la esperanza de que sea útil, pero **SIN NINGUNA GARANTÍA**,
ni siquiera la garantía implícita de COMERCIABILIDAD o IDONEIDAD PARA UN FIN DETERMINADO.
Consulte la Licencia Pública General Affero de GNU para más detalles.
El texto legal válido es el original en inglés del fichero [LICENSE](LICENSE).

## Marcas registradas

"Copyex" y el logotipo de Copyex son marcas de Copyex Ofimática S.L. La licencia AGPL-3.0
cubre únicamente el código fuente y los recursos, y no concede ningún derecho sobre estas marcas:
las versiones modificadas o redistribuidas deben quitar el nombre y el logotipo de Copyex.
