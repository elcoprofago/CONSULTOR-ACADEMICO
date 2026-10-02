# Tareas pendientes — CONSULTOR-ACADEMICO-GUI

## Ventana principal más chica al arrancar

**Hecho (2/10/2026), con la alternativa más robusta.** `Window_SourceInitialized`
(`MainWindow.xaml.cs`) toma el área de trabajo del monitor donde abre (el del
cursor): si el tamaño del XAML no entra, usa el 90 % de esa área, centrada, y
achica también los mínimos si no entran. Probado en 1920×1080 (1500×936) y en
un monitor real de 1366×768 (1229×691, entera a la vista). Queda aplicar lo
mismo en `CONSULTOR-GUI` y `PostOCRNormalizer`, en sus propias sesiones.

Tarea común a los dos consultores (éste y `CONSULTOR-GUI`, CSJN+PGN) — ver el
detalle completo del diagnóstico en
`E:\INFO\DERECHO\Jurisprudencia\C.S.J.N\CONSULTOR-GUI\TAREAS-PENDIENTES.md`,
sección "Tarea común a los dos consultores".

**Motivo.** Pantallas de menor resolución (notebooks con 1366×768 o menos) no
"entran" bien con el tamaño de arranque actual.

**Estado actual** (`MainWindow.xaml:5-7`):
```xml
Title="CONSULTOR-ACADEMICO-GUI" Height="950" Width="1500"
MinHeight="650" MinWidth="1100"
WindowStartupLocation="CenterScreen"
```
Idéntico a `CONSULTOR-GUI` — mismos valores, mismo problema.

**Propuesta.** Reducir el tamaño de arranque a algo como `Height="720"
Width="1150"` (entra en 1366×768 y en 1280×800), dejando los `MinHeight`/
`MinWidth` actuales sin tocar. Ajustar el valor exacto probando en una pantalla
chica real.

**Alternativa más robusta**: clampear el tamaño de arranque en el constructor de
`MainWindow` contra `SystemParameters.WorkArea` en vez de un valor fijo en XAML —
mismo criterio que se evalúa para `CONSULTOR-GUI` y `PostOCRNormalizer`; si se
adopta, aplicarlo igual en los tres para no repetir el mismo ajuste manual cada
vez que aparezca una pantalla más chica.

**Verificación.** Arrancar la app en una pantalla de 1366×768 (o simulada) y
confirmar que la ventana completa es visible sin redimensionar a mano.

## Posible: ubicar un PDF movido o renombrado (mejora hecha en CitaPDF)

Anotado el 2/10/2026, para evaluar si se aplica acá.

**Hecho (commit 986ec5a).** El consultor dejó de copiar los PDF: guarda la ruta
del original y permite reubicarlo (`UbicarPdfWindow`). Lo que sigue abajo
describe el estado anterior.

**Qué se hizo en CitaPDF (1.0.3).** Cuando el PDF de un registro ya no está
en la ruta guardada, el enlace "Abrir" ofrece ubicarlo en vez de fallar
(también clic derecho → "Ubicar PDF...", en la vista inicial y en la
biblioteca completa):
- **Elegir el archivo**: si su SHA-256 no coincide con el catalogado, pregunta
  antes y actualiza la huella; rechaza un PDF ya catalogado como otro registro.
- **Buscar en una carpeta**: recorre subcarpetas comparando el SHA-256 (lo
  encuentra aunque esté renombrado), prueba primero los de igual nombre, se
  puede detener, y ofrece corregir también los demás registros cuyo PDF falta
  y aparecieron ahí.

Código en CitaPDF (`F:\source\repos\CitaPDF`, commit ba81072):
`Servicios\Ubicador.cs` (búsqueda) y `UbicarPdfWindow.xaml(.cs)` (ventana).

**Diferencia a tener en cuenta acá.** Este consultor no guarda la ruta del
original: la ingesta copia el PDF a `PDF_DIR\<documento_id>.pdf` y guarda
`hash_sha256` (`ACADEMICO-SCRIPTS\ingesta.py`). Mover el original no rompe
nada. La mejora serviría si se pierde o se mueve esa copia en `PDF_DIR`, o si
con la integración del gestor de citas (abajo) se pasa a enlazar los
originales del usuario.

## Idea: integrar el gestor de citas de CitaPDF en el consultor

En curso. Hecho: los PDF se enlazan desde su ubicación original (986ec5a) y
cada documento muestra su ficha del catálogo de CitaPDF, de solo lectura
(f3d3bbd). Falta: el rediseño del motor de búsqueda.
