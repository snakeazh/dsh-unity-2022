# Unity 2022.3（DeepSeek Harness 插件）

在 harness 里直接操控正在运行的 **Unity 2022.3.62f3** 编辑器：读写场景、进出 Play 模式、
截图、读 Console、打 Player 包，以及在编辑器进程内执行 C# —— 全部以普通 agent 工具的形式提供。

与只支持 Unity 6 的编辑器集成不同，本插件自带桥接：纯 2022.3 原生环境即可工作，不联网、
不需要额外 UPM 包，`unity_eval` 也不会触发域重载。

## 环境要求

| | |
|---|---|
| Unity 编辑器 | **2022.3.62f3**（国际版）为基准版本。桥接只读取编辑器版本做标记（`isRequiredUnityVersion`），2022.3 系其他补丁版（实测 2022.3.46f1、2022.3.62f1c1）同样可用，不阻断。 |
| Harness | 提供 `tools` 服务的任意 DSH 版本 |
| 平台 | Windows（macOS/Linux 编辑器可被发现但未验证） |

## 安装

```powershell
# 在本仓库目录下
plugin_manager install_bundle C:\path\to\unity-2022-plugin
```

`cordis.patch.yml` 会插入 `unity-2022` 这一行，工具在下一轮对话生效。卸载：
`plugin_manager remove_bundle dsh-unity-2022`。

## 在其他工程里使用

插件装在 **profile** 里（整个 harness 一份），桥接包装在 **工程** 里（每个工程一份）—— 两者分开，
所以同一套工具可以管任意多个 Unity 工程：

1. **让工程具备桥接**（三选一）：
   - `unity_open { "projectPath": "D:\\Work\\OtherGame" }` —— 自动安装 + 启动 + 等待就绪；
   - `unity_install { "projectPath": "..." }` —— 只装包、不启动（适合把包提交进仓库）；
   - 手动把插件的 `unity-package/` 目录复制成 `<project>/Packages/com.dsh.unity-bridge`。
   `Packages/com.dsh.unity-bridge` 是 **embedded package**：提交进 Git 后，团队里任何人打开该工程，
   编辑器都会自动起桥接，无需再调用任何工具。
2. **把请求路由到对的编辑器**（按优先级）：
   - 调用参数里的 `projectPath`；
   - 插件配置里的 `projectPath`（默认工程）；
   - 调用方 agent 的工作目录落在某工程内 → 自动匹配该工程；
   - 只跑着一个编辑器 → 直接用它。
   同时开着多个编辑器、又没给 `projectPath` 时，会返回 `ambiguous_editor` 并列出可连工程。
3. **工程版本**：`unity_open` 会挑与工程 `ProjectVersion.txt` 匹配的 Hub 安装（本机三套 2022.3 都在
   候选里）；没有对应版本时回落到最近的 2022.3。工程版本与基准版本不同只是标记，不阻断。

## 发布到插件市场

市场只认「某个地址上的一份索引 JSON」+「索引里指向的可安装包」，所以发布 = 发包 + 上线索引。
两条路可以只做一条，也可以两条都做。

### 1. 发包到 npm

```powershell
npm login
npm publish            # 包名 dsh-unity-2022，publishConfig.access 已是 public
npm pack               # 只想先自测时用：得到一个和发布内容完全一致的 .tgz
```

`files` 白名单已经包含运行所需的一切（`index.js`、`lib/`、`unity-package/`、`locale/`、图标、
patch、README）。`plugin_manager install_bundle <某个 .tgz>` 可以本地验证「发布产物能被装上」——
本插件已用 `dsh-unity-2022-0.1.0.tgz`（24 个文件、61 KB）验证通过。

> 如果这个不带 scope 的名字在 npm 上已被占用，改 `package.json` 的 `name` 与
> `cordis.patch.yml` 里的 `name`（两处必须一致），再同步 `market/index.json` 的 `name` 与
> `versions[].source`。

### 2a. 进官方精选列表（社区市场的默认数据源）

市场 UI（`dshmarket`）和它的站点读的是精选列表
[awesome-dsh-plugin](https://github.com/awesome-dsh-plugin/awesome-dsh-plugin)：去那边提一个 PR，
在列表里加一条本插件即可，站点 CI 每天刷新 `plugins.json`，市场与发现页会自动收录（通常一天内生效）。
这是「让别人在市场里搜到」最短的一条路。

### 2b. 自建索引源（format 1）

`market/index.json` 就是一份符合 format 1 的索引，已验证可被 DSH 的解析器接受：

```powershell
# 本地预览（DSH 只接受绝对 URL，插件市场的 list 工具还只认已配置的索引源）
node tools/serve-market.cjs        # 把仓库根目录挂在 http://127.0.0.1:8199/
# 校验用 market/index.json + market/icon.svg 这套文件本身
```

把这个目录（`index.json` + `icon.svg`）放到任意 HTTPS 地址——GitHub Pages、jsDelivr（配合
发布到 npm 的 `dsh-plugin-catalog` 之类）、或自己的域名——然后在 DSH 里
**设置 → 插件 → 发现 → 索引源** 添加该 `index.json` 的 URL。之后任何人都能在那台 DSH 里搜到并一键安装。

条目里能填的展示元数据（都可选，`en` 必填、可加 `zh`）：

| 字段 | 说明 |
|---|---|
| `title` / `description` | 本地化文案，字符串或 `{ "en": ..., "zh": ... }` |
| `icon` | 相对 `index.json` 的路径、HTTPS URL，或 ≤32 KiB 的 base64 `data:` URL |
| `metadata.keywords` | 搜索关键词（自由填写） |
| `metadata.category` / `metadata.labels` | 必须先在索引根部的 `metadata.categories` / `metadata.labels` 里声明 id |
| `versions[].source` | `{ "type": "npm" }` 装 `name@version`；也可 `{ "type": "git", "url": "...", "commit": "<40 位 sha>", "path": "可选子目录" }` 直接从仓库装，不必发 npm |
| `versions[].engines.dsh` | 要求的 DSH 版本范围，市场据此提示兼容性 |

### 3. 发版

改 `package.json` 的 `version` → `npm publish` → 在 `market/index.json` 的 `versions` 里**追加**一条
（`publishTimestamp` 是 Unix 秒，整型），旧版本条目保留，市场按 SemVer 倒序展示最新版。

## 快速上手

```
unity_open    { "projectPath": "D:\\Work\\MyGame" }
unity_status  {}
unity_hierarchy { "includeComponents": true }
unity_create  { "name": "Crate", "primitive": "Cube", "position": [0, 0.5, 0] }
unity_add_component { "target": "Crate", "type": "Rigidbody", "properties": { "m_Mass": 12 } }
unity_screenshot { "view": "game", "width": 1280, "height": 720 }
```

`unity_open` 会把内置的 `com.dsh.unity-bridge` 包复制进 `<project>/Packages/`、启动匹配版本的
编辑器，并等到桥接应答为止。如果工程已经在编辑器里打开，其余工具会自动找到它 —— 只有在同时
跑多个编辑器时才需要传 `projectPath`。

## 工具一览

| 工具 | 作用 |
|---|---|
| `unity_status` | 可连接的编辑器列表，以及版本、Play 状态、编译状态、当前场景、选中对象、本机已安装编辑器。 |
| `unity_open` | 安装桥接包并启动编辑器，等待端点就绪。 |
| `unity_install` | 只把桥接包装进工程的 `Packages/`（不启动编辑器），便于提交进仓库。 |
| `unity_commands` | 列出桥接命令表（名称、参数、示例、是否修改场景）。 |
| `unity_command` | 用原始参数执行任意桥接命令（兜底入口）。 |
| `unity_hierarchy` | 场景层级树，含路径、instanceId、激活状态，可选组件名。 |
| `unity_find` | 按名字子串、组件类型、tag 或 layer 查找 GameObject。 |
| `unity_inspect` | 逐组件返回 Transform、激活状态与全部序列化属性（即 Inspector 里的值）。 |
| `unity_create` | 新建空对象或基本体，可指定父级、变换、tag、layer、是否保留碰撞体；可撤销。 |
| `unity_add_component` | 添加组件，并可同时设置属性。 |
| `unity_set_property` | 按属性路径或纯名字（`m_` 前缀可省）写序列化属性，支持嵌套结构体。 |
| `unity_set_transform` | 位置、旋转、缩放、改父级，世界或本地坐标系。 |
| `unity_delete` | 删除对象及其子级；可撤销。 |
| `unity_scene` | 列出 / 打开 / 保存场景，刷新脏资源。 |
| `unity_eval` | 在编辑器进程内执行 C#，由编辑器自带 Roslyn 编译，不触发域重载。 |
| `unity_play` | 进入、退出、暂停、恢复、单帧步进 Play 模式。 |
| `unity_logs` | 读取 Console 缓冲（错误、警告、日志），可带堆栈。 |
| `unity_screenshot` | 渲染场景或游戏相机的 PNG，可指定取景对象。 |
| `unity_menu` | 按路径执行编辑器菜单项，例如 `File/Save Project`。 |
| `unity_build` | 构建 Player，返回体积、耗时、错误与警告数。 |

所有工具返回同一个信封，失败也是数据而不是异常：

```json
{ "ok": true, "command": "hierarchy", "ms": 2, "instance": { "...": "..." }, "result": { } }
{ "ok": false, "command": "find_objects", "ms": 0, "error": { "code": "not_found", "message": "No GameObject matches 'Crat'" } }
```

常见错误码：`no_editor`、`ambiguous_editor`、`not_found`、`missing_param`、`invalid_param`、
`outside_project`、`compile_error`、`timeout`、`transport_error`。

## 配置

插件从自己的行读取 `config:` 块。编辑 profile 的 patch 文件
（`%USERPROFILE%\.dsh\profiles\<profile>\cordis.patch.yml`），追加覆盖项：

```yaml
- id: unity-2022
  config:
    projectPath: D:/Work/MyGame
    autoLaunch: true
    requestTimeoutMs: 180000
```

| 键 | 默认值 | 含义 |
|---|---|---|
| `unityPath` | `""` | 要启动的编辑器可执行文件；留空则自动匹配工程版本对应的 Hub 安装。 |
| `projectPath` | `""` | 工具调用省略 `projectPath` 时使用的默认工程。 |
| `requestTimeoutMs` | `120000` | 单条桥接命令的超时。 |
| `launchTimeoutMs` | `600000` | `unity_open` 等待桥接就绪的时长。 |
| `autoLaunch` | `false` | 工具执行时若没有可用编辑器则自动启动。 |
| `installPackageOnLaunch` | `true` | 启动前复制内置桥接包。 |

## 工作原理

```
agent tool ──▶ index.js ──▶ lib/client.mjs ──TCP 127.0.0.1:<临时端口>──▶ Unity 编辑器
                          │                                            (com.dsh.unity-bridge)
                          └─ 发现文件: %LOCALAPPDATA%\DSHUnityBridge\instances\<sha1>.json
```

* 编辑器侧的包在每次域重载后于临时端口上启动回环 TCP 服务，并为每个工程写一份发现文件
  （工程路径哈希、pid、端口、版本号）。Host 每次请求都重新读取该文件，因此 Play 模式、
  脚本编译、编辑器重启都是透明的 —— 命中已关闭套接字时会换新端口重试一次。
* 一个连接一个请求，双方各写一行 JSON。命令在编辑器主线程执行；不能阻塞主线程的
  （`eval`、`build`）自己起线程，再把结果回传给应答。
* `unity_eval` 在 `<project>/Temp/DSHUnityBridge/` 下生成一段 C#，用编辑器自带的 Roslyn
  针对编辑器与工程程序集编译成 DLL，再加载并调用 —— 因此既能用 `UnityEngine`/`UnityEditor`，
  又不会往 `Assets/` 写东西或触发重编译。

截图由插件自己摆放一台临时相机渲染，不使用编辑器自带的 Scene view 相机：后者的变换只在
repaint 时跟随 pivot，同一帧内取景会是旧的。

## 桥接命令

共 25 条：`status`、`list_commands`、`hierarchy`、`find_objects`、`inspect_object`、
`create_object`、`delete_object`、`add_component`、`remove_component`、`set_transform`、
`set_property`、`save_scene`、`open_scene`、`save_assets`、`refresh_assets`、`write_file`、
`import_package`、`execute_menu`、`selection`、`undo`、`play`、`screenshot`、`get_logs`、
`clear_logs`、`eval`、`build`。

参数以 `unity_commands` 的实时返回为准 —— 编辑器侧的包才是唯一事实源。编辑器内通过
`Tools/DSH/Unity Bridge` 打开状态窗口，可看端口、请求数，重启桥接或跑自检。

## 测试

```powershell
# 只编译 C#：对着本机 Unity 编辑器编译桥接
powershell -NoProfile -ExecutionPolicy Bypass -File tools\compile-bridge.ps1

# 端到端（加 --launch 会自己启动编辑器）
node test/smoke.mjs --project <unity 工程> --launch

# 生成一个场景并截图
node test/visual-check.mjs --project <unity 工程>
```

`test/harness.mjs` 是这些脚本用的胶水层，直接调用 `lib/client.mjs`，不依赖 harness 是否加载。

## 排错

| 现象 | 处理 |
|---|---|
| `no_editor` | 用 `unity_open` 启动编辑器，或手动打开工程；`unity_status` 可看有没有残留的发现文件。 |
| `ambiguous_editor` | 同时开了多个编辑器，传 `projectPath` 指定。 |
| 加包后桥接没起来 | Unity 需要导入一次该包：切到编辑器窗口等编译完成，再调 `unity_status`。 |
| `unity_eval` 返回 `compile_error` | 消息里带 `file(line,col): error CSxxxx`。片段可以用 C# 9、`UnityEngine`、`UnityEditor`。 |
| 其他 Unity 版本 | 只是标记：`unity_status` 里 `isRequiredUnityVersion: false`、`expectedUnityVersion` 为基准版本，功能照常（实测 2022.3.46f1）。 |
| `editor_blocked` | 编辑器主线程超过 15 秒没有 tick：通常是弹了模态框（保存对话框、导入进度、许可证窗口）。到 Unity 窗口里点掉它，再重试。 |
| `bridge_unavailable` | 编辑器进程起来了但一直没发布发现文件，几乎都是原生对话框挡住了主线程：最常见的是上一个编辑器被强杀后残留 `<project>/Temp/UnityLockfile` 触发的「工程已被打开」。关掉对话框，或退出该编辑器、删掉锁文件后重新 `unity_open`（返回里的 `staleLockfile: true` 表示启动前就存在锁文件）。 |
| `editor_exited` | 编辑器进程在桥接就绪前退出，看返回的 `logFile`（`%LOCALAPPDATA%\Unity\Editor\Editor.log`）。 |
| `unity_scene` save 报 `missing_param` | 当前场景还没存过（untitled），必须给 `path`，例如 `Assets/Scenes/Main.unity`；桥接故意不无参保存，否则 Unity 会弹窗卡住编辑器。 |
| `unity_build` 报 `no_scenes` | Build Settings 为空且当前场景未保存；传 `scenes: ["Assets/Scenes/Main.unity"]` 或先 `unity_scene` save。 |

> 桥接跑在编辑器主线程上，任何会弹模态对话框的编辑器 API 都会把请求卡住；上面这些命令都已经做了防弹窗处理（预建 asset 目录、拒绝无路径保存、主线程停摆时快速失败）。

## 许可

MIT
