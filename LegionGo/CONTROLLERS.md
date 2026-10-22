# Perfiles, botones Legion y mando virtual — 0.1.28

## Dos jugadores: una mitad cada uno (0.1.28)

**Mando → Activar dos jugadores** inicia una sesión explícita con distribución fija.
Prepara con ambos mandos acoplados, FPS OFF y juegos cerrados; Steam puede quedar
abierto. La sesión anterior se sustituye. Se reservan los puestos 1 y 2 antes de
recuperar la fuente física; si el físico ocupa cualquiera de ellos, se usa el
cambio temporal de modo ya supervisado. Un segundo puesto incorrecto o ausente
impide activar la sesión y ejecuta la recuperación física habitual.

Al terminar, suelta los controles y puedes desacoplar ambos. El helper lee una
sola fuente combinada y produce dos salidas, sin modificar asignaciones de firmware.
El mouse propio y la vibración quedan apagados. Desktop al inicio sigue usando un
solo mando completo. Guardar, seleccionar perfiles o consultar estado no activa
dos jugadores. Para aplicar un perfil editable, devuelve antes el mando físico.

| Entrada física | Jugador 1: izquierda | Jugador 2: derecha |
| --- | --- | --- |
| Stick de su mitad | Stick izquierdo (movimiento) | Stick izquierdo (movimiento) |
| Cruceta / X-B-Y-A | Stick derecho: izquierda-derecha-arriba-abajo | Stick derecho: izquierda-derecha-arriba-abajo |
| Clic del stick | A | A |
| LB / RB de su mitad | LB | LB |
| LT / RT de su mitad | LT | LT |
| Vista izquierda / Menú derecho | Start | Start |
| Legion L / Legion R | Vista | Vista |
| Y1 / Y3 | B | B |
| Y2 / M2 | RB | RB |
| M3 | — | X |

El atajo global de Game Bar conserva prioridad sobre Legion L/R. La orientación
del stick es la habitual, sin giro horizontal. Los nombres de salida son botones
Xbox: asigna las acciones dentro del juego si sus ajustes no coinciden. Los botones
de disparo no envían también A/B/X/Y; usa clic del stick para confirmar menús y
Y1/Y3 para cancelar. Si el firmware duplica un auxiliar en un botón estándar,
la fuente combinada no garantiza distinguir ambos orígenes; compruébalo antes de
atribuir acciones cruzadas al juego. No se cambia ese firmware automáticamente.

Primero comprueba en Mando los dos puestos y las dos salidas por separado y a la
vez. Después prueba ambas mitades desacopladas, el retorno, suspensión y Devolver
mando físico. Solo entonces comprueba la incorporación del segundo jugador y las
acciones en un juego cooperativo compatible. No se afirma compatibilidad por juego
a partir de las pruebas automatizadas o del puesto XInput.

La recuperación de entrada conserva los dos virtuales durante el plazo existente
de 20 segundos, exige la misma identidad y ambos puestos, y rearma al soltar todos
los controles. Ante fallo de cualquiera se detienen ambos. Devolver mando físico
los detiene también y restaura la visibilidad física; no hay inicio automático de
dos jugadores después de un paro manual.

## Recuperar una pérdida breve de entrada (0.1.27)

Si la entrada desaparece con una sesión virtual activa, el helper conserva el
virtual, envía reposo y vuelve a buscar el mismo dispositivo durante hasta 20
segundos, con intentos separados 250 ms. No cambia el modo del receptor ni crea
otro virtual. Una conexión que reaparece y vuelve a caer no renueva ese plazo:
necesita un segundo de muestras nuevas para admitir otro periodo de recuperación.
Suelta los controles y centra los sticks para rearmar la salida. El mouse propio
permanece apagado. **Devolver mando físico** cancela la espera. Ante identidad o
puesto distintos, supervisor ausente o plazo vencido, se devuelve el físico.

El diagnóstico cuenta las reconexiones por separado y conserva el motivo de la
última recuperación; una pérdida de entrada no se presenta como aviso de Windows.
Esto permite ensayar el paso entre mandos acoplados y ambos desacoplados cuando
vuelve la misma entrada XInput. No añade lectura de dos mitades D-input ni dos
jugadores independientes por sí sola. El modo explícito anterior crea las dos
salidas; las pruebas de software no validan esas transiciones
en hardware ni la compatibilidad con juegos.

Si Steam mueve el cursor, revisa su **Disposición del escritorio** para el mando
seleccionado. Ese ajuste es independiente del mouse de LegionTweaks; el helper
no modifica las disposiciones de Steam.

## Recuperar la sesión tras suspensión (0.1.26)

El helper recibe avisos nativos de suspensión/reanudación, incluida la suscripción
de aplicaciones de escritorio a Modern Standby. Si el virtual ya estaba activo,
su conexión y distribución se conservan. Durante la pausa se envía reposo, se
apaga el mouse propio y se cierra la fuente física SDL. Al despertar, el mismo
worker intenta abrir de nuevo el mismo mando durante un máximo de 20 segundos,
con intentos separados 250 ms. No se cambia el modo del receptor ni se aplica
energía. Por eso esta recuperación no necesita cerrar el juego o Steam.

Se comprueban identidad, supervisor, entrada nueva y puesto del virtual. Suelta
los botones y centra los sticks para rearmar la salida. El mouse permanece apagado.
Si falta el físico, cambia su identidad/puesto o vence el plazo, se detiene el
virtual y se ejecuta la recuperación física habitual; no hay reintentos posteriores.
**Devolver mando físico** cancela también la recuperación. Una sesión apagada antes
de suspender permanece apagada, aunque Desktop esté configurado para el arranque.

Un intervalo de al menos dos segundos sin procesar muestras también descarta el
lector anterior. No se presenta ese intervalo como prueba de suspensión. El panel
de diagnóstico incluye el último aviso nativo, su fecha, el estado de recuperación
y su contador. Las pruebas de software no demuestran conservación del puesto o
compatibilidad con un juego real: esas comprobaciones requieren el equipo.

## Utilidades de escritorio alojadas en Steam (0.1.25)

Lossless Scaling puede permanecer abierto durante la preparación por modo y el
inicio de Desktop. Se reconoce mediante su nombre y la ubicación exacta de su
ejecutable en la biblioteca de Steam. Otros programas de esa carpeta no reciben
la excepción. La detección de juegos sigue siendo limitada: cierra los juegos
antes de preparar P1. No se cambia ninguna configuración de Steam o de la utilidad.

## Jugador 1 con Steam abierto (0.1.24)

El inicio opcional de Desktop y **Activar virtual como jugador 1 (automático)**
permiten ahora el cliente Steam abierto, incluido su inicio con Windows. Cierra
los juegos, mantén FPS OFF y los mandos acoplados. No se cierra Steam ni se cambia
su configuración. El asistente manual FPS y los métodos PNP antiguos siguen
exigiendo salir de Steam; esta excepción solo corresponde al método de modo.

La preparación rechaza los juegos conocidos y los procesos ejecutados desde una
carpeta `steamapps`. Es una detección limitada, no identifica todos los juegos:
cierra los demás antes de empezar. Se vuelve a comprobar antes de cambiar el modo
y conectar el virtual; un juego que aparezca durante la preparación detiene el
intento. La recuperación del físico no depende de cerrar ninguna aplicación.
Los mandos adicionales, la identidad, los puestos y los plazos se validan igual.

La versión 0.1.24 no incluía recuperación tras suspensión; la 0.1.26 conserva la
sesión activa como se describe arriba. La compilación y las pruebas no validan por sí solas Steam Input ni el
inicio real de Windows con Steam; esas comprobaciones requieren el equipo.

## Desktop integrado y arranque del helper (0.1.23)

**Mando → Desktop** muestra un perfil de solo lectura, separado de los perfiles
editables. Lo construye el helper: botones Xbox estándar, zonas muertas del 10 %,
vibración al 100 % y Legion L/R/Y1/Y2/Y3/M2/M3 sin reasignación. No contiene ajustes
de energía ni mouse. El atajo global de Game Bar conserva su configuración.
La operación no acepta una distribución enviada por el cliente ni carga el catálogo.

Para usarlo ahora, cierra juegos y Steam, deja FPS OFF y usa **Activar Desktop ahora
como jugador 1**. Para futuros inicios, activa **Activar Desktop al iniciar el
helper** y pulsa **Guardar inicio automático**. Guardar no cambia la sesión actual.
La preferencia empieza desactivada y se conserva entre reinicios. El helper utiliza
el mismo procedimiento supervisado P1 y primero resuelve recuperaciones pendientes.

Al reiniciar el helper, hay un solo intento. Si detecta Steam o Diablo abierto,
se omite ese arranque; no espera ni fuerza su cierre. La preparación también valida
el mando y FPS OFF. Si falla o queda interrumpida, se conserva el registro y no se
reintenta automáticamente: revisa la recuperación y vuelve a guardar la opción
antes de otro inicio. **Devolver mando físico** cancela el intento actual.

Actualizar el paquete omite el arranque del mando una vez, aunque la preferencia
esté activada. La instalación conserva esa preferencia y tus perfiles. Reanudar
no inicia una sesión Desktop nueva; desde 0.1.26 recupera la sesión ya activa.
El mouse propio sigue apagado
hasta que se active expresamente. Arranque real, reinicio de Windows y compatibilidad
por juego se validan por separado de las pruebas de software.

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
Esa versión todavía no activaba el virtual al arrancar.

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

Desde 0.1.23, el inicio opcional utiliza el Desktop integrado descrito arriba.

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
- Una desconexión fuera de suspensión detiene la salida. La recuperación de una
  sesión suspendida se describe arriba. El arranque de Windows solo activa Desktop
  si guardaste su opción; devolver el físico impide reactivarlo al reanudar.
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
