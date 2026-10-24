# Changelog

## 1.3.0

(Includes everything prepared as 1.2.1, which was never released on its own.)

**New**
- Settings redesigned: tabs on top, switches with a short help line under each option, and a search box.
- "Switch to": with "End game" selected in the menu, press right to see your last 3 played games under the preview. Pick one and the current game closes and the new one opens behind a transition screen (no desktop or Playnite flashing in between). Optional ("Switch games from the menu" in the Menu tab).
- Music card: shows what is playing (Spotify, SimpMusic, YouTube Music in the browser, VLC...) through Windows' media controls. Accept pauses, left/right change track, L2/R2 or the right stick change the volume (the bar appears only while you touch it), Y toggles shuffle and R3 cycles repeat (when the player supports it). The card takes the colour and icon of the player.
- Music: when nothing is playing, the row becomes "Open music" and launches your player (Spotify by default if installed; add more in "Players to open", one per line as `Name|path`). It opens minimized and you stay in the game. A player can also be closed from the menu.
- Achievements: trophy by rarity on every row (bronze, silver, gold, platinum), trophy counters in the header, progress bar for partial ones, gold highlight for unlocked, and a platinum trophy on the main menu row when you have them all.
- New setting "Pause the game when the menu opens" (on by default), "No pausar" / "NoPause" tag per game.
- Skip the overlay per game with the "Sin overlay" / "NoOverlay" tag, or in every emulator game with "Don't open the menu in emulator games".
- New glass look and numbered controllers (1 blue, 2 red, 3 green, 4 yellow) with battery level colour.
- Button legends in the music card follow the controller style chosen in Playnite (Xbox, PlayStation, Nintendo).
- Translated into all 9 languages.

**Improved**
- Faster opening and closing of the menu, also when calling it repeatedly.
- Music apps opened from the menu are minimized only while the menu is open and no longer touch windows while you play.

**Fixed**
- Menu could disappear after closing a music player.
- Possible freeze when returning to the game after closing a player: if the game is still suspended, it is now resumed automatically and the log records the state.

## 1.2.0

- New menu design: list with colored icons, a short description under the selected option and a curved accent bar. Lighter on the GPU (no zoom, minimal animation).
- Captures gallery: clips play inside the overlay (optional mpv player, see README), with progress bar, duration on thumbnails and a "no audio" indicator. Without mpv, a clip opens in the Windows player.
- Achievements: duplicates removed, unlocked first, filters (all / unlocked / locked), rarity highlight (rare / very rare) and locked ones in grayscale.
- Controller icon in the status area is colored by battery level.
- Faster opening: no fixed waits when bringing Playnite to the front.
- Fixed: first menu sounds played all at once; Playnite could stop responding after calling the menu several times in a row; a freeze after playing a clip.

## 1.1.0

- Menu sounds for opening, moving, accepting and going back, synthesized (no files), with volume control.
- Controller vibration when the menu opens (XInput controllers).
- Optional Start + LB held for 1.5 s to close the game (off by default, in Abrir el menú).
- New toggles in General: menu animations, menu sounds and volume, controller vibration.

## 1.0.0 — first public release

- In-game overlay menu for Playnite, opened with the controller's Guide/PS button (on by default), optionally Select + Start held for 1 s (off by default), or a keyboard shortcut.
- One-time notification explaining how to open the menu.
- Cleaner settings: short labels in groups, details in tooltips, advanced options collapsed.
- Pauses the game while the menu is open and mutes only the game's audio. Games with anti-cheat are not paused.
- Menu: return to the game, take a screenshot, save a clip, captures and clips gallery, achievements, go to Playnite (game stays paused), end the game, power menu (suspend, restart, shut down).
- Cover art, live or last-frame preview, session timer, clock and controller battery (DualSense, DualShock, Xbox, Switch Pro, 8BitDo and other SDL controllers).
- Colors and font follow the Playnite theme in use; button icons follow the controller type configured in Playnite's fullscreen settings.
- Handles games running as administrator (menu without pause, optional removal of the "run as administrator" flag).
- Console-style transitions: fade in, staggered rows, focus ring with a light sweep, no flashes of Playnite when returning to the game.
- Languages: English, Spanish, French, German, Portuguese, Italian, Russian, Chinese, Japanese (follows Playnite's language, then Windows'; overridable).
