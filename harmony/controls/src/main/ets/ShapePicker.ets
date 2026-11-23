// @parity src/InkCanvas.Controls/Controls/ShapePreview.cs
// 24 图形菜单：预览由 core ShapeGenerator 生成轮廓（图形语义与绘制一致）。
import { generate, buildUnionContours, InkPoint } from '@inkcanvas/core';
import type { StrokeSummary, StrokeAttributesInput } from '@inkcanvas/core';
import { SHAPE_MENU_ROWS, ShapeMenuEntry } from './ShapePickerCatalog';
import { ThemeTokens, themeTokens } from './ThemeTokens';

@Component
export struct ShapePreviewCell {
  @Prop kind: number = 0;
  @Prop label: string = '';
  @Prop rotation: number = 0;
  @Prop selected: boolean = false;
  @Prop dark: boolean = true;
  @Prop cellSize: number = 50;
  onSelect: (kind: number, longPress: boolean) => void = (kind: number, longPress: boolean) => {
    void kind;
    void longPress;
  };

  private settings: RenderingContextSettings = new RenderingContextSettings(true);
  private context: CanvasRenderingContext2D = new CanvasRenderingContext2D(this.settings);

  build() {
    Column() {
      Canvas(this.context)
        .width(this.cellSize)
        .height(this.cellSize)
        .onReady(() => {
          this.draw();
        })
    }
    .width(this.cellSize)
    .height(this.cellSize)
    .borderRadius(4)
    .border({ width: this.selected ? 2 : 1, color: this.selected ? themeTokens(this.dark).accent : Color.Transparent })
    .onClick(() => {
      this.onSelect(this.kind, false);
    })
    .gesture(
      LongPressGesture({ repeat: false })
        .onAction(() => {
          this.onSelect(this.kind, true);
        })
    )
  }

  private draw(): void {
    const tokens = themeTokens(this.dark);
    const color = this.dark ? 0xFFFFFFFF : 0xFF000000;
    const attributes: StrokeAttributesInput = {
      ColorPacked: color,
      BaseWidth: 1.5,
      IsHighlighter: false,
      HighlighterAlpha: 96,
      TaperKind: 0
    };
    const size = 50;
    this.context.clearRect(0, 0, size, size);
    this.context.save();
    if (this.rotation === 90) {
      this.context.translate(size / 2, size / 2);
      this.context.rotate(Math.PI / 2);
      this.context.translate(-size / 2, -size / 2);
    }
    const strokes: StrokeSummary[] = generate(this.kind, 6, 44, 44, 6, attributes);
    for (let i = 0; i < strokes.length; i++) {
      const points: InkPoint[] = [];
      for (let p = 0; p < strokes[i].Points.length; p++) {
        const point = strokes[i].Points[p];
        points.push(new InkPoint(point.X, point.Y, 0.5, point.Width, 0));
      }
      const contours = buildUnionContours(points);
      this.context.fillStyle = this.dark ? '#FFFFFF' : '#000000';
      for (let c = 0; c < contours.length; c++) {
        const contour = contours[c];
        if (contour.length < 6) {
          continue;
        }
        this.context.beginPath();
        this.context.moveTo(contour[0], contour[1]);
        for (let k = 2; k + 1 < contour.length; k += 2) {
          this.context.lineTo(contour[k], contour[k + 1]);
        }
        this.context.closePath();
        this.context.fill('nonzero');
      }
    }
    this.context.restore();
    void tokens;
  }
}

@Component
export struct ShapePicker {
  @Prop dark: boolean = true;
  @Prop selectedKind: number = -1;
  @Prop uiScale: number = 1;
  @Prop materialBlur: boolean = false;
  onSelect: (kind: number, longPress: boolean) => void = (kind: number, longPress: boolean) => {
    void kind;
    void longPress;
  };

  build() {
    Column({ space: -8 * this.uiScale }) {
      ForEach(SHAPE_MENU_ROWS, (row: ShapeMenuEntry[], rowIndex: number) => {
        Row() {
          ForEach(row, (entry: ShapeMenuEntry) => {
            ShapePreviewCell({
              kind: entry.Kind,
              label: entry.Label,
              rotation: entry.Rotation,
              selected: this.selectedKind === entry.Kind,
              dark: this.dark,
              cellSize: 50 * this.uiScale,
              onSelect: (kind: number, longPress: boolean) => {
                this.onSelect(kind, longPress);
              }
            })
              .margin({
                left: entry.Margin[0] * this.uiScale,
                top: entry.Margin[1] * this.uiScale,
                right: entry.Margin[2] * this.uiScale,
                bottom: entry.Margin[3] * this.uiScale
              })
          }, (entry: ShapeMenuEntry) => entry.Id)
        }
      }, (row: ShapeMenuEntry[], rowIndex: number) => rowIndex.toString())
    }
    .padding(8 * this.uiScale)
    .backgroundColor(themeTokens(this.dark).floatBarBackground)
    .backgroundBlurStyle(this.materialBlur ? BlurStyle.Thin : BlurStyle.NONE)
    .border({ width: 1, color: themeTokens(this.dark).floatBarBorder })
    .borderRadius(5 * this.uiScale)
  }
}
