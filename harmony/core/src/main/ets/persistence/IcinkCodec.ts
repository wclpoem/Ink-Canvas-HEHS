// @parity src/InkCanvas.Features/Persistence/Stores.cs (InkFileStore)
// .icink 单表面墨迹存档：PascalCase 字段、点行 float[5]、读取不校验 Version、损坏 JSON 抛错。
import { InkColor } from '../ink/InkColor';
import { InkPoint } from '../ink/InkPoint';
import { InkStroke } from '../ink/InkStroke';
import { StrokeAttributes, TaperKind, PenNib } from '../ink/StrokeAttributes';
import type { ShapeSourceData } from '../ink/InkStroke';

export interface IcinkPointRow {
  X: number;
  Y: number;
  Pressure: number;
  Width: number;
  TimeMs: number;
}

export interface IcinkStrokeSummary {
  Color: number;
  Width: number;
  Highlighter: boolean;
  HighlighterAlpha: number;
  Taper: number;
  Nib: number;
  NibRotation: number;
  // 基线演进：图层序（null 也写出）与形状来源公式（对象或 null）。
  LayerOrder: number | null;
  ShapeSource: ShapeSourceData | null;
  Shape: boolean;
  ObjectFrame: number[] | null;
  Points: number[][];
}

function asDict(value: object): Record<string, Object> {
  return value as Record<string, Object>;
}

function num(value: Object | undefined, fallback: number): number {
  return typeof value === 'number' && isFinite(value as number) ? value as number : fallback;
}

function bool(value: Object | undefined, fallback: boolean): boolean {
  return typeof value === 'boolean' ? value as boolean : fallback;
}

export function parseIcink(text: string): InkStroke[] {
  const root = JSON.parse(text) as object;
  if (root === null || typeof root !== 'object' || Array.isArray(root)) {
    throw new Error('Invalid .icink document');
  }
  const rawStrokes = asDict(root)['Strokes'];
  if (!Array.isArray(rawStrokes)) {
    return [];
  }
  const strokes: InkStroke[] = [];
  const list = rawStrokes as Object[];
  for (let i = 0; i < list.length; i++) {
    const raw = list[i];
    if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) {
      continue;
    }
    const dict = asDict(raw);
    const attributes = new StrokeAttributes();
    attributes.Color = InkColor.fromPackedArgb(num(dict['Color'], 0));
    attributes.BaseWidth = num(dict['Width'], 0);
    attributes.IsHighlighter = bool(dict['Highlighter'], false);
    attributes.HighlighterAlpha = num(dict['HighlighterAlpha'], 96);
    attributes.TaperKind = num(dict['Taper'], 0) as TaperKind;
    // Nib 缺省 Round(0)、NibRotation 缺省 0（与 C# StrokeFile 默认一致；越界回落）。
    const nibValue = num(dict['Nib'], 0);
    attributes.Nib = (nibValue === 1 ? 1 : 0) as PenNib;
    const nibRotationValue = num(dict['NibRotation'], 0);
    attributes.NibRotation = isFinite(nibRotationValue) ? nibRotationValue : 0;
    const stroke = new InkStroke(attributes);
    stroke.IsRecognizedShape = bool(dict['Shape'], false);
    // LayerOrder：number 或 null（缺失/null 均为 null——未分层）。
    const layerValue = dict['LayerOrder'];
    stroke.LayerOrder = typeof layerValue === 'number' && isFinite(layerValue) ? layerValue : null;
    // ShapeSource：{GroupId, Provider, Definition} 对象或 null。
    const sourceValue = dict['ShapeSource'];
    if (sourceValue !== null && typeof sourceValue === 'object' && !Array.isArray(sourceValue)) {
      const sourceDict = asDict(sourceValue);
      const groupId = sourceDict['GroupId'];
      const provider = sourceDict['Provider'];
      const definition = sourceDict['Definition'];
      if (typeof groupId === 'string' && typeof provider === 'string' && typeof definition === 'string') {
        stroke.ShapeSource = { GroupId: groupId, Provider: provider, Definition: definition };
      }
    }
    const points: InkPoint[] = [];
    const rawPoints = dict['Points'];
    if (Array.isArray(rawPoints)) {
      const rows = rawPoints as Object[];
      for (let j = 0; j < rows.length; j++) {
        const row = rows[j];
        if (Array.isArray(row) && (row as Object[]).length >= 4) {
          const values = row as number[];
          points.push(new InkPoint(
            num(values[0], 0), num(values[1], 0), num(values[2], 0.5),
            num(values[3], 0), values.length > 4 ? num(values[4], 0) : 0));
        }
      }
    }
    stroke.restorePoints(points);
    stroke.complete();
    const frameValue = dict['ObjectFrame'];
    let frame: number[] | null = null;
    if (Array.isArray(frameValue) && (frameValue as Object[]).length === 10) {
      const values = frameValue as number[];
      const copy: number[] = [];
      for (let k = 0; k < 10; k++) {
        copy.push(num(values[k], 0));
      }
      frame = copy;
    }
    stroke.importObjectFrame(frame);
    strokes.push(stroke);
  }
  return strokes;
}

function summaryOf(stroke: InkStroke): IcinkStrokeSummary {
  const points: number[][] = [];
  for (let i = 0; i < stroke.Count; i++) {
    const p = stroke.Points[i];
    points.push([p.X, p.Y, p.Pressure, p.Width, p.TimeMs]);
  }
  return {
    Color: stroke.Attributes.Color.packedArgb(),
    Width: stroke.Attributes.BaseWidth,
    Highlighter: stroke.Attributes.IsHighlighter,
    HighlighterAlpha: stroke.Attributes.HighlighterAlpha,
    Taper: stroke.Attributes.TaperKind,
    Nib: stroke.Attributes.Nib,
    NibRotation: stroke.Attributes.NibRotation,
    LayerOrder: stroke.LayerOrder,
    ShapeSource: stroke.ShapeSource === null ? null : {
      GroupId: stroke.ShapeSource.GroupId,
      Provider: stroke.ShapeSource.Provider,
      Definition: stroke.ShapeSource.Definition
    },
    Shape: stroke.IsRecognizedShape,
    ObjectFrame: stroke.exportObjectFrame(),
    Points: points
  };
}

export function stringifyIcink(strokes: InkStroke[]): string {
  const list: Object[] = [];
  for (let i = 0; i < strokes.length; i++) {
    const s = strokes[i];
    const points: number[][] = [];
    for (let j = 0; j < s.Count; j++) {
      const p = s.Points[j];
      points.push([p.X, p.Y, p.Pressure, p.Width, p.TimeMs]);
    }
    list.push({
      Color: s.Attributes.Color.packedArgb(),
      Width: s.Attributes.BaseWidth,
      Highlighter: s.Attributes.IsHighlighter,
      HighlighterAlpha: s.Attributes.HighlighterAlpha,
      Taper: s.Attributes.TaperKind,
      // 以下为鸿蒙端演进字段：必须写出，否则 parse→stringify 往返会丢失
      // 图层序、形状来源、笔尖与笔尖旋转（Windows 端已同步支持同名 PascalCase 字段）。
      Nib: s.Attributes.Nib,
      NibRotation: s.Attributes.NibRotation,
      LayerOrder: s.LayerOrder,
      ShapeSource: s.ShapeSource === null ? null : {
        GroupId: s.ShapeSource.GroupId,
        Provider: s.ShapeSource.Provider,
        Definition: s.ShapeSource.Definition
      },
      Shape: s.IsRecognizedShape,
      ObjectFrame: s.exportObjectFrame(),
      Points: points
    });
  }
  return JSON.stringify({ Version: 1, Strokes: list });
}

export function icinkRoundtrip(text: string): IcinkStrokeSummary[] {
  const strokes = parseIcink(stringifyIcink(parseIcink(text)));
  const result: IcinkStrokeSummary[] = [];
  for (let i = 0; i < strokes.length; i++) {
    result.push(summaryOf(strokes[i]));
  }
  return result;
}
