# Pendientes priorizados

- Validar las correcciones 0.1.34 de RTSS (un único bloque y margen), estados de
  apariencia guardada/borrador/en helper, visor de entradas de Game Bar y consulta
  mediante toque que conserve el foco. Comprobar los rótulos de diagnóstico histórico.

- Validar en hardware el selector General/Isaac de 0.1.34: selección sin activación,
  botones A/B/X/Y horizontales sin RS en General, conservación de disparo en Isaac,
  cambio explícito y recuperación que retenga la distribución elegida.

- Validar navegación con mando dentro del widget de Game Bar tras añadir el
  registro de interfaces exigido por su SDK en 0.1.31. La app independiente y
  el widget son comprobaciones distintas; no dar el fallo por resuelto solo al compilar.
- Comprobar que solo queda el panel de ajustes en Game Bar y que RTSS conserva
  sus niveles/editor y publicación después de retirar el antiguo overlay.

- Cerrar la validación física del modo de dos jugadores: menús, desacoplar y
  reacoplar, suspensión, devolución del físico y cooperación dentro del juego.
- Validar apariencia RTSS y acceso fijo Insert de OptiScaler de 0.1.30 con el
  procedimiento de VALIDATION.md. Los niveles y el editor ya están implementados;
  no confundir envío de Insert con instalación ni compatibilidad de FSR 4/Lite.
- Validar en la Legion resolución/frecuencia de 0.1.32: modos enumerados, consulta
  sin cambios, confirmación, vencimiento, devolución y diagnóstico. La implementación
  es para una pantalla y la sesión actual; multi monitor y persistencia quedan fuera.
  No ejecutar cambios sin el usuario presente.
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
