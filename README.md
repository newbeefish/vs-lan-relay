# 死神VS火影 局域网联机助手（VsRelay）

> 把 Radmin VPN（或任何 /8 网段的虚拟局域网）的隧道，变成这款游戏能识别的 **/24 局域网**，
> 从而恢复《死神VS火影 竞技版》的局域网联机功能。

## 为什么需要它

《BLEACH VS NARUTO 竞技版》的局域网联机有两个硬性限制：

1. **只接受 `/24` 掩码的网卡**。Radmin VPN 分配的是 `26.0.0.0/8`，游戏永远选不中它。
2. **启动时一次性枚举网卡**。游戏启动之后再加网卡无效。

于是出现"双方都在同一个 Radmin 网络里、能 ping 通，但游戏搜不到房间"的经典困境。

## 思路

```
游戏 ──UDP 17477/17478──┐
                        ├─ 原始套接字抓包（所有网卡）
                        │
                        ├─ 隧道（Radmin VPN，UDP 50001）
                        │
游戏 ──TCP 17511────────┘
                        │
                        └─ 注入对端本机（辅助 IP + 普通 socket / TCP 代理）
```

**关键点：不依赖虚拟网卡承载游戏流量。** 用 Windows 原始套接字在链路层直接抓取游戏广播，
通过隧道转发到对端，再以"对端的虚拟 IP"为源地址注入回本机协议栈。

## 四个必须踩过的坑

| 坑 | 现象 | 解法 |
|---|---|---|
| 发现包无限对回 | 每秒几百个包，CPU 打满 | 协议加 `H`/`R` 类型，只对探测包回一次 |
| 重叠 I/O 缓冲区未固定 | `clr.dll` 崩溃 `0xc0000005` | 改回同步 I/O |
| 同步读取时 `CloseHandle` | 点"停止转发"界面假死 | 读取线程常驻，不再关闭句柄 |
| **对称端口被丢弃** | 发送方计数在涨，接收方永远是 0 | 发送改用独立 socket，源端口随机 |
| **把自己当成对端** | 数据全发给自己 | 比对**全部**本机网卡地址，而不是只比一个 |

## 四步自检

程序内置分步诊断，每步独立着色：

```
① 抓包引擎：原始套接字能否打开全部网卡
② 本机注入：能否把包送进本机 socket（源 IP 正确）
③ 游戏联通：游戏进程 + 17477/17478 端口状态
④ 对端回环：向对端发 PING，等待 PONG，显示实测延迟
```

## 构建

需要 .NET Framework 4.0 的 `csc.exe`（Windows 自带）：

```bat
csc.exe /target:winexe /platform:anycpu ^
  /win32icon:app.ico /win32manifest:app.manifest ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /resource:OemVista.inf,res.OemVista.inf ^
  /resource:tap0901.sys,res.tap0901.sys ^
  /resource:tap0901.cat,res.tap0901.cat ^
  Program.cs TapInstaller.cs RawNet.cs TcpProxy.cs
```

安装包（`Setup.cs`）把主程序作为嵌入资源打包，负责释放文件、建快捷方式。

## 文件说明

| 文件 | 作用 |
|---|---|
| `Program.cs` | 主窗口、发现协议、隧道转发、四步自检 |
| `RawNet.cs` | 原始套接字抓包（`SIO_RCVALL`）与 IP/UDP 包构造 |
| `TcpProxy.cs` | TCP 17511 代理（加入对局用） |
| `TapInstaller.cs` | TAP-Windows 网卡的 SetupAPI 自动安装 |
| `Setup.cs` | 单文件安装程序 |

## 已知限制

- 游戏自身的局域网同步有缺陷（**真实局域网也会出现角色闪现**），与本工具无关。
- 需要管理员权限（原始套接字 + 安装网卡）。
- 仅支持 IPv4。

## 许可

**All Rights Reserved（保留所有权利）** — 详见 [LICENSE.txt](LICENSE.txt)。

本软件**不是**开源软件。仅授权个人非商业使用，禁止复制、修改、分发、商用。

第三方组件（TAP-Windows 驱动）遵循其原有 GPLv2 许可。

请支持游戏原作者。