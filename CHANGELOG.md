# Changelog

## 1.3.5

- Fixed: several texts were shown in Spanish in every language (the achievement tabs All / Unlocked / Locked, the "0 of 46" summary, the "Very rare" label, the descriptions under some menu rows and the "no audio track" message). They are now translated in all 15 languages.
- New "Start/stop recording" row in the menu, with its own shortcut setting (Settings > Screenshots & clips > Record video). The presets fill it in: Win+Alt+R for Xbox Game Bar and Alt+F9 for NVIDIA App; leave it empty to hide the row.
- Fixed: with the menu open, Alt+Tab (or clicking another window) could not bring another app to the front because the menu and its dark curtain stayed on top. They now step back when you switch to another window and come back when you return to the menu.
- Fixed: the same achievement could appear twice, once per language, when the achievements database stored both. Exact pairs (same icon, state, unlock date and percentage) are merged into one.

## 1.3.4

- New integration with **Console Update Center** (optional; nothing changes if it is not installed). It is found by its Id and used through its integration API (version 1), without referencing its dll.
  - New "Downloads" row in the menu: shows the download in progress ("Downloading <game> · 45 % · 3 min"), the number of pending updates, or a notice when the current game has an update pending or on hold. Selecting it opens the Console Update Center panel for the current game.
  - Closing the panel (B or Guide) returns to the overlay menu, with the cursor on "Downloads", instead of leaving you on the Playnite hub.
  - Updates that finish while you play appear in the menu as "Update completed · <game> · Ready to play" until you dismiss them.
  - While the Console Update Center panel is open the overlay ignores the controller completely.
  - Guide button split: inside a game and when returning to a game from Playnite it belongs to Console Overlay; over a game cover without a running game it belongs to Console Update Center (the optional "power menu outside a game" only opens with a long press when it is installed).
- The new texts are translated into all 15 languages.

## 1.3.3

- Fixed (Desktop mode): if Playnite is set to minimize to the system tray (or the taskbar) when a game starts, it now stays that way after the game closes or after you use the overlay, instead of popping up in the taskbar.

## 1.3.2

- Returning to a game that shows "Not responding" (it often happens right after resuming it): the plugin now retries for up to 3 seconds before giving up, instead of leaving you on Playnite.
- Safety net: errors that nothing else catches (background threads and tasks) are now written to the log instead of going unnoticed. It only logs; it does not change how Playnite behaves.
- New optional "Diagnostic mode" (Advanced): logs how long the menu takes to show, the gallery list to load and the return to the game. Handy to report slowness.
- New "Hidden players" box (Menu): hide the music players you never use from "Open music".
- Fixed: the gallery of a game could show captures of other games from the same series (e.g. "SILENT HILL: Townfall" showing "SILENT HILL f" captures). The short name before ":" is no longer used when another game in your library starts with it.
- Shutting down or restarting from the menu now shows a stable dark "Shutting down…" screen while the game closes, instead of flashing Playnite for a moment. If the computer has not shut down after 30 seconds (e.g. a custom script failed) the screen is removed.
- "End game" now shows a dark "Closing <game>…" screen with the cover while the game closes and Playnite redraws, instead of the frozen last frame; it fades out once Playnite is stable.
- Fixed: the controller button used to close the menu (e.g. B) could also reach the game. The game is now resumed only after all controller buttons are released.
- When a screenshot is requested from the menu and no new file shows up within 10 seconds, a Playnite notification points to the capture program, shortcut and folder settings.
- The new settings are translated into all 15 languages (the six beta ones are still unreviewed).

## 1.3.1

- Achievements for Steam games are completed from Steam's own local stats files (read-only, no network): unlocks and progress counters show up when you open the menu as soon as the game reports them to Steam, instead of waiting for Playnite Achievements to refresh.
- Achievement names and descriptions use Steam's translation for the plugin's language when available.
- Achievements are read again whenever the data changes, so the menu no longer shows an old state within the same session.
- The controller battery always shows in its coloured glass pill (with its number), also with a single controller.
- Menu layout: bigger game preview placed higher, "Switch to" in its own glass card with more room between covers, and a lighter secondary text colour tinted with the theme.
- Fixed: after leaving Achievements the cursor could jump to another row when the mouse pointer was resting over the menu; accepting in Achievements no longer plays a sound; the Achievements header (tabs and counters) stays in place when a filter shows fewer items; the Windows Media Player shows its proper name in the player list; the menu no longer flickers out for a moment when the music player opens or closes (it is kept in front while the player takes the focus); the capture counter in the gallery is aligned with the rest of the layout.
- Sharper screenshots on 4K displays: the viewer shows the original resolution (up to 3840 px wide) and gallery thumbnails are decoded at 720 px. Thanks to the GameHQ author for pointing this out.
- Six new languages (beta, machine-translated and not yet reviewed by native speakers): Polish, Turkish, Korean, Dutch, Ukrainian and Traditional Chinese. They are marked "(beta)" in the language list, and Steam achievement texts follow the same language. Corrections are welcome (a lang\xx.txt file or a GitHub issue).
- Note: many games only report counters (e.g. "break 666 objects") to Steam when you save or quit, so those can still lag behind what you see in-game.

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
