/**
 * End-to-end smoke test for the unity-2022 plugin against a real Editor.
 *
 *   node test/smoke.mjs --project <path> [--launch] [--keep] [--port N]
 *
 * Without --launch it expects an Editor that already publishes a bridge for the
 * project. Every step is additive and cleans up after itself: it creates a
 * temporary GameObject hierarchy, screenshots it, saves the scene only when it
 * already existed, and deletes what it created.
 */

import path from "node:path";
import process from "node:process";

import {
	BundledPackage,
	call,
	ensureEditor,
	installBridgePackage
} from "./harness.mjs";

const args = process.argv.slice(2);
function flag(name, fallback) {
	const index = args.indexOf(`--${name}`);
	if (index < 0) return fallback;
	const value = args[index + 1];
	return value === undefined || value.startsWith("--") ? true : value;
}

const projectPath = path.resolve(String(flag("project", "") || "."));
const shouldLaunch = flag("launch", false) === true;
const keepArtifacts = flag("keep", false) === true;
const explicitPort = flag("port", undefined);

const results = [];
let instance;

function record(name, detail) {
	results.push({ name, detail });
	console.log(`  ok   ${name}${detail === undefined ? "" : ` — ${detail}`}`);
}

function recordFailure(name, error) {
	results.push({ name, error });
	console.log(`  FAIL ${name} — ${error.code ?? "error"}: ${error.message}`);
}

/** Re-read the discovery file after a domain reload (play mode restarts the bridge). */
async function refresh() {
	const { resolveInstance } = await import("../lib/client.mjs");
	instance = await resolveInstance({ projectPath, port: explicitPort });
	return instance;
}

/** Send one command, transparently surviving a bridge restart. */
async function send(command, params, timeoutMs = 180_000) {
	try {
		return await call(instance, command, params, { timeoutMs });
	} catch (error) {
		if (error?.code !== "transport_error") throw error;
		await refresh();
		return await call(instance, command, params, { timeoutMs });
	}
}

/** Run one bridge command and assert on the result. */
async function step(name, command, params, assert) {
	try {
		const started = Date.now();
		const { result, ms } = await send(command, params);
		await assert?.(result);
		record(name, `${ms} ms round trip, ${Date.now() - started} ms total`);
		return result;
	} catch (error) {
		recordFailure(name, error);
		return undefined;
	}
}

function isObject(value) {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}

async function main() {
	console.log(`project: ${projectPath}`);
	const installed = await installBridgePackage(projectPath, BundledPackage, { force: false });
	console.log(`bridge package: ${installed.target}${installed.changed ? " (installed)" : " (up to date)"}`);

	if (shouldLaunch) {
		const launched = await ensureEditor(
			projectPath,
			{ unityPath: String(flag("unity", "") || "") },
			{ install: false, launchTimeoutMs: 900_000, args: ["-batchmode", "-nographics", "-logFile", path.join(projectPath, "Logs", "smoke-editor.log")] }
		);
		instance = launched.instance;
		console.log(`editor: pid ${launched.pid} (${launched.editor})`);
	} else {
		const { resolveInstance } = await import("../lib/client.mjs");
		instance = await resolveInstance({ projectPath, port: explicitPort });
	}
	console.log(`bridge: ${instance.host}:${instance.port} (Unity ${instance.unityVersion ?? "?"})\n`);

	console.log("commands");

	const status = await step("status", "status", {}, (result) => {
		if (!isObject(result)) throw new Error("status did not return an object");
		if (result.isRequiredUnityVersion !== true) throw new Error(`editor is Unity ${result.unityVersion}, the bridge requires 2022.3.62f3`);
	});
	if (status === undefined) {
		console.log("\nCannot continue: the bridge did not answer status.");
		process.exit(1);
	}

	await step("list_commands", "list_commands", {}, (result) => {
		const commands = Array.isArray(result) ? result : result?.commands;
		if (!Array.isArray(commands)) throw new Error("list_commands did not return a command array");
		if (commands.length < 20) throw new Error(`only ${commands.length} commands registered`);
	});

	const before = await step("hierarchy (before)", "hierarchy", { maxDepth: 3 });
	const rootCount = Array.isArray(before?.roots) ? before.roots.length : 0;
	record("hierarchy shape", `${rootCount} root nodes`);

	const created = await step("create_object", "create_object", {
		name: "__DshSmoke",
		primitive: "Cube",
		position: { x: 1.5, y: 2, z: -0.5 }
	});
	const createdPath = created?.path ?? created?.gameObject?.path ?? "__DshSmoke";
	const childPath = `${createdPath}/__DshSmokeChild`;

	await step("create_object (child)", "create_object", {
		name: "__DshSmokeChild",
		parent: createdPath,
		localPosition: [0, 1, 0]
	});

	await step("add_component", "add_component", { target: createdPath, type: "Rigidbody" }, (result) => {
		if (result?.alreadyPresent === true) return;
		const type = result?.component?.type ?? result?.type;
		if (type !== "Rigidbody") throw new Error("add_component did not report a Rigidbody");
	});

	await step("set_property", "set_property", {
		target: createdPath,
		type: "Rigidbody",
		properties: { m_Mass: 3.5, m_UseGravity: false }
	}, (result) => {
		const applied = Array.isArray(result?.applied) ? result.applied : result?.results;
		if (Array.isArray(applied) && applied.some((entry) => entry.ok === false)) {
			throw new Error(`property write failed: ${JSON.stringify(applied)}`);
		}
	});

	await step("set_transform", "set_transform", {
		target: childPath,
		localPosition: { x: 0, y: 2.5, z: 0 },
		localEulerAngles: { x: 0, y: 45, z: 0 },
		localScale: { x: 0.5, y: 0.5, z: 0.5 }
	});

	await step("inspect_object", "inspect_object", { target: createdPath, budget: 120 }, (result) => {
		const components = result?.components;
		if (!Array.isArray(components) || components.length === 0) throw new Error("inspect_object returned no components");
		if (!components.some((component) => component.type === "Rigidbody")) throw new Error("Rigidbody missing from inspect_object");
	});

	const found = await step("find_objects", "find_objects", { name: "__DshSmoke", limit: 10 }, (result) => {
		if (!Array.isArray(result?.objects) || result.objects.length === 0) throw new Error("find_objects found nothing");
	});
	record("find_objects matches", `${found?.count ?? 0}`);

	await step("screenshot", "screenshot", { width: 480, height: 270 }, (result) => {
		if (typeof result?.path !== "string" || result.path.length === 0) throw new Error("screenshot returned no path");
	});

	await step("get_logs", "get_logs", { limit: 20 }, (result) => {
		if (!Array.isArray(result?.entries) && !Array.isArray(result)) throw new Error("get_logs returned no entries array");
	});

	const evaluated = await step("eval (expression)", "eval", { code: "Application.unityVersion", mode: "expression" }, (result) => {
		const value = result !== null && typeof result === "object" && "result" in result ? result.result : result;
		if (value !== "2022.3.62f3") throw new Error(`eval returned ${JSON.stringify(value)}`);
	});
	record("eval value", JSON.stringify(evaluated?.result ?? evaluated));

	await step("eval (statements)", "eval", {
		mode: "statements",
		code: "int total = 0;\nforeach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()) total += go.transform.childCount;\nreturn total;"
	});

	await step("play (enter)", "play", { action: "play" });
	await new Promise((resolve) => setTimeout(resolve, 2500));
	await step("play (stop)", "play", { action: "stop" });
	// Leaving play mode also reloads the domain, so poll for the fresh bridge.
	try {
		const deadline = Date.now() + 30_000;
		let state;
		while (Date.now() < deadline) {
			state = (await send("status", {})).result;
			if (state.isPlaying === false) break;
			await new Promise((resolve) => setTimeout(resolve, 500));
		}
		if (state?.isPlaying === false) record("play mode exited");
		else recordFailure("play mode exited", new Error("Editor still reports isPlaying=true"));
	} catch (error) {
		recordFailure("play mode exited", error);
	}

	await step("save_assets", "save_assets", {});

	await step("delete_object (child)", "delete_object", { target: childPath });
	await step("delete_object", "delete_object", { target: createdPath });

	if (!keepArtifacts) {
		await step("logs (after)", "get_logs", { level: "error", limit: 10 }, (result) => {
			const entries = Array.isArray(result) ? result : result?.entries;
			if (Array.isArray(entries) && entries.length > 0) {
				record("console errors", `${entries.length} error entries (inspect manually)`);
			}
		});
	}

	const failed = results.filter((entry) => entry.error !== undefined);
	console.log(`\n${results.length - failed.length}/${results.length} steps passed`);
	if (failed.length > 0) {
		for (const entry of failed) console.log(`  - ${entry.name}: ${entry.error.code ?? ""} ${entry.error.message}`);
		process.exitCode = 1;
	}
}

await main();
