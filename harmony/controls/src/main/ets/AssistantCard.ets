// @parity src/InkCanvas.Controls/Controls/FloatingBar.cs（浮窗材质/圆角/动效 token；AI 卡片为新增）
// AI 助教浮窗：360vp 宽、最大 60% 高、Acrylic 材质（API26 门控）、头部状态点/模式 Tab/关闭、
// 流式回答 + 公式卡 + 操作区 + 追问输入；浮窗内部事件不向白板冒泡（entry 按矩形拦截触摸）。
import { AssistantMode, AssistantState, latexToPlain } from '@inkcanvas/core';
import { ThemeTokens, themeTokens } from './ThemeTokens';
import { panelBackground, panelBlurStyle } from './PanelStyle';
import { StreamMessage } from './StreamMessage';
import { MOTION_STANDARD } from './Motion';

export const AI_CARD_WIDTH: number = 360;
export const AI_CARD_CORNER: number = 8;

@Component
export struct AssistantCard {
  @Prop dark: boolean = false;
  @Prop state: number = AssistantState.Idle;
  @Prop mode: number = AssistantMode.Auto;
  @Prop content: string = '';
  @Prop formula: string = '';
  @Prop statusText: string = '';
  @Prop errorText: string = '';
  @Prop reduceMotion: boolean = false;
  @Prop materialBlur: boolean = false;
  @Prop thumbnailBase64: string = '';
  @Prop cardWidth: number = AI_CARD_WIDTH;
  @Prop cardMaxHeight: number = 480;
  @Prop canPaste: boolean = false;
  @Prop modeEnabled: boolean = true;
  @Prop demoMode: boolean = false;
  @State private question: string = '';
  onClose: () => void = () => {
  };
  onStop: () => void = () => {
  };
  onRetry: () => void = () => {
  };
  onRephrase: () => void = () => {
  };
  onMode: (mode: number) => void = (mode: number) => {
    void mode;
  };
  onFollowUp: (text: string) => void = (text: string) => {
    void text;
  };
  onPaste: () => void = () => {
  };
  onCopy: () => void = () => {
  };
  onDrag: (dx: number, dy: number) => void = (dx: number, dy: number) => {
    void dx;
    void dy;
  };

  private tokens(): ThemeTokens {
    return themeTokens(this.dark);
  }

  private statusColor(): ResourceColor {
    if (this.state === AssistantState.Error) {
      return '#FFE81123';
    }
    if (this.state === AssistantState.Done) {
      return '#FF107C10';
    }
    return this.tokens().accent;
  }

  private statusLabel(): string {
    if (this.state === AssistantState.Capturing) {
      return '捕获选区';
    }
    if (this.state === AssistantState.Recognizing) {
      return this.statusText.length > 0 ? this.statusText : '正在识别墨迹…';
    }
    if (this.state === AssistantState.Streaming) {
      return '正在生成解答…';
    }
    if (this.state === AssistantState.Done) {
      return '已完成';
    }
    if (this.state === AssistantState.Error) {
      return '出错了';
    }
    return '待命';
  }

  private entryTransition(): TransitionEffect {
    if (this.reduceMotion) {
      return TransitionEffect.IDENTITY;
    }
    return TransitionEffect.OPACITY
      .combine(TransitionEffect.scale({ x: 0.96, y: 0.96 }))
      .animation({ duration: 200, curve: Curve.EaseOut });
  }

  build() {
    Column() {
      Row({ space: 8 }) {
        Circle()
          .width(8)
          .height(8)
          .fill(this.statusColor())
        Text('AI 助教')
          .fontSize(14)
          .fontWeight(FontWeight.Medium)
          .fontColor(this.tokens().textPrimary)
        this.ModeTabs()
        Blank()
        Text('✕')
          .fontSize(14)
          .fontColor(this.tokens().textSecondary)
          .width(28)
          .height(28)
          .textAlign(TextAlign.Center)
          .borderRadius(6)
          .onClick(() => {
            this.onClose();
          })
      }
      .width('100%')
      .padding({ left: 12, right: 8, top: 8, bottom: 6 })
      .gesture(
        PanGesture()
          .onActionUpdate((event: GestureEvent) => {
            this.onDrag(event.offsetX, event.offsetY);
          })
      )

      if (this.thumbnailBase64.length > 0) {
        Image('data:image/jpeg;base64,' + this.thumbnailBase64)
          .width('100%')
          .height(96)
          .objectFit(ImageFit.Contain)
          .backgroundColor(this.dark ? '#33000000' : '#0A000000')
      }

      if (this.demoMode) {
        Text('演示模式：当前显示固定示例，尚未连接 AI 服务。')
          .fontSize(12)
          .fontColor(this.tokens().textSecondary)
          .width('100%')
          .padding({ left: 12, right: 12, top: 4, bottom: 4 })
      }
      if (this.state === AssistantState.Recognizing) {
        Row({ space: 10 }) {
          LoadingProgress().width(18).height(18).color(this.tokens().accent)
          Text(this.statusLabel())
            .fontSize(12)
            .fontColor(this.tokens().textSecondary)
        }
        .width('100%')
        .padding({ left: 12, right: 12, top: 8, bottom: 4 })
        .accessibilityText(this.statusLabel())
      } else {
        Text(this.statusLabel())
          .fontSize(11)
          .fontColor(this.tokens().textSecondary)
          .width('100%')
          .padding({ left: 12, right: 12, top: 2, bottom: 2 })
      }

      if (this.state === AssistantState.Error) {
        Column({ space: 8 }) {
          Text(this.errorText.length > 0 ? this.errorText : '请求失败，可重试或换个问法。')
            .fontSize(13)
            .fontColor('#FFE81123')
            .width('100%')
          Row({ space: 8 }) {
            this.SmallButton('重试', true, () => {
              this.onRetry();
            })
            this.SmallButton('换个问法', false, () => {
              this.onRephrase();
            })
          }
          .width('100%')
        }
        .width('100%')
        .padding({ left: 12, right: 12, top: 6, bottom: 6 })
        .accessibilityText(this.errorText.length > 0 ? this.errorText : '请求失败')
      }

      Scroll() {
        Column({ space: 8 }) {
          if (this.formula.length > 0) {
            Column({ space: 4 }) {
              Row() {
                Text('LaTeX')
                  .fontSize(11)
                  .fontColor(this.tokens().textSecondary)
                Blank()
                Text('复制')
                  .fontSize(11)
                  .fontColor(this.tokens().accent)
                  .onClick(() => {
                    this.onCopy();
                  })
              }
              .width('100%')
              Text(latexToPlain(this.formula))
                .fontSize(14)
                .fontColor(this.tokens().textPrimary)
                .width('100%')
              Text(this.formula)
                .fontSize(11)
                .fontColor(this.tokens().textSecondary)
                .width('100%')
                .maxLines(2)
                .textOverflow({ overflow: TextOverflow.Ellipsis })
            }
            .width('100%')
            .padding(8)
            .borderRadius(6)
            .backgroundColor(this.dark ? '#334CC2FF' : '#0F0067C0')
            .border({ width: 1, color: this.tokens().accentSoft })
          }
          StreamMessage({
            dark: this.dark,
            text: this.content,
            streaming: this.state === AssistantState.Streaming
          })
        }
        .width('100%')
        .padding({ left: 12, right: 12, top: 6, bottom: 6 })
        .alignItems(HorizontalAlign.Start)
      }
      .width('100%')
      .layoutWeight(1)
      .align(Alignment.TopStart)
      .scrollBar(BarState.Auto)
      .accessibilityText(this.statusLabel())

      Row({ space: 8 }) {
        if (this.state === AssistantState.Streaming) {
          this.SmallButton('停止生成', true, () => {
            this.onStop();
          })
        }
        if (this.state === AssistantState.Done) {
          this.SmallButton('复制', false, () => {
            this.onCopy();
          })
          this.SmallButton('贴回白板', true, () => {
            this.onPaste();
          })
        }
        Blank()
        Text(this.canPaste ? '可贴回' : '')
          .fontSize(10)
          .fontColor(this.tokens().textSecondary)
      }
      .width('100%')
      .padding({ left: 12, right: 12, top: 4, bottom: 4 })

      Row({ space: 6 }) {
        TextInput({ placeholder: '基于选区继续追问…', text: this.question })
          .fontSize(12)
          .fontColor(this.tokens().textPrimary)
          .placeholderColor(this.tokens().textSecondary)
          .backgroundColor(this.dark ? '#33000000' : '#0A000000')
          .borderRadius(6)
          .layoutWeight(1)
          .height(34)
          .enabled(this.modeEnabled)
          .onChange((value: string) => {
            this.question = value;
          })
          .onSubmit(() => {
            this.submitQuestion();
          })
        Text('发送')
          .fontSize(12)
          .fontColor(this.dark ? Color.White : Color.White)
          .padding({ left: 10, right: 10, top: 6, bottom: 6 })
          .backgroundColor(this.question.length > 0 ? this.tokens().accent : this.tokens().textSecondary)
          .borderRadius(6)
          .onClick(() => {
            this.submitQuestion();
          })
      }
      .width('100%')
      .padding({ left: 12, right: 12, top: 4, bottom: 10 })

      Text('Esc 关闭 · 点击外部不会关闭，避免误触丢内容')
        .fontSize(9)
        .fontColor(this.tokens().textSecondary)
        .width('100%')
        .textAlign(TextAlign.Center)
        .padding({ bottom: 6 })
    }
    .width(this.cardWidth)
    .constraintSize({ maxHeight: this.cardMaxHeight })
    .backgroundColor(panelBackground(this.dark, this.materialBlur))
    .backgroundBlurStyle(panelBlurStyle(this.materialBlur))
    .borderRadius(AI_CARD_CORNER)
    .border({ width: 1, color: this.dark ? '#33FFFFFF' : '#CCFFFFFF' })
    .shadow({ radius: 32, color: '#2E000000', offsetY: 8 })
    .transition(this.entryTransition())
    .accessibilityGroup(true)
    .accessibilityText('AI 助教，' + this.statusLabel());
  }

  private submitQuestion(): void {
    const value = this.question.trim();
    if (value.length === 0) {
      return;
    }
    this.question = '';
    this.onFollowUp(value);
  }

  @Builder
  ModeTabs() {
    Row({ space: 2 }) {
      this.ModeTab('公式', AssistantMode.Formula)
      this.ModeTab('求解', AssistantMode.Solve)
      this.ModeTab('图形', AssistantMode.Shape)
      this.ModeTab('问答', AssistantMode.Chat)
    }
    .padding(2)
    .borderRadius(6)
    .backgroundColor(this.dark ? '#22FFFFFF' : '#0A000000')
  }

  @Builder
  ModeTab(label: string, value: number) {
    Text(label)
      .fontSize(11)
      .fontColor(this.mode === value ? this.tokens().textPrimary : this.tokens().textSecondary)
      .padding({ left: 6, right: 6, top: 3, bottom: 3 })
      .borderRadius(4)
      .backgroundColor(this.mode === value ? this.tokens().accentSoft : Color.Transparent)
      .onClick(() => {
        this.onMode(value);
      })
  }

  @Builder
  SmallButton(label: string, primary: boolean, action: () => void) {
    Text(label)
      .fontSize(12)
      .fontColor(primary ? Color.White : this.tokens().textPrimary)
      .padding({ left: 10, right: 10, top: 5, bottom: 5 })
      .backgroundColor(primary ? this.tokens().accent : (this.dark ? '#22FFFFFF' : '#0F000000'))
      .borderRadius(5)
      .onClick(() => {
        action();
      })
  }
}
