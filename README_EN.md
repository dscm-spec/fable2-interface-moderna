# Fable 2 Return to Albion — Modern UI v1 (keyboard & mouse)

A replacement launcher DLL for **Refii** (Fable 2 Return to Albion). Makes the menus nicer and
faster on keyboard and mouse without changing the game's original art.

## What changes

- **Mouse hover highlight** — the row under the mouse turns white, grows slightly and slides,
  without stealing the keyboard selection.
- **Clickable "Esc Back"** — lights up on hover; clicking goes back, same as Esc.
- **Home / End** jump to the top/bottom of lists.
- **Selection ring on keyboard/mouse** — the original gold ring also shows outside gamepad mode,
  with the right key (E) instead of the A button.
- **"(Equipped)"** marks the weapon and clothing you are using.
- **Gauntlet pointer** replaces the Windows arrow, scaled to the resolution. Colour variants in
  `ponteiro/variacoes/`.
- **Social wheel** — press Q to open, press again to close (no holding); pick with the mouse.
- **Faster menus** — lists and the main menu open without the old hitches.
- Host-only menu texts show in Portuguese only when the PT-BR translation is installed.

## Install

1. **Close the game.**
2. Open `Fable 2 Return to Albion\data_refii-launcher_windows_x86_64\`.
3. **Back up** `refii-launcher.dll` and `refii-launcher.pdb`.
4. Copy `dll\refii-launcher.dll` and `dll\refii-launcher.pdb` there.
5. Copy `ponteiro\refii_cursor.png` and `ponteiro\refii_cursor_hotspot.txt` next to the game exe.

## Turn parts off

Create an empty file next to the game exe:

| File | Turns off |
|---|---|
| `refii_hover_off.flag` | hover highlight and clickable Esc Back |
| `refii_cursor_off.flag` | gauntlet pointer |
| `refii_equipped_tag_off.flag` | "(Equipped)" suffix |
| `refii_wheel_hold.flag` | social wheel back to hold-Q |
| `refii_kbm_cursor_off.flag` | selection ring on keyboard/mouse |

## Uninstall

Close the game and restore the two backed-up files.

## Notes

- Built against the Refii version shipped with the current Return to Albion. Do not use it with
  a different Refii version — it replaces the whole launcher.
- Does not touch saves or `refii.toml`.

## Credits

- Refii — its authors. Gauntlet: "gauntlet cursor", OpenGameArt.org, CC0.
- Source of the changes in `codigo/` (patch + changed files).
