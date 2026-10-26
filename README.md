# Dynamic Island para Windows

Una isla dinámica para Windows 10 y 11, inspirada en la del iPhone pero pensada para PC. Vive pegada al borde superior de la pantalla, se transforma con animaciones de resorte y muestra lo que está pasando en el equipo: música, llamadas, rendimiento, temporizadores y más.

Versión 0.5 · C# · WPF · .NET 8

## Compilar

1. Instala el SDK de .NET 8: `winget install Microsoft.DotNet.SDK.8`
2. Ejecuta `build.bat`.
3. El ejecutable queda en `dist\DynamicIsland.exe`. Es autónomo y no requiere instalar nada más.

Para desarrollo, usa `run-dev.bat` o abre `src\DynamicIsland.csproj` en Visual Studio 2022.

## Funciones

| Área | Descripción |
|---|---|
| Inicio | Hora, fecha, batería, red y clima del día (Open-Meteo) según la ubicación del PC. |
| Música | Carátula con efectos dinámicos que siguen el espectro real del audio. Letras sincronizadas (LRCLIB) en la isla y en el panel. Controles y progreso. |
| Modo juego | Solo con la opción de juegos activa y un juego a pantalla completa (modo ventana sin bordes): la isla muestra FPS, CPU y GPU. Los detalles completos están al abrirla. La música queda en una gota secundaria. |
| Sistema | Modelo de CPU y GPU, uso con los contadores del Administrador de tareas, temperaturas, FPS y 1% low; almacenamiento de cada unidad como en el Explorador. |
| Tiempo | Temporizador, cronómetro, alarma y modo concentración (Pomodoro que activa No molestar). |
| Traductor | Traducción rápida con lectura en voz alta (voces de Windows). MyMemory gratis o DeepL con clave propia. |
| Llamadas | Detecta llamadas en Teams, Zoom, Discord, Meet y otras. Silenciar micrófono, ir a la llamada y colgar (Teams y Zoom). |
| Audio | Volumen por aplicación, salida, micrófono y brillo (portátiles). Avisos al cambiar de dispositivo o silenciar el micrófono. |
| Archivos | Suelta uno o varios archivos en la isla. Se guardan con su nombre e icono (también al reiniciar), se arrastran o copian a otra app y se envían al celular con un código QR por la red Wi-Fi local. |
| Portapapeles | Historial de textos copiados (solo en memoria) e inteligente: colores, operaciones, enlaces y textos en otro idioma. |
| Capturas | Al copiar una imagen (Win+Shift+S): extraer el texto (OCR de Windows, sin internet), traducirlo o guardar la captura. |
| Bluetooth | Aviso al conectar audífonos, mouse o teclados con su nivel de batería. |
| Avisos | Volumen, brillo, Bloq Mayús, red, cargador, descargas y uso de micrófono o cámara. |
| Ajustes | Todo lo anterior se puede activar o desactivar. |

## Atajos de teclado

| Atajo | Acción |
|---|---|
| Ctrl + Shift + Espacio | Abrir o cerrar el panel |
| Ctrl + Shift + Y | Traducir el texto copiado |
| Ctrl + Shift + F9 | Silenciar o activar el micrófono |
| Ctrl + Shift + F10 / F11 / F12 | Anterior, reproducir o pausar, siguiente |

Los atajos funcionan dentro de juegos, donde el mouse suele estar capturado.

## Ajustes

Se guardan en `%LOCALAPPDATA%\DynamicIsland\settings.json` y se conservan al apagar o reiniciar el PC. En la misma carpeta queda `log.txt` con los errores, útil para reportar problemas.

## Rendimiento y FPS

Fuentes de cada dato:

| Dato | Fuente |
|---|---|
| Uso de CPU | Contador "% Processor Utility" de Windows, el mismo del Administrador de tareas |
| Uso de GPU y VRAM | Contadores "GPU Engine" y "GPU Adapter Memory" de Windows, los mismos del Administrador de tareas |
| Temperaturas, potencia, relojes, ventiladores | LibreHardwareMonitor |
| FPS, tiempo por fotograma y 1% low | Intel PresentMon (cualquier API: DirectX, Vulkan, OpenGL) |

`build.bat` descarga PresentMon automáticamente junto al ejecutable. Si no está, se usa un contador propio solo para DirectX.

Permisos:

- **FPS:** requieren permiso para leer eventos de rendimiento de Windows. Hay dos formas: ejecutar como administrador o usar en Ajustes "Permitir FPS sin administrador" (una sola vez, luego cerrar sesión).
- **Temperatura del CPU:** requiere el controlador PawnIO, que `build.bat` instala automáticamente (Windows pide permiso una vez), y ejecutar la isla como administrador. Las temperaturas de la GPU no lo necesitan.
- Si un dato no está disponible se muestra "--"; nunca se estima.

## Modo administrador

Se activa en Ajustes o desde el icono de la bandeja. En este modo los archivos se pueden seguir soltando en la isla desde el Explorador, pero Windows no permite arrastrarlos desde la isla hacia apps sin permisos: usa el botón Copiar y pega con Ctrl+V. Para FPS sin estas limitaciones, usa "Permitir FPS sin administrador".

## Privacidad y servicios externos

| Función | Servicio | Qué se envía |
|---|---|---|
| Clima | Open-Meteo | Coordenadas aproximadas |
| Ubicación sin permiso de Windows | ipapi.co / ipwho.is | La IP pública (lo hace cualquier sitio web) |
| Nombre de la ciudad | BigDataCloud | Coordenadas |
| Letras | LRCLIB | Título y artista de la canción |
| Traductor | MyMemory o DeepL | El texto a traducir |

El OCR de capturas, el portapapeles inteligente y la detección de idioma funcionan sin internet. "Enviar al celular" solo funciona dentro de la red local y el enlace caduca a los 15 minutos.

## Limitaciones conocidas

- Los juegos en pantalla completa exclusiva no permiten ventanas encima; en modo ventana sin bordes sí funciona.
- Sin PresentMon, los FPS solo se miden en DirectX 9 a 12.
- Los archivos de la bandeja son accesos a su ubicación original: si se borran o mueven, desaparecen de la lista.
- "No molestar" se activa con una función interna de Windows sin documentación oficial; si una actualización la cambia, el modo concentración sigue funcionando sin silenciar notificaciones.
- La batería Bluetooth solo aparece si el dispositivo la informa a Windows.
- Colgar desde la isla usa el atajo de cada app y solo está disponible en Teams y Zoom. Silenciar actúa sobre el micrófono del sistema, por lo que funciona en cualquier app, aunque la app no lo muestre.
- El brillo solo se controla en pantallas integradas de portátiles.
- La isla aparece en el monitor principal.

## Estructura

```
src/
  App.xaml.cs           Inicio, instancia única, bandeja, inicio con Windows, elevación
  MainWindow.xaml.cs    Prioridades, física, forma y atajos globales
  Core/                 Resortes, geometría, controles, ajustes y llamadas Win32
  Modules/              Actividades de la isla (música, juego, llamadas, tiempo, avisos)
  Services/             Audio, espectro, hardware, traductor, portapapeles
  Panels/               Panel expandido y sus pestañas
  Assets/               Logo
```

Para agregar una función, crea una clase que herede de `IslandModule` (o `TransientModule` para avisos cortos) y regístrala en `MainWindow`.

## Bibliotecas de terceros

- [NAudio](https://github.com/naudio/NAudio) (MIT): audio, volumen y captura.
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0): sensores de CPU y GPU.
- [TraceEvent](https://github.com/microsoft/perfview) (MIT): conteo de FPS de respaldo.
- [Intel PresentMon](https://github.com/GameTechDev/PresentMon) (MIT): FPS de cualquier API (se descarga aparte al compilar).
- [QRCoder](https://github.com/codebude/QRCoder) (MIT): códigos QR.
- PawnIO: controlador de sensores (se instala aparte con winget, paquete `namazso.PawnIO`).

## Licencia

Código abierto bajo licencia MIT. Copyright (c) 2026 Andrey Mantilla Leon. Puedes usarlo, modificarlo y distribuirlo libremente, manteniendo el aviso de copyright y los créditos. Ver [LICENSE](LICENSE).
