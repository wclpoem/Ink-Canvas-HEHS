// @parity src/InkCanvas.Controls/Controls/Glyphs.cs
// 图标三源渲染：Fluent System Icons（MIT，microsoft/fluentui-system-icons）双风格——
// Filled（默认，InkFluentIcons）/ Regular（InkFluentIconsRegular，设置切换）；
// 与 Windows 端共用同一字体同一码点，跨 Win10/11 与鸿蒙视觉一致。
// sys.symbol（HarmonyOS 原生）为无字体回退；未映射回退文字。
// 映射数据单一来源 parity/icon-map.json（Segoe PUA 码点=跨端稳定 ID；approx 项需人工核对）。
// $r 只接受字面量资源名，故 sys.symbol 分支按名逐一展开。

export interface GlyphSymbolEntry {
  codepoint: string;
  symbol: string;
  // Fluent System Icons 码点：fluentCp=Filled（默认）/ regularCp=Regular（风格切换）。
  fluentCp: number;
  regularCp: number;
}

export interface SymbolFluentEntry {
  symbol: string;
  fluentCp: number;
  regularCp: number;
}

// 码点 → symbol 名 + 双风格 Fluent 码点（数据源：parity/icon-map.json，tools/gen-icon-map.py 生成勿手改）。
export const GLYPH_SYMBOLS: GlyphSymbolEntry[] = [
  { codepoint: 'U+E706', symbol: 'theme_light', fluentCp: 0xF8BA, regularCp: 0xF8A2 },  // Brightness（近似）
  { codepoint: 'U+E708', symbol: 'theme_dark', fluentCp: 0xF33C, regularCp: 0xF33C },  // QuietHours（近似）
  { codepoint: 'U+E70D', symbol: 'chevron_down', fluentCp: 0xF2A4, regularCp: 0xF2A4 },  // ChevronDown
  { codepoint: 'U+E70E', symbol: 'chevron_up', fluentCp: 0xF2B7, regularCp: 0xF2B7 },  // ChevronUp
  { codepoint: 'U+E70F', symbol: 'pencil_tip', fluentCp: 0xF3DD, regularCp: 0xF3DE },  // Edit
  { codepoint: 'U+E710', symbol: 'plus', fluentCp: 0xF10A, regularCp: 0xF10A },  // Add
  { codepoint: 'U+E711', symbol: 'xmark', fluentCp: 0xF36A, regularCp: 0xF36A },  // Cancel
  { codepoint: 'U+E712', symbol: 'more', fluentCp: 0xE833, regularCp: 0xE825 },  // More（近似）
  { codepoint: 'U+E713', symbol: 'gearshape', fluentCp: 0xF6B3, regularCp: 0xF6AA },  // Settings
  { codepoint: 'U+E714', symbol: 'video', fluentCp: 0xF865, regularCp: 0xF84D },  // Video
  { codepoint: 'U+E716', symbol: 'person_2', fluentCp: 0xF5B3, regularCp: 0xF5A9 },  // People
  { codepoint: 'U+E718', symbol: 'pin', fluentCp: 0xF60C, regularCp: 0xF602 },  // Pin
  { codepoint: 'U+E722', symbol: 'camera', fluentCp: 0xF255, regularCp: 0xF255 },  // Camera
  { codepoint: 'U+E72C', symbol: 'arrow_counterclockwise', fluentCp: 0xF1A0, regularCp: 0xF1A0 },  // Refresh（近似）
  { codepoint: 'U+E72E', symbol: 'lock', fluentCp: 0xE79E, regularCp: 0xE790 },  // Lock（近似）
  { codepoint: 'U+E73E', symbol: 'checkmark', fluentCp: 0xF295, regularCp: 0xF295 },  // CheckMark
  { codepoint: 'U+E740', symbol: 'magnifyingglass', fluentCp: 0xF8DD, regularCp: 0xF8C5 },  // FullScreen（近似）
  { codepoint: 'U+E74D', symbol: 'trash', fluentCp: 0xF34D, regularCp: 0xF34D },  // Delete
  { codepoint: 'U+E74E', symbol: 'save', fluentCp: 0xF68A, regularCp: 0xF680 },  // Save
  { codepoint: 'U+E75C', symbol: 'eraser_line', fluentCp: 0xE5F2, regularCp: 0xE5E5 },  // EraseTool
  { codepoint: 'U+E760', symbol: 'arrow_previous', fluentCp: 0xF16D, regularCp: 0xF16D },  // PageLeft（近似）
  { codepoint: 'U+E761', symbol: 'arrow_next', fluentCp: 0xF16B, regularCp: 0xF16B },  // PageRight（近似）
  { codepoint: 'U+E765', symbol: 'keyboard', fluentCp: 0xF4C3, regularCp: 0xF4B9 },  // KeyboardClassic（近似）
  { codepoint: 'U+E768', symbol: 'play', fluentCp: 0xF610, regularCp: 0xF606 },  // Play
  { codepoint: 'U+E769', symbol: 'pause', fluentCp: 0xF5AC, regularCp: 0xF5A2 },  // Pause
  { codepoint: 'U+E76B', symbol: 'chevron_left', fluentCp: 0xF2AB, regularCp: 0xF2AB },  // ChevronLeft
  { codepoint: 'U+E76C', symbol: 'chevron_right', fluentCp: 0xF2B1, regularCp: 0xF2B1 },  // ChevronRight
  { codepoint: 'U+E77A', symbol: 'pin_off', fluentCp: 0xF60E, regularCp: 0xF604 },  // Unpin
  { codepoint: 'U+E77B', symbol: 'person', fluentCp: 0xF5C8, regularCp: 0xF5BE },  // Contact（近似）
  { codepoint: 'U+E77C', symbol: 'note', fluentCp: 0xF576, regularCp: 0xF56C },  // Memo（近似）
  { codepoint: 'U+E77F', symbol: 'paste', fluentCp: 0xF2D6, regularCp: 0xF2D6 },  // Paste
  { codepoint: 'U+E785', symbol: 'lock_open', fluentCp: 0xE7A4, regularCp: 0xE796 },  // Unlock（近似）
  { codepoint: 'U+E786', symbol: 'play_circle', fluentCp: 0xF612, regularCp: 0xF608 },  // Slideshow（近似）
  { codepoint: 'U+E790', symbol: 'color', fluentCp: 0xF2F6, regularCp: 0xF2F6 },  // Color（近似）
  { codepoint: 'U+E7A6', symbol: 'redo', fluentCp: 0xF16F, regularCp: 0xF16F },  // Redo
  { codepoint: 'U+E7A7', symbol: 'undo', fluentCp: 0xF19A, regularCp: 0xF19A },  // Undo
  { codepoint: 'U+E7AD', symbol: 'arrow_clockwise', fluentCp: 0xF13E, regularCp: 0xF13E },  // Rotate
  { codepoint: 'U+E7B3', symbol: 'eye_slash', fluentCp: 0xE603, regularCp: 0xE5F6 },  // RedEye（近似）
  { codepoint: 'U+E7C2', symbol: 'move', fluentCp: 0xF169, regularCp: 0xF169 },  // Move
  { codepoint: 'U+E7E6', symbol: 'highlighter_tip', fluentCp: 0xF481, regularCp: 0xF47D },  // Highlight
  { codepoint: 'U+E7E8', symbol: 'arrow_right_to_line', fluentCp: 0xF619, regularCp: 0xF60F },  // PowerButton（近似）
  { codepoint: 'U+E7F4', symbol: 'square_and_pencil', fluentCp: 0xF1E3, regularCp: 0xF1E3 },  // TVMonitor（近似）
  { codepoint: 'U+E80A', symbol: 'arrow_down', fluentCp: 0xF149, regularCp: 0xF149 },  // TiltDown（近似）
  { codepoint: 'U+E815', symbol: 'hand_tap', fluentCp: 0xE6E5, regularCp: 0xE6D8 },  // Touch（近似）
  { codepoint: 'U+E81C', symbol: 'clock', fluentCp: 0xF486, regularCp: 0xF47F },  // History
  { codepoint: 'U+E81E', symbol: 'stack', fluentCp: 0xF7BE, regularCp: 0xF7A6 },  // MapLayers（近似）
  { codepoint: 'U+E823', symbol: 'arrow_counterclockwise', fluentCp: 0xF140, regularCp: 0xF140 },  // Recent（近似）
  { codepoint: 'U+E82D', symbol: 'doc_plaintext', fluentCp: 0xE173, regularCp: 0xF8FE },  // Dictionary（近似）
  { codepoint: 'U+E896', symbol: 'arrow_down_circle', fluentCp: 0xF151, regularCp: 0xF151 },  // Download（近似）
  { codepoint: 'U+E897', symbol: 'questionmark_circle', fluentCp: 0xF648, regularCp: 0xF63E },  // Help
  { codepoint: 'U+E899', symbol: 'move', fluentCp: 0xF3C1, regularCp: 0xF3C1 },  // Emoji（近似）
  { codepoint: 'U+E8A1', symbol: 'eye', fluentCp: 0xE600, regularCp: 0xE5F3 },  // PreviewLink（近似）
  { codepoint: 'U+E8A3', symbol: 'magnifyingglass', fluentCp: 0xF8DD, regularCp: 0xF8C5 },  // ZoomIn（近似）
  { codepoint: 'U+E8A5', symbol: 'doc', fluentCp: 0xF379, regularCp: 0xF379 },  // Document
  { codepoint: 'U+E8A9', symbol: 'wrench_and_screwdriver', fluentCp: 0xF467, regularCp: 0xF463 },  // ViewAll（近似）
  { codepoint: 'U+E8AB', symbol: 'arrow_left_arrow_right', fluentCp: 0xF18E, regularCp: 0xF18E },  // Switch
  { codepoint: 'U+E8B0', symbol: 'mouse', fluentCp: 0xE450, regularCp: 0xE444 },  // Click（近似）
  { codepoint: 'U+E8B1', symbol: 'shuffle', fluentCp: 0xEF37, regularCp: 0xEF37 },  // Shuffle
  { codepoint: 'U+E8B2', symbol: 'video', fluentCp: 0xF868, regularCp: 0xF850 },  // Movies（近似）
  { codepoint: 'U+E8B3', symbol: 'square_dashed', fluentCp: 0xF7A4, regularCp: 0xF78C },  // SelectAll
  { codepoint: 'U+E8B5', symbol: 'import', fluentCp: 0xF15A, regularCp: 0xF15A },  // Import
  { codepoint: 'U+E8B7', symbol: 'folder', fluentCp: 0xF41D, regularCp: 0xF419 },  // Folder
  { codepoint: 'U+E8BB', symbol: 'xmark', fluentCp: 0xF36A, regularCp: 0xF36A },  // ChromeClose
  { codepoint: 'U+E8C8', symbol: 'copy', fluentCp: 0xF32C, regularCp: 0xF32C },  // Copy
  { codepoint: 'U+E8D4', symbol: 'people', fluentCp: 0xF5B3, regularCp: 0xF5A9 },  // Contact2（近似）
  { codepoint: 'U+E8E5', symbol: 'folder_open', fluentCp: 0xF433, regularCp: 0xF42F },  // OpenFile
  { codepoint: 'U+E8EF', symbol: 'calculator', fluentCp: 0xE247, regularCp: 0xE233 },  // Calculator
  { codepoint: 'U+E8F1', symbol: 'library', fluentCp: 0xF4DD, regularCp: 0xF4D3 },  // Library
  { codepoint: 'U+E8F7', symbol: 'arrow_sync', fluentCp: 0xF191, regularCp: 0xF191 },  // SyncFolder
  { codepoint: 'U+E8FD', symbol: 'doc_text', fluentCp: 0xF6F3, regularCp: 0xF6EA },  // BulletedList（近似）
  { codepoint: 'U+E916', symbol: 'timer', fluentCp: 0xF840, regularCp: 0xF827 },  // Stopwatch
  { codepoint: 'U+E921', symbol: 'caption_minimize', fluentCp: 0xEBCB, regularCp: 0xEBD1 },  // ChromeMinimize（近似）
  { codepoint: 'U+E922', symbol: 'caption_maximize', fluentCp: 0xE7FA, regularCp: 0xE7EC },  // ChromeMaximize
  { codepoint: 'U+E923', symbol: 'caption_restore', fluentCp: 0xF7A4, regularCp: 0xF78C },  // ChromeRestore（近似）
  { codepoint: 'U+E929', symbol: 'square_and_pencil', fluentCp: 0xF1E3, regularCp: 0xF1E3 },  // Handwriting（近似）
  { codepoint: 'U+E944', symbol: 'window', fluentCp: 0xEE5E, regularCp: 0xEE5E },  // ReturnToWindow（近似）
  { codepoint: 'U+E946', symbol: 'info_circle', fluentCp: 0xF4AB, regularCp: 0xF4A4 },  // Info
  { codepoint: 'U+E948', symbol: 'plus', fluentCp: 0xF10A, regularCp: 0xF10A },  // CalculatorAddition
  { codepoint: 'U+E949', symbol: 'minus', fluentCp: 0xEBCB, regularCp: 0xEBD1 },  // CalculatorSubtract
  { codepoint: 'U+E958', symbol: 'square_dashed', fluentCp: 0xF7A4, regularCp: 0xF78C },  // StorageOptical（近似）
  { codepoint: 'U+E9D9', symbol: 'wrench', fluentCp: 0xF8D9, regularCp: 0xF8C1 },  // Diagnostic（近似）
  { codepoint: 'U+EA8F', symbol: 'timer', fluentCp: 0xF2E2, regularCp: 0xF2E2 },  // Ringer（近似）
  { codepoint: 'U+EA90', symbol: 'doc_text', fluentCp: 0xF3AD, regularCp: 0xF3AD },  // PDF
  { codepoint: 'U+EC57', symbol: 'arrow_counterclockwise', fluentCp: 0xF140, regularCp: 0xF140 },  // PlaybackRate1x（近似）
  { codepoint: 'U+ED1A', symbol: 'arrow_left_to_line', fluentCp: 0xF166, regularCp: 0xF166 },  // FoldWpf（近似）
  { codepoint: 'U+ED25', symbol: 'folder', fluentCp: 0xF433, regularCp: 0xF42F },  // OpenFolderHorizontal
  { codepoint: 'U+EDD5', symbol: 'chevron_left', fluentCp: 0xF2AB, regularCp: 0xF2AB },  // CaretLeft8
  { codepoint: 'U+EDD6', symbol: 'chevron_right', fluentCp: 0xF2B1, regularCp: 0xF2B1 },  // CaretRight8
  { codepoint: 'U+EDD7', symbol: 'chevron_up', fluentCp: 0xF2B7, regularCp: 0xF2B7 },  // CaretUp8
  { codepoint: 'U+EDD8', symbol: 'chevron_down', fluentCp: 0xF2A4, regularCp: 0xF2A4 },  // CaretDown8
  { codepoint: 'U+EE56', symbol: 'pencil_tip', fluentCp: 0xF253, regularCp: 0xF253 },  // PenPalette（近似）
  { codepoint: 'U+EE71', symbol: 'picture', fluentCp: 0xF490, regularCp: 0xF489 },  // ImageExport
  { codepoint: 'U+EF1F', symbol: 'color_background', fluentCp: 0xF2F8, regularCp: 0xF2F8 },  // BackgroundToggle（近似）
  { codepoint: 'U+F0B6', symbol: 'badge_circle', fluentCp: 0xF2BC, regularCp: 0xF2BC },  // StatusCircle7（近似）
  { codepoint: 'U+F128', symbol: 'badge_stroke', fluentCp: 0xEBCB, regularCp: 0xEBD1 },  // StrokeErase2（近似）
  { codepoint: 'U+F158', symbol: 'rectangle_stack', fluentCp: 0xF6B7, regularCp: 0xF6AE },  // DialShape3（近似）
  { codepoint: 'U+F271', symbol: 'hand_tap', fluentCp: 0xE6E5, regularCp: 0xE6D8 },  // PointerHand（近似）
  { codepoint: 'U+F8AF', symbol: 'badge_dot', fluentCp: 0xF2C0, regularCp: 0xF2C0 },  // ClickSolid（近似）
];

// symbol 名（含浮条/工具 Id 覆盖的变体名）→ 双风格 Fluent 码点；优先于码点表。
export const SYMBOL_FLUENT: SymbolFluentEntry[] = [
  { symbol: 'arrow_clockwise', fluentCp: 0xF13E, regularCp: 0xF13E },
  { symbol: 'arrow_counterclockwise', fluentCp: 0xF1A0, regularCp: 0xF1A0 },
  { symbol: 'arrow_down', fluentCp: 0xF149, regularCp: 0xF149 },
  { symbol: 'arrow_down_circle', fluentCp: 0xF151, regularCp: 0xF151 },
  { symbol: 'arrow_left_arrow_right', fluentCp: 0xF18E, regularCp: 0xF18E },
  { symbol: 'arrow_left_to_line', fluentCp: 0xF166, regularCp: 0xF166 },
  { symbol: 'arrow_next', fluentCp: 0xF16B, regularCp: 0xF16B },
  { symbol: 'arrow_previous', fluentCp: 0xF16D, regularCp: 0xF16D },
  { symbol: 'arrow_right_to_line', fluentCp: 0xF619, regularCp: 0xF60F },
  { symbol: 'arrow_sync', fluentCp: 0xF191, regularCp: 0xF191 },
  { symbol: 'badge_circle', fluentCp: 0xF2BC, regularCp: 0xF2BC },
  { symbol: 'badge_dot', fluentCp: 0xF2C0, regularCp: 0xF2C0 },
  { symbol: 'badge_stroke', fluentCp: 0xEBCB, regularCp: 0xEBD1 },
  { symbol: 'calculator', fluentCp: 0xE247, regularCp: 0xE233 },
  { symbol: 'camera', fluentCp: 0xF255, regularCp: 0xF255 },
  { symbol: 'caption_maximize', fluentCp: 0xE7FA, regularCp: 0xE7EC },
  { symbol: 'caption_minimize', fluentCp: 0xEBCB, regularCp: 0xEBD1 },
  { symbol: 'caption_restore', fluentCp: 0xF7A4, regularCp: 0xF78C },
  { symbol: 'checkmark', fluentCp: 0xF295, regularCp: 0xF295 },
  { symbol: 'chevron_down', fluentCp: 0xF2A4, regularCp: 0xF2A4 },
  { symbol: 'chevron_left', fluentCp: 0xF2AB, regularCp: 0xF2AB },
  { symbol: 'chevron_right', fluentCp: 0xF2B1, regularCp: 0xF2B1 },
  { symbol: 'chevron_up', fluentCp: 0xF2B7, regularCp: 0xF2B7 },
  { symbol: 'clock', fluentCp: 0xF486, regularCp: 0xF47F },
  { symbol: 'color', fluentCp: 0xF2F6, regularCp: 0xF2F6 },
  { symbol: 'color_background', fluentCp: 0xF2F8, regularCp: 0xF2F8 },
  { symbol: 'copy', fluentCp: 0xF32C, regularCp: 0xF32C },
  { symbol: 'doc', fluentCp: 0xF379, regularCp: 0xF379 },
  { symbol: 'doc_plaintext', fluentCp: 0xF379, regularCp: 0xF379 },
  { symbol: 'doc_text', fluentCp: 0xE564, regularCp: 0xE558 },
  { symbol: 'eraser_line', fluentCp: 0xE5F2, regularCp: 0xE5E5 },
  { symbol: 'eraser_line_fill', fluentCp: 0xE5F4, regularCp: 0xE5E7 },
  { symbol: 'eye', fluentCp: 0xE600, regularCp: 0xE5F3 },
  { symbol: 'eye_slash', fluentCp: 0xE603, regularCp: 0xE5F6 },
  { symbol: 'folder', fluentCp: 0xF433, regularCp: 0xF42F },
  { symbol: 'folder_open', fluentCp: 0xF433, regularCp: 0xF42F },
  { symbol: 'gearshape', fluentCp: 0xF6B3, regularCp: 0xF6AA },
  { symbol: 'hand_tap', fluentCp: 0xE6E5, regularCp: 0xE6D8 },
  { symbol: 'highlighter_tip', fluentCp: 0xF481, regularCp: 0xF47D },
  { symbol: 'import', fluentCp: 0xF15A, regularCp: 0xF15A },
  { symbol: 'info_circle', fluentCp: 0xF4AB, regularCp: 0xF4A4 },
  { symbol: 'keyboard', fluentCp: 0xF4C3, regularCp: 0xF4B9 },
  { symbol: 'library', fluentCp: 0xF4DD, regularCp: 0xF4D3 },
  { symbol: 'lock', fluentCp: 0xE79E, regularCp: 0xE790 },
  { symbol: 'lock_open', fluentCp: 0xE7A4, regularCp: 0xE796 },
  { symbol: 'magnifyingglass', fluentCp: 0xF8DD, regularCp: 0xF8C5 },
  { symbol: 'minus', fluentCp: 0xEBCB, regularCp: 0xEBD1 },
  { symbol: 'more', fluentCp: 0xE833, regularCp: 0xE825 },
  { symbol: 'mouse', fluentCp: 0xE450, regularCp: 0xE444 },
  { symbol: 'move', fluentCp: 0xF3C1, regularCp: 0xF3C1 },
  { symbol: 'note', fluentCp: 0xF576, regularCp: 0xF56C },
  { symbol: 'paste', fluentCp: 0xF2D6, regularCp: 0xF2D6 },
  { symbol: 'pause', fluentCp: 0xF5AC, regularCp: 0xF5A2 },
  { symbol: 'pencil_tip', fluentCp: 0xF3DD, regularCp: 0xF3DE },
  { symbol: 'people', fluentCp: 0xF5B3, regularCp: 0xF5A9 },
  { symbol: 'person', fluentCp: 0xF5C8, regularCp: 0xF5BE },
  { symbol: 'person_2', fluentCp: 0xF5B3, regularCp: 0xF5A9 },
  { symbol: 'picture', fluentCp: 0xF490, regularCp: 0xF489 },
  { symbol: 'pin', fluentCp: 0xF60C, regularCp: 0xF602 },
  { symbol: 'pin_off', fluentCp: 0xF60E, regularCp: 0xF604 },
  { symbol: 'play', fluentCp: 0xF610, regularCp: 0xF606 },
  { symbol: 'play_circle', fluentCp: 0xF612, regularCp: 0xF608 },
  { symbol: 'plus', fluentCp: 0xF10A, regularCp: 0xF10A },
  { symbol: 'questionmark_circle', fluentCp: 0xF648, regularCp: 0xF63E },
  { symbol: 'rectangle_stack', fluentCp: 0xF6B7, regularCp: 0xF6AE },
  { symbol: 'redo', fluentCp: 0xF16F, regularCp: 0xF16F },
  { symbol: 'save', fluentCp: 0xF68A, regularCp: 0xF680 },
  { symbol: 'shuffle', fluentCp: 0xEF37, regularCp: 0xEF37 },
  { symbol: 'square_and_pencil', fluentCp: 0xF1E3, regularCp: 0xF1E3 },
  { symbol: 'square_dashed', fluentCp: 0xF7A4, regularCp: 0xF78C },
  { symbol: 'stack', fluentCp: 0xF7BE, regularCp: 0xF7A6 },
  { symbol: 'theme_dark', fluentCp: 0xF33C, regularCp: 0xF33C },
  { symbol: 'theme_light', fluentCp: 0xF8BA, regularCp: 0xF8A2 },
  { symbol: 'timer', fluentCp: 0xF840, regularCp: 0xF827 },
  { symbol: 'trash', fluentCp: 0xF34D, regularCp: 0xF34D },
  { symbol: 'undo', fluentCp: 0xF19A, regularCp: 0xF19A },
  { symbol: 'video', fluentCp: 0xF865, regularCp: 0xF84D },
  { symbol: 'window', fluentCp: 0xEE5E, regularCp: 0xEE5E },
  { symbol: 'wrench', fluentCp: 0xF8D9, regularCp: 0xF8C1 },
  { symbol: 'wrench_and_screwdriver', fluentCp: 0xF467, regularCp: 0xF463 },
  { symbol: 'xmark', fluentCp: 0xF36A, regularCp: 0xF36A },
];

// 图标风格（Filled/Regular）：由 entry 依设置 appearance.isIconRegularStyle 注入；默认 Filled。
let iconStyleRegular: boolean = false;

export function setIconStyleRegular(regular: boolean): void {
  iconStyleRegular = regular;
}

export function iconStyleIsRegular(): boolean {
  return iconStyleRegular;
}

export function symbolForCodepoint(codepoint: string): string {
  for (let i = 0; i < GLYPH_SYMBOLS.length; i++) {
    if (GLYPH_SYMBOLS[i].codepoint === codepoint) {
      return GLYPH_SYMBOLS[i].symbol;
    }
  }
  return '';
}

// 双风格码点解析（symbol 覆盖表优先，回退码点表；0=无映射）。
export function fluentCpForSymbol(symbol: string, regular: boolean): number {
  for (let i = 0; i < SYMBOL_FLUENT.length; i++) {
    if (SYMBOL_FLUENT[i].symbol === symbol) {
      return regular ? SYMBOL_FLUENT[i].regularCp : SYMBOL_FLUENT[i].fluentCp;
    }
  }
  for (let i = 0; i < GLYPH_SYMBOLS.length; i++) {
    if (GLYPH_SYMBOLS[i].symbol === symbol) {
      return regular ? GLYPH_SYMBOLS[i].regularCp : GLYPH_SYMBOLS[i].fluentCp;
    }
  }
  return 0;
}

export function fluentCpForCodepoint(codepoint: string, regular: boolean): number {
  for (let i = 0; i < GLYPH_SYMBOLS.length; i++) {
    if (GLYPH_SYMBOLS[i].codepoint === codepoint) {
      return regular ? GLYPH_SYMBOLS[i].regularCp : GLYPH_SYMBOLS[i].fluentCp;
    }
  }
  return 0;
}

// 渲染字符 + 字体族：按当前风格返回（regular ? Regular 码点+族 : Filled 码点+族）。
export interface FluentGlyphResult {
  char: string;
  family: string;
}

export function fluentGlyph(symbol: string, glyph: string): FluentGlyphResult {
  const regular = iconStyleRegular;
  let cp = fluentCpForSymbol(symbol, regular);
  if (cp === 0 && glyph !== undefined && glyph.length > 0) {
    cp = fluentCpForCodepoint(codepointOf(glyph), regular);
  }
  const result: FluentGlyphResult = new FluentGlyphResultImpl();
  if (cp <= 0) {
    result.char = '';
    result.family = '';
  } else {
    result.char = String.fromCharCode(cp);
    result.family = regular ? 'InkFluentIconsRegular' : 'InkFluentIcons';
  }
  return result;
}

class FluentGlyphResultImpl implements FluentGlyphResult {
  char: string = '';
  family: string = '';
}

export function fluentCharFor(symbol: string, glyph: string): string {
  return fluentGlyph(symbol, glyph).char;
}

function codepointOf(char: string): string {
  if (char.length === 0) {
    return '';
  }
  const cp = char.codePointAt(0);
  if (cp === undefined) {
    return '';
  }
  return 'U+' + cp.toString(16).toUpperCase().padStart(4, '0');
}

// 浮条按钮：同码点不同语义时按 Id 覆盖（圆形擦/墨迹擦、图片/视频）。
export function symbolForButton(id: string, glyph: string): string {
  if (id === 'eraserStroke') {
    return 'eraser_line_fill';
  }
  if (id === 'insertVideo') {
    return 'video';
  }
  if (id === 'insertImage') {
    return 'picture';
  }
  // 与「鼠标」按钮的 mouse 图标区分，避免主栏出现两个相同图案。
  if (id === 'clearAndMouse') {
    return 'arrow_clockwise';
  }
  return symbolForCodepoint(codepointOf(glyph));
}

// 工具菜单入口 Id → symbol（无映射回退文字）。
export function symbolForTool(id: string): string {
  if (id === 'courseware') {
    return 'doc_text';
  }
  if (id === 'screenshot') {
    return 'camera';
  }
  if (id === 'desmos') {
    return 'calculator';
  }
  if (id === 'update') {
    return 'arrow_down_circle';
  }
  if (id === 'countdown') {
    return 'timer';
  }
  if (id === 'lottery') {
    return 'shuffle';
  }
  if (id === 'replay') {
    return 'play_circle';
  }
  if (id === 'settings') {
    return 'gearshape';
  }
  if (id === 'guide') {
    return 'questionmark_circle';
  }
  if (id === 'changelog') {
    return 'doc_plaintext';
  }
  if (id === 'closeTools') {
    return 'xmark';
  }
  return '';
}

export interface IconGlyphOptions {
  symbol: string;
  fallback: string;
  size: number;
  color: ResourceColor;
  enabled: boolean;
  // 可选：原始 Segoe 码点字符（跨端稳定 ID）。useIconFont 且字体就绪时经 icon-map 映射为
  // Fluent System Icons 码点渲染——与 Windows 端同字体同码点；无映射/未加载时回退 SymbolGlyph。
  glyph?: string;
  useIconFont?: boolean;
}

// 跨文件 @Builder：单对象参数按引用传递。enabled=false（能力探测不支持）
// 或 symbol 为空时回退文字，保持原有布局尺寸。
@Builder
export function IconGlyph(options: IconGlyphOptions) {
  if (options.enabled && options.useIconFont === true
    && fluentGlyph(options.symbol, options.glyph !== undefined ? options.glyph : '').char !== '') {
    // Fluent System Icons（MIT，双风格：Filled/Regular 由设置切换，与 Windows 端共用）。
    Text(fluentGlyph(options.symbol, options.glyph !== undefined ? options.glyph : '').char)
      .fontFamily(fluentGlyph(options.symbol, options.glyph !== undefined ? options.glyph : '').family)
      .fontSize(options.size)
      .fontColor(options.color)
  } else if (!options.enabled || options.symbol === '') {
    Text(options.fallback)
      .fontSize(options.size)
      .fontColor(options.color)
  } else if (options.symbol === 'mouse') {
    SymbolGlyph($r('sys.symbol.mouse')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'pencil_tip') {
    SymbolGlyph($r('sys.symbol.pencil_tip')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'highlighter_tip') {
    SymbolGlyph($r('sys.symbol.highlighter_tip')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'trash') {
    SymbolGlyph($r('sys.symbol.trash')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'eraser_line') {
    SymbolGlyph($r('sys.symbol.eraser_line')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'eraser_line_fill') {
    SymbolGlyph($r('sys.symbol.eraser_line_fill')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'square_dashed') {
    SymbolGlyph($r('sys.symbol.square_dashed')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'rectangle_stack') {
    SymbolGlyph($r('sys.symbol.rectangle_stack')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'picture') {
    SymbolGlyph($r('sys.symbol.picture')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'video') {
    SymbolGlyph($r('sys.symbol.video')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'undo') {
    SymbolGlyph($r('sys.symbol.undo')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'redo') {
    SymbolGlyph($r('sys.symbol.redo')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'eye_slash') {
    SymbolGlyph($r('sys.symbol.eye_slash')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'arrow_left_arrow_right') {
    SymbolGlyph($r('sys.symbol.arrow_left_arrow_right')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'square_and_pencil') {
    SymbolGlyph($r('sys.symbol.square_and_pencil')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'wrench_and_screwdriver') {
    SymbolGlyph($r('sys.symbol.wrench_and_screwdriver')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'arrow_left_to_line') {
    SymbolGlyph($r('sys.symbol.arrow_left_to_line')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'hand_tap') {
    SymbolGlyph($r('sys.symbol.hand_tap')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'doc_text') {
    SymbolGlyph($r('sys.symbol.doc_text')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'camera') {
    SymbolGlyph($r('sys.symbol.camera')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'calculator') {
    SymbolGlyph($r('sys.symbol.calculator')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'arrow_down_circle') {
    SymbolGlyph($r('sys.symbol.arrow_down_circle')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'timer') {
    SymbolGlyph($r('sys.symbol.timer')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'shuffle') {
    SymbolGlyph($r('sys.symbol.shuffle')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'play_circle') {
    SymbolGlyph($r('sys.symbol.play_circle')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'gearshape') {
    SymbolGlyph($r('sys.symbol.gearshape')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'questionmark_circle') {
    SymbolGlyph($r('sys.symbol.questionmark_circle')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'doc_plaintext') {
    SymbolGlyph($r('sys.symbol.doc_plaintext')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'xmark') {
    SymbolGlyph($r('sys.symbol.xmark')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'person_2') {
    SymbolGlyph($r('sys.symbol.person_2')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'move') {
    SymbolGlyph($r('sys.symbol.move')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'play') {
    SymbolGlyph($r('sys.symbol.play')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'pause') {
    SymbolGlyph($r('sys.symbol.pause')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'arrow_counterclockwise') {
    SymbolGlyph($r('sys.symbol.arrow_counterclockwise')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'info_circle') {
    SymbolGlyph($r('sys.symbol.info_circle')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'save') {
    SymbolGlyph($r('sys.symbol.save')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'folder') {
    SymbolGlyph($r('sys.symbol.folder')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'arrow_right_to_line') {
    SymbolGlyph($r('sys.symbol.arrow_right_to_line')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'chevron_up') {
    SymbolGlyph($r('sys.symbol.chevron_up')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'chevron_down') {
    SymbolGlyph($r('sys.symbol.chevron_down')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'chevron_left') {
    SymbolGlyph($r('sys.symbol.chevron_left')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'chevron_right') {
    SymbolGlyph($r('sys.symbol.chevron_right')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'lock') {
    SymbolGlyph($r('sys.symbol.lock')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'lock_open') {
    SymbolGlyph($r('sys.symbol.lock_open')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'plus') {
    SymbolGlyph($r('sys.symbol.plus')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'magnifyingglass') {
    SymbolGlyph($r('sys.symbol.magnifyingglass')).fontSize(options.size).fontColor([options.color])
  } else if (options.symbol === 'arrow_clockwise') {
    SymbolGlyph($r('sys.symbol.arrow_clockwise')).fontSize(options.size).fontColor([options.color])
  } else {
    Text(options.fallback)
      .fontSize(options.size)
      .fontColor(options.color)
  }
}