# ADR-004: bindings.json, the control table

- **Status:** **draft** (lane W, M2 T2.6). Freezes with the M2 gate; changes after that need a new ADR and updated tests in the same commit.
- **Date:** 2026-10-19
- **Owners:** lane W for M2 (Client, Client.UI, `Game/Assets/Content/core/bindings.json`); lane G takes the screens from M7.
- **Plan:** PLAN §6.1 M2 "Controls and screens" [S13]; 03 §4.26 (the browser game's bindings); `Backroads.Core.DriveInput`.

## Context

The browser game reads keys and the gamepad directly in `main.js` and `input.js`. Unity needs one table that the
input driver, the F1 overlay, the Controls screen, later rebinding and the tests all read, with every action reachable
from a gamepad, and a check that no key or button does two things at once.

## Decision

1. **One file, `Game/Assets/Content/core/bindings.json`,** read at start-up (carried into builds by
   `Resources/BrClientConfig.asset`, which only references it). The Input System actions are generated from it in
   code (`BindingTable.CreateActions`); there is no hand-edited `.inputactions` asset for the game.
2. **Shape (version 1):**

   ```json
   { "version": 1, "deadzone": 0.14, "holdSeconds": 0.3,
     "maps": [ { "name": "Driving", "bindings": [
       { "action": "ignition", "kind": "button", "keys": ["<Keyboard>/i"], "pad": ["<Gamepad>/dpad/left"],
         "hold": "starter", "help": "Ignition on / off (tap)", "since": "M2" } ] } ] }
   ```

   - Three maps: **Driving**, **OnFoot** (M7) and **UI** (always on; Driving or OnFoot is on with it).
   - `kind`: `button` (one press), `hold` (held level), `value` (0..1), `axis` (-1..1, keyboard `negative`/`positive`
     composite), `vector` (2D, `up`/`down`/`left`/`right`), `delta` (look), `scroll` (zoom).
   - `keys` (keyboard and mouse) and `pad` (gamepad) hold Input System control paths.
   - `hold`: holding this entry's controls for `holdSeconds` fires that action instead (a level while held for a
     `hold` target, one press for a `button` target); a tap fires the entry's own action on release. An action can
     have several entries, so a hold can apply to the gamepad only (Back: tap camera, hold manual/auto).
   - `padVia: "menu"`: a UI action the gamepad reaches through the Start menu (help, graphics, debug, checklist, map,
     time, fly camera).
   - `since`: the milestone in which the action starts to do something (shown dimmed in the overlay before that).
3. **The bindings** keep the browser game's (W/S/A/D and arrows, Space handbrake, E get in/out, L lights, H horn,
   C camera, R reset upright, X low range, G winch, Q/Z blade, M map, F1 help, F2 graphics, [ ] time; gamepad RT/LT,
   left stick, A handbrake, X horn, Y get in/out, Back camera, LB/RB blade, right stick look, deadzone 0.14 cut as in
   `input.js`) and add: tap I ignition, hold I starter; Left Shift / Left Ctrl gear up / down; hold V clutch; F4
   manual/auto; K diff lock; Esc menu; F3 debug overlay; F10 checklist; F8 fly camera (screenshots). Gamepad: Start =
   menu, hold Start = reset upright; D-pad up/down = gear; D-pad left = ignition, hold = starter; D-pad right =
   lights, hold = diff lock; Y hold = winch; Back hold = manual/auto; B = clutch; L3 = low range; R3 = zoom steps.
   `WinchReel` is the throttle while the winch is attached (`main.js`), not a binding of its own.
4. **`DriveInput`** (Core contract) is filled every frame for `PlayerVehicle.Current`: levels (throttle, brake, steer
   with D = +1, handbrake, horn, starter, clutch, winch reel) and press counters that only grow. While the game is
   paused or the fly camera is on, the levels are zero and the counters stand still.
5. **Rules (`Input.NoDuplicateBindings`):** no control twice in one map; no control shared by UI and Driving or
   OnFoot; known kinds and hold targets; every Driving and OnFoot action has a keyboard/mouse route and a gamepad
   route (direct or by a hold); every UI action has a gamepad control or `padVia`; the generated Input System maps
   have the same bindings and no duplicates either; every path resolves on a real keyboard, mouse and gamepad.
6. **User changes** live in `settings.json` (`bindings`: map, action, slot `keys` or `pad`, paths) and are applied on
   top of the table; a set of changes that breaks a rule is ignored with a log line, so a bad file never leaves the
   game without controls.

## Consequences

- The browser game's job keys (T tow, Backspace cancel, 1-3 load) are not in v1; lane G adds them in M9 (a new entry
  is an additive change; renaming or rebinding one is not).
- Blade up/down are presses in the `DriveInput` contract; JS holds them. M7 may need levels (For I).
- No steering-wheel support (as JS); a wheel would be a new map or new paths.
