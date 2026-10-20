# Pendientes priorizados

- Validar en hardware el inicio de Desktop como P1 con Steam iniciando con Windows
  y la preparación explícita con Steam abierto (0.1.24). No confundir esa prueba
  con compatibilidad de Steam Input o de todos los juegos.
- Recuperar la sesión virtual tras suspensión/reanudación, incluyendo Modern
  Standby, sin aplicar energía ni reactivar una sesión detenida manualmente.
- Conservar un historial de decisiones de inicio que no se pierda al guardar de
  nuevo la preferencia, y separar los errores de preparación de los de sesión.

- Ampliar validación de P1 automático: reinicios, suspensión, vibración y juegos.
- Validar en hardware el inicio opcional con Desktop integrado y la recuperación
  tras un inicio fallido; la implementación de 0.1.23 requiere esa comprobación.
- Ampliar los destinos de reasignación más allá de los botones Xbox, incluyendo
  entradas de teclado configurables por perfil. Caso solicitado: asignar **Insert**
  a un botón para abrir el menú de **OptiScaler**. Esta salida de teclado todavía
  no está implementada; las demás acciones adicionales quedan por concretar.
