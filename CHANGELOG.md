# Changelog

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
