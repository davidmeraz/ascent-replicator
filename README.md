# Ascent Replicator para NinjaTrader 8

**Versión actual: 0.1.0 (prototipo).** La versión se define en `src/ProductInfo.cs` y aparece en el título de la ventana. La DLL de comprobación utiliza versión de ensamblado/archivo `0.1.0.0` y versión de producto `0.1.0`.

Prototipo en C# de un AddOn para copiar órdenes manuales 1:1 entre cuentas visibles en una misma instancia de NinjaTrader Desktop. El adaptador utiliza `Account.CreateOrder`, `Submit`, `Change`, `Cancel` y `OrderUpdate`; las conexiones de Tradovate y Rithmic las administra NinjaTrader. No pide ni almacena credenciales.

## Estado

Código preparado para NinjaTrader 8.1.8.3 y comprobación de compilación contra sus librerías locales. Las pruebas del núcleo usan un proveedor ficticio: **no equivalen a una prueba de ejecución en Tradovate o Rithmic**. No se ha instalado ni activado en NinjaTrader ni enviado ninguna operación. Primero debe validarse entre cuentas de simulación locales.

## Comportamiento

- Una cuenta principal y varias receptoras, seleccionables desde `New > Ascent Replicator`.
- Copia del mismo instrumento/vencimiento, acción, tipo, cantidad, precios, vigencia y fecha GTD. Sin multiplicadores, inversión de dirección ni conversión mini/micro.
- Órdenes Market, Limit, StopMarket y StopLimit; vigencia Day, Gtc o Gtd. MIT, Ioc, Opg, stops simulados y órdenes personalizadas se bloquean en esta versión.
- Cambios de cantidad y precio tras su confirmación en la principal; cancelaciones y grupos OCO independientes por cuenta receptora.
- No replica cada ejecución como una nueva orden: las ejecuciones parciales pertenecen a la orden ya enviada.
- Detecta rechazos, errores, desconexiones, órdenes ajenas en receptoras y diferencias de ejecuciones/cancelaciones que persisten más de 3 segundos. Un envío/cambio sin confirmación durante 10 segundos también detiene la copia.
- Ante un error se suspenden **todos** los envíos posteriores, incluidos cambios y cancelaciones. Las órdenes ya enviadas permanecen en sus cuentas: debes revisar y gestionar cada cuenta manualmente. No hay reintentos de envío, cierre automático ni recuperación automática al reconectar.
- Exige iniciar sin posiciones ni órdenes activas en todas las cuentas seleccionadas. No adopta órdenes anteriores ni reconstruye una sesión después de reiniciar.
- El modo `Local simulation accounts only` está activado inicialmente y valida el proveedor Simulator/Playback, no el nombre de la cuenta. Las cuentas Apex, aunque sean simuladas por Apex, usan proveedores remotos y no se consideran simulación local.
- Registro UTC de envíos, cambios, cancelaciones y errores en `Documents\NinjaTrader 8\log\AscentReplicator`. No sustituye el registro completo de operaciones de NinjaTrader.

## Compilar y comprobar

No necesita instalar el SDK `dotnet`. Usa el compilador de .NET Framework de Windows:

```powershell
cd C:\Dev\CSharp\ascent-replicator
powershell -NoProfile -File .\scripts\Build.ps1
```

Si NinjaTrader está en otra carpeta, pasa `-NinjaTraderBin 'ruta\bin'`. Se generan las pruebas, una DLL de comprobación y `build\ascent-replicator-0.1.0-source.zip`. **El ZIP es un paquete de fuentes para extraer, no un archivo de Import NinjaScript.** No instales la DLL de comprobación junto con los fuentes: duplicaría las clases.

`Properties\AssemblyInfo.cs` define los metadatos de la DLL de comprobación. No se incluye en el paquete de instalación ni debe copiarse al proyecto Custom de NinjaTrader, que ya contiene sus propios atributos de ensamblado.

## Instalación manual de los fuentes

1. Cierra NinjaTrader antes de copiar archivos.
2. Copia `src\CopierCore.cs`, `src\AscentReplicator.cs` y `src\ProductInfo.cs` a `Documents\NinjaTrader 8\bin\Custom\AddOns`.
3. Abre NinjaTrader y `New > NinjaScript Editor`. Compila con F5; si hace falta, reinicia NinjaTrader para que aparezca el menú.
4. Conecta un proveedor de datos y crea al menos dos cuentas locales de simulación desde la pestaña Accounts. Usa, por ejemplo, Sim101 como principal y otra cuenta Sim como receptora.
5. Abre `New > Ascent Replicator`, pulsa `Refresh accounts`, selecciona principal y receptoras distintas, mantén `Local simulation accounts only` activado y pulsa `Start 1:1 copying`.
6. Envía las órdenes manualmente solo en la principal. Comprueba también las cuentas receptoras en las pestañas Orders, Executions y Positions.

Para cuentas remotas: configura las conexiones de evaluación Tradovate y Rithmic en NinjaTrader, con Multi-provider habilitado; las cuentas deben aparecer conectadas en Accounts. Desmarcar el modo de simulación permite seleccionar esas cuentas y `Start 1:1 copying` habilita la copia. Esta acción debe hacerla el titular después de completar las pruebas.

## Convenciones del proyecto

El código fuente, los comentarios, los mensajes, la interfaz, las pruebas y los scripts están en inglés. La documentación permanece en español. Los títulos y cuerpos de los commits se redactan en español y explican el propósito, los cambios, las decisiones relevantes, la validación y los límites de cada cambio. Estas convenciones están en `AGENTS.md`.

La versión del programa es independiente de la versión de NinjaTrader. Para publicar una nueva versión, actualiza `ProductInfo.Version`, esta guía y `CHANGELOG.md`; el ensamblado de comprobación, la ventana y el nombre del paquete derivan su versión del valor del código.

## Validación en simulación antes de usar cuentas Apex

Comprueba entrada y salida Market, Limit, StopMarket y StopLimit; movimiento de stops y targets; aumento/reducción de cantidad; cancelación mientras la copia espera confirmación; fills parciales; OCO con stop y target; rechazo; pérdida de conexión; detener/cerrar con órdenes abiertas y reinicio sin adopción. Valida ambos sentidos Tradovate→Rithmic y Rithmic→Tradovate en el entorno correspondiente antes de considerar ese sentido soportado en operación.

## Límites pendientes

Los precios de ejecución y la latencia pueden diferir entre proveedores. La detección de diferencias no garantiza impedir una divergencia ni evitar pérdidas. Si una cuenta rechaza una orden, otras pueden haberla recibido. No existe una transacción atómica entre cuentas.

OCO puede depender de la plataforma/conexión. Cuando la principal cancela una orden OCO, el núcleo espera brevemente los eventos del grupo; si otra orden principal ya se ejecutó, no retira la protección de la receptora antes de que se ejecute su contraparte. Una divergencia persistente exige intervención manual.

No se crean estrategias ATM independientes en receptoras. Solo se copian órdenes observadas con `OrderEntry.Manual`. La compatibilidad de brackets, auto-breakeven y trailing de una ATM concreta necesita pruebas en NinjaTrader y revisión de las reglas aplicables; no está certificada. No uses otras estrategias, copiers ni Group Trade sobre las mismas cuentas mientras esta sesión está activa.

El prototipo conserva referencias de órdenes de la sesión para impedir reenvíos; detén e inicia una sesión nueva solo cuando todas las cuentas estén sin posiciones y órdenes activas. La ventana no se restaura automáticamente desde un workspace.

## Documentación consultada el 8 de octubre de 2026

- [Acuerdo de Apex, sección 11](https://dashboard.apextraderfunding.com/agreement/user-agreement): permite copiar órdenes manuales entre cuentas del mismo titular bajo sus condiciones y exige reflejar exactamente las órdenes de origen. Revisa también las reglas de tu plan; este software no cuenta con aprobación de Apex.
- [Conexiones Rithmic en NinjaTrader](https://support.ninjatrader.com/s/article/Connecting-To-Evaluation-Services-via-Rithmic-Connection?language=es): Multi-provider y límite de una conexión Rithmic simultánea; una conexión puede dar acceso a varias cuentas.
- [Multi-provider con Tradovate](https://tradovate.zendesk.com/hc/en-us/articles/24841404100371-How-Can-I-Have-Multiple-Connections-on-NinjaTrader-Desktop).
- [Account.CreateOrder](https://docs.ninjatrader.com/ninjascript/createorder), [Account.OrderUpdate](https://docs.ninjatrader.com/ninjascript/orderupdate), [Account.Change](https://docs.ninjatrader.com/ninjascript/change), [AddOn](https://docs.ninjatrader.com/ninjascript/add_on).
