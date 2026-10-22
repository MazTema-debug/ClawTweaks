# Pendientes priorizados

- Cerrar la validación física del modo de dos jugadores: menús, desacoplar y
  reacoplar, suspensión, devolución del físico y cooperación dentro del juego.
- Validar apariencia RTSS y acceso fijo Insert de OptiScaler de 0.1.30 con el
  procedimiento de VALIDATION.md. Los niveles y el editor ya están implementados;
  no confundir envío de Insert con instalación ni compatibilidad de FSR 4/Lite.
- Añadir resolución/frecuencia mediante modos enumerados y cambio provisional con
  confirmación y retorno al modo anterior. No ejecutar cambios sin el usuario presente.
- Integrar Lossless Scaling por etapas: detección/apertura/atajo y luego ajustes
  por versión con respaldo. Actualmente solo existe su excepción de detección de juegos.
- Evaluar gestión de OptiScaler por juego con versiones y restauración de archivos;
  el acceso al menú de 0.1.30 no instala ni actualiza DLL.
- Ampliar las comprobaciones del inicio Desktop P1 y la preparación con Steam y
  Lossless Scaling abiertos, implementados hasta 0.1.25; incluir fallos y otros juegos.
  No confundir la presencia del cliente con compatibilidad general de Steam Input.
- Ampliar los casos de recuperación tras suspensión/reanudación de 0.1.26 y pérdida
  temporal de entrada de 0.1.27, incluidos controles mantenidos y fallos de drivers.
- Conservar un historial de decisiones de inicio que no se pierda al guardar de
  nuevo la preferencia, y separar los errores de preparación de los de sesión.

- Ampliar validación de P1 automático: reinicios, suspensión, vibración y juegos.
- Validar en hardware el inicio opcional con Desktop integrado y la recuperación
  tras un inicio fallido; la implementación de 0.1.23 requiere esa comprobación.
- Ampliar los destinos de reasignación más allá de los botones Xbox, incluyendo
  entradas de teclado configurables por perfil. Caso solicitado: asignar **Insert**
  a un botón para abrir el menú de **OptiScaler**. Esta salida de teclado todavía
  no está implementada; las demás acciones adicionales quedan por concretar.
