# Historial de versiones

## 0.1.0 — 2026-10-08

Primera versión del prototipo Ascent Replicator para NinjaTrader 8 Desktop.

- Complemento con selección de cuenta principal y varias receptoras conectadas a NinjaTrader.
- Núcleo independiente de la plataforma que replica órdenes manuales 1:1 y gestiona cambios confirmados, cancelaciones y grupos OCO separados por cuenta.
- Adaptador para los eventos y operaciones de órdenes de NinjaTrader, con instantáneas de eventos y protección contra eventos pendientes de sesiones anteriores.
- Modo de simulación local habilitado por defecto, validación inicial de cuentas sin posiciones ni órdenes activas y suspensión de envíos ante errores, rechazos, desconexiones o diferencias persistentes.
- Interfaz, mensajes, comentarios, pruebas y script de compilación en inglés.
- Versión centralizada en `src/ProductInfo.cs`, visible en la ventana y en los metadatos del ensamblado.
- 24 pruebas del núcleo con proveedor ficticio, compilación contra NinjaTrader 8.1.8.3 y paquete de fuentes para instalación manual.

Estado: compilación y pruebas del núcleo verificadas. Pendiente de validar dentro de NinjaTrader en simulación y con las conexiones de Tradovate y Rithmic. No incluye restauración de sesiones, reintentos automáticos, escalado de contratos ni estrategias ATM independientes en receptoras.
