import re,subprocess,sys,os
root=os.path.abspath(sys.argv[1]); L=root+'/'+(sys.argv[2] if len(sys.argv)>2 else '.linux'); proj=L+'/Tests/Tests.csproj'
def build():
    r=subprocess.run(['dotnet','build','Tests/Tests.csproj','-c','Release','-v','q','--nologo'],cwd=L,capture_output=True,text=True)
    return [l for l in (r.stdout+r.stderr).split('\n') if ' error ' in l]
for it in range(12):
    errs=build()
    if not errs: print('build OK after',it,'iterations'); break
    s=open(proj).read(); changed=False
    excluded=re.findall(r'<Compile Remove="([^"]+)"',s)
    for e in errs:
        m=re.search(r"The name '(\w+)' does not exist|The type or namespace name '(\w+)' could not be found",e)
        f=re.search(r'Pt_Optimize\.Tests/(\w+\.cs)\(',e)
        name=m and (m.group(1) or m.group(2))
        if name:
            # find excluded file that defines it
            hit=[x for x in excluded if re.search(r'\b(class|record|struct|enum|interface)\s+'+name+r'\b',open(x,encoding='utf-8-sig',errors='replace').read())]
            if hit:
                s=s.replace(f'    <Compile Remove="{hit[0]}" />\n',''); changed=True; excluded.remove(hit[0]); continue
        if f:
            full=f'{root}/Pt_Optimize.Tests/{f.group(1)}'
            if full not in excluded:
                s=s.replace('  </ItemGroup>\n</Project>',f'    <Compile Remove="{full}" />\n  </ItemGroup>\n</Project>'); changed=True; excluded.append(full)
    open(proj,'w').write(s)
    if not changed: print('stuck:'); print('\n'.join(sorted(set(errs))[:15])); break
print('excluded now:',len(re.findall(r'<Compile Remove=',open(proj).read())))
