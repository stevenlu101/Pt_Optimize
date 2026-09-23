#!/bin/bash
# usage: mk_linux.sh <repo_root>   -> creates <root>/.linux/{Core,Tests} mirror projects (net8.0, UI tests excluded)
ROOT=$(cd "$1" && pwd)
L=$ROOT/.linux; rm -rf $L; mkdir -p $L/Core $L/Tests
cat > $L/Core/Core.csproj <<X
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>PtOptimize</RootNamespace><AssemblyName>Pt_Optimize</AssemblyName>
    <NoWarn>\$(NoWarn);CS1591;CS8618;CS8600;CS8601;CS8602;CS8603;CS8604;CS8605;CS8619;CS8620;CS8625;CS0168;CS0219;CS0162;CS0414;CS0649;CS0169</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$ROOT/Pt_Optimize/Core/**/*.cs" />
    <PackageReference Include="MathNet.Numerics" Version="5.0.0" />
    <InternalsVisibleTo Include="Pt_Optimize.Tests" />
  </ItemGroup>
</Project>
X
UIF=$(cd $ROOT && grep -l 'System.Windows.Forms\|using PtOptimize.UI\|PtOptimize\.UI\.\|MainForm\|LineDesignPage\|ScottPlot\|System.Drawing\|\bFlow\.\|\bUI\.\|FlowState\|UiShot\|ManualPage\|WeldFloorPage\|GradeNameEditor\|SolveStageStrip' Pt_Optimize.Tests/*.cs | xargs -n1 basename)
{
cat <<X
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable><RootNamespace>PtOptimize.Tests</RootNamespace><AssemblyName>Pt_Optimize.Tests</AssemblyName>
    <NoWarn>\$(NoWarn);CS8618;CS8600;CS8601;CS8602;CS8603;CS8604;CS8605;CS8619;CS8620;CS8625;xUnit1013;xUnit2013;xUnit1026;xUnit2000;xUnit2002;CS0168;CS0219;CS0162</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <ProjectReference Include="../Core/Core.csproj" />
    <Compile Include="$ROOT/Pt_Optimize.Tests/*.cs" />
X
for f in $UIF; do echo "    <Compile Remove=\"$ROOT/Pt_Optimize.Tests/$f\" />"; done
echo '  </ItemGroup>'
echo '</Project>'
} > $L/Tests/Tests.csproj
cat > $L/Core/LinuxShims.cs <<'Y'
// Linux 镜像专用占位：Core 里只有特性参数引用这两个类型（界面编辑器），Linux 上不跑界面。
namespace System.Drawing.Design { public class UITypeEditor { } }
namespace PtOptimize.UI { public class GradeNameEditor : System.Drawing.Design.UITypeEditor { } }
Y
echo "$UIF" > $L/ui_excluded.txt
echo "mirror at $L; excluded $(echo "$UIF" | wc -w) UI test files"
