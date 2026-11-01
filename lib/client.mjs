/**
 * Transport + discovery layer for the DSH Unity bridge.
 *
 * Pure Node (no harness imports) so it can be used from the plugin and from a
 * standalone smoke test. The Unity side publishes one JSON file per running
 * Editor under %LOCALAPPDATA%\DSHUnityBridge\instances; every request re-reads
 * that file, which makes domain reloads (new port) transparent to callers.
 */

import { spawn } from "node:child_process";
import { constants as fsConstants } from "node:fs";
import fs from "node:fs/promises";
import net from "node:net";
import os from "node:os";
import path from "node:path";

export const PROTOCOL_VERSION = "1";
export const DEFAULT_TIMEOUT_MS = 120_000;
export const DEFAULT_HOST = "127.0.0.1";

/** Error carrying the bridge's machine-readable failure code. */
export class BridgeError extends Error {
	constructor(code, message, details) {
		super(message);
		this.name = "BridgeError";
		this.code = code;
		this.details = details;
	}
}

//#region discovery

/** `%LOCALAPPDATA%\DSHUnityBridge`. */
export function bridgeRoot() {
	const base = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
	return path.join(base, "DSHUnityBridge");
}

/** Directory holding one discovery file per running Editor. */
export function instancesDir() {
	return path.join(bridgeRoot(), "instances");
}

/** Forward slashes, no trailing separator, lower case: the bridge's key form. */
export function normalizePath(value) {
	if (typeof value !== "string") return "";
	return value.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
}

/** Best-effort liveness check for a pid from another process. */
export function isProcessAlive(pid) {
	if (!Number.isInteger(pid) || pid <= 0) return false;
	try {
		process.kill(pid, 0);
		return true;
	} catch (error) {
		return error?.code === "EPERM";
	}
}

function parseInstanceFile(text, file) {
	let data;
	try {
		data = JSON.parse(text);
	} catch {
		return undefined;
	}
	if (!data || typeof data !== "object") return undefined;
	const port = Number(data.port);
	if (!Number.isInteger(port) || port <= 0 || port > 65_535) return undefined;
	return {
		file,
		projectPath: typeof data.projectPath === "string" ? data.projectPath : undefined,
		projectName: typeof data.projectName === "string" ? data.projectName : undefined,
		unityVersion: typeof data.unityVersion === "string" ? data.unityVersion : undefined,
		bridgeVersion: typeof data.bridgeVersion === "string" ? data.bridgeVersion : undefined,
		protocol: typeof data.protocol === "string" ? data.protocol : undefined,
		pid: Number(data.pid) || undefined,
		host: typeof data.host === "string" && data.host.length > 0 ? data.host : DEFAULT_HOST,
		port,
		publishedAt: typeof data.publishedAt === "string" ? data.publishedAt : undefined
	};
}

/**
 * Every discovery file, newest first, annotated with liveness.
 * Stale files (dead pid) are reported but never selected.
 */
export async function listInstances() {
	let names;
	try {
		names = await fs.readdir(instancesDir());
	} catch {
		return [];
	}
	const instances = [];
	for (const name of names) {
		if (!name.endsWith(".json")) continue;
		const file = path.join(instancesDir(), name);
		let text;
		try {
			text = await fs.readFile(file, "utf8");
		} catch {
			continue;
		}
		const instance = parseInstanceFile(text, file);
		if (instance === undefined) continue;
		instance.alive = isProcessAlive(instance.pid);
		instances.push(instance);
	}
	instances.sort((a, b) => String(b.publishedAt ?? "").localeCompare(String(a.publishedAt ?? "")));
	return instances;
}

/** Remove discovery files whose Editor process is gone. */
export async function pruneInstances() {
	const removed = [];
	for (const instance of await listInstances()) {
		if (instance.alive) continue;
		await fs.rm(instance.file, { force: true }).catch(() => {});
		removed.push(instance.file);
	}
	return removed;
}

function describeInstance(instance) {
	const name = instance.projectName ?? path.basename(instance.projectPath ?? "?");
	return `${name} (Unity ${instance.unityVersion ?? "?"}, pid ${instance.pid ?? "?"}, port ${instance.port})`;
}

/**
 * Pick the Editor to talk to.
 *
 * @param options.projectPath exact project to target (any separator style).
 * @param options.port talk to this port even without a matching discovery file.
 * @param options.cwd fall back to the Editor whose project contains this directory.
 * @param options.allowMultiple return the first of several as `ambiguous`.
 */
export async function resolveInstance(options = {}) {
	const { projectPath, port, cwd = process.cwd(), allowMultiple = false } = options;
	if (port !== undefined && port !== null) {
		const numeric = Number(port);
		if (!Number.isInteger(numeric) || numeric <= 0 || numeric > 65_535) {
			throw new BridgeError("bad_request", `port must be 1..65535, received ${String(port)}.`);
		}
		const all = await listInstances();
		const match = all.find((instance) => instance.port === numeric);
		return match ?? { host: DEFAULT_HOST, port: numeric, alive: true, unknown: true };
	}

	const alive = (await listInstances()).filter((instance) => instance.alive);
	if (alive.length === 0) {
		throw new BridgeError(
			"no_editor",
			"No Unity Editor is currently reachable. Start one with unity_open (the bridge package must be installed in the project), then retry."
		);
	}

	if (projectPath !== undefined && projectPath !== null && String(projectPath).length > 0) {
		const wanted = normalizePath(path.resolve(String(projectPath)));
		const match = alive.find((instance) => normalizePath(instance.projectPath) === wanted);
		if (match === undefined) {
			throw new BridgeError(
				"no_editor",
				`No running Unity Editor has the project ${String(projectPath)} open. Reachable: ${alive.map(describeInstance).join("; ")}.`
			);
		}
		return match;
	}

	const cwdKey = normalizePath(path.resolve(cwd));
	const contained = alive.find((instance) => {
		const key = normalizePath(instance.projectPath);
		return key.length > 0 && (cwdKey === key || cwdKey.startsWith(key + "/"));
	});
	if (contained !== undefined) return contained;
	if (alive.length === 1) return alive[0];
	if (allowMultiple) return alive[0];

	throw new BridgeError(
		"ambiguous_editor",
		`Several Unity Editors are running; pass projectPath (or port) to choose one. Reachable: ${alive.map(describeInstance).join("; ")}.`
	);
}

//#endregion
//#region transport

let nextRequestId = 1;

function sendLine(socket, payload, timeoutMs) {
	return new Promise((resolve, reject) => {
		let buffer = "";
		let settled = false;
		const finish = (error, value) => {
			if (settled) return;
			settled = true;
			clearTimeout(timer);
			socket.destroy();
			if (error !== undefined) reject(error);
			else resolve(value);
		};
		const timer = setTimeout(() => {
			finish(new BridgeError("timeout", `The Unity Editor did not answer within ${timeoutMs} ms.`));
		}, timeoutMs);

		socket.setNoDelay(true);
		socket.setEncoding("utf8");
		socket.once("connect", () => {
			socket.write(payload + "\n");
		});
		socket.on("data", (chunk) => {
			buffer += chunk;
			const end = buffer.indexOf("\n");
			if (end < 0) {
				if (buffer.length > 64 * 1024 * 1024) {
					finish(new BridgeError("protocol_error", "The Unity Editor sent an oversized response."));
				}
				return;
			}
			finish(undefined, buffer.slice(0, end));
		});
		socket.once("error", (error) => {
			finish(
				new BridgeError(
					"transport_error",
					`Cannot talk to the Unity Editor on ${socket.remoteAddress ?? DEFAULT_HOST}:${socket.remotePort ?? "?"}: ${error.message}. Is the Editor still running, and is the DSH bridge package installed?`
				)
			);
		});
		socket.once("close", () => {
			finish(new BridgeError("transport_error", "The Unity Editor closed the connection before answering."));
		});
	});
}

/**
 * Invoke one bridge command.
 *
 * @param instance resolved instance ({host, port}).
 * @param command bridge command name.
 * @param params command parameters object.
 * @returns the command's `result`.
 */
export async function call(instance, command, params = {}, options = {}) {
	const timeoutMs = Number.isInteger(options.timeoutMs) ? options.timeoutMs : DEFAULT_TIMEOUT_MS;
	const id = nextRequestId++;
	const payload = JSON.stringify({ id, command, params: params ?? {}, timeoutMs });
	const socket = net.connect({ host: instance.host ?? DEFAULT_HOST, port: instance.port });
	const line = await sendLine(socket, payload, timeoutMs + 5_000);

	let response;
	try {
		response = JSON.parse(line);
	} catch {
		throw new BridgeError("protocol_error", `The Unity Editor sent malformed JSON: ${line.slice(0, 400)}`);
	}
	if (!response || typeof response !== "object") {
		throw new BridgeError("protocol_error", "The Unity Editor sent an empty response.");
	}
	if (response.ok === true) {
		return { result: response.result, ms: Number(response.ms) || 0 };
	}
	const error = response.error ?? {};
	throw new BridgeError(
		typeof error.code === "string" ? error.code : "command_failed",
		typeof error.message === "string" ? error.message : "The Unity Editor reported an unspecified failure.",
		error.details
	);
}

/** Convenience: resolve an instance and run a read-only command against it. */
export async function probeStatus(instance) {
	const { result, ms } = await call(instance, "status", {});
	return { status: result, ms };
}

//#endregion
//#region projects and editors

/** Read `ProjectSettings/ProjectVersion.txt`; returns undefined when absent. */
export async function readProjectVersion(projectPath) {
	const file = path.join(projectPath, "ProjectSettings", "ProjectVersion.txt");
	let text;
	try {
		text = await fs.readFile(file, "utf8");
	} catch {
		return undefined;
	}
	const match = /m_EditorVersion:\s*(\S+)/.exec(text);
	return match?.[1];
}

/** True when `projectPath` looks like a Unity project (Assets/ + ProjectSettings/). */
export async function isUnityProject(projectPath) {
	const [assets, settings] = await Promise.all([
		fs.stat(path.join(projectPath, "Assets")).then((s) => s.isDirectory()).catch(() => false),
		fs.stat(path.join(projectPath, "ProjectSettings")).then((s) => s.isDirectory()).catch(() => false)
	]);
	return assets && settings;
}

async function exists(target) {
	try {
		await fs.access(target, fsConstants.F_OK);
		return true;
	} catch {
		return false;
	}
}

/**
 * Installed Editors under the Unity Hub root, newest first.
 * @returns {{ version: string, path: string }[]}
 */
export async function detectUnityEditors() {
	const roots = [
		process.env.UNITY_HUB_EDITORS,
		"C:\\Program Files\\Unity\\Hub\\Editor",
		"C:\\Program Files (x86)\\Unity\\Hub\\Editor",
		path.join(process.env.LOCALAPPDATA ?? "", "Unity", "Hub", "Editor"),
		"/Applications/Unity/Hub/Editor",
		path.join(os.homedir(), "Unity", "Hub", "Editor"),
		"/opt/unity/editors"
	].filter((root) => typeof root === "string" && root.length > 0);

	const found = [];
	for (const root of roots) {
		let entries;
		try {
			entries = await fs.readdir(root, { withFileTypes: true });
		} catch {
			continue;
		}
		for (const entry of entries) {
			if (!entry.isDirectory()) continue;
			const executable =
				process.platform === "win32"
					? path.join(root, entry.name, "Editor", "Unity.exe")
					: process.platform === "darwin"
						? path.join(root, entry.name, "Unity.app", "Contents", "MacOS", "Unity")
						: path.join(root, entry.name, "Editor", "Unity");
			if (await exists(executable)) found.push({ version: entry.name, path: executable });
		}
	}
	found.sort((a, b) => compareVersions(b.version, a.version));
	return found;
}

/** Loose dotted-version comparison: "2022.3.62f3" > "2022.3.46f1". */
export function compareVersions(a, b) {
	const split = (value) => String(value ?? "").match(/\d+|[a-z]+/gi)?.map((part) => (/^\d+$/.test(part) ? Number(part) : part)) ?? [];
	const left = split(a);
	const right = split(b);
	for (let index = 0; index < Math.max(left.length, right.length); index += 1) {
		const l = left[index];
		const r = right[index];
		if (l === r) continue;
		if (l === undefined) return -1;
		if (r === undefined) return 1;
		if (typeof l === "number" && typeof r === "number") return l - r;
		return String(l).localeCompare(String(r));
	}
	return 0;
}

/**
 * Pick the Editor executable to launch: an explicit path wins, then the exact
 * project version, then the configured preference, then the newest install.
 */
export async function pickEditor(projectPath, preferredPath) {
	if (typeof preferredPath === "string" && preferredPath.length > 0 && (await exists(preferredPath))) {
		return { version: "configured", path: preferredPath };
	}
	const wanted = projectPath === undefined ? undefined : await readProjectVersion(projectPath);
	const editors = await detectUnityEditors();
	if (editors.length === 0) {
		throw new BridgeError(
			"no_editor_install",
			"No Unity Editor installation was found. Install Unity 2022.3.62f3, or set the plugin's unityPath config to the Unity executable."
		);
	}
	if (wanted !== undefined) {
		const exact = editors.find((editor) => editor.version === wanted);
		if (exact !== undefined) return exact;
	}
	return editors[0];
}

/** Name of the embedded package folder inside a project. */
export const PACKAGE_NAME = "com.dsh.unity-bridge";

/** Path the bridge package occupies inside a project. */
export function packageTargetDir(projectPath) {
	return path.join(projectPath, "Packages", PACKAGE_NAME);
}

/**
 * Copy the bundled bridge package into `<project>/Packages/com.dsh.unity-bridge`
 * (an embedded package: Unity compiles it without touching the project manifest).
 *
 * @returns {{ target: string, changed: boolean, version: string|undefined, reason?: string }}
 */
export async function installBridgePackage(projectPath, sourceDir, options = {}) {
	if (!(await isUnityProject(projectPath))) {
		throw new BridgeError("not_a_project", `${projectPath} is not a Unity project (Assets/ and ProjectSettings/ are required).`);
	}
	const target = packageTargetDir(projectPath);
	const source = path.resolve(sourceDir);
	if (normalizePath(source) === normalizePath(target)) return { target, changed: false };

	const version = await fs.readFile(path.join(source, "package.json"), "utf8").then((text) => JSON.parse(text).version).catch(() => undefined);

	let changed = true;
	let reason = "missing";
	if (!options.force) {
		const installed = await fs
			.readFile(path.join(target, "package.json"), "utf8")
			.then((text) => JSON.parse(text).version)
			.catch(() => undefined);
		if (installed !== undefined && version !== undefined && compareVersions(installed, version) === 0) {
			// Same version: only re-copy when the bundled sources are newer than the installed
			// copy, so editing the package during development does not need `force`.
			const [newestSource, newestTarget] = await Promise.all([newestMtime(source), newestMtime(target)]);
			if (newestSource !== undefined && newestTarget !== undefined && newestSource <= newestTarget) {
				changed = false;
				reason = "up-to-date";
			} else {
				reason = "stale";
			}
		} else {
			reason = installed === undefined ? "missing" : "version";
		}
	} else {
		reason = "forced";
	}
	if (changed) {
		await fs.rm(target, { recursive: true, force: true });
		await fs.mkdir(path.dirname(target), { recursive: true });
		await fs.cp(source, target, { recursive: true });
	}
	return { target, changed, version, reason };
}

/** Newest modification time under a directory, ignoring Unity's own PackageCache noise. */
async function newestMtime(dir) {
	let newest;
	async function walk(current) {
		const entries = await fs.readdir(current, { withFileTypes: true }).catch(() => []);
		for (const entry of entries) {
			const full = path.join(current, entry.name);
			if (entry.isDirectory()) {
				await walk(full);
				continue;
			}
			const stat = await fs.stat(full).catch(() => undefined);
			if (stat !== undefined && (newest === undefined || stat.mtimeMs > newest)) newest = stat.mtimeMs;
		}
	}
	await walk(dir);
	return newest;
}

/**
 * Launch the Editor for a project (detached: the harness does not own the process).
 * @returns {{ pid: number|undefined, editor: string, projectPath: string }}
 */
export async function launchEditor(projectPath, options = {}) {
	if (!(await isUnityProject(projectPath))) {
		throw new BridgeError("not_a_project", `${projectPath} is not a Unity project (Assets/ and ProjectSettings/ are required).`);
	}
	const editor = await pickEditor(projectPath, options.unityPath);
	const args = ["-projectPath", projectPath, ...(options.args ?? [])];
	// A lock left behind by a killed Editor makes the next launch stop at a native
	// "project is already open" dialog: the process runs, the main thread never ticks and
	// the bridge never publishes. Report it so the caller can explain the timeout.
	const lockfile = path.join(projectPath, "Temp", "UnityLockfile");
	const locked = await fs.stat(lockfile).then(() => true, () => false);
	const child = spawn(editor.path, args, { detached: true, stdio: "ignore", windowsHide: false });
	child.unref();
	return { pid: child.pid, editor: editor.path, version: editor.version, projectPath, args, lockfile: path.join(lockfile), locked };
}

/**
 * Poll discovery until an Editor for `projectPath` answers, or the timeout expires.
 * @returns the resolved instance, ready for `call`.
 */
export async function waitForEditor(projectPath, options = {}) {
	const timeoutMs = Number.isInteger(options.timeoutMs) ? options.timeoutMs : 300_000;
	const intervalMs = Number.isInteger(options.intervalMs) ? options.intervalMs : 500;
	const deadline = Date.now() + timeoutMs;
	let lastError;
	while (Date.now() < deadline) {
		try {
			const instance = await resolveInstance({ projectPath });
			await probeStatus(instance);
			return instance;
		} catch (error) {
			lastError = error;
		}
		if (options.pid !== undefined && !isProcessAlive(options.pid)) {
			throw new BridgeError(
				"editor_exited",
				`The Unity Editor (pid ${options.pid}) exited before publishing a bridge for ${projectPath}. Check ${editorLogPath()}${lastError === undefined ? "" : ` (last error: ${lastError.message})`}.`,
				{ projectPath, pid: options.pid, logFile: editorLogPath() }
			);
		}
		await new Promise((resolve) => setTimeout(resolve, intervalMs));
	}
	const seconds = Math.round(timeoutMs / 1000);
	if (options.pid !== undefined) {
		throw new BridgeError(
			"bridge_unavailable",
			`The Unity Editor (pid ${options.pid}) is running but published no bridge for ${projectPath} within ${seconds}s. A native dialog is probably blocking the main thread — usually "project is already open" from a stale Temp/UnityLockfile, or an import/save prompt. Close the dialog, or close the Editor, delete ${path.join(projectPath, "Temp", "UnityLockfile")}, and call unity_open again.${lastError === undefined ? "" : ` (last error: ${lastError.message})`}`,
			{ projectPath, pid: options.pid, logFile: editorLogPath() }
		);
	}
	throw new BridgeError(
		"timeout",
		`Unity did not publish a bridge for ${projectPath} within ${seconds}s${lastError === undefined ? "" : ` (last error: ${lastError.message})`}.`
	);
}

/** Unity writes one Editor.log per user; it is the first place to look after a failed launch. */
export function editorLogPath() {
	if (process.platform === "win32") return path.join(process.env.LOCALAPPDATA ?? "C:\\Users\\Default\\AppData\\Local", "Unity", "Editor", "Editor.log");
	if (process.platform === "darwin") return path.join(process.env.HOME ?? "", "Library", "Logs", "Unity", "Editor.log");
	return path.join(process.env.HOME ?? "", ".config", "unity3d", "Editor.log");
}

//#endregion
