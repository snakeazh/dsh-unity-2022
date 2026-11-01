/**
 * Visual check: builds a tiny lit scene through the bridge, captures the Scene and Game
 * views, and prints where the PNGs landed. Meant to be looked at by a human.
 *
 *   node test/visual-check.mjs --project <path>
 */

import path from "node:path";
import process from "node:process";

import { call, resolveInstance } from "./harness.mjs";

const args = process.argv.slice(2);
const index = args.indexOf("--project");
const projectPath = path.resolve(index < 0 ? "." : String(args[index + 1]));

const instance = await resolveInstance({ projectPath });
console.log(`bridge: ${instance.host}:${instance.port}`);

const setup = `// Idempotent: drop anything a previous run created before rebuilding the demo scene.
string[] previous = { "Main Camera", "Sun", "__DshShot", "__DshShotBall" };
foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
{
    if (Array.IndexOf(previous, root.name) >= 0) Object.DestroyImmediate(root);
}

var cameraGo = new GameObject("Main Camera");
cameraGo.tag = "MainCamera";
var camera = cameraGo.AddComponent<Camera>();
camera.clearFlags = CameraClearFlags.Skybox;
cameraGo.transform.position = new Vector3(0f, 1.6f, -4.5f);
cameraGo.transform.LookAt(new Vector3(0f, 0.5f, 0f));

var lightGo = new GameObject("Sun");
var sun = lightGo.AddComponent<Light>();
sun.type = LightType.Directional;
sun.intensity = 1.15f;
lightGo.transform.rotation = Quaternion.Euler(48f, -34f, 0f);

var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
cube.name = "__DshShot";
cube.transform.position = new Vector3(0f, 0.5f, 0f);
cube.transform.localScale = new Vector3(1.6f, 1f, 1.6f);
var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
sphere.name = "__DshShotBall";
sphere.transform.position = new Vector3(1.6f, 0.5f, 0.6f);
return UnityEngine.SceneManagement.SceneManager.GetActiveScene().rootCount;`;

const built = await call(instance, "eval", { code: setup, mode: "statements" }, { timeoutMs: 180_000 });
console.log("setup:", JSON.stringify(built.result));

for (const [view, target] of [["game", undefined], ["scene", "__DshShot"]]) {
	const params = { view, width: 640, height: 360 };
	if (target !== undefined) params.target = target;
	const shot = await call(instance, "screenshot", params, { timeoutMs: 120_000 });
	console.log(`${view}: ${shot.result.path} (${shot.result.bytes} bytes, camera ${shot.result.camera}, temp=${shot.result.temporaryCamera})`);
}
