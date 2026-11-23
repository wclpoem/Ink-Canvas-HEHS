export const MODULE: string = 'core';
export { toTaperKind, toPenStyle, toEraserRadius, menuCatalogProbe, PEN_NIBS, PEN_STYLES, ERASER_SIZES, ERASER_MODES, AUTO_DELETE_DAYS, THEME_OPTIONS, PEN_WIDTH_MINIMUM, PEN_WIDTH_MAXIMUM, HIGHLIGHTER_WIDTH_MAXIMUM, PEN_WIDTH_STEP, HIGHLIGHTER_ALPHA_MINIMUM, HIGHLIGHTER_ALPHA_MAXIMUM, HIGHLIGHTER_ALPHA_STEP } from './src/main/ets/settings/OriginalMenuCatalog';
export { resetToRecommendation } from './src/main/ets/settings/AppSettings';
export { InkPoint } from './src/main/ets/ink/InkPoint';
export { InkColor, packArgb, unpackArgb, withAlphaArgb } from './src/main/ets/ink/InkColor';
export { StrokeAttributes, TaperKind, PenNib, strokeAttributesEffective } from './src/main/ets/ink/StrokeAttributes';
export type { StrokeAttributesInput } from './src/main/ets/ink/StrokeAttributes';
export { effectiveWidths, applyTaperAt, createTaperProfile, TaperProfile, NoneTaperProfile, VelocityTaperProfile, TailTaperProfile, SimulatedPressureTaperProfile } from './src/main/ets/ink/TaperProfile';
export type { TaperOptionsInput } from './src/main/ets/ink/TaperProfile';
export { OneEuroFilter } from './src/main/ets/ink/OneEuroFilter';
export { InkStroke, Rect, SelectionFrameState, strokeBounds, setTaperAndWidths, strokeTaperProbe } from './src/main/ets/ink/InkStroke';
export type { StrokePointInput, StrokeTaperSampleInput, StrokeTaperProbeResult, ShapeSourceData } from './src/main/ets/ink/InkStroke';
export { InkDocument } from './src/main/ets/ink/InkDocument';
export {
  buildSegmentOutline,
  buildUnionContours,
  buildOutlineFlat,
  signedAreaOfFlat
} from './src/main/ets/ink/StrokeGeometry';
export { eraseDisc, eraseDiscSummary, EraseOutcome } from './src/main/ets/ink/StrokeEraser';
export type { EraserStrokeInput, EraserSummary, EraserPointInput, EraserFragmentSummary } from './src/main/ets/ink/StrokeEraser';
export { UndoStack, simulateUndo } from './src/main/ets/history/UndoStack';
export type { UndoAction, UndoSimResult } from './src/main/ets/history/UndoStack';
export {
  Point2,
  Matrix3x2,
  identityMatrix,
  matrixFromArray,
  matrixToArray,
  multiplyMatrix,
  transformPointByMatrix,
  createTranslationMatrix,
  createScaleMatrix,
  createRotationMatrix,
  invertMatrix,
  matrixEquals,
  matrixMultiply,
  transformPoint,
  createRotation,
  createScale,
  createTranslation,
  invertMatrixValues
} from './src/main/ets/math/Matrix3x2';
export {
  SelectionHandle,
  HANDLE_RADIUS,
  ROTATE_OFFSET,
  rotateHandlePositionRect,
  hitTestRect,
  dragMatrixRect,
  selectionHitTest,
  selectionDragMatrix,
  rotateHandlePosition
} from './src/main/ets/selection/SelectionGeometry';
export { visibleResizeHandles, visibleHandlesForFrame } from './src/main/ets/selection/SelectionGeometry';
export type { BoundsInput } from './src/main/ets/selection/SelectionGeometry';
export { placeToolbar, boardDockLayout } from './src/main/ets/selection/LayoutHelpers';
export type { BoardDockLayoutResult } from './src/main/ets/selection/LayoutHelpers';
export { LassoGeometry } from './src/main/ets/selection/LassoGeometry';
export type { LassoPoint } from './src/main/ets/selection/LassoGeometry';
export { TwoFingerManipulator, simulateTwoFinger } from './src/main/ets/ink/TwoFingerManipulator';
export type { TwoFingerOptionsInput, TwoFingerSample, TwoFingerUpdate, TwoFingerSimResult } from './src/main/ets/ink/TwoFingerManipulator';
export { MatrixTransformCenterPointOptions, resolvePivot } from './src/main/ets/ink/MatrixTransformCenter';
export { TouchIntent, TouchDecision, measureContactWidth, boundsWidth, classifyContact } from './src/main/ets/input/TouchContactClassifier';
export type { AdvancedTouchInput, TouchClassifyInput } from './src/main/ets/input/TouchContactClassifier';
export { PalmRejector, OverlayPointerKind, simulatePalm } from './src/main/ets/input/PalmRejector';
export { calibrationMultiplier } from './src/main/ets/input/TouchCalibration';
export type { PalmEvent, PalmOptions, PalmEventResult, PalmSimResult } from './src/main/ets/input/PalmRejector';
export { ReplayClock, simulateReplayClock } from './src/main/ets/replay/ReplayClock';
export type { ReplayClockSimResult } from './src/main/ets/replay/ReplayClock';
export { InkReplaySession, replayVisibleCounts, replayFrameProbe } from './src/main/ets/replay/InkReplaySession';
export type { ReplayFrameProbeResult } from './src/main/ets/replay/InkReplaySession';
export type { ReplayVisibleResult } from './src/main/ets/replay/InkReplaySession';
export { blendOver } from './src/main/ets/imaging/PixelCompositing';
export { compressTargetSize } from './src/main/ets/imaging/CompressMath';
export { PdfPageGeometry, PdfContinuousLayout, parsePdfPageRange, pdfGeometryProbe, pdfRangeProbe, pdfLayoutProbe } from './src/main/ets/pdf/PdfLayout';
export { encodePdfInk, decodePdfInkSummary, PDF_INK_MAXIMUM_POINTS } from './src/main/ets/pdf/PdfInkCodec';
export type { PdfPoint } from './src/main/ets/pdf/PdfLayout';
export type { PdfInkSummary } from './src/main/ets/pdf/PdfInkCodec';
export { svgLength, svgResolveSize } from './src/main/ets/imaging/SvgSize';
export type { SvgSize } from './src/main/ets/imaging/SvgSize';
export type { CompressTarget } from './src/main/ets/imaging/CompressMath';
export { AppTheme, getMainWindowTheme, getBoardTheme } from './src/main/ets/theming/ThemeResolver';
export { AppVersion, parseVersion, compareVersions } from './src/main/ets/versioning/AppVersion';
export type { VersionParseSummary } from './src/main/ets/versioning/AppVersion';
export { mayInstallSilently } from './src/main/ets/update/UpdatePolicy';
export type { StartupPolicyInput } from './src/main/ets/update/UpdatePolicy';
export {
  ReleaseInfo,
  parseLatest,
  parseNewestStable,
  parseLatestSummary,
  parseNewestStableSummary
} from './src/main/ets/update/ReleaseFeed';
export type { ReleaseSummary } from './src/main/ets/update/ReleaseFeed';
export { createDefaultSettings } from './src/main/ets/settings/AppSettings';
export type {
  AppSettings,
  AdvancedSettings,
  AppearanceSettings,
  AutomationSettings,
  CanvasSettings,
  GestureSettings,
  PowerPointSettings,
  InkToShapeSettings,
  StartupSettings,
  RandSettings
} from './src/main/ets/settings/AppSettings';
export { parseSettings, stringifySettings, settingsRoundtrip } from './src/main/ets/persistence/SettingsCodec';
export { parseIcink, stringifyIcink, icinkRoundtrip } from './src/main/ets/persistence/IcinkCodec';
export type { IcinkStrokeSummary, IcinkPointRow } from './src/main/ets/persistence/IcinkCodec';
export { parseBoardPack, stringifyBoardPack, boardRoundtrip } from './src/main/ets/persistence/BoardPackCodec';
export type {
  BoardPackSummary,
  BoardPageSummary,
  BoardAssetSummary,
  BoardStrokeSummary
} from './src/main/ets/persistence/BoardPackCodec';
export { parsePptManifest, stringifyPptManifest, pptManifestRoundtrip } from './src/main/ets/persistence/PptManifestCodec';
export type { PptManifest, PptManifestSlide } from './src/main/ets/persistence/PptManifestCodec';
export {
  ShapeKind,
  DashLength,
  DashPeriod,
  DotPeriod,
  DotPressure,
  EllipseDashStep,
  EllipseDashPeriod,
  SolidEllipseRatio,
  generate,
  generateCuboidDepth,
  generateHyperbolaBranches,
  generateCuboid,
  isHyperbola,
  hasHyperbolaFoci,
  hasHyperbolaAsymptotes,
  snapParallelEnd,
  parallelLines,
  stroke,
  dot,
  arrow,
  axisArrow,
  dashedLine,
  dots,
  dottedLine,
  ellipseStroke,
  ellipsePoints,
  dashedEllipseArcs
} from './src/main/ets/shapes/ShapeGenerator';
export type { PointLike, SnapResult, StrokeSummary, StrokePointSummary } from './src/main/ets/shapes/ShapeGenerator';
export {
  RecognizedShape,
  MinSize,
  recognize,
  recognizeStrokes
} from './src/main/ets/recognition/ShapeRecognizer';
export type { InkPointLike, RecognitionPoint, RecognitionSummary } from './src/main/ets/recognition/ShapeRecognizer';
export {
  AssistantState,
  AssistantMode,
  AssistantEventType,
  AssistantRole,
  emptyEvent,
  eventFromText,
  modeName,
  stateName
} from './src/main/ets/assistant/AssistantTypes';
export type {
  AssistantEvent,
  AssistantMessage,
  AssistantRequest,
  AssistantStep,
  AssistantViewport,
  InkPointPayload,
  InkStrokePayload,
  SelectionRect
} from './src/main/ets/assistant/AssistantTypes';
export {
  createSession,
  beginCapture,
  beginRecognize,
  beginStream,
  applyEvent,
  reduceEvents,
  finish,
  fail,
  firstTokenTimedOut,
  canRetry,
  beginFollowUp,
  machineProbe,
  assistantStateName
} from './src/main/ets/assistant/AssistantMachine';
export type { AssistantSession, MachineProbe } from './src/main/ets/assistant/AssistantMachine';
export {
  MIN_SELECTION_SIZE,
  emptyRect,
  isEmptyRect,
  rectWidth,
  rectHeight,
  rectCenterX,
  rectCenterY,
  lassoBounds,
  lassoPerimeter,
  isClosedPath,
  closePath,
  polygonArea,
  pointInPolygon,
  strokeCoverageRatio,
  strokeHitByLasso,
  usableSelection,
  lassoProbe
} from './src/main/ets/assistant/LassoGeometry';
export type { LassoPoint as AssistantLassoPoint, LassoSummary } from './src/main/ets/assistant/LassoGeometry';
export {
  SAFE_MARGIN,
  CARD_GAP,
  CardSide,
  clampPlacement,
  placeCard,
  followAnchor,
  dragPlacement,
  placementProbe,
  followProbe
} from './src/main/ets/assistant/AnchorLayout';
export type { CardPlacement, CardSize, ViewportSize } from './src/main/ets/assistant/AnchorLayout';
export {
  FormulaKind,
  latexToPlain,
  latexIsRenderable,
  splitFormulas,
  formulaProbe
} from './src/main/ets/assistant/LatexLite';
export type { FormulaSegment, FormulaProbe } from './src/main/ets/assistant/LatexLite';
export { MdBlockKind, parseMarkdownProgressive, markdownProbe } from './src/main/ets/assistant/MarkdownLite';
export type { MdBlock, MarkdownProbe } from './src/main/ets/assistant/MarkdownLite';
export {
  PasteKind,
  AI_CARD_CONTENT_TYPE,
  PASTE_GAP,
  estimateCardSize,
  buildPasteObject,
  clampPasteRect,
  pasteResult,
  pasteUndoProbe
} from './src/main/ets/assistant/PasteBackModel';
export type { PasteInput, PasteObject, PasteResult } from './src/main/ets/assistant/PasteBackModel';
export { SseParser, sseEventFrom, extractOpenAiDelta, parseSseChunk } from './src/main/ets/assistant/SseParser';
export type { SseParseResult } from './src/main/ets/assistant/SseParser';
export {
  AiCaptureKind,
  AI_ENGINE_LABEL,
  AI_NOTE_ACCENTS,
  normalizeRegion,
  classifyCaptureKind,
  summarizeInk,
  recognizeCapture,
  buildLectureNotes,
  toMarkdown,
  shapeSnapOptions,
  recommendedSnapIndex,
  confidencePercent,
  aiAssistantProbe,
  recognizeFromText
} from './src/main/ets/ai/AiAssistant';
export type {
  AiRegion,
  AiInkSummary,
  AiCaptureInput,
  AiTextInput,
  AiRecognizeResult,
  LectureNoteCard,
  LectureNotes,
  ShapeSnapOption
} from './src/main/ets/ai/AiAssistant';
