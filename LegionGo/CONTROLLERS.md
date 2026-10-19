# Perfiles, botones Legion y mando virtual — 0.1.22

## Preparación automática mediante cambio de modo (0.1.22)

El botón **Activar virtual como jugador 1 (automático)** usa ahora una operación
explícita distinta, anunciada por el helper 0.1.22. Selecciona un perfil virtual
guardado sin aplicarlo antes; cierra juegos y Steam, deja los mandos acoplados y
FPS en OFF. Confirma el diálogo y no muevas el interruptor durante la preparación.

Si el físico ocupa P1, el helper guarda su modo e identidad, cambia temporalmente
el receptor 61EB a 61EC, conecta una sola vez el virtual y confirma P1 antes de
restaurar el físico. El supervisor tiene un límite independiente de 30 segundos.
Solo tras comprobar el modo original y ambos mandos se retira el respaldo, se abre
la fuente SDL y se activa HidHide selectivamente. No se aplican ajustes de energía.

La nueva interfaz exige la capacidad correspondiente; no envía silenciosamente
la operación antigua a un helper anterior. La opción de lectores abiertos del
método PNP ya no forma parte del diálogo. Sus operaciones anteriores se conservan
por compatibilidad y no son la ruta de este botón. El asistente FPS sigue disponible.

El modo mouse propio empieza apagado y requiere el virtual activo; salir de Steam
ayuda a separar su asignación de escritorio de la salida de LegionTweaks. Con el
virtual apagado, el visor de entrada física no toma muestras. La prueba integrada
de P1, la entrada, vibración y compatibilidad por juego requieren validación aparte.
El helper todavía no activa el virtual al arrancar.

## Diagnóstico supervisado del cambio de modo (0.1.21)

La nueva ruta es un ensayo explícito por consola administrativa del helper
instalado: `--test-controller-mode-cycle`. Primero se detiene la tarea principal,
con juegos/Steam cerrados, virtual apagado, mandos acoplados y FPS OFF. El ensayo
intenta XInput → DirectInput → XInput, comprueba si se liberaron los puestos y
recupera el modo original sin conectar ningún Xbox virtual ni aplicar energía.

Un supervisor independiente controla los procesos de escritura y un respaldo
protegido. El arranque, `--recover-controller`, la actualización y desinstalación
reconocen una recuperación de modo pendiente. No se borra el respaldo sin lectura
válida del modo original y de su función XInput. Un fallo de dispositivo puede
impedir la recuperación a tiempo; el registro queda pendiente en ese caso.

En 0.1.21 el botón automático seguía usando Windows. Ese ensayo no activa
el virtual al iniciar. No se deben trasladar
resultados de pruebas simuladas a compatibilidad de firmware o juegos. Se necesita
validar el ciclo en hardware antes de integrar la preparación virtual de P1.

## Botones auxiliares por perfil (0.1.20)

En **Perfiles → Botones especiales Legion**, asigna Y1, Y2, Y3, M2 y M3 a
botones Xbox o deja **Sin asignación**, guarda y aplica explícitamente el perfil
virtual. Se necesita actualizar helper y widget. Un helper antiguo mantiene
desactivadas las asignaciones que no anuncia y el widget impide aplicarlas.

El lector pasivo decodifica los formatos conocidos sin cambiar el firmware.
En el byte candidato 18 (`04 00 A1`) o 20 (`04 3C 74`), Y1/Y2/Y3 usan las máscaras
80/40/20 y M2/M3 usan 04/08. Son máscaras hexadecimales, distintas de las ranuras
normalizadas de los perfiles. Back/Start y RB/M1 no se añaden como auxiliares.
Las pruebas simuladas cubren ambos formatos; eso no sustituye las pruebas de
salida remapeada en hardware y juegos.

El atajo global de Game Bar conserva prioridad sobre sus Legion L/R reservados;
no consume los cinco auxiliares. La captura pausa las asignaciones y exige soltar
todos los botones al terminar. No se añade otro lector ni una activación al inicio.

## Jugador 1 con reconexión automática

Selecciona un perfil virtual guardado y pulsa **Activar virtual como jugador 1
(automático)**. Cierra los juegos y sal de Steam antes de empezar; deja FPS en
OFF. Solo se aplica la distribución guardada del mando, sin ajustes de energía.

Cuando el físico ocupa el primer puesto, el método de 0.1.22 cambia brevemente
su modo como se describe arriba, conecta el Xbox virtual y recupera el físico.
Se verifica el puesto antes de anunciar éxito. No se deshabilita la función por
PNP ni se reinicia el padre USB. Si ya está activo en P1,
se conserva su conexión. No se intenta mover mandos de terceros.

La intención se guarda antes de la interrupción. Un supervisor separado puede
pedir recuperación incluso con el helper bloqueado; también se comprueban
recuperaciones pendientes al iniciar, actualizar o desinstalar. No se borra un
registro sin confirmar el físico rehabilitado. Los fallos de un driver pueden
impedir recuperar a tiempo; se informa del respaldo pendiente. Este método aún
requiere pruebas específicas en hardware. La opción con FPS permanece disponible.

El arranque del helper todavía no activa automáticamente el mando virtual. Esa
opción queda condicionada a validar primero la reconexión de jugador 1.

## Activar el virtual como jugador 1: asistente explícito

Esta función requiere actualizar tanto widget como helper. Está pendiente de
validación física como función integrada; las pruebas simuladas no verifican
Game Bar, SDL, HidHide, vibración ni compatibilidad por juego.

1. Cierra los juegos y sal completamente de Steam. Empieza con FPS en OFF.
2. En **Perfiles**, selecciona un perfil guardado que active el Xbox virtual.
   Guarda primero si quieres usar cambios recientes de su distribución.
3. En **Mando**, pulsa **Preparar jugador 1 con interruptor FPS** y revisa la
   confirmación. Solo usa la distribución guardada: no aplica su energía.
4. Si el puesto 1 está ocupado, sigue el mensaje para activar FPS sin desacoplar
   los mandos. El helper espera que se liberen los puestos, conecta el virtual
   definitivo y confirma su índice.
5. Cuando indique **Jugador 1 reservado**, devuelve FPS a OFF. Se vuelve a abrir
   la fuente física, se activa la ocultación selectiva y se comprueba el puesto.
   Si ya estaba libre, el asistente evita el paso FPS innecesario.
6. Abre el juego solo cuando aparezca virtual activo y jugador 1 confirmado.

Puedes cancelar desde **Cancelar preparación y devolver físico**. Los pasos
tienen un presupuesto de 90 segundos, incluso con Game Bar cerrada. Devuelve
siempre el interruptor físico a OFF después de cancelar o de un error. No se
intenta restablecer automáticamente un virtual anterior ni mover mandos ajenos.
Una llamada nativa bloqueada puede retrasar la limpieza; no se promete cancelación
instantánea frente a un fallo de driver.

El asistente comprueba Steam/Diablo cerrados y el supervisor antes de conectar y
ocultar. Rechaza dispositivos ambiguos o adicionales; el dispositivo fuente debe
ser el XUSB MI_00 original. No realiza deshabilitaciones PNP, resets USB ni
escrituras de firmware. **Aplicar perfil guardado** sigue siendo una operación
separada que puede aplicar energía y no inicia este asistente.

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
es una pista, no una confirmación de la causa. La activación estándar no cambia
el orden. El asistente explícito anterior reserva el puesto con ayuda del usuario;
no reinicia dispositivos desde Windows ni instala otros drivers.

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

Y1/Y2/Y3 y M2/M3 se incluyen desde 0.1.20. Giroscopio, touchpad, macros,
combinaciones programables y conversión de estos botones a teclado/ratón siguen pendientes.
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
   ruta estándar muestra el puesto. Si necesitas jugador 1, usa el asistente con
   el juego cerrado. No se inyectan DLL ni se cambia globalmente XInput.

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
Con el virtual desactivado el helper no mantiene abierto SDL: el panel no es una
prueba de entrada física en ese estado. **Actualizar estado del mando** permite
consultar de nuevo sin aplicar un perfil ni iniciar una grabación.

## Lectores abiertos y modo de mouse (0.1.13)

La inspección XInput se ejecuta en un lector breve que debe terminar antes de
reconectar. El diálogo automático incluye «Permitir reconectar aunque Windows u
otras aplicaciones estén leyendo el mando», desmarcado de entrada. Es una ruta
experimental explícita que usa ABSOLUTE (sin HARDWARE ni PERSIST); no garantiza
superar todos los vetos. Solo se interrumpe el endpoint Lenovo MI_00 validado y
se conserva el supervisor de recuperación. No ejecutes esta prueba con un juego
abierto. Al cancelar puede ser necesario esperar a la recuperación nativa.

En **Mando → Mouse con stick derecho**, activa el cursor con el virtual ya activo,
suelta el stick para armarlo y después muévelo. Desde 0.1.16, sensibilidad y zona
muerta se conservan al reiniciar; la activación siempre empieza apagada. El stick derecho deja de salir al mando virtual
mientras controla el cursor; no se añaden clics. Desactiva el modo antes de jugar.
Al iniciar, suspender, desconectar, devolver el físico o preparar P1 queda apagado.
Con bloqueo/UAC se pausa, y al volver o cambiar foco debes centrar el stick.
Si otra aplicación también mueve el cursor, desactiva esa función por separado.
Esta primera versión no usa el mouse con el virtual desactivado.

El diagnóstico en **Sistema** no hace una lectura nueva de XInput ni reconecta.
La marca de tiempo y el estado cacheado se distinguen del último resultado de
recuperación; finalizar una recuperación no confirma éxito de jugador 1. Copiar
o exportar es manual y el informe omite identificadores y rutas privadas.

## Asignaciones Legion e identificación (0.1.16)

En **Perfiles → Botones especiales Legion**, asigna L/R a un botón Xbox y guarda.
Solo se usan con el virtual activo. El atajo global de Game Bar reserva el botón
elegido (ambos en L+R), por lo que tiene prioridad aunque el perfil tenga asignación.
Para usar ambos como botones virtuales, guarda primero el atajo **Desactivado**.
Estas nuevas asignaciones requieren validación física independiente.

Hasta 0.1.19, los campos Y1/Y2/Y3/M2/M3 estaban deshabilitados; desde 0.1.20 se
asignan por perfil como indica la sección inicial. No se envían inicializadores
ni cambios de firmware. Para identificar señales con juegos y Steam
cerrados, elige un botón en **Mando → Identificar botones especiales** e inicia la
lectura explícita. Deja todo libre un segundo, después pulsa y suelta únicamente
ese botón tres veces. Puedes cancelar y la lectura termina a los 30 segundos.
Solo se muestran cabecera y bits digitales candidatos; no se almacena una captura
bruta. Game Bar y las asignaciones Legion se pausan durante esa lectura. Al acabar,
suelta los botones para rearmarlos. Los resultados no asignan automáticamente bits.

**Duplicar perfil guardado como borrador** copia la versión guardada con otro ID y
nombre; las ediciones pendientes siguen protegidas por el aviso. **Restablecer
ajustes del mando en el borrador** conserva potencia, nombre y modo de mando.
Revisa y guarda explícitamente; ninguna opción aplica cambios al equipo.

El diagnóstico PnP correlaciona solamente el último intento con marcas y endpoint
protegidos de esta versión, usando UTC y los registros existentes. Los resultados
antiguos sin esos datos quedan sin correlación. Los nombres permitidos son etiquetas
fijas; no se exportan rutas, IDs, XML ni nombres arbitrarios. Una lista de aplicaciones
registradas por Windows no demuestra una causa única ni garantiza la reconexión.
