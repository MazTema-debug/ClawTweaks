# Mantenimiento LegionTweaks v1

**Integración en revisión:** estas instrucciones describen la interfaz prevista; no constituyen una autorización para ejecutar una instalación. Antes de usarla se necesita una candidata aceptada, compilada y comprobada, y la preparación física de esa operación.

Esta entrada es para el flujo de mantenimiento y no inicia pruebas de mando. El alcance soportado es una Legion Go 8APU1/83E1 con Ryzen Z1 Extreme, Windows x64, una cuenta local elevada y volúmenes NTFS locales. Se bloquean dominio, identidades adicionales que tengan datos o procesos pertinentes, procesos/roles sin identificar, protección incierta y certificados de origen o destino que no estén ya en `LocalMachine\TrustedPeople`. No se importan certificados en este alcance.

El respaldo y el árbol de retorno conservan exactamente el propietario, grupo, DACL e indicadores de herencia registrados, con comprobación posterior. Los destinos nuevos deben permitir escribir esos descriptores; la operación se bloquea si faltan derechos o la lectura difiere. No habilita privilegios, toma propiedad ni cambia los originales para resolverlo. Las copias protegidas del widget mantienen su modo separado, con el descriptor original registrado.

Antes de usar `Update` o `Return49`, el operador debe confirmar para esa operación: juegos cerrados; mando devuelto al físico sin virtuales; mandos acoplados; FPS OFF y overlay apagado; QAM y ventanas/widget de LegionTweaks cerrados. Una ventana oculta no demuestra que el proceso haya salido. `-Prepared` expresa esa confirmación; el inventario vuelve a comprobar procesos y workers sin terminarlos a la fuerza. Si `settings.dat` está en uso, el respaldo se bloquea.

## Almacén y solicitud

El equipo debe tener un almacén protegido bajo `%ProgramData%\LegionTweaks\maintenance-v1`, con `packages`, `requests` y `transactions`. Sus propietarios y ACL deben impedir escrituras no privilegiadas y no deben contener enlaces; la CLI verifica la protección. El directorio propio de la transacción se crea con ACL protegida. Las carpetas y archivos de solicitudes y paquetes son datos locales: no se publican automáticamente.

La solicitud JSON versión 1 va dentro de `requests`. Las rutas absolutas de los ZIP y extracciones van dentro de `packages`. Cada uno de `Coordinator`, `SourcePackage` y `Target` contiene `Archive`, `Extracted`, `ArchiveSha256`, `HelperSha256`, `QamSha256`, `Signer` y `WidgetRelativePath`. El último es la ruta relativa del `.msix` bajo `Widget`, por ejemplo `Widget\LegionTweaks.Widget_0.1.49.0_x64_Test\LegionTweaks.Widget_0.1.49.0_x64.msix`. Los SHA-256 son 64 caracteres hexadecimales mayúsculos y `Signer` es la huella de 40 caracteres mayúsculos del certificado público. Se verifican ZIP, extracción, manifiesto, firmas, identidad y versión; la solicitud no autoriza por sí sola mantenimiento.

```json
{
  "Version": 1,
  "Direction": "Return49",
  "Id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "Coordinator": {"Archive": "C:\\ruta\\coordinador.zip", "Extracted": "C:\\ruta\\coordinador", "ArchiveSha256": "<SHA256>", "HelperSha256": "<SHA256>", "QamSha256": "<SHA256>", "Signer": "<HUELLA>", "WidgetRelativePath": "Widget\\<paquete>\\widget.msix"},
  "SourcePackage": {"Archive": "C:\\ruta\\origen.zip", "Extracted": "C:\\ruta\\origen", "ArchiveSha256": "<SHA256>", "HelperSha256": "<SHA256>", "QamSha256": "<SHA256>", "Signer": "<HUELLA>", "WidgetRelativePath": "Widget\\<paquete>\\widget.msix"},
  "Target": {"Archive": "C:\\ruta\\destino.zip", "Extracted": "C:\\ruta\\destino", "ArchiveSha256": "<SHA256>", "HelperSha256": "<SHA256>", "QamSha256": "<SHA256>", "Signer": "<HUELLA>", "WidgetRelativePath": "Widget\\<paquete>\\widget.msix"},
  "Source": {"HelperSha256": "<SHA256>", "QamSha256": "<SHA256>", "Signer": "<HUELLA>", "PackageFamilyName": "MazTema.LegionTweaks_wsckx05r95yk0", "Publisher": "CN=LegionTweaks Dev", "Version": "0.1.50.0"},
  "TargetVersion": "0.1.49.0"
}
```

El ejemplo contiene marcadores y rutas ilustrativas, no es ejecutable. `Coordinator` es el paquete nuevo que aporta la API de reserva; `SourcePackage` debe coincidir con el origen instalado; `Target` es el destino. Para `Return49` se exigen versión y pins de la .49 exacta conservada. `Id` es un identificador único de 32 caracteres hexadecimales minúsculos. Una transacción anterior sin resolver bloquea una nueva; no se borra automáticamente.

La comparación del widget distingue la entrada raíz OPC `[Content_Types].xml`, que describe el contenedor y puede no existir en WindowsApps, de los archivos entregados por el paquete. Exige tipos y referencias OPC coherentes y compara los archivos instalados de aplicación y sus footprint, incluidos manifiesto, mapa de bloques y firma; una ruta, enlace, duplicado, archivo requerido ausente o contenido distinto bloquea. Dentro del directorio reservado `AppxMetadata` solo admite el catálogo `CodeIntegrity.cat` de los paquetes soportados; otra entrada reservada bloquea para revisión. Esta comparación no sustituye la verificación independiente de identidad, versión, hash y firma del MSIX ni autoriza mantenimiento.

En PowerShell 5.1 x64 elevado, una vez preparada y verificada la solicitud:

```powershell
.\LegionTweaks-Maintenance-v1.ps1 -Action Return49 -TransactionId <id> `
  -RequestPath <ruta-absoluta-json> -RequestSha256 <sha256-del-json> -Prepared
```

Para actualizar, la solicitud usa `Direction: "Update"` y la acción `Update`. La entrada no fuerza el cierre de Game Bar, servicios ni workers. La barrera y los respaldos se mantienen hasta una publicación o compensación comprobada.

## Reconciliación

`Inspect` y `Finalize` reciben `-TransactionId`, `-WidgetPath`, `-WidgetSha256`, `-PackageVersion` y `-Signer` del destino fijado. La primera acción informa; la segunda puede anotar el commit ya demostrado bajo la reserva exclusiva. Ninguna restaura automáticamente un diario Y3 ni borra un marcador residual. Un registro sin `publish-ready`, un árbol desconocido, un respaldo incierto o una discrepancia externa dejan revisión pendiente. La información Y3 de `publish-ready` corresponde a la frontera previa: no describe necesariamente el estado físico actual. `StartupObservation` informa si la marca de omisión está presente o ausente; la ausencia es compatible con su consumo posterior por el helper, pero no demuestra por sí sola qué proceso la consumió. No se recrea durante la reconciliación.

`complete` identifica una transacción software confirmada; `aborted` indica una compensación verificada; `recovery-required` exige revisión y conserva las evidencias. `published-review-required` conserva la decisión de publicación y una posible reserva Y3 residual cuando no se confirmó la comprobación posterior; `PublicationIntegrity` distingue una observación `unverified` de una diferencia `mismatch`. `published-annotation-pending` indica que la verificación pasó pero faltó anotar el commit; `committed-cleanup-incomplete` indica limpieza pendiente tras verificar y anotar. Una reserva Y3 no bloquea el helper .49 y un fence legado se pierde al terminar su proceso. Ninguno de esos estados valida por sí mismo el mando físico, la aplicación del botón Y3 ni el funcionamiento en juego.
