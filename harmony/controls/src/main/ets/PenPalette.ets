// @parity src/InkCanvas.Controls/Controls/PenPalette.cs
import {
  HIGHLIGHTER_ALPHA_DEFAULT,
  HIGHLIGHTER_ALPHA_MAX,
  HIGHLIGHTER_ALPHA_MIN,
  HIGHLIGHTER_ALPHA_STEP,
  HIGHLIGHTER_COLORS,
  PEN_COLORS_DARK,
  PEN_COLORS_LIGHT,
  PEN_WIDTH_MAX,
  PEN_WIDTH_MIN,
  PEN_WIDTH_STEP,
  packedSwatch,
  quickTaperStyleForIndex,
  snap,
  taperKindForIndex,
  toStoredPenWidth
} from './PenPaletteCatalog';
import type { Swatch } from './PenPaletteCatalog';
import { ThemeTokens, themeTokens } from './ThemeTokens';

@Component
export struct PenPalette {
  @Prop highlighter: boolean = false;
  @Prop dark: boolean = true;
  @Prop lightColorScheme: boolean = false;
  @Prop widthMenu: number = 5;
  @Prop alpha: number = HIGHLIGHTER_ALPHA_DEFAULT;
  @Prop taperIndex: number = 0;
  @Prop currentPacked: number = 0xFF000000;
  @Prop uiScale: number = 1;
  @Prop materialBlur: boolean = false;
  @Prop showNibToggler: boolean = false;
  @Prop penNib: number = 0;
  onColor: (packed: number) => void = (packed: number) => {
    void packed;
  };
  onWidth: (storedWidth: number) => void = (storedWidth: number) => {
    void storedWidth;
  };
  onAlpha: (alpha: number) => void = (alpha: number) => {
    void alpha;
  };
  onTaper: (kind: number) => void = (kind: number) => {
    void kind;
  };
  onToggleScheme: () => void = () => {
  };
  onNib: (nib: number) => void = (nib: number) => {
    void nib;
  };
  onClose: () => void = () => {
  };

  private tokens(): ThemeTokens {
    return themeTokens(this.dark);
  }

  private swatches(): Swatch[] {
    if (this.highlighter) {
      return HIGHLIGHTER_COLORS;
    }
    return this.lightColorScheme ? PEN_COLORS_LIGHT : PEN_COLORS_DARK;
  }

  private isSelected(swatch: Swatch): boolean {
    const packed = packedSwatch(swatch);
    return ((packed >>> 8) & 0xFFFFFF) === ((this.currentPacked >>> 8) & 0xFFFFFF);
  }

  build() {
    Column() {
      this.TitleBand()
      Column({ space: 10 * this.uiScale }) {
        if (!this.highlighter) {
          this.TaperRow()
          this.WidthRow()
          if (this.showNibToggler) {
            this.NibRow()
          }
          this.ColorGrid()
        } else {
          this.AlphaRow()
          this.ColorGrid()
          this.WidthRow()
          Text('原菜单范围 20–240，步长 20。')
            .fontSize(12 * this.uiScale)
            .fontColor(this.tokens().textSecondary)
            .width('100%')
        }
      }
      .padding(12 * this.uiScale)
    }
    .width((this.highlighter ? 208 : 248) * this.uiScale)
    .constraintSize({ maxWidth: '90%' })
    .backgroundColor(this.tokens().floatBarBackground)
    .backgroundBlurStyle(this.materialBlur ? BlurStyle.Thin : BlurStyle.NONE)
    .border({ width: 1, color: this.tokens().floatBarBorder })
    .borderRadius(5 * this.uiScale)
  }

  // 笔尖模式切换（圆/椭圆45°），由设置 isEnableDisPlayNibModeToggler 控制显示。
  @Builder
  NibRow() {
    Row({ space: 8 * this.uiScale }) {
      Text('笔尖')
        .fontSize(12 * this.uiScale)
        .fontColor(this.tokens().textSecondary)
      ForEach([0, 1], (nib: number) => {
        Text(nib === 0 ? '圆形' : '椭圆45°')
          .fontSize(12 * this.uiScale)
          .fontColor(this.penNib === nib ? Color.White : this.tokens().textPrimary)
          .backgroundColor(this.penNib === nib ? '#FF0067C0' : this.tokens().surfaceAlt)
          .padding({ left: 10 * this.uiScale, right: 10 * this.uiScale, top: 4 * this.uiScale, bottom: 4 * this.uiScale })
          .borderRadius(4 * this.uiScale)
          .onClick(() => {
            this.onNib(nib);
          })
      }, (nib: number) => nib.toString())
    }
    .width('100%')
  }

  @Builder
  TitleBand() {
    Row() {
      Text(this.highlighter ? '荧光笔' : '墨迹选项')
        .fontSize(12 * this.uiScale)
        .fontWeight(FontWeight.Bold)
        .fontColor(this.tokens().textPrimary)
        .layoutWeight(1)
      Text('×')
        .fontSize(14 * this.uiScale)
        .fontColor(this.tokens().textSecondary)
        .constraintSize({ minWidth: 44, minHeight: 44 })
        .textAlign(TextAlign.Center)
        .onClick(() => {
          this.onClose();
        })
    }
    .width('100%')
    .padding({ left: 15 * this.uiScale, right: 15 * this.uiScale, top: 5 * this.uiScale, bottom: 5 * this.uiScale })
    .backgroundColor(this.tokens().surfaceAlt)
    .borderRadius({ topLeft: 5 * this.uiScale, topRight: 5 * this.uiScale })
  }

  @Builder
  TaperRow() {
    Row() {
      Text('笔锋')
        .fontSize(14 * this.uiScale)
        .fontColor(this.tokens().textPrimary)
        .margin({ right: 10 * this.uiScale })
      Row({ space: 6 * this.uiScale }) {
        ForEach(['点集', '速率', '关闭', '自然'], (label: string, index: number) => {
          Text(label)
            .fontSize(13 * this.uiScale)
            .fontColor(this.taperIndex === quickTaperStyleForIndex(index) ? Color.White : this.tokens().textPrimary)
            .backgroundColor(this.taperIndex === quickTaperStyleForIndex(index) ? this.tokens().accent : Color.Transparent)
            .borderRadius(4 * this.uiScale)
            .padding({ left: 8 * this.uiScale, right: 8 * this.uiScale, top: 4 * this.uiScale, bottom: 4 * this.uiScale })
            .constraintSize({ minWidth: 44, minHeight: 44 })
            .textAlign(TextAlign.Center)
            .onClick(() => {
              this.onTaper(taperKindForIndex(quickTaperStyleForIndex(index)));
            })
        }, (label: string) => label)
      }
      .layoutWeight(1)
      .justifyContent(FlexAlign.End)
    }
    .width('100%')
    .height(44 * this.uiScale)
  }

  @Builder
  WidthRow() {
    Row() {
      Text('粗细')
        .fontSize(14 * this.uiScale)
        .fontColor(this.tokens().textPrimary)
        .margin({ right: 10 * this.uiScale })
      Slider({
        value: this.widthMenu,
        min: PEN_WIDTH_MIN,
        max: PEN_WIDTH_MAX,
        step: PEN_WIDTH_STEP
      })
        .layoutWeight(1)
        .onChange((value: number) => {
          const snapped = snap(value, PEN_WIDTH_MIN, PEN_WIDTH_STEP);
          this.onWidth(toStoredPenWidth(snapped));
        })
      Text(Math.round(this.widthMenu).toString())
        .fontSize(14 * this.uiScale)
        .fontColor(this.tokens().textPrimary)
        .width(32 * this.uiScale)
        .textAlign(TextAlign.End)
    }
    .width('100%')
    .height(44 * this.uiScale)
  }

  @Builder
  AlphaRow() {
    Row() {
      Text('透明度')
        .fontSize(14 * this.uiScale)
        .fontColor(this.tokens().textPrimary)
        .margin({ right: 10 * this.uiScale })
      Slider({
        value: this.alpha,
        min: HIGHLIGHTER_ALPHA_MIN,
        max: HIGHLIGHTER_ALPHA_MAX,
        step: HIGHLIGHTER_ALPHA_STEP
      })
        .layoutWeight(1)
        .onChange((value: number) => {
          this.onAlpha(snap(value, HIGHLIGHTER_ALPHA_MIN, HIGHLIGHTER_ALPHA_STEP));
        })
      Text(Math.round(this.alpha).toString())
        .fontSize(14 * this.uiScale)
        .fontColor(this.tokens().textPrimary)
        .width(32 * this.uiScale)
        .textAlign(TextAlign.End)
    }
    .width('100%')
    .height(44 * this.uiScale)
  }

  @Builder
  ColorGrid() {
    Flex({ wrap: FlexWrap.Wrap, justifyContent: FlexAlign.Center }) {
      if (!this.highlighter) {
        Column() {
          Text(this.lightColorScheme ? '暗' : '亮')
            .fontSize(14 * this.uiScale)
            .fontColor(this.tokens().textPrimary)
          Text(this.lightColorScheme ? '暗系' : '亮系')
            .fontSize(9 * this.uiScale)
            .fontColor(this.tokens().textSecondary)
        }
        .width(44 * this.uiScale)
        .height(44 * this.uiScale)
        .margin(2 * this.uiScale)
        .justifyContent(FlexAlign.Center)
        .border({ width: 1.5, color: this.tokens().floatBarBorder })
        .borderRadius(22 * this.uiScale)
        .onClick(() => {
          this.onToggleScheme();
        })
      }
      ForEach(this.swatches(), (swatch: Swatch) => {
        Stack() {
          Column()
            .width(40 * this.uiScale)
            .height(40 * this.uiScale)
            .backgroundColor(CanvasInkRendererStyle(swatch))
            .border({ width: 1, color: this.tokens().floatBarBorder })
            .borderRadius(20 * this.uiScale)
          if (this.isSelected(swatch)) {
            Text('✓')
              .fontSize(16 * this.uiScale)
              .fontColor(swatch.Name === '白色' ? Color.Black : Color.White)
          }
        }
        .width(44 * this.uiScale)
        .height(44 * this.uiScale)
        .onClick(() => {
          this.onColor(packedSwatch(swatch));
        })
      }, (swatch: Swatch) => swatch.Name)
    }
    .width('100%')
  }
}

function CanvasInkRendererStyle(swatch: Swatch): string {
  return 'rgb(' + swatch.R + ',' + swatch.G + ',' + swatch.B + ')';
}
