# 死神VS火影 局域网联机助手

> 让《死神VS火影 竞技版》通过 Radmin VPN 正常联机。

---

## 为什么需要它

这个游戏（3.8.8.6）自带的局域网搜索**根本搜不到房间**，原因在 Adobe AIR 运行时：

**AIR 51 的 `DatagramSocket.send()` 在发送子网广播时会临时创建一个 socket，发完立刻关闭：**

```
socket(S2)                                    ← 临时 socket
setsockopt(S2, SO_BROADCAST, 1)
sendto(S2, 21字节, 169.254.255.255:17477)
closesocket(S2)                               ← 立刻关闭
```

而对方主机把回复发到 **这个临时 socket 的端口**，此时它早已关闭。游戏真正监听的
`0.0.0.0:17478` 永远收不到回复，所以房间列表永远是空的。

（AIR 50.2.3.3 用的是应用自己绑定的 socket，所以旧版本能正常工作。）

这个 bug 由社区开发者 [@diazynez](https://github.com/diazynez) 定位并提交：
[airsdk/Adobe-Runtime-Support#4375](https://github.com/airsdk/Adobe-Runtime-Support/issues/4375)

此外游戏的 `sendBroadcast()` 取的是**最后一个网卡**的广播地址，在多网卡机器上经常拿到
`169.254.x.255`（链路本地），查询发到了根本不存在的网段。

---

## 它是怎么解决的

游戏源码里留了一个外部搜索组件的接口：

```actionscript
var executable:File = applicationDirectory.resolvePath("assets/bridge/LANDiscovery.exe");
if (!NativeProcess.isSupported || !executable.exists) { 走内置广播; return; }
```

**只要这个文件存在，游戏就把搜索交给它**，通过标准输入输出通信：

```
游戏 → 程序:  SEARCH\t<地址>\t<十六进制查询包>
程序 → 游戏:  READY
              HOST\t<IP>\t<端口>\t<十六进制房间包>
```

而且游戏**完全信任程序给出的 IP**：

```actionscript
_loc3_.ip = param1.fromIP;
_loc3_.tcpPort = 17511;
_socket.connect(param3.ip, 17511);
```

于是方案变成：

```
游戏 ──(标准输入输出)── 补丁 ──(本机 UDP)── 主程序 ──(Radmin 隧道)── 对方主程序
                                                                          │
                                                                       对方补丁
                                                                          │
                                                                       对方游戏
```

**完全绕开广播**：查询走单播到对方的 Radmin IP，房间包直接塞回游戏的列表接口。

找到房间后，游戏会用**对方的真实 Radmin IP** 建立 TCP 连接——**对局数据走 Radmin 原生
通道**（有重传、有序），不经过本程序。

---

## 组件

| 文件 | 大小 | 位置 | 作用 |
|---|---|---|---|
| `VsRelayUI.exe` | 54 KB | `C:\VsGameRelay\` | 主程序：隧道、握手、延迟显示 |
| `LANDiscovery.exe` | 10 KB | 游戏目录 `\assets\bridge\` | 补丁：只和游戏换包 |
| `安装联机助手.exe` | 109 KB | 桌面 | 一键安装器 |

### 主程序

- 在 Radmin 网段广播握手包，**自动找到对端并锁定**（无需手填 IP）
- 隧道心跳，实时显示延迟
- 点 ✕ 收进托盘；游戏退出后自动退出
- 每次「开始运行」重新握手，不会锁在旧地址

### 补丁

- 游戏启动搜索时被拉起
- 把查询转给主程序，把房间包交回游戏
- 游戏退出自动结束（看门狗）
- 自动清理同名的旧实例，避免端口占用

---

## 安装

1. 双击 `安装联机助手.exe`
2. 它会自动找到游戏目录（也可手动选）
3. 点「一键安装」

安装内容：

```
C:\VsGameRelay\VsRelayUI.exe                      主程序
<游戏目录>\assets\bridge\LANDiscovery.exe          补丁
桌面快捷方式
```

**不需要**：管理员权限、内核驱动、虚拟网卡、修改系统设置。
**卸载**：删掉这两个文件即可。

---

## 使用

1. 两台电脑都装，都登录 Radmin VPN 并加入同一网络
2. 各自打开「死神VS火影联机助手」，点「开始运行」
3. 等界面显示 `隧道对端：26.x.x.x（已握手锁定）` 和延迟数字
4. 一台创建房间，另一台进「加入游戏」

---

## 已知限制

- 只能在**同一 Radmin 网络**内的两台机器间使用
- 延迟取决于 Radmin 线路质量
- 需要 Adobe AIR 支持 `NativeProcess`（桌面版均支持）

---

## 源码

`src2/` 目录包含三个组件的完整 C# 源码：

- `VsRelayUI.cs` — 主程序（隧道 / 握手 / 界面）
- `LANDiscovery_Patch.cs` — 游戏补丁
- `Setup.cs` — 安装器

编译（无需 Visual Studio，用系统自带的 csc）：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:exe /out:LANDiscovery.exe LANDiscovery_Patch.cs
```

---

## 许可

本项目采用 [GNU General Public License v3.0](LICENSE) 许可协议。

你可以自由使用、修改、分发本项目，但衍生作品必须以相同协议开源。