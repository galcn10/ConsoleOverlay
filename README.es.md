# Console Overlay para Playnite

Un menú superpuesto al estilo consola para [Playnite](https://playnite.link). Pulsa el botón Guide/PS del mando (o un atajo de teclado) mientras juegas: el juego se pausa, aparece un menú encima y puedes volver al juego, hacer una captura, ver tus logros, salir a Playnite, cerrar el juego o apagar el PC, todo desde el sofá y con el mando.

> English: [README.md](README.md)

*Capturas: añádelas a `docs/screenshots/` y enlázalas aquí.*

## Características

- **Solo se abre dentro de un juego**, como una consola. No hace nada en la pantalla de inicio de Playnite (salvo que actives allí el menú de energía).
- **Pausa el juego** mientras el menú está abierto y silencia solo el audio del juego. Los juegos con anticheat (EasyAntiCheat, BattlEye, Vanguard...) **no** se pausan, para no perder una partida online.
- **Menú**: volver al juego · captura de pantalla · guardar clip · galería de capturas y clips · logros · ir a Playnite (el juego sigue pausado) · finalizar juego · energía (suspender / reiniciar / apagar).
- **Aspecto nativo**: portada de tu biblioteca, vista previa (en directo o último fotograma si está pausado), temporizador de sesión, hora y batería de varios mandos a la vez (DualSense, DualShock, Xbox, Switch Pro, 8BitDo y otros SDL).
- **Sigue tu tema de Playnite**: colores y tipografía se leen del tema que uses (o de la portada del juego), y los iconos de los botones siguen el tipo de mando configurado en el modo fullscreen de Playnite.
- **Funciona con juegos ejecutados como administrador**: abre el menú sin pausar y puede quitar la marca «ejecutar como administrador» si se lo pides.
- **Programas de capturas**: pulsa el atajo de tu programa (GameHQ, NVIDIA App, Steam, Xbox Game Bar...) y localiza los archivos en las carpetas habituales.
- **Fluido**: sin animaciones continuas; los efectos solo actúan sobre la fila seleccionada y durante las transiciones.
- **9 idiomas**: inglés, español, francés, alemán, portugués, italiano, ruso, chino, japonés (el idioma de Playnite por defecto, y si no, el de Windows).

## Requisitos

- Windows 10 u 11 · Playnite 10. Desarrollado y probado en modo Fullscreen; el modo Escritorio no está probado.
- Un mando o un atajo de teclado. Playnite debe tener activado el soporte de mandos para ver el mando (SDL).

## Instalación

1. Descarga `ConsoleOverlay_x_y_z.pext` desde [Releases](https://github.com/galcn10/ConsoleOverlay/releases).
2. Haz doble clic (o arrástralo sobre Playnite). Playnite pedirá instalar y reiniciar.
3. Abre **Complementos → Ajustes de extensiones → Console Overlay**.

## Opcional: reproductor de clips en el menú (mpv)

Los clips pueden reproducirse dentro del overlay con [mpv](https://mpv.io), un reproductor gratuito. **No va incluido** en el plugin. Para activarlo, descarga mpv para Windows y copia `mpv.exe` (con sus DLL, si las tiene) en una carpeta llamada `mpv` dentro de la carpeta del plugin (`%AppData%\Playnite\Extensions\ConsoleOverlay_galcn10\mpv\`). Sin él, al elegir un clip se abre con el reproductor de Windows. mpv tiene licencia GPL y se ejecuta como programa aparte; ver [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Primeros pasos

- **Mando**: pulsa el **botón Guía/PS** (activado de serie) para abrir el menú o volver al juego. Pulsación larga: menú de energía. Si Steam u otro programa ya usa ese botón, desactiva allí su acción o activa la alternativa en *Abrir el menú*: mantener **Select + Start** 1 s (desactivada de serie). La primera vez, un aviso lo explica.
- **Teclado / JoyToKey / Steam Input**: los atajos por defecto son `Ctrl+Alt+F10` (menú) y `Ctrl+Alt+F11` (finalizar juego). Asígnalos a cualquier botón.
- **Programa de capturas**: elige el tuyo en *Capturas y clips* para que el menú pueda accionarlo.

## Qué hace el plugin en tu sistema

Como controla otros programas, esto es exactamente lo que hace:

| Acción | Cómo | Para qué |
|---|---|---|
| Pausar / reanudar el juego | `NtSuspendProcess` / `NtResumeProcess` sobre los procesos del juego | Congelar el juego con el menú abierto |
| Silenciar el juego | Sesiones de audio de Windows (Core Audio) de los procesos del juego | Silenciar solo el juego, no el sistema |
| Leer el mando | El `SDL2.dll` de Playnite y acceso HID de solo lectura para la batería | Botón Guide y nivel de batería |
| Enviar teclas | `keybd_event` para los atajos que **tú** configures | Accionar tu programa de capturas |
| Registrar atajos | `RegisterHotKey` (por defecto `Ctrl+Alt+F10/F11`) | Abrir el menú / finalizar el juego |
| Mover ventanas | Win32 `SetWindowPos`, `SetForegroundWindow`, miniaturas DWM | Mostrar el menú, devolver el juego, vista previa |
| Finalizar el juego | Cierre normal y, si no responde, forzado. En juegos elevados pide permiso de administrador a Windows (UAC) | «Finalizar juego» |
| Comandos de energía | `cmd /c <comando>` con los comandos de los ajustes (por defecto `shutdown /r /t 0`, `shutdown /s /t 0`) y la llamada estándar de suspensión | Reiniciar, apagar, suspender |
| Quitar «ejecutar como administrador» | Edita la marca de compatibilidad del *usuario actual* en `HKCU\...\AppCompatFlags\Layers` del exe de ese juego, solo si lo aceptas o activas la opción | Poder pausar el juego |
| Leer logros | Acceso de solo lectura a la base de datos del plugin *Playnite Achievements* | Mostrar logros en el menú |
| Ajuste de Playnite | Mientras hay un juego en marcha, «El botón Guide enfoca Playnite» queda limitado a «solo cuando no hay juego» y se restaura al terminar | Si no, Playnite saltaría al pulsar el botón |

**No** usa la red, no recoge datos y no modifica archivos de los juegos. Todo lo que guarda está en la carpeta de datos del plugin.

## Limitaciones conocidas

- Los juegos en pantalla completa exclusiva real pueden mostrar la vista previa en negro.
- Algunos juegos se minimizan al perder el foco; el plugin los recupera, pero el comportamiento varía.
- Un Playnite normal (sin elevar) no puede pausar ni cerrar un juego elevado: usa las opciones del plugin, ejecuta Playnite como administrador o quita la marca.
- Los juegos con anticheat no se pausan por defecto (ajuste *No pausar juegos con anticheat*).

## Compilar desde el código

Requiere el SDK de .NET. Desde esta carpeta:

```
dotnet build -c Release
```

La salida (`bin\Release\`) contiene `ConsoleOverlay.dll`, `extension.yaml`, `icon.png`, `LICENSE` y `THIRD-PARTY-NOTICES.txt`. Empaquétalo con el Toolbox de Playnite: `Toolbox.exe pack "bin\Release" "out"`.

## Licencia y créditos

Licencia MIT — Copyright (c) 2026 galcn10. Ver [LICENSE](LICENSE).

Los componentes de terceros y sus avisos están en [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt): iconos de [Lucide](https://lucide.dev) / Feather y [PlayniteNextOverlay](https://github.com/AleksejSg/PlayniteNextOverlay) (las consultas de solo lectura a la base de datos de Playnite Achievements).

## Marcas

«PlayStation» y «DualSense» son marcas de Sony Interactive Entertainment; «Xbox», de Microsoft; «Steam», de Valve; «Nintendo» y «Switch», de Nintendo; GameHQ, NVIDIA y el resto de nombres son marcas de sus propietarios. Se citan solo para indicar con qué hardware y programas es compatible. Este proyecto no está afiliado, respaldado ni patrocinado por ninguno de ellos y no usa sus logotipos ni su material gráfico.
