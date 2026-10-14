# Perfiles, botones Legion y mando virtual — 0.1.9 experimental

## Abrir Game Bar sin teclado

1. Entra a la app desde Inicio o desde Game Bar y abre **Mando**.
2. En **Abrir y cerrar Game Bar**, elige **Legion L**, **Legion R** o
   **Legion L + Legion R** y pulsa **Guardar atajo**. Empieza desactivado.
3. Suelta los botones, cierra Game Bar y pulsa el botón elegido; para la combinación,
   pulsa los dos juntos. Una pulsación envía Win+G; otra vuelve a enviarlo para cerrar.
4. Prueba con mando físico y después con el virtual activo. El indicador muestra
   informes HID recibidos, estado de L/R y Win+G enviados. El contador confirma el
   envío a Windows, no que Game Bar haya llegado a mostrarse.
5. Tras suspender/reanudar o reconectar, suelta ambos botones antes de probar de nuevo.
   El atajo se recupera automáticamente; el perfil virtual se reactiva manualmente.

Es una preferencia global, guardada en el helper, independiente del perfil de juego.
Para quitarla, elige **Desactivado** y guarda. No necesitas ViGEmBus/HidHide para
usar el atajo con el mando físico. El helper debe estar ejecutándose y Game Bar
instalada. No se envían atajos sobre el escritorio de bloqueo/UAC ni mientras se
mantienen teclas modificadoras del teclado.

Esta versión escucha los botones originales de la Legion Go 8APU1 sin modificar
el firmware. Las asignaciones de Legion Space/GoTweaks pueden seguir ejecutándose;
si se abren dos interfaces, desactiva la asignación de esa otra aplicación.
Desde 0.1.9 se reconocen también los identificadores de firmware 61EB–61EE.
El diagnóstico muestra cuántos HID Lenovo hay, cuántos son compatibles y el PID,
uso y tamaño de entrada seleccionados. Si no llegan informes, comparte el estado
completo de esta sección (incluido ese diagnóstico); el port no intenta
activar modos exclusivos del mando ni restablecer todas sus asignaciones.

## Cuando un juego no reconoce el virtual

Activa el virtual con el juego cerrado y abre después el juego. En **Mando**, revisa
**Jugador virtual**, la hora **Puesto consultado**, los informes enviados y los
valores de entrada/salida. En 0.1.8 se vuelve a consultar el puesto cada segundo:
un resultado ausente durante la conexión inicial ya no queda congelado.

Que un juego reciba el virtual valida esa ruta de entrada en ese juego. Otro puede
requerir diferente orden de mandos o enumeración; un puesto distinto de jugador 1
es una pista, no una confirmación de la causa. Esta versión no cambia el orden,
reinicia dispositivos ni instala otros drivers para intentar corregirlo.

## Preparación

1. Actualiza usando `Install-LegionGo.ps1`, con el mismo usuario de Windows.
2. Para usar **Xbox 360 virtual**, deben estar instalados **ViGEmBus** y **HidHide**.
   Descarga únicamente sus instaladores oficiales si aún faltan:
   - https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0
   - https://github.com/nefarius/HidHide/releases
   Reinicia Windows si el instalador lo solicita. LegionTweaks no instala ni actualiza estos drivers automáticamente.
3. Cierra otros remapeadores (Handheld Companion, GoTweaks, DS4Windows, etc.).
   Usa los mandos originales en modo XInput, con el modo FPS desactivado.
4. Cierra el juego antes de activar el virtual. Para la primera prueba, cierra Steam
   si el juego no lo requiere; si lo requiere, revisa que Steam Input no añada otra
   capa de remapeo. Las aplicaciones que ya tengan una excepción en HidHide pueden
   seguir viendo el mando físico: el port conserva esas excepciones.

ViGEmBus es un proyecto retirado por su autor. Esta primera salida usa su última
versión oficial y mantiene la interfaz del port separada para poder sustituirla.
La rama actual de Handheld Companion usa VIIPER; **este port no incluye VIIPER ni
copia su implementación o la de Handheld Companion**. Se revisó su separación de
lectura física, layouts, salida virtual y ocultación como referencia de diseño.

## Crear y aplicar un perfil

- En **Perfiles**, crea un perfil y escribe un nombre.
- Configura modo Lenovo, límites personalizados, Boost/EPP AC/DC y modo Windows.
  Cada ajuste que diga **Sin cambiar** queda fuera de la aplicación.
- En **Mando del perfil**, elige conservar el mando actual, usar el físico o activar
  Xbox 360 virtual. Configura botones, zonas muertas, inversión/intercambio de
  sticks, intercambio de gatillos y vibración.
- **Guardar cambios** guarda el archivo local. **Aplicar perfil guardado** aplica
  la versión guardada y verifica la respuesta del equipo. Los cambios sin guardar
  no se aplican. Editar el nombre y guardar renombra el mismo perfil.
- Los perfiles anteriores se migran conservando solo sus tres límites. El dato
  antiguo se conserva como respaldo. Un archivo dañado no se reemplaza silenciosamente.

Los botones traseros independientes, giroscopio, touchpad, macros, combinaciones
programables y conversión a teclado/ratón no forman parte de esta primera capa.
Los gatillos son analógicos. La reasignación permite botones estándar a botones
estándar o sin función. Los dos ejes Y se invierten antes de intercambiar sticks.

## Comprobación inicial

1. Crea dos perfiles con rendimiento diferente y, por ejemplo, A/B intercambiados
   en uno de ellos. Guárdalos y aplica uno.
2. En **Mando**, mantén pulsado A durante un par de segundos y observa entrada
   física A / salida virtual B. Revisa sticks, gatillos y vibración dentro del juego.
3. Abre el juego después de activar el virtual; prueba Baldur’s Gate 3 y Diablo IV.
   Comprueba que no hay doble pulsación y que el juego recibe el mando.
4. Cambia entre los perfiles: la salida virtual permanece conectada mientras cambia
   su distribución. La potencia se lee de vuelta y un fallo provoca un intento de
   restauración de los valores anteriores, indicando si queda algo sin restaurar.
5. Revisa el número de jugador en **Mando**. HidHide no garantiza el primer puesto
   de XInput: si el virtual aparece como jugador 2, un juego puede ignorarlo. Esta
   versión muestra el puesto y no afirma resolver ese caso mediante reinicios de
   dispositivos, inyección de DLL o cambios globales en XInput. Comunica ese resultado
   para la siguiente prueba de compatibilidad.

## Volver al mando físico

- Pulsa **Devolver mando físico**, o mantén **LB + RB + Back + Start** durante 2 segundos.
  La combinación se reconoce en la entrada física, antes del remapeo.
- Al desconectar el mando o recibir un evento de suspensión, la salida virtual se
  detiene. Después, aplica de nuevo el perfil. No se activa al iniciar Windows.
- Se guarda una intención de recuperación antes de ocultar el dispositivo. Un
  supervisor independiente intenta liberar el mando si el helper termina. El
  inicio siguiente, la actualización y la desinstalación también recuperan ese
  respaldo. Solo se elimina la entrada que añadió el port; se conservan las de
  otras aplicaciones. Un fallo de recuperación conserva el respaldo y se informa.
- Si la interfaz no responde, con teclado/táctil detén la tarea **LegionTweaks-Z1E**
  y después ejecuta PowerShell como administrador:

```powershell
Stop-ScheduledTask -TaskName LegionTweaks-Z1E
& "$env:ProgramFiles\LegionTweaks-Z1E\LegionTweaks.Helper.exe" --recover-controller
Start-ScheduledTask -TaskName LegionTweaks-Z1E
```

El visor del mando refresca cada 2 segundos; el envío de entradas corre en un hilo
independiente y continúa con Game Bar cerrada. La frecuencia efectiva y la
compatibilidad con los juegos deben verificarse en el equipo real. Las pruebas
simuladas no validan drivers, latencia, ocultación real ni detección por Diablo IV.
