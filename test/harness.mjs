/**
 * Test-only glue: the pieces smoke.mjs needs, without importing the plugin entry
 * point (which imports the harness packages and only resolves inside the harness).
 */

import path from "node:path";
import { fileURLToPath } from "node:url";

import {
	BridgeError,
	call,
	installBridgePackage,
	isUnityProject,
	launchEditor,
	pickEditor,
	probeStatus,
	resolveInstance,
	waitForEditor
} from "../lib/client.mjs";

const PLUGIN_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));

/** The bridge package bundled with the plugin. */
export const BundledPackage = path.join(PLUGIN_ROOT, "unity-package");

export { BridgeError, call, installBridgePackage, probeStatus, resolveInstance, waitForEditor };

/**
 * Install the bridge package (unless `install: false`), launch the Editor and wait
 * until it answers. Returns the same shape the plugin's unity_open tool returns.
 */
export async function ensureEditor(projectPath, settings = {}, options = {}) {
	const resolved = path.resolve(projectPath);
	if (!(await isUnityProject(resolved))) {
		throw new BridgeError("not_a_project", `${resolved} is not a Unity project: Assets/ and ProjectSettings/ are missing.`);
	}
	const installed = options.install === false ? undefined : await installBridgePackage(resolved, BundledPackage, { force: options.force === true });
	const launched = await launchEditor(resolved, { unityPath: settings.unityPath, args: options.args });
	const instance = options.wait === false ? undefined : await waitForEditor(resolved, { timeoutMs: options.launchTimeoutMs ?? 600_000 });
	return { ...launched, installed, instance };
}

export { pickEditor };
