# Spaceflight Simulator 1.7 TypeBridge

让**未修改的、按 1.6 编译的 DLL mod** 跑在 **Beebyte 混淆过的 SFS 1.7** 上。

**不改游戏本体，也不改硬盘上的 mod 文件。** 全部工作在内存里完成。
  
## How to Use  
1.下载`TypeBridge-auto.zip`解压 将两个文件移动到游戏根目录下 运行`一键安装.bat`即可自动启用  
注意：如果游戏装在中文路径下 `一键安装.bat` 会提示并自动创建一个英文路径  
2.或者是  
下载源码  
指令  
安装  `python tools\enable_bridge.py`  
只检查  `python tools\enable_bridge.py --check-only`  
还原   `python tools\enable_bridge.py --uninstall`  
指定游戏目录 `python tools\enable_bridge.py --game "游戏目录"`  
## 确认加载  
打开游戏目录下的`typebridge.log`  
若是  
```
maps: 1146 types, 5362 member keys, 2599 unanimous fallbacks
hook installed on System.Reflection.Assembly LoadFrom(System.String)
```
即加载成功  
  
## 免责声明

- 本仓库**不包含任何游戏二进制或 mod 二进制**。`data/*.tsv` 是**名字映射表**
  （由使用者本机拥有的两份游戏程序集推导出的「真实名 ↔ 混淆名」对应关系），
  不含游戏代码。
- 使用前请自行备份。作者不对因使用本工具造成的任何损失负责。
- Spaceflight Simulator 版权归 Team Curiosity 所有，本项目与官方无关。
