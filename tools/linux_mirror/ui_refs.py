# 输出引用界面的测试档名（只看去掉注释与字符串之后的代码）
import re,sys,os,glob
root=sys.argv[1]
PAT=re.compile(r'System\.Windows\.Forms|using PtOptimize\.UI|PtOptimize\.UI\.|\bMainForm\b|\bLineDesignPage\b|ScottPlot|System\.Drawing|\bFlow\.|\bUI\.|\bFlowState\b|\bUiShot\b|\bManualPage\b|\bWeldFloorPage\b|\bGradeNameEditor\b|\bSolveStageStrip\b|\bTextFmt\b|\bGridFmt\b|\bUiScale\b|\bStagePanel\b|\bFieldPlots\b|\bSchematics\b|\bAnalysisPage\b')
def strip(s):
    s=re.sub(r'/\*.*?\*/','',s,flags=re.S)
    s=re.sub(r'//[^\n]*','',s)
    s=re.sub(r'@"(?:[^"]|"")*"','""',s)
    s=re.sub(r'\$?"(?:\\.|[^"\\\n])*"','""',s)
    return s
for f in sorted(glob.glob(os.path.join(root,'Pt_Optimize.Tests','*.cs'))):
    if PAT.search(strip(open(f,encoding='utf-8-sig',errors='replace').read())):
        print(os.path.basename(f))
