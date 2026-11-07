# QAM de prueba

Panel WinForms net48 x64 para Legion Go original 83E1 / Z1 Extreme. Abre sin activar la ventana, a altura completa y un tercio del monitor del juego. Conserva siete pestañas y lectura HID pasiva. No es un reemplazo instalado del widget.

En la entrega instalada, el helper reconoce los accesos configurados para QAM, escritorio y Game Bar: Desactivado, Legion L, Legion R o L+R. Cada acción usa un gesto distinto; se decide al soltar todos los botones y una combinación no dispara sus dos gestos individuales. El QAM instalado no vuelve a alternar por su lector de navegación. La copia diagnóstica portátil conserva el frontal izquierdo fijo, sin sustituir la configuración del helper.

LB/RB cambia pestaña al soltar y B oculta. También admite toque en pestañas, flechas y botones. En Potencia, arriba/abajo recorre Consultar, selector y Aplicar; izquierda/derecha cambia el borrador en el selector o desplaza el resultado en las otras filas. A consulta, avanza el selector o aplica según la fila. En Pantalla, arriba/abajo recorre Consultar, 60 Hz y 144 Hz; A activa la fila e izquierda/derecha desplaza la lectura. En Juegos, arriba/abajo desplaza la lectura. El texto también admite arrastre táctil. Ctrl+Shift+F10 es el atajo alternativo. Solo se admite una instancia por sesión; termina una versión anterior desde Inicio → Terminar prueba.

## Acceso e inicio con LegionTweaks — entrega .48

En **Inicio** de la app de escritorio, **Abrir menú rápido** muestra la instancia existente o la inicia si está cerrada; nunca alterna para ocultarla. En **Ajustes**, **Iniciar QAM con LegionTweaks** y las dos asignaciones se guardan explícitamente con **Guardar configuración de menús**. Cambiar controles o cargar sus valores no guarda ni aplica nada. La opción de arranque empieza desactivada. El gesto QAM predeterminado es L, salvo conflicto con Game Bar guardado; escritorio empieza desactivado. Un conflicto conserva Game Bar y muestra el motivo para que el usuario elija otra asignación.

Con inicio activado, la tarea existente del helper arranca el QAM oculto al iniciar sesión en Windows, sin abrir la app UWP. Reintenta el inicio mientras espera a que el escritorio esté listo. El helper usa el contexto del usuario y su misma sesión interactiva, nunca sesión 0; un helper elevado usa el token del escritorio. No se crea otra tarea ni una entrada de inicio por el QAM. Desactivar la preferencia no termina una instancia ya abierta. Un gesto explícito puede iniciar el menú aunque el arranque automático esté desactivado.

Cerrar el helper no termina explícitamente el proceso del QAM. Un reinicio reutiliza una instancia que siga viva sin alternar su visibilidad; si el entorno de la tarea la terminó, la opción activada la repone oculta o una acción explícita la inicia de nuevo. Suspender oculta el panel y descarta entradas previas; reanudar puede reponer la instancia oculta si la preferencia está activada. El botón de escritorio puede iniciarlo de nuevo después de cerrarlo. `--start-hidden` conserva vivo el bucle de mensajes sin mostrar ventana; `--open` o una segunda ejecución manual solicitan mostrar al propietario existente.

La acción **Mostrar/minimizar app de escritorio** minimiza solo su vista registrada cuando está en primer plano; si está detrás, minimizada o cerrada, solicita su activación normal. La app reutiliza la vista existente y su borrador. El widget de Game Bar no registra su ventana para esta acción. La identidad de proceso/usuario/sesión y la marca de la vista se validan antes de minimizar; no se buscan ventanas solo por su título.

La coordinación de instancias usa un canal local con SID/sesión e imágenes comprobadas. Solo admite estado, mostrar, alternar QAM o la acción fija de escritorio; los últimos 16 identificadores evitan repetir alternancias. Abrir, consultar, guardar el inicio o registrar una vista no ejecuta modos, pantalla, perfiles ni energía. Las operaciones de aplicación existentes siguen separadas. Antes de actualizar o desinstalar, cerrar QAM desde Inicio → Terminar prueba; los scripts bloquean la operación si el ejecutable instalado sigue abierto.

Potencia y Juegos tienen consultas explícitas: modo Lenovo/límites guardados/potencia ASIC y detección de instalación/proceso/versión de Lossless Scaling. Pantalla renueva automáticamente resolución/Hz al entrar, reabrir y reanudar, y cada cinco segundos mientras está visible, con consulta manual opcional. Los resultados conservan la hora del helper, indican una captura anterior y se retiran ante fallo. La detección del proceso no confirma escalado ni LSFG activo. Las demás tarjetas siguen como prueba de navegación.

## Modos Lenovo desde el QAM

Potencia permite elegir Silencioso, Equilibrado o Rendimiento. Elegir solo modifica el borrador. «Aplicar modo» renueva la lectura y envía una petición explícita al helper principal, que usa su mismo motor y diario de originales. La transacción incluye únicamente el modo, con controlador nulo y sin CPU/AC/DC/Boost ni límites editables. Conserva también el original de los tres límites, aunque Personalizado esté inactivo. Los fallos usan la verificación y el intento de reversión existentes; un resultado incierto requiere consultar y no se repite automáticamente.

El canal de cambios es independiente del de lectura. Valida SID e imagen del proceso en ambos extremos: helper y QAM deben ejecutarse desde `Program Files/LegionTweaks-Z1E`, con el QAM en `Qam/LegionTweaks.QamProbe.exe`. El instalador conserva ACL protegidas y verifica las firmas de ambos. El build del paquete incluye y firma el QAM. Una copia portátil puede conservar las consultas, pero no obtiene permiso de escritura del helper. El lector temporal `QamReadOnly` sigue sin admitir cambios.

Abrir o cambiar pestaña no aplica energía. Una aplicación que pierde la vista antes de enviar se cancela; una petición ya enviada puede terminar en el helper. El identificador de la petición evita reejecutar las últimas 16 solicitudes repetidas dentro del proceso. Cerrar el QAM mantiene el último modo aplicado; los originales quedan en el diario común del helper.

## Frecuencia desde el QAM

Pantalla ofrece botones independientes para 60 y 144 Hz. Solo una activación explícita envía un cambio: abrir, navegar y consultar solo leen. Las capacidades y la captura vigente habilitan cada botón; la frecuencia actual, una opción no enumerada, una lectura vencida o un helper anterior lo dejan inactivo. Si falta el nuevo canal se conservan las consultas por el canal de lectura, sin habilitar cambios.

El canal `LegionTweaks.Z1E.QamDisplay.v1.<SID>` acepta únicamente `query` y `set_refresh` con 60/144 Hz, y valida SID e imagen instalada en ambos extremos. Usa la misma instancia de pantalla del helper principal. Las consultas no renuevan el token del widget; la aplicación toma una lectura nueva y mantiene su bloqueo compartido durante test, revalidación, cambio y lectura posterior. Conserva los píxeles actuales y solo admite modos enumerados por Windows, sin confirmación, prueba temporizada ni reversión automática. Una recuperación histórica pendiente bloquea cambios nuevos. Solicitudes duplicadas conservan resultado y captura, con un historial de 16 identificadores; las escrituras no se reenvían ante pérdida de respuesta.

Para dejar los lectores/helpers temporales se necesita empaquetar e instalar juntos el helper principal actualizado y el QAM en las rutas protegidas, con sus firmas y configuración DPI. El build/instalador ya incluyen `Helper/Qam`. Después puede cambiarse la presentación usando las operaciones existentes sin recompilar el helper; las funciones nuevas del backend sí requieren actualizarlo. Compilar en desarrollo sigue siendo una comprobación local y no reemplaza automáticamente la instalación. El QAM continúa siendo un prototipo independiente del widget UWP.

## Lector temporal de la prueba anterior

La entrega anterior de lectura requería un helper que incluyera `ReadOnlyPipeServer`. Su copia portátil conserva `Iniciar-Prueba-QAM.cmd` / `.ps1` y una carpeta `Helper` con la candidata. El helper usa el modo fijo `--qam-readonly <PID-QAM> <inicio-UTC-ticks>`, comprueba propietario/usuario/modelo/CPU y finaliza cuando termina ese QAM. Ese modo entra antes del inicio normal del servicio: no inicia mando, recorder, recuperación ni preferencias de arranque, y no carga/escribe diarios protegidos. Conserva la instalación y la tarea actuales. El panel congelado de esa entrega consulta solo al pulsar sus botones; no incluye cambios de modos ni de frecuencia.

Abrir únicamente el ejecutable del QAM utiliza un canal de lectura ya disponible; si falta, muestra la conexión indisponible. Construir o abrir el panel no actualiza el helper. La carpeta portátil contiene `LegionTweaks.QamReadOnly.exe`, compilado con `QamReadOnlyHost=true`: ese ejecutable rechaza el arranque normal y las demás opciones del helper. Su configuración y dependencias se conservan juntas.

El canal `LegionTweaks.Z1E.ReadOnly.v1.<SID>` se limita al mismo usuario local, valida el SID del proceso cliente y rechaza tráfico de red. Solo acepta `telemetry`, `display_status` y `lossless_status`, sin payloads de ajustes/contexto. Usa lectores concretos, no el despachador general. La consulta de pantalla no renueva tokens de selección del widget. El canal de comandos conserva su autenticación de usuario y familia del paquete.

```powershell
dotnet build LegionGo/Tools/QamProbe/LegionTweaks.QamProbe.csproj --configuration Release
dotnet build LegionGo/Helper/LegionTweaks.Helper.csproj --configuration Release
dotnet build LegionGo/Helper/LegionTweaks.Helper.csproj --configuration Release -p:QamReadOnlyHost=true --output <carpeta-de-entrega>/Helper
dotnet run --project LegionGo/Tests/LegionTweaks.Tests.csproj --configuration Release
```

Ejecutar el panel normalmente habilita la lectura pasiva de botones tras comprobar modelo y CPU. `--solo-teclado` omite el lector HID y permite tacto/atajo. Los registros se guardan en `%LOCALAPPDATA%\LegionTweaks\QamProbe`. La navegación aún no aísla las entradas físicas que recibe el juego.
