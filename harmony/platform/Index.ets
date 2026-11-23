export const MODULE: string = 'platform';
export {
  SYSCAP_ARKUI_FULL,
  SYSCAP_STYLUS_HANDWRITE,
  SYSCAP_STYLUS_SERVICE,
  SYSCAP_INPUT_POINTER,
  capabilityMatrix,
  sdkApiVersion,
  isApi26,
  hasSysCap,
  supportsStylusBoost,
  supportsSystemMaterial,
  supportsSymbolGlyph,
  systemMotionReduced
} from './src/main/ets/compat/Capabilities';
export type { CapabilityMatrix } from './src/main/ets/compat/CapabilityMatrix';
export { breakpointForWidth, isFullLayout } from './src/main/ets/device/Breakpoints';
export type { Breakpoint } from './src/main/ets/device/Breakpoints';
export { deviceType, is2in1, isTablet, isPhone, isFullDevice } from './src/main/ets/device/DeviceService';
export { StorageService, describeError } from './src/main/ets/storage/StorageService';
export { logInfo, logWarn, logError, setLogEnabled } from './src/main/ets/diagnostics/LogService';
export { PointerKind, normalizePointer, pointerKind, isMouseEvent } from './src/main/ets/input/InputAdapter';
export type { RawTouchEvent, NormalizedPointer } from './src/main/ets/input/InputAdapter';
export { StrokePredictor } from './src/main/ets/input/StrokePredictor';
export type { PredictedPoint } from './src/main/ets/input/StrokePredictor';
export { StylusBinding } from './src/main/ets/input/StylusBinding';
export type { StylusDoubleTapEvent, StylusSqueezeEvent, StylusDoubleTapCallback, StylusSqueezeCallback, StylusGestureType } from './src/main/ets/input/StylusBinding';
export { floatingBarPosition, clampToWindow, cornerPanelPosition } from './src/main/ets/layout/WindowLayout';
export type { PointResult } from './src/main/ets/layout/WindowLayout';
export { CanvasInkRenderer } from './src/main/ets/rendering/CanvasInkRenderer';
export { InkCanvasSurface } from './src/main/ets/rendering/InkCanvasSurface';
export { PdfPageSource } from './src/main/ets/pdf/PdfPageSource';
export {
  writeAnnotatedPdf,
  readPdfGeometries,
  containPlacement
} from './src/main/ets/pdf/PdfAnnotationWriter';
export type { AnnotatedPdfResult, PagePlacement } from './src/main/ets/pdf/PdfAnnotationWriter';
export { AutoSaveService } from './src/main/ets/persistence/AutoSaveService';
export { captureComponentToPng } from './src/main/ets/persistence/SnapshotService';
export { openExternalUrl } from './src/main/ets/system/SystemServices';
export { playCountdownFinish } from './src/main/ets/media/SoundService';
export { compressImageBuffer, COMPRESS_MAX_DIM } from './src/main/ets/media/ImageCompress';
export { CountdownPiPService, CountdownPiPParams } from './src/main/ets/window/CountdownPiP';
export {
  WPS_PHONE_BUNDLE,
  WPS_PAD_BUNDLE,
  presentationUtd,
  openInOfficeApp,
  openInWps,
  openWithChooser
} from './src/main/ets/system/WpsInterop';
export { ownBundleName, openAppGalleryDetail, checkForUpdate } from './src/main/ets/system/MarketUpdate';
export type { UpdateCheckOutcome } from './src/main/ets/system/MarketUpdate';
export {
  AiConfig,
  AI_DEFAULT_TIMEOUT_MS,
  AI_DEFAULT_MODEL,
  envValue,
  parseTimeoutMs,
  readAiConfig,
  describeAiConfig
} from './src/main/ets/assistant/AiConfig';
export { AiClient, buildPrompt, buildPayload, buildMockScript } from './src/main/ets/assistant/AiClient';
export type {
  AssistantStream,
  AssistantEventSink,
  AssistantErrorSink,
  AssistantDoneSink,
  ChatMessage,
  AssistantPayload
} from './src/main/ets/assistant/AiClient';
export { CAPTURE_MAX_EDGE, captureRegionBase64 } from './src/main/ets/assistant/AssistantCapture';

export { InkCastClient } from './src/main/ets/net/InkCastClient';
export type { InkCastEvent } from './src/main/ets/net/InkCastClient';
