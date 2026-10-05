# Spaceflight Simulator 1.7 TypeBridge

让**未修改的、按 1.6 编译的 DLL mod** 跑在 **Beebyte 混淆过的 SFS 1.7** 上。

**不改游戏本体，也不改硬盘上的 mod 文件。** 全部工作在内存里完成。

---

## 它解决什么问题

SFS 1.7 内测版用 Beebyte Obfuscator 3.11.6 混淆了 `Assembly-CSharp.dll`：
类型名、私有字段名、普通方法名全被改掉（命名空间、共有字段名、Unity 消息
`Awake`/`Update`、属性访问器、枚举成员通常保留）。

于是按 1.6 编译的 mod 会以各种方式失败：

| 失败形态 | 原因 |
|---|---|
| `TypeLoadException: Failure has occurred while loading a type.` | 引用了 1.7 里不存在的类型（**这句没有类型名**，最难查） |
| `MissingFieldException` / `MissingMethodException` | 成员被改名 |
| `override` 静默失效 | CLR 按「名字 + 签名」绑定 override |
| mod「加载成功但毫无功能」 | 加载器 JIT 第一个 mod 时炸掉，整个加载流程中断 |

## 工作原理

1. **引导**：在 `Spaceflight Simulator_Data/RuntimeInitializeOnLoads.json` 注册一项，
   Unity 在 `SubsystemRegistration` 阶段调用 `TypeBridge.Install()`（早于 mod 加载器）
2. **拦截**：用 MonoMod detour 挂钩 `System.Reflection.Assembly.LoadFrom(string)`
   —— mod 加载器直接调这个托管方法
3. **改写**：命中 mod DLL 就用 Mono.Cecil 在内存里按内嵌映射表改写，
   写到 `%TEMP%\sfs17rtbridge\<mod名>\`，让游戏加载那份副本
4. **依赖**：`AssemblyResolve` 处理 mod 之间的依赖（`Mini.Map` 依赖 `UITools`，
   而按目录序 `UITools` 排在后面）；同目录的兄弟 DLL 会一起镜像到临时目录，
   因为 `LoadFrom` 上下文只探测**加载目录**
5. **自审**：改写完成后 `AuditRefs()` 把「解析不到的类型/成员」连名字和调用点写进
   `typebridge.log`

## 快速开始

```powershell
# 安装（复制 dll 到 Managed/，并给两个 json 各加一条；自动留 .orig 备份）
python tools/install_bridge.py install

# 卸载（还原两个 json，删除 Managed/TypeBridge.dll）
python tools/install_bridge.py uninstall
```

装好后 `typebridge.log` 就在游戏根目录。

**离线复现**（不用开游戏跑完整重写流程，产物同样落 `%TEMP%\sfs17rtbridge`）：

```powershell
dotnet exec <csc> ... -out:tools/rehearse.exe tools/rehearse.cs
tools/rehearse.exe "D:\path\to\Spaceflight Simulator1.7"
```

## 编译

本机 `dotnet build` 因 NuGet 环境问题会失败（`Value cannot be null (Parameter 'path1')`），
**必须用 csc 直编**。见 `build.ps1`。

要点：不能把 `Split('\n')` 写成单参形式 —— Unity 的 Mono mscorlib 有
`Split(char, StringSplitOptions)` 重载，.NET Framework 没有，而离线工具跑在
.NET Framework 上。

## 目录结构

```
src/TypeBridge.cs              桥的全部逻辑（唯一需要改的文件）
data/bridge_map.tsv            真实类型全名 -> 混淆全名（1264 条）
data/member_map.tsv            成员映射，带置信度和匹配依据
data/member_map_overrides.tsv  人工补漏（最重要，人读的，只有十几行）
data/param_map.tsv             1.6 侧的参数名表（Harmony 参数改写用）
tools/                         生成 / 验证 / 诊断工具链
build.ps1                      一键编译
```

## 改写流程（顺序有讲究）

| 遍 | 做什么 | 为什么在这个位置 |
|---|---|---|
| pass 1 | 自身 `override` / **接口实现** / `MethodImpl` 改名 | CLR 按名字+签名绑定；接口实现是 `virtual final newslot`，与基类链无关 |
| pass 2 | IL 里的成员引用改名 | 高置信表 → 「1.7 里还有原名则不动」→ 低置信表回退 |
| pass 2.5a | `[HarmonyPatch]` 参数名 → `__N` | **必须在 2.5b 之前**，否则目标方法名已被改成混淆名，原方法名就丢了 |
| pass 2.5b | Harmony 目标字符串（**方法名 + 字段名**） | 反向补丁 / `AccessTools` 都靠字符串 |
| pass 3 | TypeRef 改名（两阶段） | 含泛型实参、`MethodImpl` 声明类型；顺序错了内层查表会落空 |
| pass 3.5 | `AuditRefs()` 自审 | 把失败点连名字写进日志 |

## 踩过的坑（改代码前先读）

1. **`SFS.UI.Button` 与 `SFS.UI.ModGUI.Button` 同名** —— 映射表生成器按裸名建索引时
   两者互相顶掉，导致 `SFS.UI.Button` 从表里彻底消失。现在有一个断言会报出重复 key。
2. **`ilhash` 匹配看不见签名** —— `Builder.CreateSeparator` 与 `CreateSpace` 方法体几乎一样，
   被配反了。`fixsig` 现在会按「参数类型 + 返回类型」校验并重新配对。
3. **属性 getter 不能用「虚槽位顺序」猜** —— `Mod` 的 5 个属性被配成轮换，
   `ModNameID` 返回了版本号字符串，于是加载器的依赖检查永远失败，
   6 个 mod 被永久搁置（表现为「ModLoader 里显示加载了、游戏内零功能」）。
   可靠信号是**元数据行序 + 至少一个锚点**，或者直接读游戏调用点的 IL。
4. **置信度阈值会误伤** —— `SceneHelper` 那 10 个字段的映射是 `conf=0.30` 但**完全正确**
   （靠 `.cctor` 的 `stsfld` 顺序 + `SceneLoader.OnEnable` 的实际读取两路交叉验证）。
   现在低置信度行保留为**回退**，只在「1.7 里确实找不到原名」时启用。
5. **嵌套声明类型的分隔符不一致** —— `member_map.tsv` 用 Cecil 的 `Outer/Inner`，
   而 `RealFull()` 用反射的 `Outer+Inner`，导致**所有嵌套类型的成员改名静默失效**。
6. **`FixOverrides` 的提前返回** —— `if (gameBase == null) return 0;` 会让
   `VisualsManager : MonoBehaviour, I_GLDrawer` 这类「只有接口、没有游戏基类」的类型
   把接口/MethodImpl 那一趟整个跳过。
7. **`TypeRefsOf` 漏了两处** —— `MethodImpl` 的声明类型 TypeRef、以及泛型实参
   （先 `Strip()` 再判断 `as GenericInstanceType`，实参永远访问不到）。
8. **Beebyte 也改参数名** —— Harmony 按**参数名**把 patch 参数对到原始方法，
   所以 patch 参数必须换成 Harmony 的位置形式 `__N`；但**反向补丁的桩不能改**
   （改成 `__0` 会被 Harmony 当成注入参数从签名里剔除）。
9. **mod 普遍会包一层** —— `AccessTools.FieldRefAccess` 常被包成自己的扩展方法
   （InfoOverload 叫 `Extensions::FieldRef`），所以判据要用**包含匹配**而不是相等。

## 重新生成映射表

需要本机的两份游戏程序集（你自行拥有）：**1.6.00.18**（mod 的编译目标，提供真实名）
与 **1.7 内测版**（提供混淆名）。

```powershell
# 1) 分别导出两边的指纹
tools/Fp.cs                      -> fp16.tsv / fp17.tsv / fp23.tsv
# 2) 结构匹配
python tools/match.py ...        -> match_result.tsv
# 3) 生成类型映射（含同名冲突处理 + 重复 key 断言）
python tools/gen_bridge_map.py   -> data/bridge_map.tsv, type_pairs.tsv
# 4) 成员映射
tools/MemberMap.exe <1.7dll> <1.6dll> type_pairs.tsv member_map.tsv
# 5) 参数名表
tools/genparammap.exe <1.6dll> data/bridge_map.tsv data/param_map.tsv
# 6) 校验：按签名集合修复错位
tools/fixsig.exe <1.6dll> <1.7dll> data/bridge_map.tsv data/member_map.tsv out.tsv
# 7) 用成员对应反向投票修正类型映射
tools/votetypes.exe <1.6dll> <1.7dll> data/bridge_map.tsv data/member_map.tsv out.tsv
```

人工判定的映射统一写进 `data/member_map_overrides.tsv`（优先级最高，
不受置信度阈值影响）——**这是最该读、最该改的文件**。

## 诊断工具

| 工具 | 作用 |
|---|---|
| `rehearse.cs` | 不开游戏跑完整重写流程 |
| `verify_rewritten.cs` | 审计游戏真正加载的那份镜像（类型/成员/override/abstract） |
| `iface.cs` | 查「某 mod 怎么用某类型」——接口 / 泛型实参 / `MethodImpl` / 调用点 |
| `ildump.cs` | 按字符串字面量定位方法并 dump IL（用来读游戏自身逻辑） |
| `refit.cs` | 按「同 kind + 成员数 + 签名集合」重配类型 |
| `votetypes.cs` | 用成员对应关系反向投票修正类型映射 |
| `fixsig.cs` | 按签名集合修复成员错位 |
| `fielduse.cs` | 字段被哪些方法读写 + `.cctor` 的 `stsfld` 顺序 |
| `proporder.cs` | 按元数据行序列出属性（跨版本配对用） |
| `modids.cs` | dump 各 mod 报出的 ID / 依赖字符串 |
| `inspect.cs` | 某类型 vs 其基类链全貌 |

## 实测状态

- 8~9 个常见 mod 全部加载成功，UI 面板正常
- 游戏本体 `Assembly-CSharp.dll` **零改动**
- `Mods/**/*.dll` **零改动**（与原始逐字节一致）

## 免责声明

- 本仓库**不包含任何游戏二进制或 mod 二进制**。`data/*.tsv` 是**名字映射表**
  （由使用者本机拥有的两份游戏程序集推导出的「真实名 ↔ 混淆名」对应关系），
  不含游戏代码。
- 使用前请自行备份。作者不对因使用本工具造成的任何损失负责。
- Spaceflight Simulator 版权归 Team Curiosity 所有，本项目与官方无关。
