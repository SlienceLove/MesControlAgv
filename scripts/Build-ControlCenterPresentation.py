from __future__ import annotations

from pathlib import Path

from PIL import Image
from pptx import Presentation
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_CONNECTOR, MSO_SHAPE
from pptx.enum.text import MSO_ANCHOR, PP_ALIGN
from pptx.util import Inches, Pt


ROOT = Path(__file__).resolve().parents[1]
TEMPLATE = ROOT / "res" / "2026年ppt模板.pptx"
OUTPUT = ROOT / "res" / "实验室自动化智能中控平台-对外汇报-20260910.pptx"

WORKFLOW_IMAGE = ROOT / "artifacts" / "field-debug-20260901-104448" / "wpf-final-workflow-fit.png"
RUN_IMAGE = ROOT / "artifacts" / "physical-acceptance" / "phase2-std-20260904-093754" / "wpf-live-screen-2.png"
MAP_IMAGE = ROOT / "artifacts" / "wpf-map-observability-final-20260810.png"
AGV_IMAGE = ROOT / "artifacts" / "wpf-latest-ui-20260828.png"

FONT = "Microsoft YaHei"
NAVY = RGBColor(8, 37, 64)
BLUE = RGBColor(9, 110, 206)
BLUE_DARK = RGBColor(4, 79, 157)
BLUE_LIGHT = RGBColor(232, 243, 255)
CYAN = RGBColor(15, 161, 190)
CYAN_LIGHT = RGBColor(228, 249, 252)
GREEN = RGBColor(24, 158, 105)
GREEN_LIGHT = RGBColor(229, 248, 238)
AMBER = RGBColor(194, 122, 0)
AMBER_LIGHT = RGBColor(255, 246, 223)
PURPLE = RGBColor(115, 77, 177)
PURPLE_LIGHT = RGBColor(244, 237, 255)
RED = RGBColor(193, 52, 68)
RED_LIGHT = RGBColor(255, 235, 237)
INK = RGBColor(31, 48, 68)
MUTED = RGBColor(92, 112, 132)
LINE = RGBColor(209, 222, 237)
PALE = RGBColor(247, 250, 253)
WHITE = RGBColor(255, 255, 255)


def set_fill(shape, color: RGBColor, transparency: int | None = None) -> None:
    shape.fill.solid()
    shape.fill.fore_color.rgb = color
    if transparency is not None:
        shape.fill.transparency = transparency


def hide_line(shape) -> None:
    shape.line.fill.background()


def set_text_frame(tf, text: str, size: float, color: RGBColor = INK,
                   bold: bool = False, align=PP_ALIGN.LEFT,
                   valign=MSO_ANCHOR.TOP, margin: float = 0.06,
                   font: str = FONT) -> None:
    tf.clear()
    tf.word_wrap = True
    tf.margin_left = Inches(margin)
    tf.margin_right = Inches(margin)
    tf.margin_top = Inches(margin)
    tf.margin_bottom = Inches(margin)
    tf.vertical_anchor = valign
    lines = str(text).split("\n")
    for index, line in enumerate(lines):
        p = tf.paragraphs[0] if index == 0 else tf.add_paragraph()
        p.text = line
        p.alignment = align
        p.space_after = Pt(2)
        for run in p.runs:
            run.font.name = font
            run.font.size = Pt(size)
            run.font.bold = bold
            run.font.color.rgb = color


def add_text(slide, x: float, y: float, w: float, h: float, text: str,
             size: float = 14, color: RGBColor = INK, bold: bool = False,
             align=PP_ALIGN.LEFT, valign=MSO_ANCHOR.TOP,
             margin: float = 0.06):
    box = slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    set_text_frame(box.text_frame, text, size, color, bold, align, valign, margin)
    return box


def add_card(slide, x: float, y: float, w: float, h: float,
             fill: RGBColor = WHITE, line: RGBColor = LINE,
             radius: bool = True, transparency: int | None = None):
    shape = slide.shapes.add_shape(
        MSO_SHAPE.ROUNDED_RECTANGLE if radius else MSO_SHAPE.RECTANGLE,
        Inches(x), Inches(y), Inches(w), Inches(h))
    set_fill(shape, fill, transparency)
    shape.line.color.rgb = line
    shape.line.width = Pt(0.8)
    return shape


def add_pill(slide, x: float, y: float, w: float, text: str,
             fill: RGBColor, color: RGBColor = WHITE):
    add_card(slide, x, y, w, 0.32, fill, fill)
    add_text(slide, x + 0.02, y + 0.02, w - 0.04, 0.24, text, 9.5,
             color, True, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, 0.01)


def add_bullets(slide, x: float, y: float, w: float, h: float,
                items: list[str], size: float = 12, color: RGBColor = INK,
                bullet_color: RGBColor | None = None):
    box = slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = box.text_frame
    tf.clear()
    tf.word_wrap = True
    tf.margin_left = Inches(0.02)
    tf.margin_right = Inches(0.02)
    tf.margin_top = Inches(0.02)
    tf.margin_bottom = Inches(0.02)
    for index, item in enumerate(items):
        p = tf.paragraphs[0] if index == 0 else tf.add_paragraph()
        p.text = f"• {item}"
        p.space_after = Pt(5)
        p.font.name = FONT
        p.font.size = Pt(size)
        p.font.color.rgb = color
    return box


def add_section_title(slide, kicker: str, title: str, subtitle: str | None = None):
    add_text(slide, 0.68, 0.88, 3.0, 0.22, kicker.upper(), 9.5, BLUE, True)
    add_text(slide, 0.68, 1.12, 11.8, 0.48, title, 25, NAVY, True)
    if subtitle:
        add_text(slide, 0.7, 1.62, 11.7, 0.3, subtitle, 10.5, MUTED)


def add_connector(slide, x1: float, y1: float, x2: float, y2: float,
                  color: RGBColor = BLUE, width: float = 1.8):
    line = slide.shapes.add_connector(
        MSO_CONNECTOR.STRAIGHT, Inches(x1), Inches(y1), Inches(x2), Inches(y2))
    line.line.color.rgb = color
    line.line.width = Pt(width)
    try:
        line.line.end_arrowhead = True
    except Exception:
        pass
    return line


def add_image_fit(slide, path: Path, x: float, y: float, w: float, h: float,
                  border: RGBColor = LINE, caption: str | None = None):
    frame = add_card(slide, x, y, w, h, WHITE, border)
    if not path.exists():
        add_text(slide, x + 0.15, y + h / 2 - 0.15, w - 0.3, 0.3,
                 f"图片缺失：{path.name}", 10, RED, True, PP_ALIGN.CENTER,
                 MSO_ANCHOR.MIDDLE)
        return frame
    with Image.open(path) as image:
        iw, ih = image.size
    ratio = iw / ih
    box_ratio = w / h
    if ratio > box_ratio:
        pw, ph = w - 0.08, (w - 0.08) / ratio
        px, py = x + 0.04, y + (h - ph) / 2
    else:
        ph, pw = h - 0.08, (h - 0.08) * ratio
        px, py = x + (w - pw) / 2, y + 0.04
    slide.shapes.add_picture(str(path), Inches(px), Inches(py),
                             width=Inches(pw), height=Inches(ph))
    if caption:
        add_text(slide, x + 0.12, y + h - 0.28, w - 0.24, 0.18,
                 caption, 8.5, MUTED, False, PP_ALIGN.RIGHT, MSO_ANCHOR.MIDDLE, 0)
    return frame


def add_icon_circle(slide, x: float, y: float, label: str,
                    fill: RGBColor, text_color: RGBColor = WHITE,
                    diameter: float = 0.48):
    shape = slide.shapes.add_shape(
        MSO_SHAPE.OVAL, Inches(x), Inches(y), Inches(diameter), Inches(diameter))
    set_fill(shape, fill)
    hide_line(shape)
    add_text(slide, x, y + 0.01, diameter, diameter - 0.02, label, 13,
             text_color, True, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, 0)
    return shape


def add_kpi_card(slide, x: float, y: float, w: float, h: float,
                 label: str, value: str, detail: str, accent: RGBColor,
                 fill: RGBColor):
    add_card(slide, x, y, w, h, fill, accent)
    add_text(slide, x + 0.16, y + 0.14, w - 0.3, 0.22, label, 10, MUTED, True)
    add_text(slide, x + 0.16, y + 0.46, w - 0.3, 0.32, value, 15.5, accent, True)
    add_text(slide, x + 0.16, y + h - 0.35, w - 0.3, 0.2, detail, 8.8, MUTED)


def clear_text(shape, text: str, size: float, color: RGBColor,
               bold: bool = False, align=PP_ALIGN.LEFT):
    if not hasattr(shape, "text_frame"):
        return
    set_text_frame(shape.text_frame, text, size, color, bold, align,
                   MSO_ANCHOR.MIDDLE, 0.02)


def configure_cover(slide):
    text_shapes = [s for s in slide.shapes
                   if hasattr(s, "text_frame") and not getattr(s, "is_placeholder", False)]
    # The original template intentionally places the cover title in the lower
    # left. Reuse the existing non-placeholder text boxes so its typography
    # and alignment remain part of the supplied template.
    title = next((s for s in text_shapes
                  if Inches(3.0) < s.top < Inches(4.1) and s.height > Inches(0.8)), None)
    subtitle = next((s for s in text_shapes
                     if Inches(4.2) < s.top < Inches(5.1)), None)
    meta = next((s for s in text_shapes if s.top > Inches(5.0)), None)
    if title is not None:
        clear_text(title, "实验室自动化智能中控平台", 28, BLUE, True)
    if subtitle is not None:
        clear_text(subtitle, "连接设备 · 编排流程 · 看见全局", 15, BLUE, True)
    if meta is not None:
        clear_text(meta, "青源峰达 QUENDA  ·  对外能力介绍\n2026年9月", 13, NAVY, True)
    add_text(slide, 0.84, 2.92, 5.7, 0.26,
             "MES  ·  Workflow  ·  Digital Twin  ·  AI-ready", 10.5, BLUE, True)
    add_text(slide, 7.5, 5.95, 4.6, 0.28,
             "让实验室自动化，从设备连接走向全局协同", 12, BLUE_DARK, True,
             PP_ALIGN.RIGHT, MSO_ANCHOR.MIDDLE)


def configure_closing(slide):
    text_shapes = [s for s in slide.shapes
                   if hasattr(s, "text_frame") and not getattr(s, "is_placeholder", False)]
    title = next((s for s in text_shapes
                  if Inches(2.8) < s.top < Inches(3.9) and s.height > Inches(0.8)), None)
    tagline = next((s for s in text_shapes if s.top > Inches(4.0)), None)
    if title is not None:
        clear_text(title, "让每一台设备，都成为可协同的能力", 30, BLUE, True)
    if tagline is not None:
        clear_text(tagline, "实验室自动化智能中控平台\n从可视化中控，到可追溯、可演进的实验室自动化", 15, BLUE, True)
    add_text(slide, 0.9, 5.65, 5.6, 0.35,
             "期待与客户共同完成现场联调与价值验证", 12, MUTED)
    add_pill(slide, 0.9, 6.18, 2.0, "连接 · 编排 · 追溯", BLUE)
    add_pill(slide, 3.1, 6.18, 1.8, "安全 · 开放", GREEN)


def slide_why(slide):
    add_section_title(slide, "01 价值起点", "从设备孤岛，到实验室全局协同",
                      "客户看到的是一套中控，平台解决的是设备、任务、流程和数据之间的断点。")
    cards = [
        ("设备多", "AGV、机械臂、仪器各自有界面，信息无法横向关联。", BLUE, BLUE_LIGHT, "设"),
        ("流程长", "任务、排程、实验步骤和人工确认彼此割裂。", CYAN, CYAN_LIGHT, "流"),
        ("风险高", "异常、权限和未知状态缺少统一的处置边界。", AMBER, AMBER_LIGHT, "安"),
        ("数据散", "样品、耗材、方法和结果难以形成完整追溯链。", PURPLE, PURPLE_LIGHT, "链"),
    ]
    for i, (label, detail, accent, fill, icon) in enumerate(cards):
        x = 0.72 + i * 3.13
        add_card(slide, x, 2.18, 2.78, 1.65, fill, accent)
        add_icon_circle(slide, x + 0.18, 2.38, icon, accent)
        add_text(slide, x + 0.78, 2.4, 1.75, 0.25, label, 15, NAVY, True)
        add_text(slide, x + 0.2, 2.95, 2.38, 0.64, detail, 10.5, INK)
    add_text(slide, 0.72, 4.25, 2.2, 0.25, "中控平台的价值", 16, NAVY, True)
    flow = [("接入", "统一设备与能力目录", BLUE), ("编排", "任务、流程、资源协同", CYAN),
            ("监控", "状态、地图、异常一屏可见", GREEN), ("追溯", "样品、物料、结果可回放", PURPLE)]
    for i, (label, detail, accent) in enumerate(flow):
        x = 0.78 + i * 3.05
        if i < len(flow) - 1:
            add_connector(slide, x + 2.25, 5.15, x + 2.92, 5.15, LINE, 2.2)
        add_card(slide, x, 4.7, 2.35, 1.05, WHITE, accent)
        add_text(slide, x + 0.12, 4.86, 2.1, 0.25, label, 15, accent, True, PP_ALIGN.CENTER)
        add_text(slide, x + 0.12, 5.22, 2.1, 0.27, detail, 9.2, MUTED, False, PP_ALIGN.CENTER)


def slide_platform(slide):
    add_section_title(slide, "02 平台总览", "一套模型贯穿设备、任务、流程与审计",
                      "前台负责看见和协同，中台负责状态和规则，设备侧保留协议边界与安全控制。")
    layers = [
        ("WPF 智能中控", "KPI · 排程 · 流程 · 地图 · 运行监控", BLUE, BLUE_LIGHT),
        ("MES / Workflow 业务中台", "任务状态 · 资源调度 · 审计 · 恢复 · 追溯", CYAN, CYAN_LIGHT),
        ("Adapter / 网关 / Worker", "能力目录 · 协议转换 · 就绪准入 · 幂等与未知保护", GREEN, GREEN_LIGHT),
        ("现场设备层", "AGV · AUBO 机械臂 · 离子色谱 · 开盖分液", PURPLE, PURPLE_LIGHT),
    ]
    for i, (label, detail, accent, fill) in enumerate(layers):
        y = 2.15 + i * 0.9
        add_card(slide, 0.95, y, 7.2, 0.66, fill, accent)
        add_text(slide, 1.18, y + 0.11, 2.15, 0.22, label, 13, accent, True)
        add_text(slide, 3.45, y + 0.11, 4.35, 0.25, detail, 10.5, INK)
        if i < len(layers) - 1:
            add_connector(slide, 4.55, y + 0.68, 4.55, y + 0.88, LINE, 1.4)
    add_card(slide, 8.65, 2.15, 3.8, 3.34, WHITE, LINE)
    add_text(slide, 8.95, 2.45, 3.0, 0.25, "统一数据闭环", 16, NAVY, True)
    loop = [("任务", 9.3, 3.08, BLUE), ("状态", 11.05, 3.08, CYAN),
            ("结果", 11.05, 4.34, PURPLE), ("审计", 9.3, 4.34, GREEN)]
    for label, x, y, accent in loop:
        add_card(slide, x, y, 1.25, 0.55, accent, accent)
        add_text(slide, x, y + 0.09, 1.25, 0.28, label, 12, WHITE, True, PP_ALIGN.CENTER)
    add_connector(slide, 10.55, 3.35, 11.0, 3.35, LINE, 1.4)
    add_connector(slide, 11.65, 3.68, 11.65, 4.28, LINE, 1.4)
    add_connector(slide, 11.0, 4.62, 10.55, 4.62, LINE, 1.4)
    add_connector(slide, 9.92, 4.28, 9.92, 3.68, LINE, 1.4)
    add_text(slide, 8.98, 5.7, 3.15, 0.7,
             "每一次执行都有上下文、状态和证据；\n每一个设备能力都可以被流程复用。", 11, MUTED)


def slide_cockpit(slide):
    add_section_title(slide, "03 运营驾驶舱", "把复杂现场压缩成一屏决策",
                      "KPI 不是孤立数字，而是任务、样品、耗材和仪器状态的业务投影。")
    add_image_fit(slide, AGV_IMAGE, 0.7, 2.05, 6.85, 4.15, BLUE,
                  "项目现有中控界面：AGV 通讯与调度")
    add_text(slide, 7.85, 2.08, 4.2, 0.25, "可配置指标示例", 15, NAVY, True)
    add_kpi_card(slide, 7.85, 2.5, 2.0, 1.2, "任务总览", "总量 · 运行", "来源：MES 任务状态", BLUE, BLUE_LIGHT)
    add_kpi_card(slide, 10.0, 2.5, 2.0, 1.2, "完成情况", "完成 · 失败", "按日期与批次筛选", GREEN, GREEN_LIGHT)
    add_kpi_card(slide, 7.85, 3.88, 2.0, 1.2, "样品处理", "待处理 · 进行中", "样品批次与任务关联", CYAN, CYAN_LIGHT)
    add_kpi_card(slide, 10.0, 3.88, 2.0, 1.2, "耗材余量", "库存 · 预警", "扫码与仓库策略接入", AMBER, AMBER_LIGHT)
    add_card(slide, 7.85, 5.35, 4.15, 0.85, WHITE, LINE)
    add_text(slide, 8.08, 5.54, 3.7, 0.38,
             "同一屏看见：计划、实际、当前块、设备状态和异常摘要", 11, NAVY, True,
             PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE)


def slide_digital_twin(slide):
    add_section_title(slide, "04 数字孪生", "地图不是装饰，而是运行上下文",
                      "把站点、路径、AGV 状态和任务语义叠加在同一张现场视图上。")
    add_image_fit(slide, MAP_IMAGE, 0.68, 2.05, 8.35, 4.2, BLUE,
                  "项目现有地图视图：站点、路径与实时叠加")
    add_card(slide, 9.35, 2.05, 3.25, 4.2, WHITE, LINE)
    add_text(slide, 9.65, 2.35, 2.6, 0.26, "数字孪生可见信息", 15, NAVY, True)
    add_bullets(slide, 9.65, 2.82, 2.55, 1.7, [
        "配置地图 / 实时地图 / 布局校验",
        "站点、路线、方向和可达关系",
        "AGV 当前任务、位置和路径",
        "阻塞、异常和运行上下文",
    ], 11)
    add_card(slide, 9.65, 4.9, 2.6, 0.92, BLUE_LIGHT, BLUE)
    add_text(slide, 9.82, 5.1, 2.25, 0.45,
             "从坐标到任务语义，\n让现场状态可读、可定位。", 11, BLUE_DARK, True,
             PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE)


def slide_workflow(slide):
    add_section_title(slide, "05 流程编排", "从画流程到管结果，实验步骤可复用、可校验",
                      "流程管理、运行监控和人工确认共用一套执行上下文，避免“画面完成、现场失联”。")
    add_image_fit(slide, WORKFLOW_IMAGE, 0.68, 2.0, 8.45, 4.35, BLUE,
                  "项目现有界面：实验流程管理")
    add_card(slide, 9.42, 2.0, 3.18, 4.35, WHITE, LINE)
    steps = [("01", "模板复用", "固定流程可复制，减少重复配置", BLUE),
             ("02", "可视化节点", "AGV、机械臂、等待和仪器能力可组合", CYAN),
             ("03", "校验清单", "发布前检查设备、资源和参数", AMBER),
             ("04", "全屏监控", "运行节点、异常和人工确认集中呈现", GREEN)]
    for i, (num, label, detail, accent) in enumerate(steps):
        y = 2.35 + i * 0.88
        add_icon_circle(slide, 9.7, y, num, accent, diameter=0.48)
        add_text(slide, 10.32, y + 0.02, 2.0, 0.22, label, 12.5, NAVY, True)
        add_text(slide, 10.32, y + 0.31, 2.0, 0.32, detail, 9.2, MUTED)
    add_pill(slide, 9.7, 5.92, 2.45, "准备 → 执行 → 反馈 → 追溯", BLUE)


def slide_material(slide):
    add_section_title(slide, "06 样品与物料", "每一份样品，都有来路和去向",
                      "把扫码、仓库、耗材、实验任务和结果串成一条可追溯链；基础能力已纳入平台演进。")
    nodes = [
        ("扫码建档", "样品 / 耗材\n唯一标识", BLUE, BLUE_LIGHT),
        ("仓库管理", "仓位 / 批次\n库存与效期", CYAN, CYAN_LIGHT),
        ("库存预占", "排程前校验\n资源不冲突", AMBER, AMBER_LIGHT),
        ("实验执行", "流程 / 设备\n任务上下文", GREEN, GREEN_LIGHT),
        ("结果追溯", "结果 / 审计\n可回放", PURPLE, PURPLE_LIGHT),
    ]
    for i, (label, detail, accent, fill) in enumerate(nodes):
        x = 0.78 + i * 2.45
        if i < len(nodes) - 1:
            add_connector(slide, x + 1.95, 3.18, x + 2.34, 3.18, LINE, 1.8)
        add_card(slide, x, 2.48, 1.95, 1.4, fill, accent)
        add_icon_circle(slide, x + 0.72, 2.68, str(i + 1), accent)
        add_text(slide, x + 0.1, 3.2, 1.75, 0.22, label, 12.5, NAVY, True, PP_ALIGN.CENTER)
        add_text(slide, x + 0.1, 3.5, 1.75, 0.28, detail, 9.2, MUTED, False, PP_ALIGN.CENTER)
    add_card(slide, 0.78, 4.48, 5.75, 1.45, WHITE, LINE)
    add_text(slide, 1.05, 4.75, 2.1, 0.25, "平台管理对象", 15, NAVY, True)
    add_bullets(slide, 1.05, 5.12, 5.0, 0.58,
                ["样品、耗材、批次、仓位、方法、结果", "支持扫码溯源与样品编号关联"], 10.5)
    add_card(slide, 6.82, 4.48, 5.8, 1.45, AMBER_LIGHT, AMBER)
    add_text(slide, 7.1, 4.75, 2.1, 0.25, "演进边界", 15, AMBER, True)
    add_text(slide, 7.1, 5.12, 5.05, 0.58,
             "基础闭环与接口先行；扫码设备、仓库策略和仪器结果回传按现场条件逐步接入。", 10.5, INK)


def slide_devices(slide):
    add_section_title(slide, "07 多设备协同", "同级设备、统一准入、分层开放能力",
                      "不把 AGV 的操作任务和仪器的启动方法混为一谈；每类设备保留自己的能力目录和安全边界。")
    devices = [
        ("AGV", "导航 / 到站 / 状态", "地图、任务和控制权", BLUE, BLUE_LIGHT),
        ("AUBO 机械臂", "程序 / 握手 / 运行", "程序目录与就绪校验", GREEN, GREEN_LIGHT),
        ("离子色谱", "D160+ / 自动进样器", "同级设备，子设备状态可扩展", CYAN, CYAN_LIGHT),
        ("开盖分液", "工作站状态 / 方法", "HTTP 接口预留与任务准入", PURPLE, PURPLE_LIGHT),
    ]
    for i, (label, capability, detail, accent, fill) in enumerate(devices):
        x = 0.72 + (i % 2) * 6.12
        y = 2.12 + (i // 2) * 1.35
        add_card(slide, x, y, 5.65, 1.05, fill, accent)
        add_icon_circle(slide, x + 0.18, y + 0.25, label[:1], accent)
        add_text(slide, x + 0.85, y + 0.18, 2.1, 0.24, label, 14, NAVY, True)
        add_text(slide, x + 3.0, y + 0.18, 2.35, 0.24, capability, 10.8, accent, True)
        add_text(slide, x + 0.85, y + 0.55, 4.35, 0.22, detail, 9.7, MUTED)
    add_text(slide, 0.72, 5.02, 2.2, 0.25, "统一安全准入", 16, NAVY, True)
    stages = [("Ready", GREEN), ("Authorized", BLUE), ("Running", CYAN), ("Audited", PURPLE)]
    for i, (label, accent) in enumerate(stages):
        x = 0.92 + i * 2.6
        if i < len(stages) - 1:
            add_connector(slide, x + 1.55, 5.68, x + 2.48, 5.68, LINE, 1.8)
        add_card(slide, x, 5.35, 1.6, 0.66, WHITE, accent)
        add_text(slide, x, 5.52, 1.6, 0.25, label, 11.5, accent, True, PP_ALIGN.CENTER)
    add_text(slide, 0.92, 6.18, 10.7, 0.24,
             "Unknown / 断线 / 急停 → 停止推进并人工核销；写入能力按设备安全验证逐步开放。",
             10.5, RED, True)


def slide_ai(slide):
    add_section_title(slide, "08 AI 演进", "让中控从“看见”走向“建议”",
                      "AI 作为中控的辅助层：先读懂数据、解释异常，再在人工确认下给出可执行建议。")
    pillars = [
        ("智能排程", "结合资源、时长、优先级和设备就绪，给出冲突提示与排程建议。", BLUE, BLUE_LIGHT, "排"),
        ("异常分析", "把任务、设备、审计和日志摘要成可理解的原因链。", CYAN, CYAN_LIGHT, "析"),
        ("自然语言助手", "用一句话查询当前阻塞、样品进度、设备状态和历史证据。", PURPLE, PURPLE_LIGHT, "问"),
        ("预测维护", "基于趋势和异常频次提示风险，辅助安排维护窗口。", GREEN, GREEN_LIGHT, "预"),
    ]
    for i, (label, detail, accent, fill, icon) in enumerate(pillars):
        x = 0.72 + (i % 2) * 6.12
        y = 2.15 + (i // 2) * 1.47
        add_card(slide, x, y, 5.65, 1.15, fill, accent)
        add_icon_circle(slide, x + 0.2, y + 0.31, icon, accent)
        add_text(slide, x + 0.88, y + 0.22, 2.0, 0.25, label, 14, NAVY, True)
        add_text(slide, x + 0.88, y + 0.58, 4.35, 0.35, detail, 9.7, INK)
    add_text(slide, 0.72, 5.32, 2.0, 0.25, "演进路径", 16, NAVY, True)
    timeline = [("现在", "规则 + 审计", BLUE), ("下一步", "AI 辅助建议", CYAN), ("未来", "人机协同优化", PURPLE)]
    for i, (label, detail, accent) in enumerate(timeline):
        x = 0.95 + i * 3.95
        if i < 2:
            add_connector(slide, x + 2.6, 6.02, x + 3.72, 6.02, LINE, 1.8)
        add_card(slide, x, 5.68, 2.7, 0.7, WHITE, accent)
        add_text(slide, x + 0.1, 5.78, 0.75, 0.22, label, 10.5, accent, True)
        add_text(slide, x + 0.9, 5.78, 1.65, 0.22, detail, 10.5, NAVY, True)
    add_text(slide, 9.55, 1.66, 2.6, 0.24, "规划中 / 预留接口", 10, RED, True, PP_ALIGN.RIGHT)


def build() -> None:
    if not TEMPLATE.exists():
        raise FileNotFoundError(TEMPLATE)
    prs = Presentation(str(TEMPLATE))
    prs.core_properties.title = "实验室自动化智能中控平台"
    prs.core_properties.subject = "对外产品能力介绍"
    prs.core_properties.author = "青源峰达 QUENDA"
    prs.core_properties.comments = "基于 2026 年 PPT 模板制作，保留原母版。"

    configure_cover(prs.slides[0])
    slide_why(prs.slides[1])

    # Add new content slides using the only template layout. The layout carries
    # the original header/footer master, so no header/footer is recreated here.
    platform = prs.slides.add_slide(prs.slide_layouts[0])
    slide_platform(platform)
    cockpit = prs.slides.add_slide(prs.slide_layouts[0])
    slide_cockpit(cockpit)
    twin = prs.slides.add_slide(prs.slide_layouts[0])
    slide_digital_twin(twin)
    workflow = prs.slides.add_slide(prs.slide_layouts[0])
    slide_workflow(workflow)
    material = prs.slides.add_slide(prs.slide_layouts[0])
    slide_material(material)
    devices = prs.slides.add_slide(prs.slide_layouts[0])
    slide_devices(devices)
    ai = prs.slides.add_slide(prs.slide_layouts[0])
    slide_ai(ai)

    # The original third slide is the closing master composition. Move it to
    # the end so its background and brand elements remain intact.
    closing_id = prs.slides._sldIdLst[2]
    prs.slides._sldIdLst.remove(closing_id)
    prs.slides._sldIdLst.append(closing_id)
    configure_closing(prs.slides[-1])

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    prs.save(str(OUTPUT))
    print(f"wrote {OUTPUT}")
    print(f"slides={len(prs.slides)} size={prs.slide_width / 914400:.2f}x{prs.slide_height / 914400:.2f}")


if __name__ == "__main__":
    build()
