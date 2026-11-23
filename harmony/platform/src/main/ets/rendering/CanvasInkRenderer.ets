// @parity src/InkCanvas.Ink/Rendering/InkSceneRenderer.cs（ArkUI Canvas 实现）
// 双层渲染 + OffscreenCanvas 离屏缓存：
// - committed（已提交笔画）绘制在视口尺寸的 OffscreenCanvas 上持久缓存，视图矩阵在离屏内应用，
//   产出的 ImageBitmap 是屏幕空间位图；内容不变的帧（高频书写中）零路径填充、零位图分配，
//   仅在缓存发布帧把位图 drawImage 到下层可见 Canvas。
// - active（活动笔画/选区）留在上层可见 Canvas，按前后帧世界脏盒求并集后局部 clear/clip 重绘；
//   选区显示、视图变化等复杂场景保守回退全屏重绘。
// 说明：transferToImageBitmap 后离屏后备位图是否清空在文档中未明确，增量追加前统一先把旧位图
// drawImage 回离屏画布做语义兜底（复制语义下为同像素覆盖，移交语义下等价于恢复），两种实现均正确。
// 注意：真实 fillRule/性能需在模拟器与真机核对（见 porting notes）。
import { DrawCommand, Invalidation, InkRenderer, InkScene } from '@inkcanvas/ink';

type Any2DContext = CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D;

const MATRIX_EPSILON: number = 1e-6;
const WORLD_BOX_PADDING_VP: number = 1;
const SCREEN_BOX_PADDING_VP: number = 1.5;

export class CanvasInkRenderer implements InkRenderer {
  readonly Kind: string = 'arkui-canvas';
  private committedLayer: CanvasRenderingContext2D;
  private active: CanvasRenderingContext2D;
  private widthVp: number = 0;
  private heightVp: number = 0;

  // 离屏缓存状态
  private offscreen: OffscreenCanvas | null = null;
  private offCtx: OffscreenCanvasRenderingContext2D | null = null;
  private cacheBitmap: ImageBitmap | null = null;
  private cacheDirty: boolean = true;
  private cachedView: number[] | null = null;
  private cachedSignatures: string[] = [];

  // 活动层脏矩形状态（世界坐标）
  private prevActiveBox: number[] | null = null;
  private prevSelectionVisible: boolean = false;
  // F3 形状预览（世界坐标）脏盒缓存；橡皮筋为视图 DIP，跟踪上一帧屏幕盒用于局部清除。
  private prevPreviewBox: number[] | null = null;
  private prevMarqueeBox: number[] | null = null;

  // Pen Kit 报点预测临时段：世界坐标扁平点列 [x0,y0,x1,y1,...]。
  // 只存在于活动层，每帧由宿主用最新 TouchEvent 整体替换；绝不进入 OffscreenCanvas 离屏缓存、
  // InkDocument 或任何持久化。抬笔/取消/能力不可用时宿主调用 clearPrediction 清空。
  private prediction: number[] = [];
  private predictionColor: number = 0;
  private predictionWidth: number = 0;
  private prevPredictionBox: number[] | null = null;

  constructor(committed: CanvasRenderingContext2D, active: CanvasRenderingContext2D) {
    this.committedLayer = committed;
    this.active = active;
  }

  // 更新本帧预测段（世界坐标）。传空数组等价于本帧不绘制预测。
  // 入口自防御：点列必须为偶数个坐标对且全部有限、线宽为正；任一不满足直接丢弃，
  // 避免 NaN/Infinity 脏盒把局部帧降级为全屏重绘或画出发散路径。
  setPrediction(pointsWorld: number[], colorPacked: number, widthWorld: number): void {
    if (pointsWorld.length < 4 || pointsWorld.length % 2 !== 0 || widthWorld <= 0) {
      this.clearPrediction();
      return;
    }
    for (let i = 0; i < pointsWorld.length; i++) {
      if (!Number.isFinite(pointsWorld[i])) {
        this.clearPrediction();
        return;
      }
    }
    this.prediction = pointsWorld;
    this.predictionColor = colorPacked;
    this.predictionWidth = widthWorld;
  }

  clearPrediction(): void {
    this.prediction = [];
    this.predictionColor = 0;
    this.predictionWidth = 0;
  }

  resize(widthVp: number, heightVp: number): void {
    const sizeChanged = this.widthVp !== widthVp || this.heightVp !== heightVp;
    this.widthVp = widthVp;
    this.heightVp = heightVp;
    this.committedLayer.setTransform(1, 0, 0, 1, 0, 0);
    this.committedLayer.clearRect(0, 0, widthVp, heightVp);
    this.active.setTransform(1, 0, 0, 1, 0, 0);
    this.active.clearRect(0, 0, widthVp, heightVp);
    // 活动层位图已被无条件清空，其脏盒/预测段状态必须同步复位（同尺寸回调也会走到这里），
    // 否则下一局部帧按已失效的旧脏盒清屏，造成盒与位图状态不自洽。
    this.prevActiveBox = null;
    this.prevSelectionVisible = false;
    this.prevPredictionBox = null;
    this.prevPreviewBox = null;
    this.prevMarqueeBox = null;
    this.prediction = [];
    this.predictionColor = 0;
    this.predictionWidth = 0;
    // 尺寸变化必须重建离屏画布（位图尺寸不可变），离屏缓存状态作废。
    if (sizeChanged || this.offscreen === null) {
      this.destroyOffscreen();
      this.cacheDirty = true;
      this.cachedView = null;
      this.cachedSignatures = [];
    }
  }

  render(scene: InkScene, invalidation: Invalidation): void {
    // 外部当前统一传入 Invalidation.full()；渲染器内部以视图矩阵 + 命令签名做更细的缓存决策，
    // 参数保留以兼容未来调用方下发精确脏盒。
    void invalidation;
    if (this.widthVp <= 0 || this.heightVp <= 0) {
      return;
    }
    this.ensureOffscreen();
    const offCtx = this.offCtx;
    if (offCtx === null || this.offscreen === null) {
      return;
    }
    const v = scene.View;
    const signatures: string[] = [];
    for (let i = 0; i < scene.Committed.length; i++) {
      signatures.push(CanvasInkRenderer.commandSignature(scene.Committed[i]));
    }
    const viewChanged = this.cachedView === null || !CanvasInkRenderer.viewsEqual(this.cachedView, v);
    let mode: number; // 0=full 1=append 2=reuse
    if (viewChanged || this.cacheDirty) {
      mode = 0;
    } else {
      mode = this.classifyCommands(signatures);
    }

    let cachePublished = false;
    if (mode === 0) {
      this.rebuildOffscreen(offCtx, scene, v);
      this.publishCache();
      cachePublished = true;
    } else if (mode === 1) {
      // 前缀匹配的纯新增：先恢复旧位图，再在其上按当前视图矩阵追加新命令。
      offCtx.setTransform(1, 0, 0, 1, 0, 0);
      if (this.cacheBitmap !== null) {
        offCtx.drawImage(this.cacheBitmap, 0, 0, this.widthVp, this.heightVp);
      }
      offCtx.setTransform(v[0], v[1], v[2], v[3], v[4], v[5]);
      this.drawCommands(offCtx, scene.Committed.slice(this.cachedSignatures.length), false);
      this.publishCache();
      cachePublished = true;
    }
    this.cachedView = v.slice();
    this.cachedSignatures = signatures;
    this.cacheDirty = false;

    // 下层可见 Canvas：仅在缓存发布时重贴位图；复用帧保留持久位图，不产生任何绘制调用。
    if (cachePublished && this.cacheBitmap !== null) {
      this.committedLayer.setTransform(1, 0, 0, 1, 0, 0);
      this.committedLayer.clearRect(0, 0, this.widthVp, this.heightVp);
      this.committedLayer.drawImage(this.cacheBitmap, 0, 0, this.widthVp, this.heightVp);
    }

    this.renderActiveLayer(scene, v, mode === 0);
  }

  // 0=full（数量减少或任一既有命令变化），1=append（前缀全匹配且仅尾部新增），2=reuse（完全一致）
  private classifyCommands(signatures: string[]): number {
    const oldLen = this.cachedSignatures.length;
    const newLen = signatures.length;
    if (newLen === oldLen) {
      for (let i = 0; i < oldLen; i++) {
        if (this.cachedSignatures[i] !== signatures[i]) {
          return 0;
        }
      }
      return 2;
    }
    if (newLen < oldLen) {
      return 0;
    }
    for (let i = 0; i < oldLen; i++) {
      if (this.cachedSignatures[i] !== signatures[i]) {
        return 0;
      }
    }
    return 1;
  }

  private rebuildOffscreen(
    offCtx: OffscreenCanvasRenderingContext2D,
    scene: InkScene,
    v: number[]
  ): void {
    offCtx.setTransform(1, 0, 0, 1, 0, 0);
    offCtx.clearRect(0, 0, this.widthVp, this.heightVp);
    offCtx.setTransform(v[0], v[1], v[2], v[3], v[4], v[5]);
    this.drawCommands(offCtx, scene.Committed, false);
  }

  private publishCache(): void {
    if (this.offscreen === null) {
      return;
    }
    if (this.cacheBitmap !== null) {
      this.cacheBitmap.close();
      this.cacheBitmap = null;
    }
    this.cacheBitmap = this.offscreen.transferToImageBitmap();
  }

  private renderActiveLayer(scene: InkScene, v: number[], forceFull: boolean): void {
    // 自防御：没有活体笔画时预测段不允许存在（切工具/Dispose/切文档等路径宿主可能漏清）。
    // 清空后上一帧预测盒仍并入下方脏盒，残留像素会被擦除而不会被重画。
    if (scene.Active === null) {
      this.clearPrediction();
    }
    const selectionVisible = scene.Selection !== null;
    const selectionToggled = selectionVisible !== this.prevSelectionVisible;
    const activeBox = scene.Active !== null
      ? CanvasInkRenderer.commandBox(scene.Active, WORLD_BOX_PADDING_VP)
      : null;
    // F3：形状预览与活体笔画同处世界坐标，脏盒一并并入；橡皮筋为视图 DIP，单独并入屏幕脏盒。
    const previewBox = CanvasInkRenderer.commandsBox(scene.Preview, WORLD_BOX_PADDING_VP);
    const currMarqueeBox = CanvasInkRenderer.padScreenBox(scene.Marquee, this.widthVp, this.heightVp);
    const currPredictionBox = this.predictionAabb();
    // 当前帧活动层世界脏盒 = 活体笔画盒 ∪ 预测段盒 ∪ 预览盒。
    let currBox = activeBox;
    if (currPredictionBox !== null) {
      currBox = currBox === null ? currPredictionBox : CanvasInkRenderer.unionBox(currBox, currPredictionBox);
    }
    if (previewBox !== null) {
      currBox = currBox === null ? previewBox : CanvasInkRenderer.unionBox(currBox, previewBox);
    }
    // 性能（M1）：预览/橡皮筋为 transient 覆盖层，改走局部脏盒，不再每帧全屏重绘活动层；
    // 仅选区在场/切换或宿主强制时才全量重绘（选区涉及内容搬移，保持全量以控风险）。
    const fullRedraw = forceFull || selectionVisible || selectionToggled;

    if (fullRedraw) {
      this.active.setTransform(1, 0, 0, 1, 0, 0);
      this.active.clearRect(0, 0, this.widthVp, this.heightVp);
      this.active.setTransform(v[0], v[1], v[2], v[3], v[4], v[5]);
      if (scene.Active !== null) {
        this.drawCommands(this.active, [scene.Active], false);
      }
      this.drawPrediction(v);
      // R1 DrawLive 叠放顺序：live 之后画 shape preview，同款视图变换与描边样式。
      if (scene.Preview.length > 0) {
        this.drawCommands(this.active, scene.Preview, false);
      }
      if (scene.Selection !== null) {
        this.drawSelection(scene);
      }
      if (scene.Marquee !== null) {
        this.drawMarquee(scene.Marquee);
      }
    } else {
      let dirtyWorld: number[] | null = null;
      if (this.prevActiveBox !== null && currBox !== null) {
        dirtyWorld = CanvasInkRenderer.unionBox(this.prevActiveBox, currBox);
      } else if (currBox !== null) {
        dirtyWorld = currBox.slice();
      } else if (this.prevActiveBox !== null) {
        dirtyWorld = this.prevActiveBox.slice();
      }
      // 上一帧预测段所在区域也必须纳入清除/重绘范围，避免预测线拖影。
      if (dirtyWorld === null) {
        if (this.prevPredictionBox !== null) {
          dirtyWorld = this.prevPredictionBox.slice();
        }
      } else if (this.prevPredictionBox !== null) {
        dirtyWorld = CanvasInkRenderer.unionBox(dirtyWorld, this.prevPredictionBox);
      }
      // 上一帧形状预览所在区域一并纳入，避免两步体驻留预览拖影。
      if (dirtyWorld === null) {
        if (this.prevPreviewBox !== null) {
          dirtyWorld = this.prevPreviewBox.slice();
        }
      } else if (this.prevPreviewBox !== null) {
        dirtyWorld = CanvasInkRenderer.unionBox(dirtyWorld, this.prevPreviewBox);
      }
      // 屏幕脏盒 = 世界脏盒映射 ∪ 上一帧橡皮筋 ∪ 当前橡皮筋（橡皮筋为视图 DIP，不套视图矩阵）。
      let dirtyScreen: number[] | null = dirtyWorld !== null ? this.worldToScreenBox(dirtyWorld, v) : null;
      dirtyScreen = CanvasInkRenderer.unionScreenBox(dirtyScreen, this.prevMarqueeBox);
      dirtyScreen = CanvasInkRenderer.unionScreenBox(dirtyScreen, currMarqueeBox);
      if (dirtyScreen === null) {
        // 前后帧均无活动层内容：上层保持空位图，无需任何绘制。
        this.prevActiveBox = null;
        this.prevPredictionBox = null;
        this.prevPreviewBox = null;
        this.prevMarqueeBox = currMarqueeBox;
        this.prevSelectionVisible = selectionVisible;
        return;
      }
      this.active.setTransform(1, 0, 0, 1, 0, 0);
      this.active.clearRect(dirtyScreen[0], dirtyScreen[1],
        dirtyScreen[2] - dirtyScreen[0], dirtyScreen[3] - dirtyScreen[1]);
      // 世界内容（活体 + 预测 + 预览）clip 到世界脏盒，避免越界重绘。
      this.active.setTransform(v[0], v[1], v[2], v[3], v[4], v[5]);
      if (scene.Active !== null || scene.Preview.length > 0) {
        this.active.save();
        if (dirtyWorld !== null) {
          this.active.rect(dirtyWorld[0], dirtyWorld[1],
            dirtyWorld[2] - dirtyWorld[0], dirtyWorld[3] - dirtyWorld[1]);
          this.active.clip();
        }
        if (scene.Active !== null) {
          this.drawCommands(this.active, [scene.Active], false);
          this.drawPrediction(v);
        }
        if (scene.Preview.length > 0) {
          this.drawCommands(this.active, scene.Preview, false);
        }
        this.active.restore();
        // Active===null 时入口已 clearPrediction，预测段不再补画，避免无活体笔画的残留重绘。
      }
      // 橡皮筋在身份变换下绘制（drawMarquee 内部自设 identity），不套视图矩阵、不受世界 clip 影响。
      if (scene.Marquee !== null) {
        this.drawMarquee(scene.Marquee);
      }
    }

    this.prevActiveBox = activeBox;
    this.prevPredictionBox = currPredictionBox;
    this.prevPreviewBox = previewBox;
    this.prevMarqueeBox = currMarqueeBox;
    this.prevSelectionVisible = selectionVisible;
  }

  // 预测段世界坐标轴对齐包围盒；点数不足（不足一条线段）时返回 null。
  private predictionAabb(): number[] | null {
    if (this.prediction.length < 4) {
      return null;
    }
    let minX = Number.POSITIVE_INFINITY;
    let minY = Number.POSITIVE_INFINITY;
    let maxX = Number.NEGATIVE_INFINITY;
    let maxY = Number.NEGATIVE_INFINITY;
    for (let i = 0; i + 1 < this.prediction.length; i += 2) {
      const x = this.prediction[i];
      const y = this.prediction[i + 1];
      if (x < minX) {
        minX = x;
      }
      if (y < minY) {
        minY = y;
      }
      if (x > maxX) {
        maxX = x;
      }
      if (y > maxY) {
        maxY = y;
      }
    }
    if (!Number.isFinite(minX) || !Number.isFinite(minY) || !Number.isFinite(maxX) || !Number.isFinite(maxY)) {
      return null;
    }
    // 线宽一半作为额外填充，避免圆角线帽被裁掉。
    const pad = WORLD_BOX_PADDING_VP + this.predictionWidth * 0.5;
    return [minX - pad, minY - pad, maxX + pad, maxY + pad];
  }

  // 在活动层按当前视图矩阵描边预测段；调用方已设置好 transform（局部分支已 clip 到脏盒）。
  private drawPrediction(v: number[]): void {
    if (this.prediction.length < 4 || this.predictionWidth <= 0) {
      return;
    }
    const context = this.active;
    context.save();
    context.setTransform(v[0], v[1], v[2], v[3], v[4], v[5]);
    context.strokeStyle = CanvasInkRenderer.cssColor(this.predictionColor);
    context.lineWidth = this.predictionWidth;
    context.lineCap = 'round';
    context.lineJoin = 'round';
    context.beginPath();
    context.moveTo(this.prediction[0], this.prediction[1]);
    for (let i = 2; i + 1 < this.prediction.length; i += 2) {
      context.lineTo(this.prediction[i], this.prediction[i + 1]);
    }
    context.stroke();
    context.restore();
  }

  private ensureOffscreen(): void {
    if (this.offscreen !== null && this.offCtx !== null) {
      return;
    }
    // OffscreenCanvas 构造默认单位为 vp，与可见 CanvasRenderingContext2D 坐标系一致。
    const canvas = new OffscreenCanvas(this.widthVp, this.heightVp);
    const context = canvas.getContext('2d', new RenderingContextSettings(true));
    this.offscreen = canvas;
    this.offCtx = context;
    this.cacheDirty = true;
  }

  private destroyOffscreen(): void {
    if (this.cacheBitmap !== null) {
      this.cacheBitmap.close();
      this.cacheBitmap = null;
    }
    this.offscreen = null;
    this.offCtx = null;
  }

  private drawCommands(context: Any2DContext, commands: DrawCommand[], clear: boolean = true): void {
    if (clear) {
      context.clearRect(0, 0, this.widthVp, this.heightVp);
    }
    for (let i = 0; i < commands.length; i++) {
      const command = commands[i];
      context.fillStyle = CanvasInkRenderer.cssColor(command.Color);
      for (let c = 0; c < command.Contours.length; c++) {
        const contour = command.Contours[c];
        if (contour.length < 6) {
          continue;
        }
        // 防御：NaN/Infinity 轮廓在 Canvas 里会被近似成原点，表现为一条连到画布角落的长线。
        let valid = true;
        for (let k = 0; k + 1 < contour.length; k += 2) {
          if (!Number.isFinite(contour[k]) || !Number.isFinite(contour[k + 1])) {
            valid = false;
            break;
          }
        }
        if (!valid) {
          continue;
        }
        context.beginPath();
        context.moveTo(contour[0], contour[1]);
        for (let p = 2; p + 1 < contour.length; p += 2) {
          context.lineTo(contour[p], contour[p + 1]);
        }
        context.closePath();
        context.fill('nonzero');
      }
    }
  }

  private drawSelection(scene: InkScene): void {
    const selection = scene.Selection;
    if (selection === null) {
      return;
    }
    const left = selection.Bounds[0];
    const top = selection.Bounds[1];
    const right = selection.Bounds[2];
    const bottom = selection.Bounds[3];
    const centerX = (left + right) / 2;
    const centerY = (top + bottom) / 2;
    const context = this.active;
    // 选框/连接线/手柄描边与半径对齐 C# 屏幕固定像素（InkSceneRenderer.cs:73-88 在屏幕空间以 1.5/6/7 绘制）；
    // 鸿蒙在视图矩阵下绘制，故除以视图缩放模长抵消缩放，默认 zoom=1 时逐位不变。
    // 旋转柄 -28 偏移仍为世界单位（C# 同样先取世界坐标再套视图变换），不在此抵消。
    const vm = scene.View;
    const rawScale = Math.sqrt(vm[0] * vm[0] + vm[1] * vm[1]);
    const scale = Number.isFinite(rawScale) && rawScale > 0 ? rawScale : 1;
    context.save();
    context.translate(centerX, centerY);
    context.rotate(selection.RotationRadians);
    context.strokeStyle = 'rgba(0,120,212,1)';
    context.lineWidth = 1.5 / scale;
    context.strokeRect(left - centerX, top - centerY, right - left, bottom - top);
    // 旋转柄连接线（R1 InkSceneRenderer.cs:82-86）：顶边中点局部 (0, top-centerY)，
    // 旋转柄局部 (0, top-28-centerY)，与 SceneBuilder 第 5 手柄点一致。
    context.beginPath();
    context.moveTo(0, top - centerY);
    context.lineTo(0, top - 28 - centerY);
    context.stroke();
    context.fillStyle = '#FFFFFF';
    for (let i = 0; i < selection.Handles.length; i++) {
      const handle = selection.Handles[i];
      const radius = (i === 4 ? 7 : 6) / scale;
      context.beginPath();
      context.arc(handle.X - centerX, handle.Y - centerY, radius, 0, Math.PI * 2);
      context.fill();
      context.stroke();
    }
    context.restore();
  }

  // F3 框选橡皮筋：视图 DIP 矩形，平台通用选择框样式——虚线描边 + 淡色半透明填充，
  // accent 与现有选区同款 rgba(0,120,212,*)。必须在身份变换下绘制，不套视图矩阵。
  private drawMarquee(bounds: number[]): void {
    if (bounds.length !== 4) {
      return;
    }
    const left = bounds[0];
    const top = bounds[1];
    const right = bounds[2];
    const bottom = bounds[3];
    if (right <= left || bottom <= top) {
      return;
    }
    const context = this.active;
    context.save();
    context.setTransform(1, 0, 0, 1, 0, 0);
    context.fillStyle = 'rgba(0,120,212,0.12)';
    context.fillRect(left, top, right - left, bottom - top);
    context.strokeStyle = 'rgba(0,120,212,1)';
    context.lineWidth = 1.5;
    context.setLineDash([4, 4]);
    context.strokeRect(left, top, right - left, bottom - top);
    context.setLineDash([]);
    context.restore();
  }

  // 命令身份指纹：仅读取轮廓元数据与首末坐标，不做几何计算，用于缓存命中判定。
  private static commandSignature(command: DrawCommand): string {
    let totalPoints = 0;
    let firstX = 0;
    let firstY = 0;
    let lastX = 0;
    let lastY = 0;
    for (let c = 0; c < command.Contours.length; c++) {
      const contour = command.Contours[c];
      totalPoints += contour.length / 2;
      if (c === 0 && contour.length >= 2) {
        firstX = contour[0];
        firstY = contour[1];
      }
      if (contour.length >= 2) {
        lastX = contour[contour.length - 2];
        lastY = contour[contour.length - 1];
      }
    }
    return command.Color + '#' + command.Contours.length + '#' + totalPoints + '#'
      + firstX.toFixed(2) + ',' + firstY.toFixed(2) + '#' + lastX.toFixed(2) + ',' + lastY.toFixed(2);
  }

  private static viewsEqual(a: number[], b: number[]): boolean {
    if (a.length !== 6 || b.length !== 6) {
      return false;
    }
    for (let i = 0; i < 6; i++) {
      if (Math.abs(a[i] - b[i]) > MATRIX_EPSILON) {
        return false;
      }
    }
    return true;
  }

  private static commandBox(command: DrawCommand, padding: number): number[] | null {
    let minX = Number.POSITIVE_INFINITY;
    let minY = Number.POSITIVE_INFINITY;
    let maxX = Number.NEGATIVE_INFINITY;
    let maxY = Number.NEGATIVE_INFINITY;
    for (let c = 0; c < command.Contours.length; c++) {
      const contour = command.Contours[c];
      for (let p = 0; p + 1 < contour.length; p += 2) {
        const x = contour[p];
        const y = contour[p + 1];
        if (x < minX) {
          minX = x;
        }
        if (y < minY) {
          minY = y;
        }
        if (x > maxX) {
          maxX = x;
        }
        if (y > maxY) {
          maxY = y;
        }
      }
    }
    if (!Number.isFinite(minX) || !Number.isFinite(minY) ||
        !Number.isFinite(maxX) || !Number.isFinite(maxY)) {
      return null;
    }
    return [minX - padding, minY - padding, maxX + padding, maxY + padding];
  }

  private static unionBox(a: number[], b: number[]): number[] {
    return [
      Math.min(a[0], b[0]),
      Math.min(a[1], b[1]),
      Math.max(a[2], b[2]),
      Math.max(a[3], b[3])
    ];
  }

  // 视图 DIP 矩形加屏幕填充并夹取到视口；空/退化/非有限返回 null。
  private static padScreenBox(bounds: number[] | null, widthVp: number, heightVp: number): number[] | null {
    if (bounds === null || bounds.length !== 4) {
      return null;
    }
    const l = bounds[0];
    const t = bounds[1];
    const r = bounds[2];
    const b = bounds[3];
    if (!Number.isFinite(l) || !Number.isFinite(t) || !Number.isFinite(r) || !Number.isFinite(b)) {
      return null;
    }
    const left = Math.max(0, l - SCREEN_BOX_PADDING_VP);
    const top = Math.max(0, t - SCREEN_BOX_PADDING_VP);
    const right = Math.min(widthVp, r + SCREEN_BOX_PADDING_VP);
    const bottom = Math.min(heightVp, b + SCREEN_BOX_PADDING_VP);
    if (right <= left || bottom <= top) {
      return null;
    }
    return [left, top, right, bottom];
  }

  // 可空屏幕盒并集；任一为 null 时退化为另一个。
  private static unionScreenBox(a: number[] | null, b: number[] | null): number[] | null {
    if (a === null) {
      return b;
    }
    if (b === null) {
      return a;
    }
    return [
      Math.min(a[0], b[0]), Math.min(a[1], b[1]),
      Math.max(a[2], b[2]), Math.max(a[3], b[3])
    ];
  }

  // 多条命令的世界坐标并集包围盒；全部为空/退化为 null。
  private static commandsBox(commands: DrawCommand[], padding: number): number[] | null {
    let result: number[] | null = null;
    for (let i = 0; i < commands.length; i++) {
      const box = CanvasInkRenderer.commandBox(commands[i], padding);
      if (box !== null) {
        result = result === null ? box : CanvasInkRenderer.unionBox(result, box);
      }
    }
    return result;
  }

  // 世界坐标脏盒经仿射视图矩阵映射为屏幕空间轴对齐包围盒，并与视口求交。
  private worldToScreenBox(world: number[], v: number[]): number[] | null {
    const cornersX: number[] = [world[0], world[2], world[0], world[2]];
    const cornersY: number[] = [world[1], world[1], world[3], world[3]];
    let minX = Number.POSITIVE_INFINITY;
    let minY = Number.POSITIVE_INFINITY;
    let maxX = Number.NEGATIVE_INFINITY;
    let maxY = Number.NEGATIVE_INFINITY;
    for (let i = 0; i < 4; i++) {
      const x = cornersX[i];
      const y = cornersY[i];
      const sx = v[0] * x + v[2] * y + v[4];
      const sy = v[1] * x + v[3] * y + v[5];
      if (!Number.isFinite(sx) || !Number.isFinite(sy)) {
        return null;
      }
      if (sx < minX) {
        minX = sx;
      }
      if (sy < minY) {
        minY = sy;
      }
      if (sx > maxX) {
        maxX = sx;
      }
      if (sy > maxY) {
        maxY = sy;
      }
    }
    minX = Math.max(0, minX - SCREEN_BOX_PADDING_VP);
    minY = Math.max(0, minY - SCREEN_BOX_PADDING_VP);
    maxX = Math.min(this.widthVp, maxX + SCREEN_BOX_PADDING_VP);
    maxY = Math.min(this.heightVp, maxY + SCREEN_BOX_PADDING_VP);
    if (maxX <= minX || maxY <= minY) {
      return null;
    }
    return [minX, minY, maxX, maxY];
  }

  private static cssColor(packed: number): string {
    const n = packed >>> 0;
    const a = ((n >>> 24) & 0xff) / 255;
    const r = (n >>> 16) & 0xff;
    const g = (n >>> 8) & 0xff;
    const b = n & 0xff;
    return 'rgba(' + r + ',' + g + ',' + b + ',' + a.toFixed(3) + ')';
  }
}
