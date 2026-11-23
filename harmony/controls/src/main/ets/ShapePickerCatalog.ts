// @parity src/InkCanvas.Controls/Controls/ShapePreview.cs（ShapeMenuCatalog 当前 8 行 38 项）
export interface ShapeMenuEntry {
  Kind: number;
  Label: string;
  Id: string;
  Row: number;
  Margin: number[];
  SlotWidth: number;
  Rotation: number;
  LongPress: boolean;
}

function item(kind: number, label: string, id: string, row: number, rotation: number = 0, longPress: boolean = false): ShapeMenuEntry {
  return { Kind: kind, Label: label, Id: id, Row: row, Margin: [0, 10, 0, 10], SlotWidth: 0, Rotation: rotation, LongPress: longPress };
}

export const SHAPE_MENU_ROWS: ShapeMenuEntry[][] = [
  [
    item(0, '直线', 'Line', 0, 0, true),
    item(1, '虚线', 'DashedLine', 0, 0, true),
    item(2, '点线', 'DotLine', 0, 0, true),
    item(3, '箭头', 'Arrow', 0, 0, true),
    item(4, '平行线', 'ParallelLine', 0, 0, true)
  ],
  [
    item(5, '坐标系（双向）', 'CoordinateBoth', 1),
    item(6, '坐标系（x 单向）', 'CoordinateXOneWay', 1),
    item(7, '坐标系（y 单向）', 'CoordinateYOneWay', 1),
    item(8, '坐标系（第一象限）', 'CoordinateFirstQuadrant', 1),
    item(9, '空间坐标系', 'Coordinate3D', 1)
  ],
  [
    item(10, '矩形（两点）', 'Rectangle', 2),
    item(32, '正方形', 'Square', 2),
    item(13, '圆（两点）', 'Circle', 2),
    item(12, '椭圆（两点）', 'Ellipse', 2),
    item(33, '三角形', 'Triangle', 2)
  ],
  [
    item(11, '中心矩形', 'RectangleFromCenter', 3),
    item(25, '中心圆', 'CircleFromCenter', 3),
    item(14, '中心椭圆', 'EllipseFromCenter', 3),
    item(15, '椭圆（两点，含焦点）', 'EllipseWithFoci', 3),
    item(29, '中心椭圆（含焦点）', 'EllipseWithFociFromCenter', 3)
  ],
  [
    item(16, '虚线圆（两点）', 'DashedCircle', 4),
    item(26, '中心虚线圆', 'DashedCircleFromCenter', 4),
    item(34, '直角三角形', 'RightTriangle', 4),
    item(35, '菱形', 'Diamond', 4),
    item(36, '平行四边形', 'Parallelogram', 4)
  ],
  [
    item(17, '双曲线', 'Hyperbola', 5),
    item(18, '双曲线（含焦点）', 'HyperbolaWithFoci', 5),
    item(27, '双曲线（含渐近线）', 'HyperbolaWithAsymptotes', 5),
    item(28, '双曲线（含焦点和渐近线）', 'HyperbolaWithFociAndAsymptotes', 5)
  ],
  [
    item(21, '抛物线（纵向）', 'ParabolaUp', 6),
    item(19, '抛物线（横向）', 'ParabolaRight', 6, 90),
    item(30, '抛物线（纵向，含焦点）', 'ParabolaUpWithFoci', 6),
    item(20, '抛物线（横向，含焦点）', 'ParabolaWithFoci', 6, 90),
    item(37, '梯形', 'Trapezoid', 6)
  ],
  [
    item(22, '圆柱', 'Cylinder', 7),
    item(23, '圆锥', 'Cone', 7),
    item(24, '长方体（两笔）', 'Cuboid', 7),
    item(31, '四面体', 'Tetrahedron', 7)
  ]
];

export interface ShapeCatalogSummary {
  Count: number;
  Kinds: number[];
  RowCounts: number[];
}

export function shapeCatalogSummary(): ShapeCatalogSummary {
  const kinds: number[] = [];
  const rowCounts: number[] = [];
  for (let r = 0; r < SHAPE_MENU_ROWS.length; r++) {
    rowCounts.push(SHAPE_MENU_ROWS[r].length);
    for (let i = 0; i < SHAPE_MENU_ROWS[r].length; i++) {
      kinds.push(SHAPE_MENU_ROWS[r][i].Kind);
    }
  }
  return { Count: kinds.length, Kinds: kinds, RowCounts: rowCounts };
}
