// AI 网关：把 InkCanvas 自定义 AI 请求协议转发到 OpenAI 兼容上游（DeepSeek/通义/OpenAI 等）。
// 零依赖：Node 标准库 + 内置 fetch。
//
// 用法：
//   node tools/ai-gateway.mjs
//   AI_UPSTREAM_API_KEY=sk-xxx AI_UPSTREAM_MODEL=deepseek-chat \
//     AI_UPSTREAM_BASE_URL=https://api.deepseek.com/v1 node tools/ai-gateway.mjs
//
// 环境变量：
//   AI_GATEWAY_PORT        监听端口，默认 8787（仅监听 127.0.0.1）
//   AI_UPSTREAM_BASE_URL   上游基础地址，默认 https://api.openai.com/v1
//   AI_UPSTREAM_API_KEY    上游密钥；为空时自动降级为本地 mock 流
//   AI_UPSTREAM_MODEL      上游模型，默认 gpt-4o-mini（优先于客户端传的 model）
//   AI_UPSTREAM_TIMEOUT_MS 上游超时，默认 60000
//
// 路由：
//   POST /v1/assistant           客户端自定义协议入口
//   POST /v1/chat/completions    兼容 OpenAI 路径，同一处理逻辑
//   GET  /health                 返回 {"ok":true,"mode":"upstream|mock","model":"..."}
import { createServer } from 'node:http';

const HOST = '127.0.0.1';
const PORT = positiveInt(process.env.AI_GATEWAY_PORT, 8787);
const UPSTREAM_BASE_URL = (process.env.AI_UPSTREAM_BASE_URL || 'https://api.openai.com/v1')
  .trim()
  .replace(/\/+$/, '');
const UPSTREAM_API_KEY = (process.env.AI_UPSTREAM_API_KEY || '').trim();
const UPSTREAM_MODEL = (process.env.AI_UPSTREAM_MODEL || 'gpt-4o-mini').trim();
const UPSTREAM_TIMEOUT_MS = positiveInt(process.env.AI_UPSTREAM_TIMEOUT_MS, 60000);
const UPSTREAM_ROUTE = '/chat/completions';
const MODE = UPSTREAM_API_KEY ? 'upstream' : 'mock';
const MAX_BODY_BYTES = 64 * 1024 * 1024;
const MOCK_TOKEN_INTERVAL_MS = 45;
const MOCK_IMAGE_PREFIX = 'data:image/png;base64,';
const TRUNCATES = { slideCurrent: 1200, slideNeighbour: 400, imageText: 600, errorDetail: 300 };

const SSE_HEADERS = {
  'Content-Type': 'text/event-stream; charset=utf-8',
  'Cache-Control': 'no-cache, no-transform',
  Connection: 'keep-alive',
  'X-Accel-Buffering': 'no',
  'Access-Control-Allow-Origin': '*'
};

const inFlightControllers = new Set();

// ---------- 通用工具 ----------

function positiveInt(value, fallback) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? Math.floor(parsed) : fallback;
}

function log(message) {
  console.log('[AI] ' + message);
}

function upstreamHost() {
  try {
    return new URL(UPSTREAM_BASE_URL).host || '(empty)';
  } catch {
    return '(invalid-url)';
  }
}

function upstreamUrl() {
  return UPSTREAM_BASE_URL + UPSTREAM_ROUTE;
}

function pick(source, ...keys) {
  if (!source || typeof source !== 'object') return undefined;
  for (const key of keys) {
    if (source[key] !== undefined && source[key] !== null) return source[key];
  }
  return undefined;
}

function asString(value) {
  return typeof value === 'string' ? value : value === undefined || value === null ? '' : String(value);
}

function asNumber(value) {
  const parsed = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function truncate(value, max) {
  const text = asString(value).trim();
  return text.length <= max ? text : text.slice(0, max) + '…';
}

function describeError(error) {
  if (!error) return '未知错误';
  if (error.name === 'AbortError') return '请求已中止';
  const message = asString(error.message).trim();
  return message || asString(error.code) || '网络请求失败';
}

function formatRect(rect) {
  if (!rect || typeof rect !== 'object') return '';
  const left = asNumber(pick(rect, 'left', 'Left'));
  const top = asNumber(pick(rect, 'top', 'Top'));
  const right = asNumber(pick(rect, 'right', 'Right'));
  const bottom = asNumber(pick(rect, 'bottom', 'Bottom'));
  return `(${round1(left)}, ${round1(top)}, ${round1(right)}, ${round1(bottom)})`;
}

function round1(value) {
  return Math.round(value * 10) / 10;
}

function writeEvent(res, name, data) {
  if (res.destroyed || res.writableEnded) return;
  res.write('event: ' + name + '\n');
  res.write('data: ' + JSON.stringify(data) + '\n\n');
}

function beginSse(res) {
  if (res.headersSent) return;
  res.writeHead(200, SSE_HEADERS);
  if (typeof res.flushHeaders === 'function') res.flushHeaders();
}

function sendJson(res, status, body) {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(payload),
    'Access-Control-Allow-Origin': '*'
  });
  res.end(payload);
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function readJsonBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    let tooLarge = false;
    req.on('data', (chunk) => {
      if (tooLarge) return;
      size += chunk.length;
      if (size > MAX_BODY_BYTES) {
        tooLarge = true;
        chunks.length = 0;
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
      if (tooLarge) {
        const error = new Error('请求体超过上限（' + MAX_BODY_BYTES + ' 字节）');
        error.code = 'BODY_TOO_LARGE';
        reject(error);
        return;
      }
      resolve(Buffer.concat(chunks).toString('utf8'));
    });
    req.on('error', reject);
  });
}

// ---------- 请求体解析：兼容 camelCase / PascalCase 两种序列化 ----------

function collectImages(payload) {
  const stage = asString(pick(payload, 'stage')).trim();
  const images = [];

  if (Array.isArray(payload.images)) {
    const sorted = payload.images
      .filter((item) => item && typeof item === 'object')
      .map((item, index) => ({ item, index }))
      .sort((left, right) => {
        const diff = asNumber(pick(left.item, 'order', 'Order')) - asNumber(pick(right.item, 'order', 'Order'));
        return diff !== 0 ? diff : left.index - right.index;
      });

    for (const { item, index } of sorted) {
      const base64 = asString(pick(item, 'imageBase64', 'ImageBase64')).trim();
      const orderValue = pick(item, 'order', 'Order');
      images.push({
        base64,
        order: orderValue === undefined ? index : asNumber(orderValue),
        text: asString(pick(item, 'text', 'Text')).trim(),
        id: asString(pick(item, 'id', 'Id')).trim(),
        rect: pick(item, 'anchor', 'Anchor', 'rect', 'Rect'),
        fromArray: true
      });
    }
  }

  const hasUsableArrayImage = images.some((image) => image.base64.length > 0);
  const primary = asString(pick(payload, 'imageBase64', 'ImageBase64')).trim();
  if (!hasUsableArrayImage && primary && stage !== 'inkOnly') {
    images.push({ base64: primary, order: 0, text: '', id: '', rect: undefined, fromArray: false });
  }

  return images.filter((image) => stage === 'inkOnly' ? false : image.base64.length > 0);
}

function summarizeInkStrokes(payload) {
  const strokes = pick(payload, 'inkStrokes', 'InkStrokes');
  if (!Array.isArray(strokes) || strokes.length === 0) return null;

  let pointCount = 0;
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  let hasPoint = false;

  for (const stroke of strokes) {
    if (!stroke || typeof stroke !== 'object') continue;
    const points = pick(stroke, 'Points', 'points');
    if (!Array.isArray(points)) continue;
    pointCount += points.length;
    for (const point of points) {
      if (!point || typeof point !== 'object') continue;
      const x = asNumber(pick(point, 'X', 'x'));
      const y = asNumber(pick(point, 'Y', 'y'));
      minX = Math.min(minX, x);
      minY = Math.min(minY, y);
      maxX = Math.max(maxX, x);
      maxY = Math.max(maxY, y);
      hasPoint = true;
    }
  }

  return {
    strokeCount: strokes.length,
    pointCount,
    bbox: hasPoint ? formatRect({ left: minX, top: minY, right: maxX, bottom: maxY }) : ''
  };
}

function buildSlideContextLines(slide) {
  if (!slide || typeof slide !== 'object') return [];
  const lines = [];
  const title = asString(pick(slide, 'title', 'Title')).trim();
  if (title) lines.push('课件标题：' + truncate(title, 200));

  const slideIndex = asNumber(pick(slide, 'slideIndex', 'SlideIndex'));
  const animationStep = asNumber(pick(slide, 'animationStep', 'AnimationStep'));
  if (slideIndex > 0) {
    lines.push('当前页：第 ' + slideIndex + ' 页' + (animationStep > 0 ? '（动画阶段 ' + animationStep + '）' : ''));
  }

  const currentText = truncate(pick(slide, 'currentText', 'CurrentText'), TRUNCATES.slideCurrent);
  if (currentText) lines.push('当前页文本：' + currentText);
  const previousText = truncate(pick(slide, 'previousText', 'PreviousText'), TRUNCATES.slideNeighbour);
  if (previousText) lines.push('上一页文本（仅供参考）：' + previousText);
  const nextText = truncate(pick(slide, 'nextText', 'NextText'), TRUNCATES.slideNeighbour);
  if (nextText) lines.push('下一页文本（仅供参考）：' + nextText);
  const selectionText = truncate(pick(slide, 'selectionText', 'SelectionText'), TRUNCATES.slideCurrent);
  if (selectionText) lines.push('用户补充选区文本：' + selectionText);

  if (lines.length > 0) {
    lines.push('约束：课件文本仅为辅助上下文，题目内容以框选图片与墨迹为准；冲突时以框选内容为准。');
  }
  return lines;
}

function buildUserText(payload, images, inkSummary) {
  const section = (title, lines) => {
    if (!lines || lines.length === 0) return [];
    return ['【' + title + '】', ...lines, ''];
  };

  const lines = [];

  const slideLines = buildSlideContextLines(pick(payload, 'slideContext', 'SlideContext'));
  lines.push(...section('课件上下文', slideLines));

  if (images.length > 0) {
    const imageLines = images.map((image, index) => {
      const segments = ['选区 #' + (index + 1) + '（order=' + image.order + '）'];
      const rect = formatRect(image.rect);
      if (rect) segments.push('anchor=' + rect);
      if (image.text) segments.push('识别文本=' + truncate(image.text, TRUNCATES.imageText));
      return segments.join(' ');
    });
    lines.push(...section('框选内容', imageLines));
  }

  if (inkSummary) {
    lines.push(...section('墨迹摘要', [
      '笔画数=' + inkSummary.strokeCount + '，总点数=' + inkSummary.pointCount
        + (inkSummary.bbox ? '，包围盒=' + inkSummary.bbox : '')
    ]));
  }

  const viewport = pick(payload, 'viewport', 'Viewport');
  if (viewport && typeof viewport === 'object') {
    const scale = asNumber(pick(viewport, 'scale', 'Scale'));
    lines.push(...section('视口', ['scale=' + scale]));
  }

  const requestLines = [];
  const mode = asString(pick(payload, 'mode', 'Mode')).trim();
  const teaching = asString(pick(payload, 'teaching', 'Teaching')).trim();
  const stage = asString(pick(payload, 'stage', 'Stage')).trim();
  const sessionId = asString(pick(payload, 'sessionId', 'SessionId')).trim();
  const purpose = asString(pick(payload, 'purpose', 'Purpose')).trim();
  const instruction = truncate(pick(payload, 'instruction', 'Instruction'), TRUNCATES.slideCurrent);
  const difficulty = asString(pick(payload, 'difficulty', 'Difficulty')).trim();
  if (mode) requestLines.push('mode=' + mode);
  if (teaching) requestLines.push('teaching=' + teaching);
  if (stage) requestLines.push('stage=' + stage);
  if (sessionId) requestLines.push('sessionId=' + sessionId);
  if (purpose) requestLines.push('purpose=' + purpose);
  if (instruction) requestLines.push('instruction=' + instruction);
  const targetPageIndex = pick(payload, 'targetPageIndex', 'TargetPageIndex');
  if (targetPageIndex !== undefined) requestLines.push('targetPageIndex=' + asNumber(targetPageIndex));
  if (difficulty) requestLines.push('difficulty=' + difficulty);
  lines.push(...section('请求参数', requestLines));

  const question = truncate(pick(payload, 'question', 'prompt', 'Prompt'), TRUNCATES.slideCurrent);
  if (question) lines.push(...section('用户追问', [question]));

  lines.push('【任务】请讲解上述框选内容，并严格遵守 teaching 的答案披露约束。');
  return lines.join('\n');
}

function buildSystemPrompt(teaching) {
  const lines = [
    '你是一名面向课堂的中文教学助手，负责讲评学生在电子白板上框选或手写的题目（含手写公式、图形与题干）。',
    '输出要求：',
    '1. 先说明识别到的关键信息，再给出分步推导；每一步只做一个变换，并说明依据。',
    '2. 每一步都必须可验证：给出可代回检验的中间结果或理由，不跳步、不编造条件。',
    '3. 数学表达式使用 $...$ 或 LaTeX，中文表述简洁，适合投屏阅读。',
    '4. 图片、墨迹与课件文本冲突时，以框选图片与墨迹为准。'
  ];

  if (teaching === 'hint') {
    lines.push('5. 当前 teaching=hint：只给思路提示与下一步方向，不给步骤解答，也不公布最终答案。');
  } else if (teaching === 'step' || teaching === 'stepbystep') {
    lines.push('5. 当前 teaching=step（分步引导）：先给出第一步引导并保持逐步推进；'
      + '不要在第一段直接公布完整答案、最终数值或完整解答，只在后续步骤中逐步给出。');
  } else if (teaching === 'teacher') {
    lines.push('5. 当前 teaching=teacher：面向教师，可指出易错点、常见误区与课堂追问建议，可给出完整解答。');
  } else {
    lines.push('5. 当前 teaching=full：可以给出完整解答，但仍需分步写清依据。');
  }

  return lines.join('\n');
}

function toDataUrl(base64) {
  return /^data:/i.test(base64) ? base64 : MOCK_IMAGE_PREFIX + base64;
}

function buildUpstreamBody(payload) {
  const images = collectImages(payload);
  const inkSummary = summarizeInkStrokes(payload);
  const teaching = asString(pick(payload, 'teaching', 'Teaching')).trim().toLowerCase();
  const userText = buildUserText(payload, images, inkSummary);

  const content = [{ type: 'text', text: userText }];
  for (const image of images) {
    content.push({ type: 'image_url', image_url: { url: toDataUrl(image.base64) } });
  }

  return {
    model: UPSTREAM_MODEL,
    stream: true,
    messages: [
      { role: 'system', content: buildSystemPrompt(teaching) },
      { role: 'user', content }
    ]
  };
}

// ---------- 上游 SSE 转发 ----------

function consumeUpstreamLine(line, res, stats) {
  if (!line || line.startsWith(':')) return null;
  if (!line.startsWith('data:')) return null;

  const data = line.slice(5).trim();
  if (!data) return null;
  if (data.toUpperCase() === '[DONE]') return 'done';

  let payload;
  try {
    payload = JSON.parse(data);
  } catch {
    return null;
  }

  if (payload && typeof payload === 'object' && payload.error) {
    const detail = truncate(
      asString(pick(payload.error, 'message')).trim() || JSON.stringify(payload.error),
      TRUNCATES.errorDetail
    );
    writeEvent(res, 'error', { type: 'error', text: '上游返回错误：' + detail });
    stats.outcome = 'error';
    return 'error';
  }

  const choice = Array.isArray(payload?.choices) ? payload.choices[0] : null;
  if (!choice || typeof choice !== 'object') return null;

  const delta = choice.delta && typeof choice.delta === 'object' ? choice.delta : null;
  const message = choice.message && typeof choice.message === 'object' ? choice.message : null;
  const content = (delta && typeof delta.content === 'string' && delta.content)
    || (message && typeof message.content === 'string' && message.content)
    || '';

  if (content) {
    stats.tokenChars += content.length;
    writeEvent(res, 'token', { type: 'token', text: content });
  }
  return null;
}

async function streamFromUpstream(res, payload, stats, controller, isClientClosed) {
  const upstreamBody = buildUpstreamBody(payload);
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, UPSTREAM_TIMEOUT_MS);

  try {
    const response = await fetch(upstreamUrl(), {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'text/event-stream',
        Authorization: 'Bearer ' + UPSTREAM_API_KEY
      },
      body: JSON.stringify(upstreamBody),
      signal: controller.signal
    });
    stats.upstreamStatus = response.status;

    if (!response.ok) {
      let detail = '';
      try {
        detail = truncate(await response.text(), TRUNCATES.errorDetail);
      } catch {
        detail = '';
      }
      writeEvent(res, 'error', {
        type: 'error',
        text: '上游返回 HTTP ' + response.status + (detail ? '：' + detail : ''),
        status: response.status
      });
      stats.outcome = 'error';
      return;
    }

    if (!response.body) {
      writeEvent(res, 'error', { type: 'error', text: '上游响应为空' });
      stats.outcome = 'error';
      return;
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8');
    let buffer = '';
    let sentDone = false;
    let failed = false;

    while (!sentDone && !failed && !isClientClosed()) {
      const chunk = await reader.read();
      if (chunk.done) break;

      buffer += decoder.decode(chunk.value, { stream: true });
      let newline = buffer.indexOf('\n');
      while (newline >= 0 && !sentDone && !failed) {
        let line = buffer.slice(0, newline);
        buffer = buffer.slice(newline + 1);
        if (line.endsWith('\r')) line = line.slice(0, -1);

        const signal = consumeUpstreamLine(line, res, stats);
        if (signal === 'done') sentDone = true;
        if (signal === 'error') failed = true;

        newline = buffer.indexOf('\n');
      }
    }

    if (isClientClosed()) {
      try {
        await reader.cancel();
      } catch {
        // 客户端已断开，忽略取消异常。
      }
      stats.outcome = 'client-closed';
      return;
    }

    if (!sentDone && !failed) {
      const rest = (buffer + decoder.decode()).trim();
      if (rest) {
        const signal = consumeUpstreamLine(rest, res, stats);
        if (signal === 'done') sentDone = true;
        if (signal === 'error') failed = true;
      }
    }

    if (failed) return;

    if (!sentDone) {
      try {
        await reader.cancel();
      } catch {
        // 上游流已结束，取消失败不影响结果。
      }
    }

    writeEvent(res, 'done', { type: 'done' });
  } catch (error) {
    if (isClientClosed()) {
      stats.outcome = 'client-closed';
      return;
    }
    if (timedOut) {
      writeEvent(res, 'error', { type: 'error', text: '上游超时（' + UPSTREAM_TIMEOUT_MS + 'ms）' });
      stats.outcome = 'error';
      return;
    }
    writeEvent(res, 'error', { type: 'error', text: '上游连接失败：' + describeError(error) });
    stats.outcome = 'error';
  } finally {
    clearTimeout(timer);
  }
}

// ---------- mock 模式（未配置密钥时） ----------

const BOARD_WRITE_PLAN = {
  type: 'board_plan',
  schema: 1,
  intent: 'write',
  target: { board: 'main', pageIndex: 0 },
  originals: [
    '1/2 x² - 3x + 2 = 0',
    '求根：x = 2 或 x = 3',
    '判别式 Δ = 1'
  ],
  confidence: 0.78,
  warnings: [
    '整体置信度 0.78 偏低，请复核公式系数',
    '原文疑似笔误：第二行 x1 可能应为 x_1'
  ],
  blocks: [
    {
      id: 'b1',
      kind: 'formula',
      latex: '\\frac{1}{2}x^2 - 3x + 2 = 0',
      rect: { x: 0.06, y: 0.1, w: 0.42, h: 0.14 },
      style: { fontSize: 28, align: 'center', columns: 1, lineHeight: 1.4 },
      confidence: 0.72,
      note: '一次项系数疑似笔误，已按 -3x 整理'
    },
    {
      id: 'b2',
      kind: 'text',
      text: '一元二次方程综合讲解',
      confidence: 0.95,
      rect: { x: 0.06, y: 0.28, w: 0.42, h: 0.1 },
      style: { fontSize: 24, align: 'left', columns: 1, lineHeight: 1.4 }
    },
    {
      id: 'b3',
      kind: 'wordlist',
      lines: ['判别式', '配方法', '因式分解'],
      confidence: 0.88,
      rect: { x: 0.54, y: 0.1, w: 0.24, h: 0.28 },
      style: { fontSize: 24, align: 'left', columns: 1, lineHeight: 1.4 }
    },
    {
      id: 'b4',
      kind: 'poem',
      title: '静夜思',
      author: '唐·李白',
      lines: ['床前明月光', '疑是地上霜', '举头望明月', '低头思故乡'],
      confidence: 0.9,
      note: '标题根据正文自动生成',
      rect: { x: 0.54, y: 0.42, w: 0.42, h: 0.3 },
      style: { fontSize: 26, align: 'center', columns: 1, lineHeight: 1.6 }
    }
  ],
  notes: ['第 2 块标题为自动生成，可按需修改']
};

const SUMMARY_PLAN = {
  type: 'board_plan',
  schema: 1,
  intent: 'write',
  target: { board: 'main', pageIndex: 0 },
  originals: ['一元二次方程综合讲解', '判别式 / 配方法 / 因式分解', '1/2 x² - 3x + 2 = 0'],
  confidence: 0.86,
  warnings: [],
  blocks: [
    {
      id: 's1',
      kind: 'summary',
      title: '本课总结',
      text: '一元二次方程的三种解法：直接开平方、配方法与因式分解。\n判别式 Δ = b² - 4ac 决定根的情况。',
      rect: { x: 0.08, y: 0.12, w: 0.6, h: 0.32 },
      style: { fontSize: 26, align: 'left', columns: 1, lineHeight: 1.5 },
      confidence: 0.92
    }
  ],
  notes: ['总结页将新建，原始板书保留']
};

const VARIANT_TEXT = [
  '题目：解方程 x^2 - 5x + 6 = 0（变式题，mock 模式）\n',
  '答案：x1 = 2，x2 = 3\n',
  '知识点：因式分解, 一元二次方程'
];

function mockTokenScript(mode) {
  if (mode === 'formula') {
    return [
      '已识别到手写公式（mock 模式，未配置 AI_UPSTREAM_API_KEY）。\n\n',
      '对照原迹：二次项系数为 $\\frac{1}{2}$，一次项为 $-3x$，常数项为 $2$。\n',
      '两边乘以 2 可先化为 $x^2 - 6x + 4 = 0$；再配方得 $(x-3)^2 = 5$。\n',
      '可继续追问该式的判别式或图像。'
    ];
  }
  if (mode === 'solve') {
    return [
      '下面开始求解（mock 模式，未配置 AI_UPSTREAM_API_KEY）。\n\n',
      '原方程为 $x^2 - 5x + 6 = 0$。\n',
      '因式分解得 $(x-2)(x-3) = 0$，因此 $x_1 = 2,\\ x_2 = 3$。\n',
      '检验：把 $x=2$ 与 $x=3$ 代回原式均成立。'
    ];
  }
  if (mode === 'shape') {
    return [
      '识别到直角三角形（mock 模式，未配置 AI_UPSTREAM_API_KEY）。\n\n',
      '已知两条直角边分别为 3 与 4，斜边为 5。\n\n',
      '可推导性质：两锐角互余，且满足 $a^2+b^2=c^2$。'
    ];
  }
  return [
    '这是框选内容的要点（mock 模式，未配置 AI_UPSTREAM_API_KEY）：\n\n',
    '1. 先识别手写公式的结构与符号；\n',
    '2. 再给出可验证的推导步骤；\n',
    '3. 最后给出一句话结论。\n\n',
    '如需更精确的识别，可重新框选更清晰的区域。'
  ];
}

async function streamMock(req, res, payload, stats, isClientClosed) {
  const mode = asString(pick(payload, 'mode', 'Mode')).trim().toLowerCase() || 'auto';
  const purpose = asString(pick(payload, 'purpose', 'Purpose')).trim().toLowerCase();
  const instruction = asString(pick(payload, 'instruction', 'Instruction'));

  if (mode === 'error' || req.headers['x-mock-fail'] === '1') {
    writeEvent(res, 'error', { type: 'error', text: 'mock 服务故障（用于错误态自测）' });
    stats.outcome = 'error';
    return;
  }

  if (purpose === 'variant' || mode === 'variant') {
    for (const text of VARIANT_TEXT) {
      if (isClientClosed() || res.destroyed) {
        stats.outcome = 'client-closed';
        return;
      }
      stats.tokenChars += text.length;
      writeEvent(res, 'token', { type: 'token', text });
      await delay(MOCK_TOKEN_INTERVAL_MS);
    }
    writeEvent(res, 'done', { type: 'done' });
    return;
  }

  if (mode === 'board_write' || purpose === 'board_write') {
    const plan = instruction.includes('总结') ? SUMMARY_PLAN : BOARD_WRITE_PLAN;
    stats.tokenChars += JSON.stringify(plan).length;
    writeEvent(res, 'board_plan', plan);
    await delay(MOCK_TOKEN_INTERVAL_MS);
    if (isClientClosed() || res.destroyed) {
      stats.outcome = 'client-closed';
      return;
    }
    writeEvent(res, 'done', { type: 'done' });
    return;
  }

  const script = mockTokenScript(mode);
  for (const text of script) {
    if (isClientClosed() || res.destroyed) {
      stats.outcome = 'client-closed';
      return;
    }
    stats.tokenChars += text.length;
    writeEvent(res, 'token', { type: 'token', text });
    await delay(MOCK_TOKEN_INTERVAL_MS);
  }

  if (isClientClosed() || res.destroyed) {
    stats.outcome = 'client-closed';
    return;
  }
  writeEvent(res, 'done', { type: 'done' });
}

// ---------- 请求入口 ----------

async function handleRequest(req, res, route) {
  const startedAt = Date.now();
  const controller = new AbortController();
  inFlightControllers.add(controller);

  const stats = {
    route,
    mode: MODE,
    imageCount: 0,
    withImage: false,
    tokenChars: 0,
    outcome: 'ok',
    upstreamStatus: 0,
    clientModel: ''
  };

  let clientClosed = false;
  const isClientClosed = () => clientClosed || res.destroyed;
  const onClose = () => {
    if (!res.writableEnded) {
      clientClosed = true;
      controller.abort();
    }
  };
  res.on('close', onClose);

  try {
    const rawBody = await readJsonBody(req);
    let payload;
    try {
      payload = JSON.parse(rawBody);
    } catch {
      payload = null;
    }

    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
      beginSse(res);
      writeEvent(res, 'error', { type: 'error', text: '请求体不是合法的 JSON 对象' });
      stats.outcome = 'error';
      return;
    }

    const images = collectImages(payload);
    stats.imageCount = images.length;
    stats.withImage = images.length > 0;
    stats.clientModel = asString(pick(payload, 'model', 'Model')).trim();

    beginSse(res);
    if (MODE === 'upstream') {
      await streamFromUpstream(res, payload, stats, controller, isClientClosed);
    } else {
      await streamMock(req, res, payload, stats, isClientClosed);
    }
  } catch (error) {
    if (clientClosed) {
      stats.outcome = 'client-closed';
      return;
    }
    stats.outcome = stats.outcome === 'ok' ? 'error' : stats.outcome;
    beginSse(res);
    writeEvent(res, 'error', {
      type: 'error',
      text: error?.code === 'BODY_TOO_LARGE' ? describeError(error) : '网关处理失败：' + describeError(error)
    });
  } finally {
    inFlightControllers.delete(controller);
    res.off('close', onClose);
    if (!res.writableEnded && !res.destroyed) {
      res.end();
    }
    logRequest(stats, Date.now() - startedAt);
  }
}

function logRequest(stats, elapsedMs) {
  const parts = [
    'route=' + stats.route,
    'mode=' + stats.mode,
    'images=' + stats.imageCount,
    'withImage=' + stats.withImage,
    'chars=' + stats.tokenChars,
    'elapsedMs=' + elapsedMs,
    'outcome=' + stats.outcome
  ];
  if (stats.upstreamStatus) parts.push('upstreamStatus=' + stats.upstreamStatus);
  if (stats.clientModel) parts.push('clientModel=' + stats.clientModel);
  log('request ' + parts.join(' '));
}

// ---------- HTTP 服务 ----------

const server = createServer((req, res) => {
  let pathname = '/';
  try {
    pathname = new URL(req.url || '/', 'http://' + HOST + ':' + PORT).pathname;
  } catch {
    pathname = '/';
  }

  if (req.method === 'OPTIONS') {
    res.writeHead(204, {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Headers': 'Content-Type, Accept, Authorization',
      'Access-Control-Allow-Methods': 'POST, GET, OPTIONS'
    });
    res.end();
    return;
  }

  if (req.method === 'GET' && pathname === '/health') {
    sendJson(res, 200, { ok: true, mode: MODE, model: UPSTREAM_MODEL });
    return;
  }

  if (req.method === 'POST' && (pathname === '/v1/assistant' || pathname === '/v1/chat/completions')) {
    void handleRequest(req, res, pathname);
    return;
  }

  sendJson(res, 404, { ok: false, error: 'not found', path: pathname });
});

server.on('error', (error) => {
  if (error && error.code === 'EADDRINUSE') {
    log('端口已被占用：' + PORT + '（可用 AI_GATEWAY_PORT 指定其他端口）');
  } else {
    log('server error: ' + describeError(error));
  }
  process.exit(1);
});

server.listen(PORT, HOST, () => {
  log('gateway listening on http://' + HOST + ':' + PORT);
  log(
    'mode=' + MODE
      + (MODE === 'mock' ? '（未配置 AI_UPSTREAM_API_KEY）' : '')
      + ' upstreamHost=' + (MODE === 'upstream' ? upstreamHost() : '(disabled)')
      + ' model=' + UPSTREAM_MODEL
      + ' timeoutMs=' + UPSTREAM_TIMEOUT_MS
  );
  log('routes POST /v1/assistant, POST /v1/chat/completions, GET /health');
});

let shuttingDown = false;
function shutdown(signal) {
  if (shuttingDown) return;
  shuttingDown = true;
  log('received ' + signal + '，正在中止在途请求并退出…');
  for (const controller of inFlightControllers) {
    try {
      controller.abort();
    } catch {
      // 忽略中止异常。
    }
  }
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 3000).unref();
}

process.on('SIGINT', () => shutdown('SIGINT'));
process.on('SIGTERM', () => shutdown('SIGTERM'));