# Unity 2022.3 for DeepSeek Harness

Drive a running **Unity 2022.3.62f3** Editor from the harness: read and edit the
scene, run Play mode, capture screenshots, read the console, build players, and
execute C# inside the Editor process — all as ordinary agent tools.

Unlike editor integrations that only support Unity 6, this plugin ships its own
bridge. It works on stock Unity 2022.3 with no extra packages, no network access,
and no domain reloads for `unity_eval`.

## Requirements

| | |
|---|---|
| Unity Editor | **2022.3.62f3** (international build) is the reference version. The bridge only reports the Editor version (`isRequiredUnityVersion`); other 2022.3 patches (2022.3.46f1 and 2022.3.62f1c1 were tested) work the same way and are never blocked. |
| Harness | any DSH build that provides the `tools` service |
| Platform | Windows (macOS/Linux editors are detected but untested) |

## Install

```powershell
git clone https://github.com/snakeazh/dsh-unity-2022
plugin_manager install_bundle .\dsh-unity-2022
```

The plugin row `unity-2022` is inserted by `cordis.patch.yml`; the tools appear
in the next turn. To remove it again: `plugin_manager remove_bundle dsh-unity-2022`.

The plugin sits in the [awesome-dsh-plugin](https://github.com/awesome-dsh-plugin/awesome-dsh-plugin/pull/11083)
review queue, so once the entry lands you can also install it from the DSH
plugin directory with one click.

## Using it on other projects

The plugin lives in the **profile** (one copy for the whole harness) while the bridge
package lives in the **project** (one copy per project), so the same tools drive any
number of Unity projects:

1. **Give a project the bridge** — pick one:
   - `unity_open { "projectPath": "D:\\Work\\OtherGame" }` installs, launches and waits;
   - `unity_install { "projectPath": "..." }` installs the package without launching
     (the right call when the package should be committed to the repository);
   - or copy the plugin's `unity-package/` folder to `<project>/Packages/com.dsh.unity-bridge`.
   `Packages/com.dsh.unity-bridge` is an **embedded package**: once committed, every
   teammate who opens that project gets a bridge automatically, with no tool call.
2. **Route a request to the right Editor** (in order of precedence):
   - `projectPath` on the call;
   - `projectPath` in the plugin config (the default project);
   - the calling agent's working directory inside a project — that project is chosen;
   - only one Editor running — it is used.
   With several Editors alive and no `projectPath`, the call fails with
   `ambiguous_editor` and lists the reachable projects.
3. **Project version**: `unity_open` picks the Hub installation that matches the
   project's `ProjectVersion.txt` (all three local 2022.3 builds are candidates) and
   falls back to the closest 2022.3 build. A version that differs from the reference
   build is reported, never enforced.

## Publishing to a plugin market

A market is just "one index JSON at some URL" plus "the installable packages that index
points at", so publishing means publishing the package and hosting the index. Do one or
both paths.

### 1. Publish the package to npm

```powershell
npm login
npm publish            # package name: dsh-unity-2022; publishConfig.access is already public
npm pack               # to self-test first: produces a .tgz identical to the published files
```

The `files` whitelist already carries everything at runtime (`index.js`, `lib/`,
`unity-package/`, `locale/`, the icon, the patch, both READMEs). `plugin_manager
install_bundle <some .tgz>` verifies that "the publish artifact actually installs" —
done here with `dsh-unity-2022-0.1.0.tgz` (24 files, 61 KB).

> If the unscoped name is taken on npm, change `name` in `package.json` and in
> `cordis.patch.yml` (both must match), then update `name` and `versions[].source` in
> `market/index.json` too.

### 2a. Get listed in the community catalogue

The market UI (`dshmarket`) and its site read the curated list
[awesome-dsh-plugin](https://github.com/awesome-dsh-plugin/awesome-dsh-plugin): open a PR
there that adds one entry for this plugin. Their CI refreshes `plugins.json` daily and the
market plus DSH's Discover tab pick it up automatically (usually within a day). This is the
shortest path to "strangers can search for it".

### 2b. Host your own index (format 1)

`market/index.json` is a format-1 index, already validated against DSH's own parser:

```powershell
# local preview (DSH requires absolute URLs, and its market list tool only reads
# index sources the user has configured)
node tools/serve-market.cjs        # serves the repository root on http://127.0.0.1:8199/
```

Put that folder (`index.json` + `icon.svg`) on any HTTPS address — GitHub Pages, jsDelivr
(pairs well with an npm-published catalog package), or your own domain — then add the
`index.json` URL under **Settings → Plugins → Discover → Index sources** in DSH. Anyone
running that DSH can then find and one-click install it.

Display metadata an entry may carry (all optional; `en` is required, `zh` may be added):

| Field | Meaning |
|---|---|
| `title` / `description` | localized text, a string or `{ "en": ..., "zh": ... }` |
| `icon` | path relative to `index.json`, an HTTPS URL, or a base64 `data:` URL of at most 32 KiB decoded |
| `metadata.keywords` | free-form search keywords |
| `metadata.category` / `metadata.labels` | ids that must first be declared in the index root's `metadata.categories` / `metadata.labels` |
| `versions[].source` | `{ "type": "npm" }` installs `name@version`; `{ "type": "git", "url": "...", "commit": "<40-hex sha>", "path": "optional subdir" }` installs straight from a repository instead |
| `versions[].engines.dsh` | DSH version range the market uses for compatibility hints |

### 3. Releasing

Bump `version` in `package.json` → `npm publish` → **append** an entry to `versions` in
`market/index.json` (`publishTimestamp` is Unix seconds, an integer). Keep the old entries;
the market sorts versions by descending SemVer and shows the newest.

## Quick start

```
unity_open    { "projectPath": "D:\\Work\\MyGame" }
unity_status  {}
unity_hierarchy { "includeComponents": true }
unity_create  { "name": "Crate", "primitive": "Cube", "position": [0, 0.5, 0] }
unity_add_component { "target": "Crate", "type": "Rigidbody", "properties": { "m_Mass": 12 } }
unity_screenshot { "view": "game", "width": 1280, "height": 720 }
```

`unity_open` installs the bundled bridge package into `<project>/Packages/com.dsh.unity-bridge`,
launches the matching editor, and waits until the bridge answers. If a project is
already open in an editor, every other tool finds it automatically — pass
`projectPath` only when several editors are running.

## Tools

| Tool | What it does |
|---|---|
| `unity_status` | Editors this plugin can reach, plus version, play mode, compilation state, active scene, selection, installed editor builds. |
| `unity_open` | Install the bridge package and launch the editor; wait for the endpoint. |
| `unity_install` | Install the bridge package into `<project>/Packages` without launching (commit-friendly). |
| `unity_commands` | List the bridge command table (names, parameters, examples, mutating flag). |
| `unity_command` | Run any bridge command with raw parameters — the escape hatch. |
| `unity_hierarchy` | Scene tree with paths, instance ids, active state and (optionally) component names. |
| `unity_find` | Find GameObjects by name substring, component type, tag or layer. |
| `unity_inspect` | Transform, active state and every serialized property of each component, as shown in the Inspector. |
| `unity_create` | Create an empty GameObject or a primitive, with parent, transform, tag, layer and collider options. Undo-able. |
| `unity_add_component` | Add a component and optionally set its properties in the same call. |
| `unity_set_property` | Write serialized properties by property path or plain name (`m_` prefix optional), including nested structs. |
| `unity_set_transform` | Position, rotation, scale and reparenting, world or local. |
| `unity_delete` | Delete a GameObject and its children. Undo-able. |
| `unity_scene` | List / open / save scenes, flush dirty assets. |
| `unity_eval` | Run C# in the Editor process, compiled by the editor's own Roslyn. No domain reload. |
| `unity_play` | Enter, leave, pause, resume or step Play mode. |
| `unity_logs` | Read the console buffer (errors, warnings, logs) with stack traces. |
| `unity_screenshot` | Render a PNG of the scene or the game camera, framed on an object when asked. |
| `unity_menu` | Invoke an editor menu item by path, e.g. `File/Save Project`. |
| `unity_build` | Build a player for a target and report size, duration, errors and warnings. |

Every tool answers with the same envelope, so failures are data rather than
exceptions:

```json
{ "ok": true, "command": "hierarchy", "ms": 2, "instance": { "...": "..." }, "result": { } }
{ "ok": false, "command": "find_objects", "ms": 0, "error": { "code": "not_found", "message": "No GameObject matches 'Crat'" } }
```

Common error codes: `no_editor`, `ambiguous_editor`, `not_found`, `missing_param`,
`invalid_param`, `outside_project`, `compile_error`, `timeout`, `transport_error`.

## Configuration

The plugin reads a `config:` block from its row. Edit the profile patch
(`%USERPROFILE%\.dsh\profiles\<profile>\cordis.patch.yml`) and add an override:

```yaml
- id: unity-2022
  config:
    projectPath: D:/Work/MyGame
    autoLaunch: true
    requestTimeoutMs: 180000
```

| Key | Default | Meaning |
|---|---|---|
| `unityPath` | `""` | Editor executable to launch; empty auto-detects the Hub install matching the project version. |
| `projectPath` | `""` | Default project for tool calls that omit `projectPath`. |
| `requestTimeoutMs` | `120000` | Timeout for one bridge command. |
| `launchTimeoutMs` | `600000` | How long `unity_open` waits for the bridge. |
| `autoLaunch` | `false` | Launch the editor when a tool runs and none answers. |
| `installPackageOnLaunch` | `true` | Copy the bundled bridge package before launching. |

## How it works

```
agent tool ──▶ index.js ──▶ lib/client.mjs ──TCP 127.0.0.1:<ephemeral>──▶ Unity Editor
                          │                                              (com.dsh.unity-bridge)
                          └─ discovery files: %LOCALAPPDATA%\DSHUnityBridge\instances\<sha1>.json
```

* The editor package starts a loopback TCP server on an ephemeral port after every
  domain reload and publishes a per-project discovery file (path hash, pid, port,
  versions). The host re-reads that file for each request, so Play mode, script
  compilation and editor restarts are transparent — a request that hits a closed
  socket is retried once against the new port.
* One request per connection, one JSON line each way. Commands run on the editor's
  main thread; handlers that must not block it (`eval`, `build`) run their own
  threads and are joined back for the reply.
* `unity_eval` writes a tiny C# program under `<project>/Temp/DSHUnityBridge/`,
  compiles it with the editor's bundled Roslyn against the editor and project
  assemblies, loads the DLL and invokes it — so editor-facing code can use
  `UnityEngine`/`UnityEditor` without touching `Assets/` or triggering a reload.

Screenshots render through a temporary camera positioned by the plugin. The editor's
own Scene view camera is not used: its transform only follows the pivot during a
repaint, which yields stale framing in the same frame.

## Bridge command reference

25 commands: `status`, `list_commands`, `hierarchy`, `find_objects`, `inspect_object`,
`create_object`, `delete_object`, `add_component`, `remove_component`, `set_transform`,
`set_property`, `save_scene`, `open_scene`, `save_assets`, `refresh_assets`,
`write_file`, `import_package`, `execute_menu`, `selection`, `undo`, `play`,
`screenshot`, `get_logs`, `clear_logs`, `eval`, `build`.

Call `unity_commands` for live parameters — the editor package is the source of truth.
Inside the editor, `Tools/DSH/Unity Bridge` opens a window with the port, request
count, a restart button and a self test.

## Tests

```powershell
# C# only: compile the bridge against this machine's Unity editor
powershell -NoProfile -ExecutionPolicy Bypass -File tools\compile-bridge.ps1

# end to end over the real bridge (starts the editor when --launch is passed)
node test/smoke.mjs --project <unity project> --launch

# screenshots of a generated scene
node test/visual-check.mjs --project <unity project>
```

`test/harness.mjs` is the glue these scripts use; it talks to `lib/client.mjs`
directly so it does not depend on the harness being loaded.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `no_editor` | Start the editor with `unity_open`, or open the project manually. Check `unity_status` for stale discovery files. |
| `ambiguous_editor` | Several editors are running; pass `projectPath`. |
| Bridge missing after adding the package | Unity must import the package once: focus the editor, wait for compilation, then `unity_status` again. |
| `compile_error` from `unity_eval` | The message carries `file(line,col): error CSxxxx`. Snippets get C# 9, `UnityEngine` and `UnityEditor`. |
| Another Unity version | Informational only: `unity_status` reports `isRequiredUnityVersion: false` plus `expectedUnityVersion`, and everything still works (2022.3.46f1 verified). |
| `editor_blocked` | The editor main thread has not ticked for 15 s, which usually means a modal dialog (save prompt, import progress, license window) is waiting. Click it away in the Unity window, then retry. |
| `bridge_unavailable` | The editor process is up but never published a discovery file, almost always because a native dialog holds the main thread — typically "this project is already open" caused by a stale `<project>/Temp/UnityLockfile` left by a killed editor. Dismiss the dialog, or quit that editor, delete the lock file and call `unity_open` again (`staleLockfile: true` in the result means the lock file already existed before launch). |
| `editor_exited` | The editor process died before the bridge came up; inspect the returned `logFile` (`%LOCALAPPDATA%\Unity\Editor\Editor.log`). |
| `unity_scene` save returns `missing_param` | The open scene has no asset path yet (untitled); pass `path`, for example `Assets/Scenes/Main.unity`. The bridge refuses to save path-less on purpose: Unity would open a modal dialog and freeze the editor. |
| `unity_build` returns `no_scenes` | Build Settings is empty and the open scene is unsaved; pass `scenes: ["Assets/Scenes/Main.unity"]`, or save the scene first. |

> The bridge runs on the editor main thread, so any editor API that opens a modal
> dialog stalls the request. Those commands are guarded: asset folders are created
> through `AssetDatabase`, path-less saves are rejected, and requests fail fast with
> `editor_blocked` when the main thread stops ticking.

## License

MIT
