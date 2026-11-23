// 从 WinUI3 Glyphs.cs 提取图标清单，生成 parity/glyphs.json。
// Glyphs.cs 的值自 2026-11-22 起为 Fluent System Icons（MIT）码点（跨端同字体同码点）；
// target（sys.symbol）与 segoeCodepoint（跨端稳定 ID）由 parity/icon-map.json 按常量名填充。
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const SRC_PATH = resolve(ROOT, 'src/InkCanvas.Controls/Controls/Glyphs.cs');
export const OUT_PATH = resolve(ROOT, 'parity/glyphs.json');
const MAP_PATH = resolve(ROOT, 'parity/icon-map.json');

export function extractGlyphs(text) {
  const out = [];
  const re = /public const string (\w+) = "(?:\\u([0-9A-Fa-f]{4,5})|([^"\\]))";/g;
  let m;
  while ((m = re.exec(text)) !== null) {
    const name = m[1];
    if (name.endsWith('Font')) {
      continue;
    }
    const cp = m[2] !== undefined ? parseInt(m[2], 16) : m[3].codePointAt(0);
    out.push({
      name,
      codepoint: 'U+' + cp.toString(16).toUpperCase().padStart(4, '0'),
      char: String.fromCodePoint(cp),
      target: null,
    });
  }
  return out;
}

// Glyphs.cs 常量名 → 官方 Segoe 图标名（icon-map 键）。值相同的常量（Highlight/HighlightWpf）共用别名。
const ALIAS = new Map(Object.entries({
  Pen: 'Edit', Eraser: 'EraseTool', Select: 'SelectAll', Board: 'TVMonitor',
  Close: 'Cancel', Exit: 'ChromeClose', Restart: 'Refresh', Replay: 'Recent',
  TimeMachine: 'History', SlideList: 'BulletedList', Pdf: 'PDF', Cursor: 'Click',
  PenWpf: 'PenPalette', HighlightWpf: 'Highlight', EraserOverlayCircle: 'StatusCircle7',
  EraserOverlayStroke: 'StrokeErase2', SelectWpf: 'StorageOptical', SelectOverlay: 'ClickSolid',
  ShapesWpf: 'DialShape3', InsertMedia: 'ImageExport', BoardWpf: 'Handwriting',
  Tools: 'ViewAll', Emoji: 'Emoji2', EmojiDrag: 'Emoji', Gesture: 'PointerHand',
  TimerWpf: 'Ringer', Rand: 'Shuffle', SaveStrokes: 'Save', OpenStrokes: 'OpenFolderHorizontal',
  ReplayWpf: 'PlaybackRate1x', GuideWpf: 'Dictionary', ExitWpf: 'PowerButton',
  HideControl: 'RedEye', LRSwitch: 'Switch', GestureTranslate: 'Move',
  GestureZoom: 'FullScreen', GestureRotate: 'Rotate',
}));

// icon-map 按官方 Segoe 图标名建立索引。
function targetsByName() {
  const map = JSON.parse(readFileSync(MAP_PATH, 'utf8'));
  const byName = new Map();
  for (const entry of map.segoe) {
    byName.set(entry.name, entry);
  }
  return byName;
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const glyphs = extractGlyphs(readFileSync(SRC_PATH, 'utf8'));
  const byName = targetsByName();
  let mapped = 0;
  for (const g of glyphs) {
    const official = ALIAS.get(g.name) ?? g.name;
    const entry = byName.get(official);
    if (entry !== undefined) {
      g.target = entry.symbol.startsWith('custom:') ? 'custom:text' : 'sys.symbol.' + entry.symbol;
      if (entry.fluentName !== undefined) {
        g.fluentName = entry.fluentName;
      }
      if (entry.segoeCodepoint !== undefined) {
        g.segoeCodepoint = entry.segoeCodepoint;
      }
      mapped += 1;
    }
  }
  writeFileSync(OUT_PATH, JSON.stringify({
    note: 'codepoint=Fluent System Icons（MIT，两平台共用同字体同码点）；target=sys.symbol 回退映射；segoeCodepoint=原 Segoe 稳定 ID；权威映射 parity/icon-map.json',
    glyphs,
  }, null, 2) + '\n', 'utf8');
  console.log('glyphs written:', glyphs.length, 'mapped:', mapped);
}