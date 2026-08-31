
# FUSE ENGINE : the source engine inspiration 

It's simply an engine I decided to make in my free time to study OpenGL.

I don't yet have a clear goal for what kind of game to create with this engine, but for now I'm focused on making a game about bhop.

! The project was ported from C++ to C# simply because I prefer C#. !

![Logo](https://file.garden/Z5fpJocFXF3TjRhg/Captura%20de%20tela%202026-06-26%20171524.png)
![Preview](https://file.garden/Z5fpJocFXF3TjRhg/fuse%20engine/screenshot_2026-06-29_11-46-03.png)
![Preview2](https://file.garden/Z5fpJocFXF3TjRhg/fuse%20engine/screenshot_2026-06-29_11-49-00.png)

## Installation

```bash
  git clone https://github.com/SaitoxBeats/FuseEngine.git
```
Choose the "blowtorch" project for the map editor, and the "Fuse" project to open the game.

You also can open the game directly from the map editor by pressing F5.

## Fuse: controls and shortcuts

### Gameplay

| Key/action | Function |
|---|---|
| `W` `A` `S` `D` | Move the player |
| `Mouse` | Control the camera when the cursor is captured |
| `Space` | Jump; move upward in noclip mode |
| `Left Ctrl` | Crouch; move downward in noclip mode |
| `Left Shift` | Sprint |
| `F` | Toggle the flashlight |
| `E` | Interact with the object being viewed or pick up/drop an object when unarmed |
| `Left mouse button` | Fire a weapon; throw the held object |
| `R` | Reload the equipped weapon |
| `1` | Equip the Glock |
| `2` | Equip the AK |
| `0` | Holster the weapon |
| `Esc` | Pause/unpause and release/capture the cursor |
| `Mouse wheel` | Adjust the camera FOV in 2-degree increments when the interface is not using the mouse |

### Interface and development tools

| Key | Function |
|---|---|
| `` ` `` | Open/close the console |
| `Insert` | Open/close the ImGui debug interface |
| `F1` | Toggle noclip mode |
| `F2` | Capture a screenshot |
| `F3` | Toggle spider pursuit of the player |
| `F4` | Reload all shaders |
| `F5` | Reload the current map |
| `F6` | Toggle patrol AI |
| `F8` | Toggle enemy selection mode; requires the Debug Drawer to be active |
| `F9` | Toggle the Debug Drawer |
| `F10` | Toggle post-processing |
| `F12` | Toggle shadows |

### Test actions

These keys work during gameplay or when the weapon context is active:

| Key | Function |
|---|---|
| `G` | Create an explosion at the raycast hit point |
| `J` | Spawn an enemy at the raycast hit point |
| `V` | Spawn a spider at the raycast hit point |
| `T` | Apply a decal and play the spray sound at the raycast hit point |
| `Z` | Apply test damage to the player |

When an interface window is capturing the keyboard or mouse, the corresponding gameplay controls are blocked.
