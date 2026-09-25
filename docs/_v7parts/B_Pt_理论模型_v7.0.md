# 3 电学模型

## 3.1 体积生热率

由微分形式欧姆定律 $\mathbf{E}=\rho\,\mathbf{J}$，单位体积电功率

$$q_v=\rho\,J^2\quad[\mathrm{W/m^3}].$$

这条平方律是全文最重要的一条。截面稍微变大，发热能力就急剧下降。它也是「法兰虽然导电却几乎不发热」的根本原因。

电阻率 $\rho(T)$ 在求解链里只有一支：纯铂随温度的二次拟合，出处是鉑金電氣計算.xlsx，拟合区间 0～1500 °C。<!-- 出处: Pt_Optimize/Core/Materials.cs:6 档头；Pt_Optimize/Core/Materials.cs:37-41 PtFitMaxC 与 PtResistivity --> 1500 °C 以上是外推。<!-- 出处: Pt_Optimize/Core/Materials.cs:37-38 --> 段解的焦耳热、法兰壳的电流场、板件二维电流三处都读这一支。选别的牌号时它不跟着变，见第 4.9 节。<!-- 出处: HANDOVER.md:193；Pt_Optimize/Core/DesignInputs.cs:179-184 GradeNameNote -->

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $q_v$ | 单位体积电功率（体积生热率） | W/m³ |
| $\rho$ | 铂电阻率，随温度上升；求解链一律按纯铂二次拟合（第 4.9 节） | Ω·m |
| $\mathbf{J}$、$J$ | 电流密度矢量及其模；工程上以 A/mm² 报 | A/m²（1 A/mm² = 10⁶ A/m²） |
| $\mathbf{E}$ | 电场强度 | V/m |

## 3.2 质量方程

设一段铂件须供给功率 $P$，电流密度均匀时体积 $V=P/(\rho J^2)$，铂质量

$$m=\rho_m V=\frac{\rho_m\,P}{\rho\,J^2}.$$

在电流密度被上限钳死时，铂金质量与所需功率成严格正比。供料管的功率几乎全部用于补偿散热，故省铂金 = 省热损失：任何被吹走、辐射掉、传导流失的瓦特，都必须由铂金发出来，因而都折算成克数。

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $m$ | 该段铂件的质量 | kg |
| $\rho_m$ | 铂密度，21 450；整线铂重一律用这个纯铂值（第 4.9 节） | kg/m³ |
| $V$ | 铂件体积 | m³ |
| $P$ | 该段须供给的电功率（稳态 ≈ 散热） | W |
| $\rho$、$J$ | 同 3.1 节：电阻率、电流密度 | Ω·m、A/m² |

<!-- 出处: Pt_Optimize/Core/Materials.cs:20 PtDensity = 21450.0；Pt_Optimize/Core/LineRunner.cs:2209,2923 整线法兰与管的质量都乘 Materials.PtDensity -->

## 3.3 薄板：面电流守恒

法兰是「圆盘 + 平面舌片」，电流由舌片单侧汇入管孔，是本质二维问题。深度平均后的控制方程

$$\nabla\cdot\left(t\,\sigma\,\nabla\varphi\right)=0,\qquad \mathbf{K}=t\,\mathbf{J}=-t\,\sigma\nabla\varphi,$$

守恒量是面电流密度 $\mathbf{K}$。单位面积的发热（壳模型里真正进入热方程的量）

$$q_A=\rho\,J^2\,t=\frac{\rho\,K^2}{t}.$$

这是后面所有「加厚」类对策的理论依据，也解释了它们的双向性：加厚同时降低单位面积发热（∝ 1/t）与提高横向导热能力（∝ t）。

边界条件：舌端压接段整面贴铜排，整段取同一电位；管孔一侧取零电位；其余边界零通量。<!-- 出处: Pt_Optimize/Core/ShellCurrent.cs:67-71 -->

离散时，相邻两格之间的面电导 = 两侧 $\sigma t$ 的调和平均 × 面长 ÷ 两格形心距。两格形心到面等距时，调和平均等于两个半格串联的电导，所以厚度突变处不失真。现在格的形心取材料形心，部分格到面不等距；程序对所有面都按等权调和平均取。<!-- 出处: Pt_Optimize/Core/ShellCurrent.cs:143-157；Pt_Optimize/Core/ShellThermal.cs:676-679 --> 面长取这条边上有料的长度（第 4.3 节）。

电流密度由各面的通量用最小二乘重构。料只剩两条边的楔形碎格，重构方向的条件数 $\kappa'$ 小于 0.2 时，只沿强方向重构；电位场不受影响。<!-- 出处: Pt_Optimize/Core/ShellCurrent.cs:56-64 SliverKappaMin = 0.2；HANDOVER.md:46 -->

仍开放的一处：管孔边的格目前是整格取零电位，还不是在孔边界面上取零电位。几何走一小步，被钉住的格就可能换一批，电极位置因此带半格量级的跳动。<!-- 出处: HANDOVER.md:482 F6；HANDOVER.md:425 门 b -->

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $t$ | 板厚（板面上逐点取值：板身、各级环、叉臂、舌片） | m |
| $\sigma$ | 铂电导率，= 1/ρ | S/m |
| $\varphi$ | 电位 | V |
| $\nabla$ | 板面内的二维梯度算子 | 1/m |
| $\mathbf{K}$ | 面电流密度（沿厚度积分的电流密度），守恒量 | A/m |
| $q_A$ | 单位板面积的发热（进入壳热方程的源项） | W/m² |
| $\kappa'$ | 按有料份额加权的重构方向条件数；满格为 1 | 无量纲 |
| $\rho$、$J$ | 同 3.1 节：电阻率、电流密度 | Ω·m、A/m² |

## 3.4 共用法兰的电流

每段各有一台可控硅调功，三台的一次侧分别接不同的相对。相邻两段的线电压因此相差 120°，阻性负载下电流也相差 120°。<!-- 出处: Pt_Optimize/Core/LineSolver.cs:125-129 -->

共用片处管子连续，左段电流流入、右段电流流出，法兰承担的是两段电流之差

$$I_{\text{共用}}=\left|I_2-I_1\right|=\sqrt{I_1^2+I_2^2+I_1 I_2},$$

两段电流相等时

$$I_{\text{共用}}=\left|I\,e^{j0}-I\,e^{j120^\circ}\right|=\sqrt{3}\,I.$$

这不是经验系数，是两段电流相量相减的结果。端片承担的就是相邻那一段的电流。<!-- 出处: Pt_Optimize/Core/LineSolver.cs:131-138,150-159 JointCurrentA；Pt_Optimize/Core/DesignCurrent.cs:32 -->

同一几何下发热 ∝ I²，共用片为端片的 3 倍。按 J 定截面（第 3.6 节）后，截面随电流放大 √3 倍，单位体积发热 ρJ² 不变，由第 3.2 节 P = ρJ²V，发热约为端片的 √3 倍。<!-- 出处: Pt_Optimize/Core/SectionSizing.cs:30-33 -->

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $I$、$I_1$、$I_2$ | 单段电流（有效值）；$I_1$、$I_2$ 为共用片左右两段 | A |
| $I_{\text{共用}}$ | 共用片承担的电流（两侧段电流的相量差） | A |
| $j$、$e^{j\theta}$ | 虚数单位与相位因子（θ 为相位角） | 无量纲；θ 以 ° 计 |

## 3.5 设计电流：20 °C/h 升温全程的峰值

稳态有玻璃时后两段的玻璃在加热管子，电流很小。空管升温才是尺寸的依据。管子自己的准静态电流由

$$I^2 R_{\text{管}}(T)=Q_{\text{管散热}}(T)+C_{\text{管}}\,\frac{dT}{dt}$$

给出，沿升温全程 25 → 1150 °C 取最大。散热随温度涨，峰在目标温度处。20 °C/h 下热容项约 1 W，散热是千瓦级。逐段各算，共用片按相量合成。<!-- 出处: Pt_Optimize/Core/LineRunner.cs:293 RampFromC = 25、RampTargetC = 1150；Pt_Optimize/Core/LineRunner.cs:313 RampRateKPerH = 20.0；Pt_Optimize/Core/DesignCurrent.cs:14-16；Pt_Optimize/Core/RampTwoNode.cs:653-655 -->

为什么不用「管 + 法兰」两节点模型的电流当尺寸依据：两节点里法兰自热会让法兰比管热、热倒灌回管子，管电流算少；而法兰电阻随板厚与温度变，闭式估计与场解可以差一个量级以上，设计电流会跳。<!-- 出处: Pt_Optimize/Core/DesignCurrent.cs:18-21 --> 尺寸依据不许被「法兰帮管子发热」省掉，也不许随法兰自己漂。所以取管子单独所需的电流：确定、闭式。两节点的结果另算一份当对照，不进尺寸链。<!-- 出处: Pt_Optimize/Core/DesignCurrent.cs:21,27 -->

它不是整线稳态段电流的上界。整线段解里有法兰抽热与段间耦合，管子单独的准静态电流里没有，段电流可以比它略大（原因是推断，未拆开验）。<!-- 出处: Pt_Optimize/Core/DesignCurrent.cs:22-27 --> W08／W06 在最终口径下两者之差：【待补：W08/W06 最终重解】。

设计电流只由管子、管保温与升温工况定。圆盘保温、法兰网格、外层停机容差都不进这个式子。<!-- 出处: Pt_Optimize/Core/RampTwoNode.cs:639-669 QuasiStaticBreakdown；Pt_Optimize/Core/DesignCurrent.cs:110-120 --> 比热只经热容项进来，比热改 10 % 只挪动设计电流 1e-5 量级。<!-- 出处: Pt_Optimize/Core/RampTwoNode.cs:653-655,666 --> W08／W06 在最终口径下的设计电流由最终重解印出：

| 设计 | 管壁 mm | 管保温 mm | 段电流 A | 各片电流 A |
|---|---|---|---|---|
| W08（管壁 0.8 · 留余量，3 段 4 片） | 0.8 | 5 | 【待补：W08/W06 最终重解】 | 【待补：W08/W06 最终重解】 |
| W06（管壁 0.6 · 底档，3 段 4 片） | 0.6 | 5 | 【待补：W08/W06 最终重解】 | 【待补：W08/W06 最终重解】 |

<!-- 出处: 管壁 Pt_Optimize/Core/DesignSpec.cs:1179,1225；管保温 Pt_Optimize/Core/DesignSpec.cs:83 -->

管保温越厚，升温时散热越少，设计电流越小。共用片在两侧段电流相等时是 √3 倍。

【待决定：v6.1 在这里并列了 2 段对照工况（管保温 10 mm）的设计电流。若摘要里的两个对照案例删去，这一句一并删去；若保留，须在最终口径下重算后再写数。】<!-- 出处: docs/Pt_理论模型_v6.1.md:120,216；本文摘要【待决定】 -->

判据「升温」看的就是这条式子：所需电流折成管电流密度，全程峰值不得超过管 J 许用 12 A/mm²。超了就被截住，按设定速率升不到目标。<!-- 出处: Pt_Optimize/Core/DesignCurrent.cs:107-121；Pt_Optimize/Core/DesignInputs.cs:326-327 TubeJAllowAPerMm2 = 12.0；Pt_Optimize/Core/Criteria.cs:114 -->

终验第一关把升温全程逐点再走一遍：8 个设定点 300／450／600／750／900／1000／1080／1150 °C，每点判场有效；管 J 与截面 J 在「设计电流」与「该点实际电流」两条上都不超限；伸长只报数。<!-- 出处: Pt_Optimize/Core/RampSweep.cs:176 TrajectoryC；Pt_Optimize/Core/FinalCheck.cs:88 Run；deliverable/R48_M_终验三关_生产路_W08_本次开跑于2026-09-18_152526.txt:21 --> W08／W06 在最终口径下这一关的结论：【待补：W08/W06 最终重解】。

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $I$ | 管段的准静态升温电流；全程峰值即设计电流 | A |
| $R_{\text{管}}(T)$ | 管段电阻，随温度变 | Ω |
| $Q_{\text{管散热}}(T)$ | 管段外表面在温度 T 下的散热功率（保温、辐射、对流） | W |
| $C_{\text{管}}$ | 管段热容（铂管质量 × 比热，加保温层热容的一半） | J/K |
| $dT/dt$ | 升温速率；20 °C/h，程序里除以 3600 换成 K/s（这里的 t 是时间，与 3.3 节的板厚 t 不同） | K/s |
| $T$ | 管金属温度 | °C |

<!-- 出处: C_管 含保温层热容的一半 Pt_Optimize/Core/RampTwoNode.cs:656-666（:662 那一行乘 0.5） -->

## 3.6 按 J 定截面

设计输入给定电流密度 J，预设 10 A/mm²，计算极限 J + 1。<!-- 出处: Pt_Optimize/Core/SectionSizing.cs:41-45 --> 法兰上电流必经的每一个截面都要满足 $I/A \le J$：

| 截面 | 在哪里 | 面积怎么算 |
|---|---|---|
| 舌片各处 | 沿舌长逐点，压接段除外 | 舌宽（扣掉舌孔弦长）× 舌片厚 |
| 舌盘交界 | 舌片与圆盘相切处 | 交界弦长（扣管孔弦、扣圆盘切口）× 厚，加上舌片脚印里那段孔边焊弧 × 孔边板厚 |
| 圆盘各圈 | 焊脚之外到盘缘，逐圈 | 2πr（扣槽弧长、扣盘上直孔弧长）× 该圈厚 |

<!-- 出处: Pt_Optimize/Core/SectionSizing.cs:12-16,186-248；Pt_Optimize/Core/LineRunner.cs:4227 -->

圆盘按整圈算，等于假设电流沿周向均匀进孔。电流实际从舌片一侧进，近舌侧局部 J 高于整圈平均值；局部峰值见参考量「法兰 J_max」。<!-- 出处: Pt_Optimize/Core/SectionSizing.cs:14-16,216-248；Pt_Optimize/Core/LineRunner.cs:4173-4180 -->

舌片厚由此闭式定下：

$$t_{\text{舌}}=\frac{I}{J\,w_{\min}},$$

其中 $w_{\min}$ 是舌片最窄有效宽（有舌孔时扣掉孔的弦长）。结果不低于参数表的「焊接工艺最小厚度」（默认 0.6 mm），再向上落到图纸格 0.01 mm。<!-- 出处: Pt_Optimize/Core/DesignSpec.cs:1062 SizeTongue 里的 Q()；Pt_Optimize/Core/DesignInputs.cs:265；Pt_Optimize/Core/Solver.cs:2643 QuantThickMm = 0.01 --> 舌根有切口（舌孔，或伸进舌根的圆盘背侧槽）时，切口那一段按带内最窄宽单独加厚成叉臂，带外还是杆厚。这就是 Y 形舌板的来历（第 8.4 节）。<!-- 出处: Pt_Optimize/Core/DesignSpec.cs:1066-1087 -->

例：W08 与 W06 的舌半宽都是 30 mm。<!-- 出处: Pt_Optimize/Core/DesignSpec.cs:1180,1226 W08、W06 的 TabHalfWidthMm = 30.0；deliverable/R48_L_端到端_格子0.5_W08_本次开跑于2026-09-17_164425.txt:20 --> 不开舌孔时的闭式舌片厚与圆盘侧各级截面给出的板厚下角：【待补：W08/W06 最终重解】。求解器若在最终解里开了舌孔，舌片厚随之重算。最终解的舌片厚与叉臂：【待补：W08/W06 最终重解】。

终验：全体截面的截面 J 小于设定 J + 1，作为判据「法兰截面 J」印在判据表里。带玻璃稳态与空管到温稳态它都卡交付；升温全程每个设定点还按该点实际电流再核一次。<!-- 出处: Pt_Optimize/Core/Criteria.cs:136；Pt_Optimize/Core/LineRunner.cs:1174；HANDOVER.md:640 -->

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $t_{\text{舌}}$ | 舌片厚（闭式，不是可调参数） | mm |
| $I$ | 该片的设计电流（端片 = 相邻段设计电流，共用片按 3.4 节相量合成）；W08／W06 的值【待补：W08/W06 最终重解】 | A |
| $J$ | 设计输入给定的电流密度，预设 10；极限 J + 1 | A/mm² |
| $w_{\min}$ | 舌片最窄有效宽（压接段以外；有舌孔时扣掉孔的弦长） | mm |
| $A$ | 某一必经截面的面积：舌片与舌盘交界 = 宽 × 厚（交界另加焊弧项），圆盘各圈 = 周长 × 厚 | mm² |

# 4 热学模型

## 4.1 管段：通电肋片方程

对轴向微元作能量平衡（导入、导出、焦耳生成、外表面散失、传给玻璃）：

$$\frac{d}{dx}\left(kA\frac{dT}{dx}\right)+\frac{\rho(T)\,I^2}{A}-h_{\text{eff}}P\,(T-T_\infty)-q_{\text{玻璃}}(T)=0.$$

与经典肋片方程的唯一区别是多了一个随温度变化的内部热源。正是这一项的温度依赖性带来热稳定性问题（第 4.5 节）。

空管时 $q_{\text{玻璃}}=0$，管腔成了连续的高温腔体，腔内辐射沿轴向传热，相当于在 $kA$ 上再加一份轴向导热 $kA_{\text{rad}}\propto T^3$。带玻璃时腔被玻璃充满，没有这条路。<!-- 出处: Pt_Optimize/Core/DesignInputs.cs:613-618 --> 程序按段控温点温度的三次方，从 1150 °C 时的系数换算。系数默认 0.023 W·m/K，另一档估计是 0.056 W·m/K。两档都是推理估算，未经实测，两档之间还没有依据取舍。<!-- 出处: Pt_Optimize/Core/SegmentSolver.cs:497,505 CavityRadRefC、CavityRadKA1150Low／High；Pt_Optimize/Core/DesignInputs.cs:614,622-629,664 --> 【待决定：空管管腔辐射系数取 0.023 还是 0.056 W·m/K；定之前，空管到温的结论是否两档并报】

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $x$ | 沿管轴的坐标 | m |
| $T$ | 管金属温度 | K（差值即 °C） |
| $k$ | 铂导热系数，纯铂式：71.6（0 °C）→ 83.8（1150 °C）→ 85.4（1300 °C）；700 °C 以上为推荐值外推，带宽 ±8 %（1150 °C 处 77.1～90.5） | W/(m·K) |
| $A$ | 管壁截面积 = π·(外径² − 内径²)/4 | m² |
| $\rho(T)$ | 铂电阻率（纯铂二次拟合） | Ω·m |
| $I$ | 管电流 | A |
| $h_{\text{eff}}$ | 外表面等效换热系数（保温层导热 + 外表面辐射与对流，线性化；第 4.8 节） | W/(m²·K) |
| $P$ | 管外周长 = π·外径 | m |
| $T_\infty$ | 环境温度，25 °C（298 K） | K |
| $q_{\text{玻璃}}(T)$ | 单位长度传给管内玻璃的热流 = 内壁换热系数 × 内周长 × (T − T_玻璃)；空管为 0 | W/m |
| $kA_{\text{rad}}$ | 空管管腔轴向辐射折成的等效轴向导热（带玻璃为 0） | W·m/K |

<!-- 出处: k 行 Pt_Optimize/Core/Materials.cs:52,57-66,69；T∞ 行 Pt_Optimize/Core/DesignInputs.cs:197；kA_rad 行 Pt_Optimize/Core/SegmentSolver.cs:346,447 -->

## 4.2 特征长度与管根增量温降的闭式

令 $\theta$ 为对平台温度的偏离。线性化后管段的轴向刚度为散热与导热的几何平均。设法兰自管孔抽走热流 $Q$，则管根的增量温降

$$\Delta T_{\text{根}}=\frac{Q}{\sqrt{\left(h'P+\text{玻璃耦合}\right)\,kA}}.$$

上式是管只在一侧（半无限肋）的情形，对应端片。共用片两侧都有管，抽热两侧分摊；两侧刚度相同时

$$\Delta T_{\text{根}}=\frac{Q}{2\sqrt{\left(h'P+\text{玻璃耦合}\right)\,kA}}.$$

这是本文的核心闭式：法兰造成的管根增量温降，等于抽热量除以「管子沿轴向的散热与导热几何平均刚度」。三条推论：想压增量温降就得压抽热，别无他途；管保温越厚（h' 越小）增量温降越大，与直觉相反；减薄管壁（A 小）会加深法兰冷点。

判据怎么用这条闭式。控温热偶装在每段中点，判定以热偶读数为基准，不以模型算的管根温度为基准，因为后者会随设计变量挪动。<!-- 出处: Pt_Optimize/Core/Criteria.cs:121-130；HANDOVER.md:5476-5477 --> 判据「管根低于热偶读数」= 热偶读数基准 − 接头处管根较冷端。<!-- 出处: Pt_Optimize/Core/LineRunner.cs:1069-1070 --> 在同一管根点上，它可以拆成两项：

$$\text{管根低于热偶读数}=\underbrace{\left(T_{\text{热偶}}-T_{\text{根,无法兰}}\right)}_{\text{控温点梯度}}+\Delta T_{\text{根}}.$$

前一项由控温点梯度定，法兰管不着；后一项就是上面的闭式。<!-- 出处: Pt_Optimize/Core/Criteria.cs:167,170 FlangeDip 与 SetpointDrift 两行的说明；HANDOVER.md:5477 --> 于是抽热窗口的两侧都落在 $Q$ 上：$Q > 0$ 是判据「管孔净流入」；$Q$ 太大，$\Delta T_{\text{根}}$ 太大，管根低于热偶读数越过预算。预算是设计输入，默认 5 K，取自热偶读数误差。<!-- 出处: Pt_Optimize/Core/DesignInputs.cs:94,100；Pt_Optimize/Core/LineRunner.cs:328 ThermocoupleErrorK = 5.0 --> 这两条只在带玻璃稳态卡交付，空管到温稳态只作参考。<!-- 出处: Pt_Optimize/Core/LineRunner.cs:1159-1177 RequiredByState --> 旧判法「法兰增量温降」直接拿 $\Delta T_{\text{根}}$ 比 10 K，现只作对照，不卡交付。<!-- 出处: Pt_Optimize/Core/LineRunner.cs:1074；HANDOVER.md:5479 --> W08／W06 在最终口径下的 $Q$ 与这两条判据的值：【待补：W08/W06 最终重解】。

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $\Delta T_{\text{根}}$ | 法兰造成的管根增量温降（参考量「法兰增量温降（旧判法）」，对照限 10 K） | K |
| $Q$ | 这一片法兰自管孔抽走的总热流（判据「管孔净流入」，带玻璃稳态须 > 0） | W |
| $h'$ | 管外表面散热对温度的切线斜率（线性化外散热系数） | W/(m²·K) |
| $P$ | 管外周长 | m |
| 玻璃耦合 | 单位长度的玻璃换热 = 内壁换热系数 × π × 内径 | W/(m·K) |
| $k$、$A$ | 同 4.1 节：铂导热系数、管壁截面积 | W/(m·K)、m² |
| $\theta$ | 对平台温度的偏离 | K |
| $T_{\text{热偶}}$ | 热偶读数基准：端片取本段设定值，共用片取两侧设定值的开尔文对数平均 | °C |
| $T_{\text{根,无法兰}}$ | 同一管根点在无法兰基线下的温度 | °C |

<!-- 出处: T_热偶 行 Pt_Optimize/Core/Criteria.cs:125；HANDOVER.md:5476 -->

## 4.3 法兰：二维壳

单位面积发热由 3.3 节给出。散热按分区取：圆盘区是 r ≤ 盘半径的整块圆盘，取圆盘保温；舌片取舌保温；没包的面按裸铂。拿不到盘半径时才退回旧规则（按舌片切点的 x 划一刀），并把用了哪条规则写进说明。<!-- 出处: Pt_Optimize/Core/ShellThermal.cs:135-144 DiscZoneRule、InsulRule；Pt_Optimize/Core/ShellThermal.cs:1058；HANDOVER.md:1906 --> 保温分界圆穿过的格，按格里落在圆内的有料面积份额混合两种散热。<!-- 出处: Pt_Optimize/Core/PlateMaterial.cs:18-22 --> 分区热账与参考量「圆盘区最高温 − 管温（旧判法）」目前还按格心归属。三条热学硬判据不吃这个分区：最热铂取两区之最大，管根与净流入不分区。<!-- 出处: HANDOVER.md:483 F4 -->

网格按板的真实形状建：

- 每一格的料按解析板逐列精确积分，得到面积、体积与材料形心；格的形心取材料形心。<!-- 出处: Pt_Optimize/Core/PlateMaterial.cs:18-27,55-61；HANDOVER.md:410 -->
- 板厚按分区分段取常数（板身、各级环、叉臂带、舌片），再叠加焊脚环。<!-- 出处: HANDOVER.md:410 -->
- 相邻两格之间的面长 = 这条边上有料的长度。零长度不建面。<!-- 出处: HANDOVER.md:411；Pt_Optimize/Core/ShellMesh.cs:1379 BuildFromMaterial -->
- 管孔边界面就是落在格里的那段圆弧。<!-- 出处: Pt_Optimize/Core/ShellMesh.cs:1529 附近 AddHoleArcFaces；HANDOVER.md:411 -->
- 孔圆切出的外角薄片格，以及覆盖率低于 1e-3 的碎格，并入共有最长有料边的邻格。<!-- 出处: Pt_Optimize/Core/ShellMesh.cs:1145-1151 CellMergeFrac = 1e-3；HANDOVER.md:411 -->
- 面导热 = 两侧 $k t$ 的调和平均 × 面长 ÷ 形心距，与 3.3 节的面电导同一个道理。<!-- 出处: Pt_Optimize/Core/ShellThermal.cs:664-684 -->

用这套网格，W08 与 W06 共 536 张几何上，每片总体积、环内体积、舌区体积对参考积分的偏差都在 0.1 % 以内。<!-- 出处: HANDOVER.md:424 改后一列 -->

管温施加在管孔边界的弧面上。从管子抽走的热量 = 通过这些弧面的热流之和，逐面直接算：<!-- 出处: Pt_Optimize/Core/ShellThermal.cs:440-446,462 holeFaceDirichlet = true -->

$$Q=\sum_{\text{孔边面}} g_{\text{面}}\left(T_{\text{管}}-T_{\text{格}}\right).$$

<!-- 出处: Pt_Optimize/Core/ShellThermal.cs:960-964 -->

压接段同理，夹持温度施加在压接面上。<!-- 出处: Pt_Optimize/Core/ShellThermal.cs:675-681 --> 能量闭合残差 = Σ(发热 − 散热) + 管孔净流入 − 铜排带走，用来核对场解。管孔与铜排两项都是直接算的，不用能量恒等式反推，否则对账就成了循环论证。<!-- 出处: Pt_Optimize/Core/ShellThermal.cs:53-61 -->

【待决定：是否补一张示意图：一格里的材料、按有料长度裁剪的面、管孔圆弧。】<!-- 出处: docs/_v7parts/口径变更清单_v6.1之后.md:173 -->

仍开放的一处：发热目前按格算（ρJ²tA），不是按面。等温电流场上，按格累加的发热与按端电压算的耗散之比为 1.002～1.009。<!-- 出处: HANDOVER.md:484 F3 -->

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $Q$ | 法兰自管孔抽走的热流（同 4.2 节） | W |
| $g_{\text{面}}$ | 孔边弧面的导热 = 格一侧的 $k t$ × 弧长 ÷ 材料形心到弧中点的距离 | W/K |
| $T_{\text{管}}$ | 管根温度（由管段解给出，施加在弧面上） | °C |
| $T_{\text{格}}$ | 与该弧面相邻那一格的温度 | °C |

<!-- 出处: g_面 行 HANDOVER.md:411（DistAB = 材料形心到弧中点）；Pt_Optimize/Core/ShellThermal.cs:440-446 -->

## 4.4 辐射线性化：割线与切线

在任何线性化或摄动分析中须用切线斜率 $4\varepsilon\sigma T^3$ 而不是割线斜率。二者之比以 1150 °C 与 25 °C 计为 3.17（1300 °C 时 3.25）。误用割线会把散热的抗扰动能力低估约 3.2 倍。本模型一律使用数值切线。<!-- 出处: HANDOVER.md:3234（R44 审查：1150 °C 应为 3.17，3.25 是 1300 °C） -->

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $\varepsilon$ | 表面发射率：铂 0.18、保温外表面 0.45、铜排（氧化铜）0.7 | 无量纲 |
| $\sigma$ | 斯特藩-玻尔兹曼常数，5.670374×10⁻⁸（本节专用；3.3 节的 σ 是电导率） | W/(m²·K⁴) |
| $T$ | 表面绝对温度 | K |
| $4\varepsilon\sigma T^3$ | 辐射散热对温度的切线系数 | W/(m²·K) |

<!-- 出处: ε 行 Pt_Optimize/Core/DesignInputs.cs:348,352；Pt_Optimize/Core/BusbarSizing.cs:109,141；σ 行 Pt_Optimize/Core/Materials.cs:161 -->

## 4.5 局部热失稳：稳定条件只含材料物性

恒流下某处温度升高 → 电阻率升高 → 该处发热更多，构成正反馈。把包保温的舌片视为「两端定温、通电、绝热」的杆。两端的定温锚点一端是管孔（经圆盘），一端是压接段；最不利点在两者正中，$L$ = 自由段/2。<!-- 出处: Pt_Optimize/Core/LocalStability.cs:44-58 TabHalfSpanMm；Pt_Optimize/Program.cs:8020-8024 -->

局部稳定给发热一个上界。包保温时表面散热的温度导数可略，稳定要求

$$J\le J_{\text{stab}}=\frac{1}{L}\sqrt{\frac{k}{\rho\cdot\mathrm{TCR}}},$$

横向导热按 k·t/L² 取量级（程序口径）。两端定温杆基频模态的系数为 π/2，比 1 大，所以本式偏保守，下面的可行界同样偏保守。<!-- 出处: Pt_Optimize/Core/LocalStability.cs:16-22；Pt_Optimize/Program.cs:8024-8026 -->

代入 $P=I\,J\,\rho\,\ell$、$L=\ell/2$，得

$$P_{\max}=2I\sqrt{\rho k/\mathrm{TCR}}.$$

舌长与截面全部约掉。<!-- 出处: Pt_Optimize/Program.cs:8024-8026；Pt_Optimize/Core/LocalStability.cs:16-22 -->

导热漏给发热一个下界。带均布发热、两端定温的杆，从热端流进杆的热 = $kA\Delta T/\ell - P/2$：发的热一半往热端走、一半往冷端走。令它为零（不抽管），得 $P=2kA\Delta T/\ell$。与 $P=I^2\rho\ell/A$ 相乘，$\ell/A$ 抵消：

$$P_{\min}=I\sqrt{2\rho k\,\Delta T}.$$

<!-- 出处: Pt_Optimize/Program.cs:8028-8035 -->

两者相除：

$$\frac{P_{\max}}{P_{\min}}=\sqrt{\frac{2}{\mathrm{TCR}\cdot\Delta T}},\qquad \text{可行}\iff \mathrm{TCR}\cdot\Delta T\le 2.$$

既不含几何，也不含电流。纯铂在舌片 900～1400 °C、夹持 25～600 °C 的整个范围内 TCR·ΔT ≈ 0.21～0.62，裕度 1.8～3.1，全区可行。<!-- 出处: Pt_Optimize/Program.cs:8037,8069-8070；HANDOVER.md:5214 --> 这组数用的是纯铂的电阻率与导热系数（第 4.9 节）。

TCR 随温度下降而升高：25 °C 时约 3.58×10⁻³ /K，1150 °C 时只有 5.48×10⁻⁴ /K，相差 6.5 倍。冷态下表面散热的温度导数又趋近于零。两头夹击，升温初段是局部失稳最危险的窗口，稳态反而宽松。<!-- 出处: Pt_Optimize/Core/LocalStability.cs:24-29 -->

判据表里以两条参考量印出，都不卡交付：<!-- 出处: Pt_Optimize/Core/Criteria.cs:173-174 -->

- 「整片热稳定」= 散热对温度的导数 ÷ 发热对温度的导数，须 > 1。逐片评，取裕度最小的那一片；任一片判不了，整条判不了。<!-- 出处: Pt_Optimize/Core/LineRunner.cs:1095-1096；HANDOVER.md:2722 J6 -->
- 「局部热稳定」= $J_{\text{stab}}$ ÷ 实际 J，取峰值点，须 > 1。<!-- 出处: Pt_Optimize/Core/LineRunner.cs:1097-1098 -->

W08／W06 在最终口径下这两条的裕度：【待补：W08/W06 最终重解】。

一条必须记住的机理：压接段不发热。铜排把那一段短接（铜比铂导电 25 倍、厚 10 倍），舌片有效发热长度 = 舌长 − 压接长。<!-- 出处: HANDOVER.md:7364；Pt_Optimize/Program.cs:8109 -->

| 符号 | 含义 | 单位 |
|:---|:------------------|:-------|
| $J_{\text{stab}}$ | 局部失稳电流密度上限 | A/m² |
| $L$ | 最不利点到最近定温锚点的距离 = 自由段/2 | m |
| $\ell$ | 舌片发热段长度（自由段） | m |
| $A$ | 舌片截面积 | m² |
| $k$、$\rho$ | 铂导热系数、电阻率（纯铂） | W/(m·K)、Ω·m |
| TCR | 电阻温度系数 = (1/ρ)(dρ/dT) | 1/K |
| $I$、$J$ | 舌片电流、电流密度 | A、A/m² |
| $P$、$P_{\max}$、$P_{\min}$ | 舌片发热；局部稳定允许的上界；不抽管所需的下界 | W |
| $\Delta T$ | 舌片热端与冷端（压接段）的温差 | K |

<!-- 出处: TCR 定义 Pt_Optimize/Core/Materials.cs:43-49 PtTcr -->

## 4.6 工艺下界：由焊接定，不由强度定

强度约束关闭后，壁厚下界改由焊接给出。可算的那一半是屈曲变形：焊缝冷却时纵向收缩，在焊缝附近留下受拉区，靠板的其余部分受压来平衡。板越薄抗压屈曲能力越弱（$\sigma_{cr}\propto t^2$），到某个厚度以下就鼓曲。由收缩力（tendon force）、全熔透线能量与板屈曲临界应力联立，E 与 ρ 都约掉：

$$t_{\min}=\frac{12(1-\nu^2)\,C\,\beta\,\alpha\,\left(\bar c\,\Delta T_m+L_f\right)}{k_b\,\pi^2\,c\,\eta_{\text{melt}}}\;b.$$

<!-- 出处: Pt_Optimize/Core/WeldDistortion.cs:11-27 推导；Pt_Optimize/Core/WeldDistortion.cs:98-106 Slope -->

结果只取决于材料的热学量与三个工艺系数，并正比于板的无支撑宽度 b。它给出的是 t/b 的下限，必须按每条焊缝各自的 b 来算。三个系数里板屈曲系数 $k_b$ 是大头：四边简支 4.0，三边简支一边自由 0.43，差 9.3 倍。法兰盘是后一种。<!-- 出处: Pt_Optimize/Core/WeldDistortion.cs:52-73 -->

| 符号 | 含义 | 程序取值（纯铂） | 单位 |
|:---|:--------------|:-----|:----|
| $t_{\min}$ | 不鼓曲的最小板厚 | 结果 | mm |
| $b$ | 板的无支撑宽度（法兰盘：环宽 = 盘半径 − 管孔半径） | 逐案 | mm |
| $\nu$ | 泊松比 | 0.39 | 无量纲 |
| $C$ | 收缩力（tendon force）经验系数，在钢上标定 | 0.2 | 无量纲 |
| $\beta$ | 焊道宽 / 板厚 | 2.0 | 无量纲 |
| $\alpha$ | 线膨胀系数（室温到熔点均值） | 1.0×10⁻⁵ | 1/K |
| $\bar c$、$c$ | 0 °C 到熔点的平均比热（分子分母同一个量） | 145 | J/(kg·K) |
| $\Delta T_m$ | 熔点 − 初温 | 1768.2 − 20 | K |
| $L_f$ | 熔化潜热 | 113×10³ | J/kg |
| $k_b$ | 板屈曲系数：三边简支一边自由 | 0.43 | 无量纲 |
| $\eta_{\text{melt}}$ | 熔化效率 | 0.35 | 无量纲 |
| $\sigma_{cr}$ | 板的屈曲临界压应力（推导中出现，∝ t²） | 推导中约掉 | Pa |
| $E$、$\rho$ | 弹性模量（168 GPa）、密度：推导中约掉，结果不含 | 推导中约掉 | Pa、kg/m³ |
| $t$ | 板厚（推导中 σ_cr ∝ t²） | 推导中出现 | mm |
| π | 圆周率 | 3.1416 | 无量纲 |
| SF | 安全系数，乘在 t_min 上 | 2.0 | 无量纲 |

<!-- 出处: ν、α、E、L_f、c̄ 行 Pt_Optimize/Core/Materials.cs:126-143；C、β、η、k_b 行 Pt_Optimize/Core/WeldDistortion.cs:47-73；ΔT_m 行 Pt_Optimize/Core/Materials.cs:35 PtMeltC 与 Pt_Optimize/Core/WeldDistortion.cs:111 startTempC = 20；SF 行 Pt_Optimize/Core/DesignInputs.cs:276 -->

安全系数 2.0 乘在 $t_{\min}$ 上，只覆盖 C、β、η 三个工艺系数的不确定度；$k_b$ 已按最不利取。<!-- 出处: Pt_Optimize/Core/DesignInputs.cs:268-276 -->

按 b 列出（含 SF 2.0，烧穿下界取默认 0.6 mm）：

| 无支撑宽度 b mm | 屈曲下界 mm | 与烧穿 0.6 取大 mm | 控制机理 |
|---|---|---|---|
| 34 | 4.71 | 4.71 | 屈曲 |
| 10 | 1.39 | 1.39 | 屈曲 |
| 4 | 0.55 | 0.6 | 烧穿 |

<!-- 出处: 前两列 HANDOVER.md:7510-7516；烧穿 0.6 Pt_Optimize/Core/DesignInputs.cs:265 -->

两份设计的盘半径都是 30 mm：<!-- 出处: Pt_Optimize/Core/DesignSpec.cs:1180,1226 W08、W06 定义中 DiscRadiusMm = 30.0 -->

| 设计 | 管孔半径 mm | b mm | 屈曲下界 mm | 取大 mm | 控制机理 |
|---|---|---|---|---|---|
| W08（管壁 0.8） | 25.8 | 4.2 | 0.58 | 0.60 | 烧穿 |
| W06（管壁 0.6） | 25.6 | 30 − 25.6 | 0.61 | 0.61 | 屈曲 |

<!-- 出处: 管孔半径 Pt_Optimize/Core/DesignSpec.cs:359,511，W08 25.8 与 b 4.2 见 HANDOVER.md:1039（「30 − 25.8 = 4.2 mm」），W06 25.6 见 deliverable/R48_L_端到端_格子0.5_W06_本次开跑于2026-09-17_164520.txt:20；W08 屈曲 0.58 Pt_Optimize/Core/DesignSpec.cs:516；取大一列 deliverable/R48_L_端到端_格子0.5_W08_本次开跑于2026-09-17_164425.txt:251 与 deliverable/R48_L_端到端_格子0.5_W06_本次开跑于2026-09-17_164520.txt:251（焊接下界只由盘径、管孔半径与焊接常数定，WeldDistortion.cs 与 Materials.cs 的焊接常数在这两跑之后未改） -->

管（圆筒）走另一条判据：环缝收缩的轴压与筒轴压临界值相比，t 与 R 同时约掉。纯铂这个比值约 0.007，远小于 1，任何壁厚都不会被环缝压屈。管壁下界只能由烧穿定。<!-- 出处: Pt_Optimize/Core/WeldDistortion.cs:112-126 -->

三条可执行结论：

1. 缩小圆盘直径同时放松焊接下界，与省铂、与端片热平衡三者同向。
2. 圆盘的焊接下界是否被按 J 定出的板厚盖住，要拿按 J 定截面给出的板厚下角来比：【待补：W08/W06 最终重解】。
3. 舌片与圆盘同板切出、无焊缝，舌厚不受随 b 变的屈曲下界约束，但同受参数表「焊接工艺最小厚度」的下限。<!-- 出处: Pt_Optimize/Core/DesignSpec.cs:1062 -->

烧穿下界算不出来，只能由焊法定：手工 TIG 约 0.5、自动 TIG 约 0.3、激光或电阻缝焊约 0.1 mm，这三个是行业常规，不是本项目实测。现场目前是手工焊接。<!-- 出处: HANDOVER.md:7526,7529 --> 参数表默认 0.6 mm，参数表说明自己写明这是待现场确认的假设，不是实测。<!-- 出处: Pt_Optimize/Core/DesignInputs.cs:259-265 -->

三处没有论证完的地方，照实写在这里：

- 法兰盘是内边焊死、外边自由的环。把「一边自由的长条板」的 $k_b$ 用在环上，是一次没有论证的模型替换：环缝是周向收缩把环往里箍，与长条板受单向压不是一回事。<!-- 出处: Pt_Optimize/Core/WeldDistortion.cs:68-71 -->
- $C$ 是在钢上标定的，铂上没有标定过。<!-- 出处: Pt_Optimize/Core/DesignInputs.cs:269 -->
- 平均比热 $\bar c$ 取 145。按第 4.9 节的纯铂比热式在 0 °C 到熔点取中点约 157，差 8 %。改它会挪板厚下角与铂重。<!-- 出处: Pt_Optimize/Core/Materials.cs:134-143 --> 【待决定：焊接屈曲下界里的平均比热是否改用新比热式；改则板厚下角与铂重跟着变，需单独复核】

APP 的「参考工具 ▸ 焊接下界小算盘」是这条公式的独立算盘：改一格当场重算，并列出一串盘径看从哪一档起由屈曲控制。它不进计算链，不改页面上的数。<!-- 出处: Pt_Optimize/UI/ManualPage.cs:1169；Pt_Optimize.Tests/WeldFloorCalcTests.cs:9 -->

## 4.7 熔化：只判断，不当可调参数

纯铂熔点 1768.2 °C。<!-- 出处: Pt_Optimize/Core/Materials.cs:35 PtMeltC --> 方程本身不会拒绝熔化的解：电阻率二次拟合的温度斜率要到 3392 °C 才变号，发热随温度一直上升，所以没有熔点这道判断，场解会给出远超熔点的温度和一份闭合的能量账。<!-- 出处: Pt_Optimize/Core/Materials.cs:24-33,43-49 --> 熔化因此必须显式判。

按 J 定截面之后，各片在约束盒下角本不该熔。处置分三处，板厚一位都不动：<!-- 出处: Pt_Optimize/Core/Solver.cs:1606-1635 MeltFloor 与 EvalMelt -->

- 求解器每遍开头验一次。已提交的解熔了，就停机，判「该解不存在」，不抬厚度。<!-- 出处: Pt_Optimize/Core/Solver.cs:31,358,404-411 -->
- 试探点熔了，该候选不成立；二分中点熔了，判不了。<!-- 出处: Pt_Optimize/Core/Solver.cs:948,1375 -->
- 判据层：一片最高温越过表面散热表上限（即铂熔点），这一片的场就判不了。吃这一片场的判据一律判不了，判不了不算过。<!-- 出处: Pt_Optimize/Core/LineRunner.cs:2307-2315 -->

熔化说明两件事之一：闭式截面漏了一刀（先看判据表「法兰截面 J」那行的最紧截面），或 J 设得太高。<!-- 出处: Pt_Optimize/Core/Solver.cs:404-406 --> 熔化区里的温度数一个都不引用。

## 4.8 保温：交付的是材质与各区厚度

散热通道是串联的：铂表面 → 各保温层导热 → 外表面辐射与自然对流。每层导热系数取 $k(T)=k_0+k_1T$，逐层串联。管按同心圆筒层算，法兰按平板层两面算。<!-- 出处: Pt_Optimize/Core/RampTwoNode.cs:650-651 Insulation.CylinderLoss；Pt_Optimize/Core/DesignScreen.cs:169-182 PlateFluxWPerM2 --> 法兰上保温厚不小于 0.05 mm 才按「包着」算，外表面发射率取 0.45；否则按裸铂，发射率 0.18。<!-- 出处: Pt_Optimize/Core/DesignScreen.cs:155-159；Pt_Optimize/Core/DesignInputs.cs:348,352 --> 法兰保温的材料取参数表内层（贴铂）那一层的 $k(T)$。<!-- 出处: Pt_Optimize/Core/DesignScreen.cs:175-179 -->

三个区，各自一个厚度：

| 区 | 模型里怎么取 | 现行取值 | 谁定 |
|---|---|---|---|
| 管 | 沿全长等厚 | W08／W06 为 5 mm | 设计输入 |
| 圆盘（r ≤ 盘半径） | 整盘一个厚度，分不了内外环 | 默认 20 mm；界面上界 60 mm | 设计输入；两个数都是历史值，出处待补 |
| 舌片 | 逐片一个厚度 | 裸舌 0.30 mm，或 0.5 mm 的整数倍 | 求解器可调参数 |

<!-- 出处: 管 Pt_Optimize/Core/DesignSpec.cs:83 TubeInsulMm = 5.0；deliverable/R48_L_端到端_格子0.5_W08_本次开跑于2026-09-17_164425.txt:19；deliverable/R48_L_端到端_格子0.5_W06_本次开跑于2026-09-17_164520.txt:19 -->
<!-- 出处: 圆盘 Pt_Optimize/Core/DesignSpec.cs:205-215 FlangeInsulMm = 20.0；Pt_Optimize/Core/DesignSpec.cs:217-223 FlangeInsulMaxMm = 60.0；HANDOVER.md:1035-1037 -->
<!-- 出处: 舌片 Pt_Optimize/Core/Solver.cs:2636 InsLoMm = 0.3；Pt_Optimize/Core/Solver.cs:2654-2668 QuantInsulMm = 0.5 -->

舌保温的合法值只有两类：什么都不缠时的等效厚度 0.30 mm（0 层），或 n 层 × 每层 0.5 mm。<!-- 出处: Pt_Optimize/Core/Solver.cs:2636 InsLoMm = 0.3；Pt_Optimize/Core/Solver.cs:2668 QuantInsulMm = 0.5；Pt_Optimize/Core/InsulationSearch.cs:94 LayerMm = 0.5 --> 二分在图纸格点上走：每个中点先向上对齐到格再评估，括号宽不超过一格即停；舌保温一格 0.5 mm，板厚一格 0.01 mm。停下时上端是判得过的最小格点，所以交出的值就是现场包得出来的层数。<!-- 出处: Pt_Optimize/Core/Solver.cs:1362-1368,1399；HANDOVER.md:338 -->

裸舌在热解里按 0.30 mm 内层保温、外表面发射率 0.45 计散热，不按裸铂 0.18：0.30 大于「包着」的门槛 0.05 mm。<!-- 出处: Pt_Optimize/Core/DesignScreen.cs:155-159,169-181；Pt_Optimize/Core/ShellThermal.cs:335-338；Pt_Optimize/Core/Solver.cs:2635-2636 --> 0.30 这个等效厚度，仓库里查不到出处。【待决定：裸舌等效厚度 0.30 mm 的出处由谁给定】 舌保温的搜索上界 20 mm 没有出处。<!-- 出处: Pt_Optimize/Core/Solver.cs:2598 InsHiMm = 20.0；HANDOVER.md:79 -->

交付物是保温方案：每区一行，材质、$k(T)$ 与出处、厚度、折合层数、圈数提示。怎么包（缠绕还是预制保温块）由现场定。<!-- 出处: Pt_Optimize/Core/InsulationPlan.cs:47；Pt_Optimize/Core/WrapLimits.cs:81-83 PlanOnlyNote --> 管与圆盘接合区的缠绕圈数只作参考行：参考线 20 圈 × 每圈 0.5 mm = 10 mm，超过就提示改用预制保温块，不卡交付。<!-- 出处: Pt_Optimize/Core/WrapLimits.cs:68-75；Pt_Optimize/Core/LineRunner.cs:1172 RequiredByState 该行两态均为 Reference --> W08／W06 在最终口径下的保温方案表：【待补：W08/W06 最终重解】。

终验时逐片把舌保温在解值 ±1.0 mm 内按 0.1 mm 挪动，其余片与其余参数不动，每点跑一次整线，量出这一片舌保温的可行窗口与窗口里落得进的层数。<!-- 出处: HANDOVER.md:782-785；Pt_Optimize/Core/InsulWindow.cs:225 Measure；Pt_Optimize/Core/DesignInputs.cs:107 --> 窗口的读法见求解器一章。W08／W06 在最终口径下的窗口：【待补：W08/W06 最终重解】。

模型没有覆盖、照实写出的四处：

- 三层保温材料的 $k(T)$ 系数在仓库里查不到出处，保温方案表上印「出处待补」。<!-- 出处: HANDOVER.md:566；Pt_Optimize/Core/Insulation.cs:37 KSourceNote -->
- 圆盘保温默认 20 mm 与界面上界 60 mm 都查不到出处。<!-- 出处: Pt_Optimize/Core/DesignSpec.cs:210-211,221 --> 【待决定：圆盘保温默认 20 mm 的出处由谁给；给出之前，正文与判据说明一律写「历史值，出处待补」】
- 圆盘保温整盘只有一个厚度，「内环带薄、外区厚」目前表达不了。<!-- 出处: HANDOVER.md:1035-1037 -->
- 管保温沿全长等厚。现场管端靠圆盘那一小段会渐变减薄，模型没建。影响是局部的，方向偏乐观：角部实际比模型多散一点热。<!-- 出处: HANDOVER.md:879-882 -->

## 4.9 物性：哪些按牌号，哪些按纯铂

参数表可选的牌号只有四类数据（电阻率、热膨胀、持久强度、热导率与比热）齐全的 4 个：Pt、Pt-Rh/90-10、Tanaka-ZGS-Pt、Tanaka-ZGS-PtRh10。材料库共 12 个，其余 8 个灰显，并写明缺哪几类。默认纯铂。<!-- 出处: HANDOVER.md:599,604；Pt_Optimize/Core/GradeChoices.cs:89,97 -->

选了牌号之后，各物性走哪一支：

| 物性 | 进到哪里 | 按牌号还是纯铂 |
|---|---|---|
| 电阻率 ρ(T) | 段解焦耳热、法兰壳电流场、板件二维电流、局部热稳定 | 纯铂（二次拟合，0～1500 °C） |
| 导热系数 k(T) | 段解轴向导热、法兰壳热解、板件二维热、升温两节点 | 纯铂（700 °C 以上为推荐值外推，±8 %） |
| 比热 cp(T) | 设计电流的热容项、升温求解 | 纯铂（Kaye & Laby 四点拟合，500 °C 以上外推） |
| 密度 | 整线铂重 | 纯铂 21 450 kg/m³ |
| 密度 | 分段解析强度与铂重页 | 按牌号 |
| 持久强度 | 管强度利用率（参考量） | 按牌号 |
| 热膨胀 | 终验第一关的伸长量（只报数） | 按牌号 |
| 焊接屈曲用的 α、E、ν、L_f、c̄ | 焊接屈曲下界（第 4.6 节） | 纯铂文献值 |

<!-- 出处: 电阻率行 HANDOVER.md:193；Pt_Optimize/Core/Materials.cs:40；Pt_Optimize/Core/LocalStability.cs:68-69 -->
<!-- 出处: 导热系数行 HANDOVER.md:194；Pt_Optimize/Core/Materials.cs:57-69 -->
<!-- 出处: 比热行 HANDOVER.md:195；Pt_Optimize/Core/Materials.cs:86-117 -->
<!-- 出处: 密度两行 Pt_Optimize/Core/LineRunner.cs:2209,2923；Pt_Optimize/Core/LineSolver.cs:44-46；Pt_Optimize/UI/Flow.cs:46 -->
<!-- 出处: 持久强度行 HANDOVER.md:197；Pt_Optimize/Core/Mechanics.cs:131；Pt_Optimize/Core/TubeStrength.cs:210 -->
<!-- 出处: 热膨胀行 HANDOVER.md:196；Pt_Optimize/Core/FinalCheck.cs:110 -->
<!-- 出处: 焊接行 Pt_Optimize/Core/Materials.cs:120-143 -->

换句话说：在界面上把牌号改成 Pt-Rh/90-10，强度与伸长那两路会变，电、热那一路一位都不动。参数表的牌号说明已照此写明。<!-- 出处: Pt_Optimize/Core/DesignInputs.cs:170-184 GradeNameNote -->

纯铂两支（求解链写死的那一支与按牌号的那一支）在 1150 °C 逐位相同，所以对纯铂接不接线没有差。差全在非纯铂牌号上：Pt-Rh/90-10 对纯铂，电阻率最大 +84.91 %（20 °C；1150 °C 处 +2.80 %），热导率最大 −40.82 %（20 °C；1150 °C 处 −17.29 %），比热最大 −6.07 %（600 °C）。<!-- 出处: HANDOVER.md:667-673；deliverable/R48_M_物性按牌号与纯铂差量_本次开跑于2026-09-18_143953.txt --> 这些差对各判据往哪个方向推，没有算过。<!-- 出处: HANDOVER.md:676 --> 【待决定：电阻率、热导率、比热按牌号接进求解链之前，本文的热电结论是否声明只对纯铂（及数据与纯铂相同的 Tanaka-ZGS-Pt）成立】

持久强度的数据区间有下限。纯铂持久强度拟合下限 1100 °C，段控温点低于它时不外推，改用拟合下限温度处的断裂强度当保守许用值，算出的利用率是上界。<!-- 出处: HANDOVER.md:899-912 --> 这条规则的判法属于判据一章。

热膨胀曲线的数据点到 1000 °C 为止，伸长表 1000 °C 以上的行标「外推」。<!-- 出处: HANDOVER.md:648 -->
