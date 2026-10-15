# Assignment 3 – Task 1: Exhaustive Variable and View Extraction (NorthStar)

Code: `Assets/NorthStar/Scripts/ControlRoom/`. Nothing has to be added to a scene. `ControlRoomBootstrap` spawns a
`[ControlRoom]` object when Play starts and keeps it across scene loads, so Launch, Beat1–7 and FreeSailing are all covered.

| File | Role |
|---|---|
| `ControlRoomBootstrap.cs` | Spawns the rig and recorder; disables the Assignment 2 `NorthStarTelemetry` director so two directors do not fight over the output |
| `ControlRoomContext.cs` | Finds the player rig (head/hands), boat, wheel, sail, grabbables, joints, harpoons, NPCs and subtitles once per scene |
| `ExhaustiveTelemetryRecorder.cs` | Continuous logging, with no event filtering, to CSV, plus a live UDP JSON stream |
| `ReflectiveStateSampler.cs` | Logs every field of every NorthStar / Meta.Utilities component and every physics body |
| `UnityEventTap.cs` | Listens to every `UnityEvent` on game components (harpoon fire, buttons, levers, dialogue, …) |
| `SpectatorCameraRig.cs` | All taxonomy views as cameras, each rendering to its own RenderTexture, plus a program output camera |
| `ControlRoomInput.cs` | Keyboard and UDP commands for switching views, saving snapshots and adding marks |
| `Editor/ControlRoomMenu.cs`, `Editor/ControlRoomPanel.cs` | Window-menu commands and the dockable Control Room panel |
| `MultiviewDebugOverlay.cs` | Press **M** to show a grid of every live view (for testing and report screenshots) |

## How to run

1. Open `Assets/NorthStar/Scenes/#GameplayBeats/FreeSailing.unity` (or `Launch.unity` for the full game) and press **Play**.
   This works with the Meta XR Simulator or with a Quest over Link.
2. The Console prints `[ControlRoom] Recording session to …`.
3. Control the views in either of two ways:

**From the panel (recommended):** open **Window → NorthStar Control Room → Open Panel** and dock it next to the Game view.
It has a button for each view (the view on program is highlighted red, occluded views are flagged), a hard cut / smooth
blend toggle, snapshot, mark, the multiview overlay, and the live recording status. It works without Game-view focus or
keyboard shortcuts.

**From the menu:** the same actions are under **Window → NorthStar Control Room**. They live under Unity's built-in Window
menu because a custom top-level menu can be hidden when the macOS menu bar is full.

| Menu shortcut (Cmd/Ctrl+Alt+…) | Action |
|---|---|
| `M` | Show or hide the multiview grid (click a tile to put that view on program) |
| `N` / `B` | Next / previous view |
| `T` | Switch between hard cut and smooth blend |
| `Y` | Save a PNG of every view to `snapshots/` |
| `K` | Add a `mark` event |

**From the Game view keys:** click inside the Game view first. Unity only sends key presses to the game while the Game view has focus.

| Key | Action |
|---|---|
| `1`–`9`, `0` | Put view 1–10 on program |
| `Tab` / `Shift+Tab` | Next / previous view |
| `B` | Switch between hard cut and smooth blend |
| `M` | Show or hide the multiview grid |
| `P` | Save a PNG of every view to `snapshots/` |
| `K` | Add a `mark` event |

The Game view shows the **program** camera, which is what OBS captures. The headset still shows the normal player view.

## Output: one folder per session

In the editor, sessions are saved to `<project>/ControlRoomSessions/<yyyyMMdd_HHmmss>_<player>/`. The folder is
git-ignored and sits outside `Assets`, so Unity does not reimport it. On a Quest build, sessions go to
`Application.persistentDataPath/ControlRoomSessions/`.

| File | Format | Rate | Contents |
|---|---|---|---|
| `telemetry.csv` | wide, fixed columns | 30 Hz | the variables below |
| `views.csv` | long, one row per view | 10 Hz | `view_id, family, on_program, subject_visible, subject_in_frame, subject_distance, occluded_s, pose, fov` |
| `events.csv` | one row per event | when events happen | every UnityEvent plus derived events (see below) |
| `state_changes.csv` | long | 5 Hz | `t_session, frame, kind(S/D), object_path, component, instance_id, field, value` |
| `session.json` | JSON | start and end | player, device, rates, row counts, column list, view list |
| `snapshots/*.png` | PNG | on `P` / `snapshot` command | one image per view |

Set the player name with `PlayerPrefs` key `ControlRoom.PlayerName`, or in the recorder's *Player Name* field.

### `telemetry.csv` variables by category

- **System / session:** `t_session, t_game, frame, dt, fps, time_scale, player_name, scene_active, scenes_loaded, is_loading, load_progress, program_view, program_blending, hmd_present, user_present, display_hz, battery, hand_tracking, active_controller`
- **Kinematic (player):** for each of `head`, `lhand` and `rhand`: `_pos_xyz, _euler_xyz, _rot_q(xyzw), _vel_xyz, _speed, _ang_speed`. Also `rig_pos_xyz, rig_yaw` (locomotion/teleport). `head_ang_speed` also serves as a biometric proxy.
- **Kinematic (boat):** `boat_present, boat_pos_xyz, boat_euler_xyz, boat_world_pos_xyz, boat_world_euler_xyz, boat_heading, boat_vel_xyz, boat_speed, boat_yaw_rate, boat_stopped, sail_speed`
- **Spatial:** `head_in_boat_xyz, head_height, wind_vec_xyz, wind_yaw, wind_pitch, wind_speed, env_profile, ocean_height_at_boat, dist_wheel, dist_harpoon, nearest_npc, dist_nearest_npc, npc_count, npc_angle_to_player`. Per-view line of sight and occlusion are in `views.csv`.
- **Interactive:** `steering, steering_grabbed, steering_override, sail_rope_tight, sail_wind_starboard, sail_dir_xyz, held_count, held_objects, interaction_focus_xyz, joints_grabbed, joints_grabbed_names, harpoon_state`, and for each controller (`l_`, `r_`): `ctrl_connected, trigger, grip, stick_xy, btn_primary, btn_secondary, btn_stick`
- **Biometric (motion-derived proxies):** `head_jitter, lhand_jitter, rhand_jitter, hand_span, head_pitch, head_roll, activity, calibrated_height, seated, comfort_level`. The Quest has no heart-rate or skin sensors, so these motion signals stand in as proxies for arousal, effort and tremor.
- **Game state / narrative:** `task_count, current_tasks, dialogue_speaker, dialogue_text`

Empty cells mean the source does not exist in that scene (for example, there is no boat in `Launch`).

### `events.csv`

- **Every `UnityEvent`** on NorthStar / Meta.Utilities components, hooked by reflection. This covers `Harpoon.OnFire/OnReload/OnRearm/OnGrabbed`, button, lever, crank and slider value changes (logged only when the value changes; bursts within 0.2 s are merged), `DialoguePlayer.OnCompleteSection`, `GameFlowController.SceneChangeComplete`, triggers, and more.
- **Derived events:** `grab_start/grab_end`, `joint_grab_start/joint_grab_end`, `harpoon_state` (Loading→Locking→Armed→Airborne), `task_started/task_ended`, `dialogue_line`, `boat_started/boat_stopped`, `loading_started/loading_finished`, `scene_loaded/scene_unloaded`, `program_cut`, `snapshot`, `mark`, `session_start/session_end`.

### `state_changes.csv`: the rest of the state

The sampler reads every instance field of every in-scope component: public fields, `[SerializeField]` fields and
private runtime state. It also reads the position, rotation and velocity of every non-kinematic Rigidbody. It writes a full
snapshot (`S`) at start, on every scene load and every 10 s, and in between writes only the values that changed (`D`).
To rebuild the full game state at time *t*, take the latest snapshot before *t* and apply the deltas up to *t*.
`session.json` records how many components and fields were tracked.

## Camera views (taxonomy → implementation)

| # | id | Taxonomy family | How it is built |
|---|---|---|---|
| 1 | `fpv_raw` | Raw HMD / FPV | Copies the head pose exactly |
| 2 | `fpv_smoothed` | Smoothed / stabilized FPV | Head position; damped rotation |
| 3 | `fpv_cropped` | Cropped-FOV FPV | 50° FOV, light damping |
| 4 | `fpv_horizon` | Horizon-stabilized FPV | Head direction with roll removed |
| 5 | `over_shoulder` | Over-the-Shoulder | Behind the right shoulder, follows head yaw, pulled in front of walls |
| 6 | `close_orbit` | Close orbit / chase | 1.8 m orbit around the player at 12°/s |
| 7 | `torso_anchored` | Torso-anchored rig | Chest height, follows yaw only, tilted toward the hands |
| 8 | `close_interaction` | Close interaction | Frames the held object, or the space between the hands |
| 9 | `trailing_follow` | Trailing / follow | 38 m behind and 9 m above the boat |
| 10 | `wide_establishing` | Fixed wide / establishing | Fixed relative to the boat, wide shot (default program) |
| 11 | `isometric_tactical` | Isometric / tactical | High 45° / 45° angle from 45 m with a 40° lens. NorthStar draws its ocean only within a fixed radius of each camera, so a true orthographic or far-away camera shows the ship over an empty background. |
| 12 | `dynamic_tracking` | Dynamic pan / tracking | Fixed on the bow; pans smoothly to track the player |
| 13 | `crane_dolly` | Crane / dolly trajectory | 20 s sweep from a low side angle to a high rear angle |
| + | `legacy_*` | Assignment 2 director cameras | Picked up automatically if `Left/Right/PlayerDirectorCamera` exist |

The taxonomy's transitions are implemented in the program switcher:

- **Hard cut:** `SelectView(id, HardCut)`.
- **Spline / visibility-preserving transition:** `SmoothBlend` interpolates position, rotation and FOV over 0.75 s.
- **Occlusion recovery:** the player-relative cameras are pulled in front of geometry with a sphere cast. Every view also reports `subject_visible` and `occluded_s`, so the director (Task 3) can cut away from a blocked view.

Each view renders to a 640×360 RenderTexture at 15 fps, with render times staggered across views. The program camera
renders every frame. All of these values can be changed on the `SpectatorCameraRig` component. Spectator cameras have
`allowXRRendering = false`, so they never draw into the headset.

## Live interface for the Task 2 dashboard

- **Telemetry out:** UDP to `127.0.0.1:9050`. Each datagram is a JSON object. `{"type":"telemetry", …all telemetry.csv columns…}` is sent at 10 Hz, and `{"type":"event","t_session":…,"source":…,"event":…,"args":…}` is sent for each event.
- **Commands in:** UDP to `127.0.0.1:9051`, sent as plain text: `select <view_id> [cut|blend]`, `snapshot [view_id]`, `mark <text>`.
- **In Unity:** use `SpectatorCameraRig.Instance.Views[i].Monitor`, which is the RenderTexture for each view, along with `SelectView()`, `ProgramChanged` and `ExhaustiveTelemetryRecorder.Instance.LatestRow`.

Quick test from a terminal, with Play running:

```bash
echo -n "select over_shoulder blend" | nc -u -w0 127.0.0.1 9051
```

## Coordinate frames (NorthStar's "fake" boat movement)

NorthStar keeps the boat, and the player on it, at a fixed spot for physics and gameplay. `FakeMovement` moves them to
their real world position only for rendering, then moves them back at the end of each frame. The control room handles
this as follows:

- **Logs:** positions in `telemetry.csv` and `views.csv` (head, hands, boat transform, view positions) are in that fixed
  **physics frame**, the same frame the game's own scripts see. The boat's travelled position is logged separately as
  `boat_world_pos_*` / `boat_world_euler_*`.
- **Cameras:** spectator cameras compute their poses and occlusion raycasts in the physics frame. Just before rendering,
  the same offset `FakeMovement` applies is added to them (`OnBeginContextRendering`), so they film the boat where it is
  actually drawn.

## Known limits

- Quest hardware testing is still pending, per the hardware checkout policy. On standalone Quest, 14 extra cameras are expensive, so lower `Monitor Fps` / resolution or disable views for on-device runs. The control room is intended to run over Link with the editor.
- Biometric data consists of motion-derived proxies only. Eye and face tracking would need a Quest Pro and permissions.
- Without a headset the player rig sits at floor height. This puts `torso_anchored` below the deck and points `close_interaction` at the floor; with a Quest connected over Link they frame correctly.
- Camera offsets are tuned for the FreeSailing boat. Other beats may need tweaks on the `SpectatorCameraRig` view list in the Inspector.
