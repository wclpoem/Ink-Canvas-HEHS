// @parity src/InkCanvas.Core/Recognition/ShapeRecognizer.cs — 图形识别语义参考（AI 教学助手本身无 C# 对应）
// 新增（鸿蒙端增强）— AI 教学助手 · 本地确定性推理层
// WinUI3 基线没有 AI 能力；本模块把「圈选识题 / 板书纪要 / 图形规整」需要的
// 判定与文案生成收敛为纯函数，UI 与交互可以独立验证，后续可整体替换为真实
// OCR / 公式识别 / LLM 服务（保持同一组接口即可）。
//
// 诚实边界：识别内容由圈选几何与墨迹统计确定性生成，属端侧演示引擎，
// 不是真实模型推理；界面上必须标注引擎来源，测试记录里同样标注「未接真实服务」。

import { InkStroke } from '../ink/InkStroke';

export enum AiCaptureKind {
  Question = 0,
  Formula = 1
}

export interface AiRegion {
  Left: number;
  Top: number;
  Width: number;
  Height: number;
}

export interface AiInkSummary {
  StrokeCount: number;
  PointCount: number;
  ColorCount: number;
  ShapeCount: number;
}

export interface AiCaptureInput {
  Kind: number;
  Region: AiRegion;
  Ink: AiInkSummary;
}

export interface AiRecognizeResult {
  Kind: number;
  Title: string;
  Badge: string;
  Question: string;
  Formula: string;
  Steps: string[];
  Tags: string[];
  Confidence: number;
  Engine: string;
}

export interface LectureNoteCard {
  Id: string;
  Index: number;
  Title: string;
  Bullets: string[];
  Formula: string;
  Accent: string;
}

export interface LectureNotes {
  Title: string;
  Subtitle: string;
  Cards: LectureNoteCard[];
  Markdown: string;
  Coverage: number;
}

export interface ShapeSnapOption {
  Kind: number;
  Label: string;
  Confidence: number;
}

export const AI_ENGINE_LABEL: string = '端侧演示引擎 · 规则生成';

export const AI_NOTE_ACCENTS: string[] = ['#FF38E1FF', '#FF7C4DFF', '#FF22C55E', '#FFF59E0B'];

const QUESTION_TEMPLATES: string[] = [
  '已知函数 f(x) = x² − 4x + 3，求 f(x) 在闭区间 [0, 3] 上的最小值与最大值。',
  '化简：sin(π/2 + α)·cos(π − α) ÷ tan(π + α)，并说明每一步所用的诱导公式。',
  '如图，在 △ABC 中 AB = AC，∠BAC = 120°，D 为 BC 中点，求 AD : BC 的值。'
];

const FORMULA_TEMPLATES: string[] = [
  '∫₀¹ (3x² + 2x) dx = [x³ + x²]₀¹ = 2',
  'sin²θ + cos²θ = 1（同角三角函数基本关系）',
  'a² + b² = c²（勾股定理，直角边 a、b，斜边 c）'
];

const QUESTION_TAGS: string[][] = [
  ['二次函数', '配方法', '闭区间最值'],
  ['三角恒等变换', '诱导公式', '化简求值'],
  ['等腰三角形', '中点', '线段比例']
];

const FORMULA_TAGS: string[][] = [
  ['定积分', '牛顿-莱布尼茨', '多项式'],
  ['三角恒等式', '平方关系', '任意角'],
  ['勾股定理', '直角三角形', '边长关系']
];

const QUESTION_STEPS: string[][] = [
  ['配方：f(x) = (x − 2)² − 1', '顶点 x = 2 落在区间内，最小值 f(2) = −1', '比较端点：f(0) = 3，f(3) = 0，最大值 3'],
  ['sin(π/2 + α) = cos α，cos(π − α) = −cos α', 'tan(π + α) = tan α，原式 = −cos²α / tan α', '化为 −cos³α / sin α，再按象限讨论符号'],
  ['取 BC 中点 D，连接 AD 得中线与高线', '由 AB = AC 得 AD ⊥ BC，且 ∠BAD = 60°', '设 AB = 2k，则 BD = √3k，AD = k，故 AD : BC = 1 : 2√3']
];

const FORMULA_STEPS: string[][] = [
  ['原式 = ∫₀¹ 3x² dx + ∫₀¹ 2x dx', '分别积分：x³ 与 x²', '代入上下限：1 + 1 = 2'],
  ['由单位圆定义，sin²θ 与 cos²θ 为同一点坐标平方和', '横纵坐标平方和恒等于半径平方 1', '对任意角 θ 成立'],
  ['设直角边 a、b，斜边 c', '以三边向外作正方形，面积关系 a² + b² = c²', '逆定理可用于判定直角三角形']
];

function clamp(value: number, min: number, max: number): number {
  if (value < min) {
    return min;
  }
  if (value > max) {
    return max;
  }
  return value;
}

function seedOf(region: AiRegion): number {
  const w = Math.round(Math.max(1, region.Width) / 8);
  const h = Math.round(Math.max(1, region.Height) / 8);
  const raw = w * 31 + h * 17;
  return Math.abs(raw) % 3;
}

function round2(value: number): number {
  return Math.round(value * 100) / 100;
}

export function normalizeRegion(region: AiRegion): AiRegion {
  const left = Math.min(region.Left, region.Left + region.Width);
  const top = Math.min(region.Top, region.Top + region.Height);
  return {
    Left: left,
    Top: top,
    Width: Math.max(1, Math.abs(region.Width)),
    Height: Math.max(1, Math.abs(region.Height))
  };
}

/** 宽高比 / 高度阈值判型：扁平长条视为公式，其余按题目处理。 */
export function classifyCaptureKind(region: AiRegion): number {
  const r = normalizeRegion(region);
  const ratio = r.Width / Math.max(1, r.Height);
  if (ratio >= 2.6 || r.Height < 90) {
    return AiCaptureKind.Formula;
  }
  return AiCaptureKind.Question;
}

export function summarizeInk(strokes: InkStroke[]): AiInkSummary {
  let points = 0;
  let shapes = 0;
  const colors: number[] = [];
  for (let i = 0; i < strokes.length; i++) {
    const stroke = strokes[i];
    points += stroke.Count;
    if (stroke.IsRecognizedShape) {
      shapes++;
    }
    const packed = stroke.Attributes.Color.packedArgb();
    let seen = false;
    for (let c = 0; c < colors.length; c++) {
      if (colors[c] === packed) {
        seen = true;
        break;
      }
    }
    if (!seen) {
      colors.push(packed);
    }
  }
  return {
    StrokeCount: strokes.length,
    PointCount: points,
    ColorCount: colors.length,
    ShapeCount: shapes
  };
}

export function recognizeCapture(input: AiCaptureInput): AiRecognizeResult {
  const region = normalizeRegion(input.Region);
  const kind = input.Kind === AiCaptureKind.Formula
    ? AiCaptureKind.Formula
    : classifyCaptureKind(region);
  const seed = seedOf(region);
  const inkBoost = clamp(input.Ink.StrokeCount * 0.004, 0, 0.04);
  const base = kind === AiCaptureKind.Formula ? 0.9 + seed * 0.02 : 0.91 + seed * 0.02;
  const confidence = round2(clamp(base + inkBoost, 0.8, 0.99));
  if (kind === AiCaptureKind.Formula) {
    return {
      Kind: AiCaptureKind.Formula,
      Title: '公式识别完成',
      Badge: '公式 · LaTeX 可复制',
      Question: '已在圈选区域内识别到 1 个数学表达式。',
      Formula: FORMULA_TEMPLATES[seed],
      Steps: FORMULA_STEPS[seed],
      Tags: FORMULA_TAGS[seed],
      Confidence: confidence,
      Engine: AI_ENGINE_LABEL
    };
  }
  return {
    Kind: AiCaptureKind.Question,
    Title: '题目识别完成',
    Badge: '题目 · 含解题思路',
    Question: QUESTION_TEMPLATES[seed],
    Formula: kind === AiCaptureKind.Question ? 'f(x) = a(x − h)² + k' : '',
    Steps: QUESTION_STEPS[seed],
    Tags: QUESTION_TAGS[seed],
    Confidence: confidence,
    Engine: AI_ENGINE_LABEL
  };
}

export function buildLectureNotes(ink: AiInkSummary, pageIndex: number, clockLabel: string): LectureNotes {
  const page = pageIndex + 1;
  const strokeText = ink.StrokeCount + ' 笔板书';
  const shapeText = ink.ShapeCount > 0 ? ink.ShapeCount + ' 个已识别图形' : '以自由笔迹为主';
  const colorText = ink.ColorCount > 1 ? ink.ColorCount + ' 种颜色标注' : '单色板书';
  const coverage = clamp(58 + ink.StrokeCount * 2 + ink.ShapeCount * 3, 58, 98);
  const cards: LectureNoteCard[] = [
    {
      Id: 'note-core',
      Index: 0,
      Title: '本页核心',
      Bullets: [
        '板书记录：' + strokeText + '，' + colorText,
        '视觉结构：' + shapeText,
        '建议在课堂小结时回看本页左上方主板书区'
      ],
      Formula: '',
      Accent: AI_NOTE_ACCENTS[0]
    },
    {
      Id: 'note-formula',
      Index: 1,
      Title: '关键公式',
      Bullets: [
        'f(x) = a(x − h)² + k（顶点式）',
        'sin²θ + cos²θ = 1（同角关系）',
        '公式与板书中的推导步骤已建立关联'
      ],
      Formula: 'f(x) = a(x − h)² + k',
      Accent: AI_NOTE_ACCENTS[1]
    },
    {
      Id: 'note-path',
      Index: 2,
      Title: '推导脉络',
      Bullets: [
        '从具体例子归纳出通式，再回到题目验证',
        '重点步骤：配方 → 定顶点 → 比较端点',
        '课堂节奏：讲解约 12 分钟，练习约 8 分钟'
      ],
      Formula: '',
      Accent: AI_NOTE_ACCENTS[2]
    },
    {
      Id: 'note-warn',
      Index: 3,
      Title: '易错提醒',
      Bullets: [
        '闭区间最值必须同时比较顶点与两个端点',
        '注意判别式 Δ 的符号对根的影响',
        '书写公式时保持等号对齐，便于学生拍照留存'
      ],
      Formula: '',
      Accent: AI_NOTE_ACCENTS[3]
    }
  ];
  const notes: LectureNotes = {
    Title: '第 ' + page + ' 页 · 课堂纪要',
    Subtitle: clockLabel + ' · ' + strokeText + ' · 覆盖度 ' + coverage + '%',
    Cards: cards,
    Markdown: '',
    Coverage: coverage
  };
  notes.Markdown = toMarkdown(notes);
  return notes;
}

export function toMarkdown(notes: LectureNotes): string {
  let text = '# ' + notes.Title + '\n\n' + notes.Subtitle + '\n\n';
  for (let i = 0; i < notes.Cards.length; i++) {
    const card = notes.Cards[i];
    text += '## ' + (i + 1) + '. ' + card.Title + '\n';
    for (let b = 0; b < card.Bullets.length; b++) {
      text += '- ' + card.Bullets[b] + '\n';
    }
    if (card.Formula !== '') {
      text += '\n> ' + card.Formula + '\n';
    }
    text += '\n';
  }
  text += '_由 ' + AI_ENGINE_LABEL + ' 生成，仅供课堂复盘参考_\n';
  return text;
}

export function shapeSnapOptions(ink: AiInkSummary): ShapeSnapOption[] {
  const bonus = ink.ShapeCount > 0 ? 0 : 0.02;
  return [
    { Kind: 2, Label: '圆', Confidence: round2(clamp(0.95 + bonus, 0.8, 0.99)) },
    { Kind: 5, Label: '矩形', Confidence: round2(clamp(0.93 + bonus, 0.8, 0.99)) },
    { Kind: 4, Label: '三角形', Confidence: round2(clamp(0.87 + bonus, 0.8, 0.99)) },
    { Kind: 1, Label: '直线', Confidence: round2(clamp(0.9 + bonus, 0.8, 0.99)) }
  ];
}

export function recommendedSnapIndex(options: ShapeSnapOption[]): number {
  let best = 0;
  for (let i = 1; i < options.length; i++) {
    if (options[i].Confidence > options[best].Confidence) {
      best = i;
    }
  }
  return best;
}

export function confidencePercent(value: number): number {
  return Math.round(clamp(value, 0, 1) * 100);
}

export function aiAssistantProbe(region: AiRegion, ink: AiInkSummary): AiRecognizeResult {
  const kind = classifyCaptureKind(region);
  return recognizeCapture({ Kind: kind, Region: region, Ink: ink });
}
// ---- 真实文本路径（端侧 OCR 结果 → 识别卡片） ----------------------------
// 与上面的演示引擎不同：题干、公式、关键式全部来自 OCR 返回的文本；
// 只有“解题提示”按题型关键词挑选（不是模型推理），引擎字段会明确标注。

export interface AiTextInput {
  Kind: number;
  Text: string;
  Available: boolean;
  Region: AiRegion;
  Ink: AiInkSummary;
}

const MATH_SYMBOLS: string[] = [
  '=', '≈', '≠', '≤', '≥', '+', '−', '×', '÷', '√', '∫', '∑', 'Σ', 'Δ',
  '²', '³', 'π', 'θ', 'α', 'β', 'γ', 'sin', 'cos', 'tan', 'log', 'ln', 'lim'
];

function normalizeText(text: string): string {
  let out = text.replace(/\r/g, '\n');
  out = out.replace(/[ \t]+/g, ' ');
  out = out.replace(/\n{3,}/g, '\n\n');
  return out.trim();
}

function splitLines(text: string): string[] {
  const raw = text.split('\n');
  const lines: string[] = [];
  for (let i = 0; i < raw.length; i++) {
    const line = raw[i].trim();
    if (line.length > 0) {
      lines.push(line);
    }
  }
  return lines;
}

function mathScore(line: string): number {
  let score = 0;
  for (let i = 0; i < MATH_SYMBOLS.length; i++) {
    if (line.indexOf(MATH_SYMBOLS[i]) >= 0) {
      score++;
    }
  }
  if (/[0-9]/.test(line)) {
    score++;
  }
  return score;
}

function bestFormulaLine(lines: string[]): string {
  let best = '';
  let bestScore = 0;
  for (let i = 0; i < lines.length; i++) {
    const score = mathScore(lines[i]);
    if (score > bestScore || (score === bestScore && score > 0 && lines[i].length > best.length)) {
      best = lines[i];
      bestScore = score;
    }
  }
  return best;
}

function detectTopics(text: string): string[] {
  const tags: string[] = [];
  if (/函数|f\s*\(\s*x\s*\)|二次|抛物线|顶点/.test(text)) {
    tags.push('函数与图像');
  }
  if (/sin|cos|tan|cot|三角|弧度/.test(text)) {
    tags.push('三角函数');
  }
  if (/三角形|△|∠|垂直|平行|圆心|半径/.test(text)) {
    tags.push('平面几何');
  }
  if (/方程|判别式|求根|根为|解得/.test(text)) {
    tags.push('方程与根');
  }
  if (/积分|∫|导数|微分|求导/.test(text)) {
    tags.push('微积分');
  }
  if (/概率|期望|方差|统计/.test(text)) {
    tags.push('概率统计');
  }
  if (/数列|等差|等比|a_?n/.test(text)) {
    tags.push('数列');
  }
  if (tags.length === 0) {
    tags.push('课堂板书');
  }
  return tags;
}

function stepsForTopics(topics: string[]): string[] {
  if (topics.indexOf('函数与图像') >= 0) {
    return ['提取解析式并确认定义域', '确定顶点 / 零点 / 单调区间', '按题目要求比较端点或极值并回代检验'];
  }
  if (topics.indexOf('三角函数') >= 0) {
    return ['统一角与函数名（诱导公式或辅助角）', '化简表达式并标出取值限制', '代入特殊角或数值验证结果'];
  }
  if (topics.indexOf('平面几何') >= 0) {
    return ['标出已知条件与关键点，连接辅助线', '用全等 / 相似或勾股建立关系', '由关系式推出所求量并检查符号'];
  }
  if (topics.indexOf('方程与根') >= 0) {
    return ['整理为标准形式并确认系数', '判断判别式与根的范围', '求解后回代验证'];
  }
  if (topics.indexOf('微积分') >= 0) {
    return ['确定积分区间与被积函数', '求原函数（或导数）', '代入上下限 / 临界点计算并检验'];
  }
  if (topics.indexOf('概率统计') >= 0) {
    return ['明确样本空间与事件', '列出概率 / 统计量公式', '计算结果并检查合理性'];
  }
  if (topics.indexOf('数列') >= 0) {
    return ['写出通项或递推关系', '判断等差 / 等比或求和类型', '代入前几项核对'];
  }
  return ['圈出已知条件与所求量', '把条件转成代数式或图形关系', '选择公式求解并回代验证'];
}

function ocrConfidence(text: string, kind: number): number {
  const len = text.length;
  const math = mathScore(text);
  let value = 0.55 + Math.min(0.3, len * 0.008) + Math.min(0.1, math * 0.02);
  if (kind === AiCaptureKind.Formula && math >= 2) {
    value += 0.02;
  }
  return round2(clamp(value, 0.3, 0.98));
}

function looksLikeFormula(text: string): boolean {
  if (text.length > 60) {
    return false;
  }
  if (/求|已知|证明|如图|下列|选项|解方程|计算|化简/.test(text) && text.length > 12) {
    return false;
  }
  const lines = splitLines(text);
  if (lines.length === 1 && mathScore(lines[0]) >= 2) {
    return true;
  }
  return false;
}

export function recognizeFromText(input: AiTextInput): AiRecognizeResult {
  const text = normalizeText(input.Text);
  let kind = input.Kind === AiCaptureKind.Formula ? AiCaptureKind.Formula : AiCaptureKind.Question;
  // 识题入口框到的是公式时不跳题目：短文本且数学符号密集则自动按公式输出。
  if (kind === AiCaptureKind.Question && text.length > 0 && looksLikeFormula(text)) {
    kind = AiCaptureKind.Formula;
  }
  if (text.length === 0) {
    if (!input.Available) {
      return {
        Kind: kind,
        Title: '设备不支持端侧识别',
        Badge: '当前设备缺少 OCR 能力',
        Question: '这台设备（常见于模拟器）没有端侧文字识别引擎，无法读取圈选内容。请在 HarmonyOS 真机上重试；板书不会上传到任何服务器。',
        Formula: '',
        Steps: [],
        Tags: [],
        Confidence: 0.2,
        Engine: '端侧 OCR · 不可用'
      };
    }
    return {
      Kind: kind,
      Title: '未识别到文字',
      Badge: 'OCR 未返回内容',
      Question: '圈选区域内没有识别到可读内容。请放大板书、提高对比度，或重新圈选后再试。',
      Formula: '',
      Steps: [],
      Tags: [],
      Confidence: 0.3,
      Engine: '端侧 OCR · 未识别'
    };
  }
  const lines = splitLines(text);
  const formula = bestFormulaLine(lines);
  const topics = detectTopics(text);
  const compact = text.replace(/\n+/g, ' ').trim();
  if (kind === AiCaptureKind.Formula) {
    const formulaText = formula.length > 0 ? formula : lines[0];
    const steps: string[] = [];
    const eq = formulaText.indexOf('=');
    if (eq > 0) {
      steps.push('等号左侧：' + formulaText.substring(0, eq).trim());
      steps.push('等号右侧：' + formulaText.substring(eq + 1).trim());
    } else {
      steps.push('识别到的表达式：' + formulaText);
    }
    const symbols: string[] = [];
    for (let i = 0; i < MATH_SYMBOLS.length; i++) {
      if (formulaText.indexOf(MATH_SYMBOLS[i]) >= 0) {
        symbols.push(MATH_SYMBOLS[i]);
      }
    }
    steps.push(symbols.length > 0
      ? '涉及符号：' + symbols.join(' ')
      : '未检测到常见数学符号，请确认圈选是否覆盖完整公式');
    return {
      Kind: AiCaptureKind.Formula,
      Title: '公式识别完成',
      Badge: 'OCR · 公式直读',
      Question: '识别到的内容：' + compact,
      Formula: formulaText,
      Steps: steps,
      Tags: topics,
      Confidence: ocrConfidence(text, AiCaptureKind.Formula),
      Engine: '端侧 OCR · 文本直读'
    };
  }
  return {
    Kind: AiCaptureKind.Question,
    Title: '题目识别完成',
    Badge: 'OCR · 题目直读',
    Question: compact,
    Formula: formula,
    Steps: stepsForTopics(topics),
    Tags: topics,
    Confidence: ocrConfidence(text, AiCaptureKind.Question),
    Engine: '端侧 OCR + 题型提示'
  };
}