/**
 * DSH plugin: drive the Unity 2022.3 Editor from the harness.
 *
 * Every tool is a thin wrapper over the loopback bridge that the Unity editor
 * package `com.dsh.unity-bridge` hosts inside the Editor process. The bridge
 * publishes a discovery file per running Editor, so these tools keep working
 * across domain reloads without any reconnect logic.
 *
 * Supported editor: Unity 2022.3.62f3 (the bridge refuses to run elsewhere).
 */

import path from "node:path";
import { fileURLToPath } from "node:url";

import {
	BridgeError,
	PACKAGE_NAME,
	call,
	detectUnityEditors,
	installBridgePackage,
	isUnityProject,
	launchEditor,
	listInstances,
	normalizePath,
	packageTargetDir,
	pruneInstances,
	readProjectVersion,
	resolveInstance,
	waitForEditor
} from "./lib/client.mjs";

export const name = "unity-2022";

export const inject = ["tools"];

const PLUGIN_ROOT = path.dirname(fileURLToPath(import.meta.url));
const BUNDLED_PACKAGE = path.join(PLUGIN_ROOT, "unity-package");
const REQUIRED_UNITY_VERSION = "2022.3.62f3";

/**
 * Settings shape, supplied by the plugin row's `config:` block in a Cordis
 * patch (see README). Every key is optional; the defaults below apply.
 *
 *   unityPath             absolute path of the Unity executable to launch ("" = auto-detect
 *                         the Hub install whose version matches the project)
 *   projectPath           default project for tool calls that omit projectPath
 *   requestTimeoutMs      timeout for one bridge command (120000)
 *   launchTimeoutMs       how long unity_open waits for the bridge (600000)
 *   autoLaunch            launch the Editor when a tool runs and none answers (false)
 *   installPackageOnLaunch copy the bundled bridge package before launching (true)
 */
const DEFAULT_SETTINGS = {
	unityPath: "",
	projectPath: "",
	requestTimeoutMs: 120_000,
	launchTimeoutMs: 600_000,
	autoLaunch: false,
	installPackageOnLaunch: true
};

/**
 * Turn the compact parameter notation used below into the JSON Schema that
 * `ctx.tools.register` expects. `type: "json"` means "any JSON value".
 */
function toJsonSchema(parameters) {
	const properties = {};
	const required = [];
	for (const [key, spec] of Object.entries(parameters ?? {})) {
		const property = { description: spec.description };
		if (spec.type !== "json") property.type = spec.type;
		if (spec.enum !== undefined) property.enum = spec.enum;
		if (spec.items !== undefined) property.items = spec.items;
		properties[key] = property;
		if (spec.required === true) required.push(key);
	}
	const schema = { type: "object", properties, additionalProperties: false };
	if (required.length > 0) schema.required = required;
	return schema;
}

/**
 * Register one tool. Tools return a plain JSON envelope; the harness surface is
 * a string, so it is serialised here once instead of in every tool.
 */
const TEXT_OUTPUT = {
	schema: { type: "string" },
	render: (_args, value) => [{ type: "text", text: typeof value === "string" ? value : JSON.stringify(value, null, 2) }]
};

function defineTool(spec) {
	return {
		name: spec.name,
		description: spec.description,
		parameters: toJsonSchema(spec.parameters),
		output: TEXT_OUTPUT,
		execute: async (args, exec) => {
			const value = await spec.execute(args, exec);
			return typeof value === "string" ? value : JSON.stringify(value, null, 2);
		}
	};
}

/** Read a config value whether the Loader hands over a plain value or a reactive cell. */
function readValue(config, key, fallback) {
	const value = config?.[key];
	if (value !== null && typeof value === "object" && typeof value.get === "function") {
		const inner = value.get();
		return inner === undefined || inner === null || inner === "" ? fallback : inner;
	}
	return value === undefined || value === null || value === "" ? fallback : value;
}

function readSettings(config) {
	const settings = {};
	for (const [key, fallback] of Object.entries(DEFAULT_SETTINGS)) {
		settings[key] = readValue(config, key, fallback);
	}
	return settings;
}

/** The calling agent's session cwd, else the harness process cwd. */
function cwdOf(exec) {
	return exec?.agent?.session?.header?.cwd ?? process.cwd();
}

function instanceSummary(instance) {
	return {
		projectPath: instance.projectPath,
		projectName: instance.projectName,
		unityVersion: instance.unityVersion,
		bridgeVersion: instance.bridgeVersion,
		protocol: instance.protocol,
		pid: instance.pid,
		host: instance.host,
		port: instance.port
	};
}

function success(command, instance, result, ms) {
	return { ok: true, command, ms, instance: instanceSummary(instance), result };
}

function failure(command, error, instance) {
	return {
		ok: false,
		command,
		ms: 0,
		...(instance === undefined ? {} : { instance: instanceSummary(instance) }),
		error: {
			code: error instanceof BridgeError ? error.code : "internal_error",
			message: error instanceof Error ? error.message : String(error),
			...(error instanceof BridgeError && error.details !== undefined ? { details: error.details } : {})
		}
	};
}

/** Resolve the target Editor, optionally launching it first. */
async function targetOf(args, exec, settings) {
	const wanted = args?.projectPath || settings.projectPath || undefined;
	try {
		return await resolveInstance({ projectPath: wanted, port: args?.port, cwd: cwdOf(exec) });
	} catch (error) {
		if (!settings.autoLaunch || wanted === undefined || error?.code !== "no_editor") throw error;
		await ensureEditor(wanted, settings, { install: settings.installPackageOnLaunch });
		return await resolveInstance({ projectPath: wanted, cwd: cwdOf(exec) });
	}
}

/** Install the bundled package (once) and launch the Editor, then wait for the bridge. */
async function ensureEditor(projectPath, settings, options = {}) {
	const resolved = path.resolve(projectPath);
	if (!(await isUnityProject(resolved))) {
		throw new BridgeError("not_a_project", `${resolved} is not a Unity project: Assets/ and ProjectSettings/ are missing.`);
	}
	let installed;
	if (options.install !== false) {
		installed = await installBridgePackage(resolved, BUNDLED_PACKAGE, { force: options.force === true });
	}
	const launched = await launchEditor(resolved, { unityPath: settings.unityPath, args: options.args });
	const instance =
		options.wait === false
			? undefined
			: await waitForEditor(resolved, { timeoutMs: options.timeoutMs ?? settings.launchTimeoutMs, pid: launched.pid });
	return {
		pid: launched.pid,
		editor: launched.editor,
		version: launched.version,
		projectPath: resolved,
		args: launched.args,
		staleLockfile: launched.locked === true,
		installed,
		instance: instance === undefined ? undefined : instanceSummary(instance)
	};
}

/** True when the Editor restarted (domain reload) rather than rejecting the request. */
function isStaleEndpoint(error) {
	return (
		error instanceof BridgeError &&
		error.code === "transport_error" &&
		/ECONNREFUSED|ECONNRESET|EPIPE|socket hang up/i.test(error.message)
	);
}

/** Run one bridge command, turning failures into a structured envelope. */
async function invoke(args, exec, settings, command, params, options = {}) {
	let instance;
	for (let attempt = 0; ; attempt += 1) {
		try {
			instance = await targetOf(args, exec, settings);
			const { result, ms } = await call(instance, command, params, {
				timeoutMs: options.timeoutMs ?? settings.requestTimeoutMs
			});
			return success(command, instance, result, ms);
		} catch (error) {
			// Play mode and script compilation reload the domain, which restarts the bridge on a
			// fresh port and leaves the cached instance pointing at a closed socket. The request
			// never reached the Editor in that case, so one retry against the new port is safe.
			if (attempt === 0 && isStaleEndpoint(error)) continue;
			return failure(command, error, instance);
		}
	}
}

//#region parameter descriptions

const PROJECT_PATH = {
	type: "string",
	description:
		"Absolute path of the Unity project whose Editor to target. Omit to use the plugin's default project, the Editor owning the current working directory, or the only running Editor."
};

const TARGET = {
	type: "string",
	required: true,
	description: "GameObject to act on: hierarchy path like 'Main Camera/Target', its name, or its instance id (as a string)."
};

const COMPONENT_TYPE = {
	type: "string",
	required: true,
	description:
		"Component type name, e.g. 'Rigidbody', 'BoxCollider', 'AudioSource', 'Camera', or a full name like 'UnityEngine.UI.Text'. A 'Type:index' suffix selects one of several components of the same type."
};

/**
 * Every tool answers with the same envelope: `{ ok, command, ms, instance?,
 * result | error: { code, message, details? } }`. It is serialised by
 * defineTool, so the specs below only need to name the field.
 */
const OUTPUT = TEXT_OUTPUT;

//#endregion
//#region tools

function registerTools(ctx, config) {
	const disposers = [];
	const settings = () => readSettings(config);

	const tool = (spec) => {
		disposers.push(ctx.tools.register(defineTool(spec)));
	};

	tool({
		name: "unity_status",
		description:
			"Report the Unity Editors this plugin can reach and the state of the target one (version, play mode, compilation, active scene, selection). Call this before scene or asset work to confirm which Editor is live; success=false with code no_editor means a project needs unity_open first.",
		parameters: { projectPath: PROJECT_PATH, prune: { type: "boolean", description: "Delete discovery files left behind by Editors that have exited." } },
		output: OUTPUT,
		timeoutMs: 60_000,
		isConcurrencySafe: () => true,
		async execute(args, exec) {
			const current = settings();
			try {
				if (args.prune === true) await pruneInstances();
				const instances = await listInstances();
				const alive = instances.filter((instance) => instance.alive);
				if (alive.length === 0) {
					return {
						ok: false,
						command: "status",
						ms: 0,
						result: { instances: [], stale: instances.length },
						error: {
							code: "no_editor",
							message:
								"No Unity Editor is publishing a bridge. Open the project with unity_open (it installs com.dsh.unity-bridge automatically)."
						}
					};
				}
				const instance = await resolveInstance({ projectPath: args.projectPath || current.projectPath || undefined, cwd: cwdOf(exec) });
				const { result, ms } = await call(instance, "status", {}, { timeoutMs: 60_000 });
				const editors = await detectUnityEditors();
				return {
					ok: true,
					command: "status",
					ms,
					instance: instanceSummary(instance),
					result: {
						...result,
						expectedUnityVersion: REQUIRED_UNITY_VERSION,
						instances: instances.map(instanceSummary),
						staleInstances: instances.length - alive.length,
						installedEditors: editors,
						plugin: {
							defaultProject: current.projectPath || undefined,
							unityPath: current.unityPath || undefined,
							autoLaunch: current.autoLaunch
						}
					}
				};
			} catch (error) {
				return failure("status", error);
			}
		},
		presentCall: () => ({ card: "generic", title: "Check Unity Editor status", kind: "read" })
	});

	tool({
		name: "unity_open",
		description:
			"Open a Unity project in the Editor and wait until the bridge answers. Installs the bundled com.dsh.unity-bridge package into <project>/Packages when needed, launches the Editor matching the project's version (Unity 2022.3.62f3), and returns the discovered endpoint. Use this whenever no Editor is reachable.",
		parameters: {
			projectPath: { ...PROJECT_PATH, required: true, description: "Absolute path of the Unity project to open." },
			wait: { type: "boolean", description: "Wait until the bridge answers before returning. Defaults to true." },
			timeoutMs: { type: "number", description: "Override how long to wait for the Editor, in milliseconds." },
			force: { type: "boolean", description: "Reinstall the bridge package even when the same version is already present." },
			args: { type: "array", items: { type: "string" }, description: "Extra command line arguments passed to the Unity executable." }
		},
		output: OUTPUT,
		timeoutMs: 900_000,
		isConcurrencySafe: () => false,
		async execute(args) {
			const current = settings();
			try {
				const result = await ensureEditor(args.projectPath, current, {
					install: current.installPackageOnLaunch,
					force: args.force === true,
					wait: args.wait !== false,
					args: Array.isArray(args.args) ? args.args : [],
					timeoutMs: args.timeoutMs
				});
				const version = await readProjectVersion(path.resolve(args.projectPath));
				return {
					ok: true,
					command: "open",
					ms: 0,
					...(result.instance === undefined ? {} : { instance: result.instance }),
					result: { ...result, projectVersion: version, expectedUnityVersion: REQUIRED_UNITY_VERSION }
				};
			} catch (error) {
				return failure("open", error);
			}
		},
		presentCall: (args) => ({ card: "generic", title: `Open Unity: ${args.projectPath}`, kind: "execute" })
	});

	tool({
		name: "unity_install",
		description:
			"Install or refresh com.dsh.unity-bridge inside a Unity project without launching the Editor. The package lands in <project>/Packages/com.dsh.unity-bridge, so committing it makes every teammate's Editor publish a bridge automatically. Use force=true to reinstall unconditionally; use unity_open when the Editor should also be started.",
		parameters: {
			projectPath: { ...PROJECT_PATH, required: true, description: "Absolute path of the Unity project that should receive the bridge package." },
			force: { type: "boolean", description: "Reinstall even when the installed copy is up to date." }
		},
		output: OUTPUT,
		timeoutMs: 120_000,
		isConcurrencySafe: () => true,
		async execute(args) {
			const project = path.resolve(String(args.projectPath));
			try {
				const installed = await installBridgePackage(project, BUNDLED_PACKAGE, { force: args.force === true });
				const version = await readProjectVersion(project);
				return {
					ok: true,
					command: "install",
					ms: 0,
					result: {
						...installed,
						packageName: PACKAGE_NAME,
						projectPath: project,
						projectVersion: version,
						expectedUnityVersion: REQUIRED_UNITY_VERSION,
						bundledPackage: BUNDLED_PACKAGE
					}
				};
			} catch (error) {
				return failure("install", error);
			}
		},
		presentCall: (args) => ({ card: "generic", title: `Install Unity bridge: ${args.projectPath}`, kind: "execute" })
	});

	tool({
		name: "unity_commands",
		description:
			"List every command the connected Unity Editor implements, with parameter names, examples and whether it mutates the scene. Read this before unity_command so you pass the right names.",
		parameters: { projectPath: PROJECT_PATH },
		output: OUTPUT,
		timeoutMs: 60_000,
		isConcurrencySafe: () => true,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "list_commands", {});
		},
		presentCall: () => ({ card: "generic", title: "List Unity bridge commands", kind: "read" })
	});

	tool({
		name: "unity_command",
		description:
			"Run any bridge command by name with a raw parameter object. Escape hatch for commands that have no dedicated tool; parameters are passed to the Editor verbatim. Discover names with unity_commands, or prefer the dedicated tools (unity_hierarchy, unity_create, unity_eval, ...) which validate their inputs.",
		parameters: {
			command: { type: "string", required: true, description: "Bridge command name, exactly as unity_commands reports it." },
			params: { type: "json", description: "Parameter object handed to the command; omit for commands without parameters." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 180_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			const params = args.params !== null && typeof args.params === "object" ? args.params : {};
			return await invoke(
				args,
				exec,
				settings(),
				String(args.command),
				params,
				{ timeoutMs: args.command === "eval" ? 180_000 : undefined }
			);
		},
		presentCall: (args) => ({ card: "generic", title: `Unity: ${args.command}`, kind: "execute", rawInput: args.params })
	});

	tool({
		name: "unity_hierarchy",
		description:
			"Read the scene hierarchy as a tree of GameObjects (name, path, active state, optionally components). Use it to orient yourself before editing a scene, then unity_inspect on the nodes you care about.",
		parameters: {
			root: { type: "string", description: "Only walk this subtree, given as a hierarchy path or name." },
			maxDepth: { type: "number", description: "Depth limit; defaults to the bridge's 4." },
			includeComponents: { type: "boolean", description: "Also list component type names per node (heavier)." },
			includeInactive: { type: "boolean", description: "Include inactive GameObjects; defaults to true." },
			maxNodes: { type: "number", description: "Node budget; defaults to the bridge's 3000." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 120_000,
		isConcurrencySafe: () => true,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "hierarchy", {
				root: args.root,
				maxDepth: args.maxDepth,
				includeComponents: args.includeComponents,
				includeInactive: args.includeInactive,
				maxNodes: args.maxNodes
			});
		},
		presentCall: () => ({ card: "generic", title: "Read Unity scene hierarchy", kind: "read" })
	});

	tool({
		name: "unity_find",
		description:
			"Find GameObjects by name substring, component type, tag or layer. Returns matches with hierarchy paths and instance ids you can hand to the other tools.",
		parameters: {
			name: { type: "string", description: "Name substring (case-insensitive)." },
			component: { type: "string", description: "Only GameObjects carrying this component type, e.g. 'Camera' or 'Rigidbody'." },
			tag: { type: "string", description: "Only GameObjects with this tag." },
			layer: { type: "string", description: "Only GameObjects on this layer (name or index)." },
			limit: { type: "number", description: "Maximum matches to return; defaults to 50." },
			includeInactive: { type: "boolean", description: "Include inactive GameObjects; defaults to true." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 120_000,
		isConcurrencySafe: () => true,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "find_objects", {
				name: args.name,
				component: args.component,
				tag: args.tag,
				layer: args.layer,
				limit: args.limit,
				includeInactive: args.includeInactive
			});
		},
		presentCall: () => ({ card: "generic", title: "Find GameObjects in Unity", kind: "read" })
	});

	tool({
		name: "unity_inspect",
		description:
			"Inspect one GameObject: transform, active state and every component with its serialized properties (values as shown in the Inspector). Use the returned property names with unity_set_property.",
		parameters: {
			target: TARGET,
			budget: { type: "number", description: "Maximum serialized properties to return; defaults to the bridge's 200." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 120_000,
		isConcurrencySafe: () => true,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "inspect_object", { target: args.target, budget: args.budget });
		},
		presentCall: (args) => ({ card: "generic", title: `Inspect ${args.target}`, kind: "read" })
	});

	tool({
		name: "unity_create",
		description:
			"Create a GameObject in the open scene, optionally as a primitive with a parent, transform and tag. Undo-able. Returns the new object's path and instance id.",
		parameters: {
			name: { type: "string", required: true, description: "Name for the new GameObject." },
			primitive: {
				type: "string",
				description: "Create a primitive instead of an empty object: Cube, Sphere, Capsule, Cylinder, Plane or Quad."
			},
			parent: { type: "string", description: "Parent GameObject (path or name); omit to create at the scene root." },
			position: { type: "json", description: "World position as {x,y,z} or [x,y,z]." },
			euler: { type: "json", description: "Local rotation in degrees as {x,y,z} or [x,y,z]." },
			scale: { type: "json", description: "Local scale as {x,y,z} or [x,y,z]." },
			tag: { type: "string", description: "Tag to assign, e.g. 'Player'." },
			layer: { type: "string", description: "Layer name or index." },
			active: { type: "boolean", description: "Set inactive when false; defaults to active." },
			collider: { type: "boolean", description: "Keep the collider on a primitive; defaults to true." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 120_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "create_object", {
				name: args.name,
				primitive: args.primitive,
				parent: args.parent,
				position: args.position,
				euler: args.euler,
				scale: args.scale,
				tag: args.tag,
				layer: args.layer,
				active: args.active,
				collider: args.collider
			});
		},
		presentCall: (args) => ({ card: "generic", title: `Create ${args.name}`, kind: "execute" })
	});

	tool({
		name: "unity_add_component",
		description:
			"Add a component to a GameObject and optionally set its properties in the same call (e.g. a Rigidbody with mass). Reports alreadyPresent when a DisallowMultipleComponent type is already attached.",
		parameters: {
			target: TARGET,
			type: COMPONENT_TYPE,
			properties: { type: "json", description: "Object mapping serialized property names to values, e.g. {\"m_Mass\": 5}." },
			enabled: { type: "boolean", description: "Enable the component after adding; defaults to its own default." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 120_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "add_component", {
				target: args.target,
				type: args.type,
				properties: args.properties,
				enabled: args.enabled
			});
		},
		presentCall: (args) => ({ card: "generic", title: `Add ${args.type} to ${args.target}`, kind: "execute" })
	});

	tool({
		name: "unity_set_property",
		description:
			"Set serialized properties on a component, addressed by property path or by plain name (the 'm_' prefix is optional). Accepts nested objects for struct properties. Returns per-key ok/error so partial failures are visible.",
		parameters: {
			target: TARGET,
			type: COMPONENT_TYPE,
			properties: { type: "json", required: true, description: "Object mapping property names/paths to values, e.g. {\"m_LocalScale\": [1,2,1]}." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 120_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "set_property", {
				target: args.target,
				type: args.type,
				properties: args.properties
			});
		},
		presentCall: (args) => ({ card: "generic", title: `Set properties on ${args.target}`, kind: "execute" })
	});

	tool({
		name: "unity_set_transform",
		description:
			"Move, rotate, scale or reparent a GameObject. Vectors accept {x,y,z} or [x,y,z]; rotation accepts a quaternion {x,y,z,w} or degrees via euler. Space defaults to world for position/rotation and local for euler/scale.",
		parameters: {
			target: TARGET,
			position: { type: "json", description: "Position in the selected space, {x,y,z}." },
			localPosition: { type: "json", description: "Explicit local position, {x,y,z}." },
			euler: { type: "json", description: "Euler angles in degrees, {x,y,z}." },
			localEulerAngles: { type: "json", description: "Explicit local euler angles, {x,y,z}." },
			rotation: { type: "json", description: "Quaternion {x,y,z,w}." },
			localScale: { type: "json", description: "Local scale, {x,y,z}." },
			scale: { type: "json", description: "Alias of localScale." },
			parent: { type: "string", description: "New parent (path or name); use an empty string to move to the scene root." },
			space: { type: "string", enum: ["world", "local"], description: "Reference space for position/rotation; defaults to world." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 120_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "set_transform", {
				target: args.target,
				position: args.position,
				localPosition: args.localPosition,
				euler: args.euler,
				localEulerAngles: args.localEulerAngles,
				rotation: args.rotation,
				localScale: args.localScale,
				scale: args.scale,
				parent: args.parent,
				space: args.space
			});
		},
		presentCall: (args) => ({ card: "generic", title: `Transform ${args.target}`, kind: "execute" })
	});

	tool({
		name: "unity_delete",
		description: "Delete a GameObject (and its children) from the open scene. Undo-able.",
		parameters: { target: TARGET, projectPath: PROJECT_PATH },
		output: OUTPUT,
		timeoutMs: 60_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "delete_object", { target: args.target });
		},
		presentCall: (args) => ({ card: "generic", title: `Delete ${args.target}`, kind: "execute" })
	});

	tool({
		name: "unity_scene",
		description:
			"Scene housekeeping: list open scenes, open or add a scene by asset path, save the active scene, or flush dirty assets. Saving writes the .unity file, so make sure the scene is the one you mean.",
		parameters: {
			action: {
				type: "string",
				required: true,
				enum: ["list", "open", "save", "save_assets"],
				description: "Which scene operation to run."
			},
			path: { type: "string", description: "Scene asset path such as 'Assets/Scenes/Main.unity' (required for open)." },
			additive: { type: "boolean", description: "Open additively instead of replacing the open scenes." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 180_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			const current = settings();
			if (args.action === "list") return await invoke(args, exec, current, "status", {});
			if (args.action === "save") return await invoke(args, exec, current, "save_scene", { path: args.path });
			if (args.action === "save_assets") return await invoke(args, exec, current, "save_assets", {});
			if (args.action !== "open") {
				return failure("open_scene", new BridgeError("bad_request", `Unknown action '${args.action}'.`));
			}
			if (typeof args.path !== "string" || args.path.length === 0) {
				return failure("open_scene", new BridgeError("bad_request", "action 'open' requires path, e.g. 'Assets/Scenes/Main.unity'."));
			}
			return await invoke(args, exec, current, "open_scene", { path: args.path, additive: args.additive });
		},
		presentCall: (args) => ({ card: "generic", title: `Unity scene: ${args.action}`, kind: "execute" })
	});

	tool({
		name: "unity_eval",
		description:
			"Execute C# inside the Editor process with full UnityEngine/UnityEditor access and no domain reload. Pass statements ending in `return <expr>;` to get a value back (e.g. `return Application.unityVersion;`). The code is compiled by the editor's own Roslyn and reports compiler diagnostics with line numbers.",
		parameters: {
			code: { type: "string", required: true, description: "C# statements or a single expression; end with `return <expr>;` to produce a value." },
			mode: { type: "string", enum: ["auto", "expression", "statements"], description: "How to wrap the snippet; defaults to auto-detection." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 300_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "eval", { code: args.code, mode: args.mode }, { timeoutMs: 300_000 });
		},
		presentCall: () => ({ card: "generic", title: "Run C# in Unity", kind: "execute" })
	});

	tool({
		name: "unity_play",
		description:
			"Control Play mode: play, stop, pause, resume or step one frame. When entering play mode the call returns as soon as the Editor is playing.",
		parameters: {
			action: { type: "string", required: true, enum: ["play", "stop", "pause", "resume", "step"], description: "Play mode transition." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 180_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "play", { action: args.action });
		},
		presentCall: (args) => ({ card: "generic", title: `Unity play mode: ${args.action}`, kind: "execute" })
	});

	tool({
		name: "unity_logs",
		description:
			"Read the Editor console buffer captured by the bridge (up to 2000 entries, oldest dropped). Filter by level to spot errors after an edit or a play session; pass clear to reset the buffer afterwards.",
		parameters: {
			sinceIndex: { type: "number", description: "Only entries strictly newer than this index (use a previous call's nextIndex)." },
			level: { type: "string", enum: ["all", "error", "warning", "info", "log"], description: "Minimum severity; defaults to all." },
			limit: { type: "number", description: "Maximum entries to return; defaults to the bridge's 200." },
			includeStackTrace: { type: "boolean", description: "Include stack traces for errors." },
			clear: { type: "boolean", description: "Clear the buffer after reading it." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 60_000,
		isConcurrencySafe: () => true,
		async execute(args, exec) {
			const current = settings();
			const envelope = await invoke(args, exec, current, "get_logs", {
				sinceIndex: args.sinceIndex,
				level: args.level,
				limit: args.limit,
				includeStackTrace: args.includeStackTrace
			});
			if (args.clear !== true) return envelope;
			const cleared = await invoke(args, exec, current, "clear_logs", {});
			return { ...envelope, result: { logs: envelope.result, cleared: cleared.ok } };
		},
		presentCall: () => ({ card: "generic", title: "Read Unity console", kind: "read" })
	});

	tool({
		name: "unity_screenshot",
		description:
			"Render a PNG of the scene and return its file path. view='game' uses a real camera (Camera.main, else any enabled camera, else a temporary framing camera); view='scene' frames the whole scene, or `target` when given, with a temporary camera. In Play mode a game view captures the running game.",
		parameters: {
			path: { type: "string", description: "Output PNG path; relative paths resolve inside the project. Defaults to Temp/DSHUnityBridge/screenshots/<timestamp>.png." },
			width: { type: "number", description: "Pixel width; defaults to 1920." },
			height: { type: "number", description: "Pixel height; defaults to 1080." },
			view: { type: "string", enum: ["scene", "game"], description: "What to capture; defaults to scene." },
			target: { type: "string", description: "GameObject to frame, by hierarchy path or name; omit to frame the whole scene. With view='game' it is only used when the scene has no enabled camera." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 120_000,
		isConcurrencySafe: () => true,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "screenshot", {
				path: args.path,
				width: args.width,
				height: args.height,
				view: args.view,
				target: args.target
			});
		},
		presentCall: () => ({ card: "generic", title: "Capture Unity screenshot", kind: "execute" })
	});

	tool({
		name: "unity_menu",
		description:
			"Invoke an Editor menu item by its exact path, e.g. 'Window/General/Game' or 'File/Save Project'. Only menu paths that exist in this Editor are accepted.",
		parameters: {
			path: { type: "string", required: true, description: "Menu path as shown in the Editor's menu bar, separated by '/'." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 120_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "execute_menu", { path: args.path });
		},
		presentCall: (args) => ({ card: "generic", title: `Unity menu: ${args.path}`, kind: "execute" })
	});

	tool({
		name: "unity_build",
		description:
			"Run a player build for one target and report the result, size, duration and error/warning counts. Produces a real player in Builds/<target>/ unless outputPath says otherwise; expect it to take minutes.",
		parameters: {
			target: { type: "string", description: "Build target: StandaloneWindows64, StandaloneWindows, StandaloneOSX, StandaloneLinux64, Android or WebGL. Defaults to the active target." },
			outputPath: { type: "string", description: "Output path or folder; defaults to Builds/<target>/game[.exe]." },
			projectPath: PROJECT_PATH
		},
		output: OUTPUT,
		timeoutMs: 1_800_000,
		isConcurrencySafe: () => false,
		async execute(args, exec) {
			return await invoke(args, exec, settings(), "build", { target: args.target, outputPath: args.outputPath }, { timeoutMs: 1_800_000 });
		},
		presentCall: (args) => ({ card: "generic", title: `Build Unity: ${args.target ?? "active target"}`, kind: "execute" })
	});

	return () => {
		for (const dispose of disposers) dispose();
	};
}

//#endregion

/**
 * Mount the plugin.
 * @param ctx registrant context carrying the tool registry.
 * @param config resolved plugin configuration.
 */
export function apply(ctx, config) {
	const dispose = registerTools(ctx, config);
	ctx.effect(() => () => dispose(), "unity 2022 tools");
}

export { PACKAGE_NAME, packageTargetDir, normalizePath };
