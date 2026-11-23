// 新增（鸿蒙端增强）— 画板三栏底栏
// 左栏页面、中栏工具、右栏画面与文件；替代平板/2in1 上原先左上角的三组浮条。
// 触控热区最小 44vp；与悬浮栏共用磨砂玻璃参数，保持质感一致。
import { ThemeTokens, themeTokens } from './ThemeTokens';

@Component
export struct BoardDock {
  @Prop dark: boolean = false;
  @Prop uiScale: number = 1;
  @Prop pageIndex: number = 0;
  @Prop pageCount: number = 1;
  @Prop boardBackground: number = 0;
  @Prop selectedId: string = 'pen';
  @Prop materialBlur: boolean = false;
  @Prop glassIntensity: number = 55;
  onCommand: (id: string) => void = (id: string) => {
    void id;
  };

  private tokens(): ThemeTokens {
    return themeTokens(this.dark);
  }

  private alphaHex(alpha: number): string {
    const value = Math.max(0, Math.min(255, Math.round(alpha)));
    const hex = value.toString(16).toUpperCase();
    return hex.length === 1 ? '0' + hex : hex;
  }

  private dockTint(): string {
    const intensity = Math.min(100, Math.max(0, this.glassIntensity));
    const alpha = 0.9 - intensity * 0.005;
    return this.dark
      ? ('#' + this.alphaHex(alpha * 255) + '14161C')
      : ('#' + this.alphaHex(alpha * 255) + 'FFFFFF');
  }

  private glassRadius(): number {
    return 2 + Math.min(100, Math.max(0, this.glassIntensity)) * 0.32;
  }

  private backgroundLabel(): string {
    if (this.boardBackground === 1) {
      return '白';
    }
    if (this.boardBackground === 2) {
      return '网格';
    }
    if (this.boardBackground === 3) {
      return '横线';
    }
    return '黑';
  }

  private scaleFactor(): number {
    return Math.max(1, this.uiScale);
  }

  @Builder
  DockButton(id: string, label: string, active: boolean) {
    Column() {
      Text(label)
        .fontSize(12 * this.scaleFactor())
        .fontColor(active ? '#FF05070C' : this.tokens().textPrimary)
        .maxLines(1)
    }
    .constraintSize({ minWidth: 44, minHeight: 44 })
    .padding({ left: 8 * this.scaleFactor(), right: 8 * this.scaleFactor() })
    .justifyContent(FlexAlign.Center)
    .backgroundColor(active ? '#FF38E1FF' : (this.dark ? '#14FFFFFF' : '#0F000000'))
    .borderRadius(this.tokens().buttonRadius)
    .onClick(() => {
      this.onCommand(id);
    })
  }


  build() {
    Row({ space: 10 * this.scaleFactor() }) {
      // 左栏：页面导航
      Row({ space: 3 * this.scaleFactor() }) {
        this.DockButton('pagePrev', '‹', false)
        Column() {
          Text((this.pageIndex + 1) + '/' + this.pageCount)
            .fontSize(12 * this.scaleFactor())
            .fontColor(this.tokens().textPrimary)
            .maxLines(1)
        }
        .height(44)
        .constraintSize({ minWidth: 44 })
        .justifyContent(FlexAlign.Center)
        this.DockButton('pageNext', '›', false)
        this.DockButton('pageAdd', '＋', false)
        this.DockButton('pageRemove', '删', false)
      }
      .padding(6 * this.scaleFactor())
      .backgroundColor(this.dockTint())
      .borderRadius(this.tokens().barCorner)
      .border({ width: 1, color: this.tokens().floatBarBorder })
      .backdropBlur(this.materialBlur ? this.glassRadius() : 0)

      // 中栏：黑板工具
      Row({ space: 3 * this.scaleFactor() }) {
        this.DockButton('pen', '笔', this.selectedId === 'pen')
        this.DockButton('eraserCircle', '橡', this.selectedId === 'eraserCircle' || this.selectedId === 'eraserStroke')
        this.DockButton('undo', '↶', false)
        this.DockButton('redo', '↷', false)
        this.DockButton('clearPage', '清页', false)
        this.DockButton('bgCycle', this.backgroundLabel(), false)
      }
      .padding(6 * this.scaleFactor())
      .backgroundColor(this.dockTint())
      .borderRadius(this.tokens().barCorner)
      .border({ width: 1, color: this.tokens().floatBarBorder })
      .backdropBlur(this.materialBlur ? this.glassRadius() : 0)

      // 右栏：文件与退出
      Row({ space: 3 * this.scaleFactor() }) {
        this.DockButton('insertImage', '图', false)
        this.DockButton('insertVideo', '视', false)
        this.DockButton('save', '保存', false)
        this.DockButton('open', '打开', false)
        this.DockButton('exit', '退出画板', false)
      }
      .padding(6 * this.scaleFactor())
      .backgroundColor(this.dockTint())
      .borderRadius(this.tokens().barCorner)
      .border({ width: 1, color: this.tokens().floatBarBorder })
      .backdropBlur(this.materialBlur ? this.glassRadius() : 0)
    }
    .padding(4 * this.scaleFactor())
    .constraintSize({ maxWidth: '96%' })
  }
}