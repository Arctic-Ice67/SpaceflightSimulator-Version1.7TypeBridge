# build.ps1 — 编译 TypeBridge.dll
# 用法:  .\build.ps1 [-ManagedDir "D:\...\Spaceflight Simulator_Data\Managed"]
#
# 注意：本机不能用 `dotnet build`（NuGet 环境问题：Value cannot be null (Parameter 'path1')），
#       必须像这样用 csc 直编。
param(
    [string]$ManagedDir = "D:\2SFS Project\Spaceflight Simulator1.7\Spaceflight Simulator_Data\Managed",
    [string]$Out = "$PSScriptRoot\TypeBridge.dll"
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$csc  = "C:\Program Files\dotnet\sdk\10.0.401\Roslyn\bincore\csc.dll"
if (-not (Test-Path $csc)) {
    $csc = (Get-ChildItem "C:\Program Files\dotnet\sdk\*\Roslyn\bincore\csc.dll" |
            Sort-Object FullName -Descending | Select-Object -First 1).FullName
}
if (-not (Test-Path $ManagedDir)) { throw "找不到 Managed 目录: $ManagedDir" }

# 四个数据表要作为内嵌资源编进 dll（桥在运行时用 ReadResource 读它们）
$resources = @(
    "/resource:$here\data\bridge_map.tsv,bridge_map",
    "/resource:$here\data\member_map.tsv,member_map",
    "/resource:$here\data\member_map_overrides.tsv,member_overrides",
    "/resource:$here\data\param_map.tsv,param_map"
)
$refs = @("mscorlib.dll","System.dll","System.Core.dll","netstandard.dll",
          "MonoMod.RuntimeDetour.dll","MonoMod.Utils.dll","MonoMod.Core.dll",
          "Mono.Cecil.dll","System.Runtime.dll") | ForEach-Object { "/r:$ManagedDir\$_" }

Write-Host "编译 -> $Out"
dotnet exec $csc /noconfig /nostdlib+ /target:library /optimize+ /nologo `
    /out:"$Out" @resources @refs "$here\src\TypeBridge.cs"
if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit=$LASTEXITCODE)" }

$fi = Get-Item $Out
Write-Host ("完成: {0}  ({1} 字节, md5 {2})" -f $fi.Name, $fi.Length,
            (Get-FileHash $Out -Algorithm MD5).Hash)
