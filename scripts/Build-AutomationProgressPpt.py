from pathlib import Path

from PIL import Image
from pptx import Presentation
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_CONNECTOR, MSO_SHAPE
from pptx.enum.text import MSO_ANCHOR, PP_ALIGN
from pptx.util import Inches, Pt


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "res" / "自动化平台进度-202609更新.pptx"

NAVY = RGBColor(9, 35, 58)
NAVY_2 = RGBColor(17, 51, 80)
BLUE = RGBColor(29, 126, 255)
BLUE_LIGHT = RGBColor(226, 239, 255)
CYAN = RGBColor(20, 177, 191)
GREEN = RGBColor(25, 160, 103)
GREEN_LIGHT = RGBColor(226, 247, 237)
AMBER = RGBColor(194, 122, 0)
AMBER_LIGHT = RGBColor(255, 244, 215)
RED = RGBColor(191, 48, 65)
RED_LIGHT = RGBColor(255, 231, 234)
PURPLE = RGBColor(125, 77, 180)
INK = RGBColor(25, 43, 62)
MUTED = RGBColor(92, 112, 132)
LIGHT = RGBColor(244, 247, 251)
WHITE = RGBColor(255, 255, 255)
LINE = RGBColor(216, 226, 237)

FONT = "Microsoft YaHei"


def rgb_fill(shape, color):
    shape.fill.solid()
    shape.fill.fore_color.rgb = color


def no_line(shape):
    shape.line.fill.background()


def add_text(slide, x, y, w, h, text, size=16, color=INK, bold=False,
             align=PP_ALIGN.LEFT, valign=MSO_ANCHOR.TOP, font=FONT,
             margin=0.06, italic=False):
    box = slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = box.text_frame
    tf.clear()
    tf.word_wrap = True
    tf.margin_left = Inches(margin)
    tf.margin_right = Inches(margin)
    tf.margin_top = Inches(margin)
    tf.margin_bottom = Inches(margin)
    tf.vertical_anchor = valign
    lines = str(text).split("\n")
    for idx, line in enumerate(lines):
        p = tf.paragraphs[0] if idx == 0 else tf.add_paragraph()
        p.text = line
        p.alignment = align
        p.space_after = Pt(3)
        for run in p.runs:
            run.font.name = font
            run.font.size = Pt(size)
            run.font.bold = bold
            run.font.italic = italic
            run.font.color.rgb = color
    return box


def add_card(slide, x, y, w, h, fill=WHITE, line=LINE, radius=True):
    shape = slide.shapes.add_shape(
        MSO_SHAPE.ROUNDED_RECTANGLE if radius else MSO_SHAPE.RECTANGLE,
        Inches(x), Inches(y), Inches(w), Inches(h))
    rgb_fill(shape, fill)
    shape.line.color.rgb = line
    shape.line.width = Pt(0.8)
    return shape


def add_title(slide, kicker, title, subtitle=None, page=None):
    add_text(slide, 0.62, 0.28, 2.2, 0.24, kicker.upper(), 9, BLUE, True)
    add_text(slide, 0.62, 0.56, 11.2, 0.46, title, 27, NAVY, True)
    if subtitle:
        add_text(slide, 0.64, 1.04, 11.5, 0.3, subtitle, 11, MUTED)
    if page is not None:
        add_text(slide, 12.15, 0.34, 0.55, 0.28, f"{page:02d}", 10, MUTED, True, PP_ALIGN.RIGHT)


def add_footer(slide, source="MesControlAgv · 更新：2026.09.08"):
    add_text(slide, 0.65, 7.18, 11.8, 0.16, source, 7.5, MUTED)
    line = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(0.62), Inches(7.08), Inches(12.1), Inches(0.008))
    rgb_fill(line, LINE)
    no_line(line)


def add_status_pill(slide, x, y, w, text, fill, color=WHITE):
    shape = add_card(slide, x, y, w, 0.32, fill, fill)
    add_text(slide, x, y + 0.02, w, 0.23, text, 9, color, True, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, margin=0.02)
    return shape


def add_bullets(slide, x, y, w, h, items, size=13, color=INK, gap=0.05):
    box = slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = box.text_frame
    tf.clear()
    tf.word_wrap = True
    tf.margin_left = Inches(0.02)
    tf.margin_right = Inches(0.02)
    tf.margin_top = Inches(0.02)
    tf.margin_bottom = Inches(0.02)
    for idx, item in enumerate(items):
        p = tf.paragraphs[0] if idx == 0 else tf.add_paragraph()
        p.text = "• " + item
        p.level = 0
        p.space_after = Pt(5)
        p.font.name = FONT
        p.font.size = Pt(size)
        p.font.color.rgb = color
    return box


def add_image_fit(slide, path, x, y, w, h, border=LINE):
    path = Path(path)
    if not path.exists():
        add_card(slide, x, y, w, h, LIGHT, LINE)
        add_text(slide, x + 0.1, y + h / 2 - 0.15, w - 0.2, 0.3, f"图片缺失：{path.name}", 10, RED, True, PP_ALIGN.CENTER)
        return
    with Image.open(path) as image:
        iw, ih = image.size
    ratio = iw / ih
    box_ratio = w / h
    if ratio > box_ratio:
        pw = w
        ph = w / ratio
        px = x
        py = y + (h - ph) / 2
    else:
        ph = h
        pw = h * ratio
        px = x + (w - pw) / 2
        py = y
    frame = add_card(slide, x, y, w, h, WHITE, border)
    slide.shapes.add_picture(str(path), Inches(px), Inches(py), width=Inches(pw), height=Inches(ph))
    return frame


def new_slide(prs):
    slide = prs.slides.add_slide(prs.slide_layouts[6])
    slide.background.fill.solid()
    slide.background.fill.fore_color.rgb = WHITE
    return slide


def add_connector(slide, x1, y1, x2, y2, color=BLUE, width=2.2):
    line = slide.shapes.add_connector(MSO_CONNECTOR.STRAIGHT, Inches(x1), Inches(y1), Inches(x2), Inches(y2))
    line.line.color.rgb = color
    line.line.width = Pt(width)
    line.line.end_arrowhead = True
    return line


def add_node(slide, x, y, w, h, title, subtitle, fill=WHITE, accent=BLUE):
    add_card(slide, x, y, w, h, fill, accent)
    bar = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(x), Inches(y), Inches(0.08), Inches(h))
    rgb_fill(bar, accent)
    no_line(bar)
    add_text(slide, x + 0.18, y + 0.15, w - 0.28, 0.3, title, 15, NAVY, True)
    add_text(slide, x + 0.18, y + 0.53, w - 0.28, h - 0.62, subtitle, 10.5, MUTED)


def slide_title(prs):
    slide = prs.slides.add_slide(prs.slide_layouts[6])
    slide.background.fill.solid()
    slide.background.fill.fore_color.rgb = NAVY
    # subtle right-side blocks
    for x, y, w, h, c in [(9.5, -0.2, 4.5, 2.2, NAVY_2), (10.7, 5.0, 3.0, 3.0, BLUE), (8.5, 5.8, 2.0, 1.0, CYAN)]:
        shape = slide.shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, Inches(x), Inches(y), Inches(w), Inches(h))
        rgb_fill(shape, c)
        no_line(shape)
        shape.fill.transparency = 18
    add_text(slide, 0.72, 0.78, 4.4, 0.28, "MES · AGV · AUBO · SHINELAB", 11, CYAN, True)
    add_text(slide, 0.72, 1.52, 8.8, 1.0, "自动化平台\n阶段性进度汇报", 34, WHITE, True)
    add_text(slide, 0.78, 3.0, 8.0, 0.8,
             "从软件基线到现场证据\n聚焦工作流一键执行、WPF 中控和离子色谱架构调整", 15, RGBColor(209, 225, 241))
    add_status_pill(slide, 0.78, 4.48, 1.45, "软件基线稳定", GREEN)
    add_status_pill(slide, 2.38, 4.48, 1.45, "现场证据推进", AMBER)
    add_status_pill(slide, 3.98, 4.48, 1.45, "生产准入关闭", RED)
    add_text(slide, 0.78, 6.48, 5.3, 0.26, "更新日期：2026年9月8日", 11, RGBColor(209, 225, 241))
    add_text(slide, 10.2, 6.48, 2.4, 0.26, "MesControlAgv", 11, WHITE, True, PP_ALIGN.RIGHT)
    return slide


def build():
    prs = Presentation()
    prs.slide_width = Inches(13.333333)
    prs.slide_height = Inches(7.5)
    slide_title(prs)

    # 2 overview
    slide = new_slide(prs)
    add_title(slide, "01 · 项目总览", "软件基线已稳定，现场闭环继续按门禁推进",
              "最新状态：平台能力可回归，实体设备仍按只读预检、授权和人工核销推进。", 1)
    cards = [
        (0.7, "1056 / 5 / 0", "Release 门禁", "1056 项通过\n5 项登记跳过 · 0 失败", GREEN, GREEN_LIGHT),
        (3.85, "9 节点", "现场标准流程", "9/4 完成一次现场证据\n7/7 节点成功 · 4/4 AGV 到站", BLUE, BLUE_LIGHT),
        (7.0, "WPF", "中控产品形态", "流程管理 · 运行监控\nAGV/AUBO · ShineLab 页面", PURPLE, RGBColor(241, 233, 252)),
        (10.15, "NO-GO", "生产准入", "无线只读和就绪监督继续\n自动派发与批量执行默认关闭", RED, RED_LIGHT),
    ]
    for x, value, label, detail, accent, fill in cards:
        add_card(slide, x, 1.62, 2.5, 1.65, fill, accent)
        add_text(slide, x + 0.15, 1.86, 2.2, 0.38, value, 22, accent, True)
        add_text(slide, x + 0.15, 2.31, 2.2, 0.28, label, 12, NAVY, True)
        add_text(slide, x + 0.15, 2.72, 2.2, 0.4, detail, 9.5, MUTED)
    add_card(slide, 0.7, 3.65, 12.0, 2.55, WHITE, LINE)
    add_text(slide, 0.95, 3.92, 3.0, 0.3, "本阶段主线", 16, NAVY, True)
    add_bullets(slide, 0.98, 4.38, 5.2, 1.45, [
        "工作流：AGV Move 与 AUBO 程序节点形成标准模板和一键执行入口。",
        "可靠性：Unknown、超时、断线、取消和控制权释放均保持人工核销边界。",
        "现场：AMR 无线只读已验证，完整 AGV 快照和新鲜授权仍是准入前置条件。",
    ], 12.5)
    add_text(slide, 6.65, 3.92, 4.5, 0.3, "重要判断", 16, NAVY, True)
    add_text(slide, 6.65, 4.38, 5.45, 1.28,
             "当前项目的主要瓶颈已从代码实现转为现场网络、设备状态证据、控制权和可复核验收。\n\n离子色谱支线已切换为 ShineLab 代理架构，不再推进中控直接占用串口。",
             12.5, INK)
    add_footer(slide, "数据源：docs/PROGRESS.md · 更新：2026.09.08")

    # 3 architecture
    slide = new_slide(prs)
    add_title(slide, "02 · 目标架构", "统一中控链路，设备协议留在边界内",
              "WPF 只承载操作和观察；MES 负责任务、状态、审计与恢复；设备由 Adapter/Worker 或 ShineLab 负责。", 2)
    add_node(slide, 0.75, 2.05, 2.15, 1.15, "WPF 中控", "流程、许可、监控\n不直接连接设备", WHITE, BLUE)
    add_node(slide, 3.65, 2.05, 2.15, 1.15, "MES / Workflow", "任务、排程、审计\nUnknown 与恢复", WHITE, CYAN)
    add_node(slide, 6.55, 2.05, 2.15, 1.15, "Adapter / Worker", "设备能力、控制权\n超时和幂等门禁", WHITE, GREEN)
    add_node(slide, 9.45, 1.48, 2.25, 1.15, "AGV / AUBO", "现场导航、程序\n只读预检与受监督写入", GREEN_LIGHT, GREEN)
    add_node(slide, 9.45, 3.08, 2.25, 1.15, "ShineLab", "TCP Client\n负责串口和仪器动作", BLUE_LIGHT, BLUE)
    add_connector(slide, 2.9, 2.62, 3.62, 2.62)
    add_connector(slide, 5.8, 2.62, 6.52, 2.62)
    add_connector(slide, 8.75, 2.35, 9.4, 2.08, GREEN)
    add_connector(slide, 8.75, 2.9, 9.4, 3.62, BLUE)
    add_text(slide, 0.85, 4.98, 11.5, 0.5, "离子色谱新链路", 18, NAVY, True)
    add_text(slide, 0.86, 5.48, 11.4, 0.52,
             "ShineLab（TCP Client）  →  CentralTcpServer / MES（TCP Server）  →  WPF\n中控不再打开 SHA-18i / D160+ 串口；ShineLab 负责设备和串口控制。",
             14, INK)
    add_status_pill(slide, 0.86, 6.32, 1.6, "历史直控：停止", RED)
    add_status_pill(slide, 2.65, 6.32, 1.8, "当前代理架构：采用", GREEN)
    add_footer(slide, "架构依据：docs/SHINELAB-SHA18I-INTERFACE-REQUIREMENTS-2026-09-01.md")

    # 4 workflow evidence
    slide = new_slide(prs)
    add_title(slide, "03 · 工作流现场进展", "AGV 与 AUBO 已形成标准一键流程证据",
              "2026-09-04 完成一次受监督现场运行；后续每次必须使用新 RunId、新授权和新鲜只读预检。", 3)
    add_card(slide, 0.72, 1.58, 12.0, 1.05, GREEN_LIGHT, GREEN)
    add_text(slide, 0.98, 1.78, 2.1, 0.28, "现场结论", 13, GREEN, True)
    add_text(slide, 3.0, 1.76, 9.25, 0.38, "Workflow Completed · 7/7 节点成功 · 4/4 AGV 到站 · 3/3 AUBO 程序成功 · 控制权释放", 14, NAVY, True)
    # timeline
    nodes = [
        ("LM1→LM7", "AGV 到站", GREEN),
        ("取料盘.pro", "AUBO 程序", BLUE),
        ("LM7→LM2", "AGV 到站", GREEN),
        ("放料盘.pro", "AUBO 程序", BLUE),
        ("LM2→LM7", "AGV 到站", GREEN),
        ("回收料盘.pro", "AUBO 程序", BLUE),
        ("LM7→LM1", "AGV 到站", GREEN),
    ]
    x0 = 0.78
    for i, (name, kind, color) in enumerate(nodes):
        x = x0 + i * 1.73
        add_card(slide, x, 3.12, 1.48, 1.12, WHITE, color)
        add_text(slide, x + 0.08, 3.35, 1.32, 0.32, name, 10.5, NAVY, True, PP_ALIGN.CENTER)
        add_text(slide, x + 0.08, 3.78, 1.32, 0.24, kind, 9, color, True, PP_ALIGN.CENTER)
        if i < len(nodes) - 1:
            add_connector(slide, x + 1.5, 3.68, x + 1.7, 3.68, MUTED, 1.2)
    add_card(slide, 0.72, 4.78, 5.72, 1.48, BLUE_LIGHT, BLUE)
    add_text(slide, 0.98, 5.03, 2.8, 0.25, "已固化能力", 14, BLUE, True)
    add_bullets(slide, 0.98, 5.35, 5.15, 0.7, [
        "一次性操作员 / 安全监护人 / 许可校验",
        "程序加载、启动、停止、取消、超时和重启恢复",
        "WPF 运行监控、节点状态、控制权释放和审计",
    ], 10.5)
    add_card(slide, 6.72, 4.78, 6.0, 1.48, AMBER_LIGHT, AMBER)
    add_text(slide, 6.98, 5.03, 3.2, 0.25, "当前门禁", 14, AMBER, True)
    add_bullets(slide, 6.98, 5.35, 5.45, 0.7, [
        "PhysicalReadinessSupervisor 默认关闭，批量执行默认关闭",
        "无线只读已通过，完整 AGV 位置 / 任务 / 控制权快照仍需补齐",
        "Unknown、断线、急停和结果不明：停止并人工核销，不自动重试",
    ], 10.5)
    add_footer(slide, "证据：artifacts/physical-acceptance/phase2-std-20260904-093754/phase2-field-acceptance-evidence-20260904.md")

    # 5 WPF workflow screenshot
    slide = new_slide(prs)
    add_title(slide, "04 · WPF 当前界面", "实验流程管理：模板、节点、校验与发布入口",
              "当前界面已经从单一设备按钮扩展为可编辑、可校验、可发布的流程中控。", 4)
    add_image_fit(slide, ROOT / "artifacts/field-debug-20260901-104448/wpf-final-autofit.png", 0.72, 1.45, 8.2, 5.35)
    add_card(slide, 9.25, 1.55, 3.4, 5.12, LIGHT, LINE)
    add_text(slide, 9.55, 1.9, 2.8, 0.28, "当前可展示", 15, NAVY, True)
    add_bullets(slide, 9.55, 2.38, 2.75, 2.1, [
        "标准搬运实验模板",
        "AGV 到站节点",
        "AUBO 程序节点",
        "节点属性与流程说明",
        "流程校验问题列表",
    ], 12)
    add_text(slide, 9.55, 4.82, 2.7, 0.25, "当前边界", 15, NAVY, True)
    add_text(slide, 9.55, 5.28, 2.7, 0.98,
             "WPF 只提交业务请求和现场授权；设备写入由 MES / Adapter / Worker 按门禁执行。\n\n截图：2026.09.01 当前构建。",
             11, MUTED)
    add_footer(slide, "界面证据：artifacts/field-debug-20260901-104448/wpf-final-autofit.png")

    # 6 WPF run monitor screenshot
    slide = new_slide(prs)
    add_title(slide, "05 · WPF 运行监控", "从运行状态到现场许可、异常和控制权释放",
              "运行监控页承载操作员、原因、许可、节点状态和当前设备上下文。", 5)
    add_image_fit(slide, ROOT / "artifacts/physical-acceptance/phase2-std-20260904-093754/wpf-live-screen-2.png", 0.72, 1.42, 8.5, 5.45)
    add_card(slide, 9.52, 1.58, 3.12, 5.08, WHITE, LINE)
    add_text(slide, 9.82, 1.93, 2.4, 0.28, "监控要点", 15, NAVY, True)
    add_bullets(slide, 9.82, 2.42, 2.5, 2.15, [
        "运行 ID 与节点执行记录",
        "操作员 / 安全监护人",
        "现场 Move 验收许可",
        "当前节点与执行状态",
        "暂停、取消和人工核销入口",
    ], 11.5)
    add_text(slide, 9.82, 4.95, 2.45, 0.25, "产品化边界", 15, NAVY, True)
    add_text(slide, 9.82, 5.4, 2.45, 0.92,
             "界面可观察不等于设备自动执行；Physical 模式仍由现场服务、授权和新鲜预检共同决定。",
             11, MUTED)
    add_footer(slide, "界面证据：artifacts/physical-acceptance/phase2-std-20260904-093754/wpf-live-screen-2.png")

    # 7 network and readiness
    slide = new_slide(prs)
    add_title(slide, "06 · 现场网络与就绪监督", "无线可达已经建立，完整设备快照仍是准入条件",
              "9 月 7 日无线只读证据通过；系统继续坚持 fail-closed，不把 Ping/TCP 成功等同于可运行。", 6)
    add_card(slide, 0.72, 1.62, 5.65, 4.85, BLUE_LIGHT, BLUE)
    add_text(slide, 1.02, 1.94, 3.1, 0.3, "最新只读证据", 16, BLUE, True)
    rows = [
        ("生产 SSID", "AMR"),
        ("总控电脑", "192.168.1.11 / 24"),
        ("AGV 状态端口", "192.168.1.2:19204"),
        ("AUBO 状态通道", "WebSocket :9012"),
        ("AUBO 状态", "Running / Normal / Automatic / Stopped"),
        ("当前判定", "canDetermineGo = false"),
    ]
    for idx, (k, v) in enumerate(rows):
        y = 2.48 + idx * 0.55
        add_text(slide, 1.02, y, 1.62, 0.26, k, 11.5, MUTED, True)
        add_text(slide, 2.7, y, 3.25, 0.28, v, 11.5, NAVY, idx == 5)
    add_card(slide, 6.72, 1.62, 5.98, 4.85, AMBER_LIGHT, AMBER)
    add_text(slide, 7.02, 1.94, 3.5, 0.3, "为什么仍未进入生产", 16, AMBER, True)
    add_bullets(slide, 7.02, 2.48, 5.18, 2.4, [
        "AGV 完整位置、活动任务、控制权、地图 / 定位和安全快照尚未齐全",
        "就绪监督器已增加 DeviceEpoch 和重新上线复核，但默认仍关闭",
        "历史 RunId、许可和验收单不可复用，必须以新鲜证据重新授权",
        "任何 Unknown、急停、断线或结果不明均进入人工处置",
    ], 12)
    add_status_pill(slide, 7.02, 5.55, 2.0, "只读可继续", GREEN)
    add_status_pill(slide, 9.25, 5.55, 2.0, "写入需授权", RED)
    add_footer(slide, "证据：docs/PROGRESS.md · 2026.09.07/08 无线只读与就绪监督记录")

    # 8 ion chromatography new plan
    slide = new_slide(prs)
    add_title(slide, "07 · 离子色谱方案调整", "从中控直连串口，切换为 ShineLab 代理架构",
              "方案变化：中控只管理业务任务和状态，ShineLab 独占 SHA-18i / D160+ 串口。", 7)
    add_card(slide, 0.75, 1.65, 5.55, 3.55, RED_LIGHT, RED)
    add_text(slide, 1.05, 1.98, 3.8, 0.3, "历史方案 · 停止采用", 16, RED, True)
    add_text(slide, 1.05, 2.55, 4.8, 1.2,
             "WPF → MES → 仪器网关\n            → COM / Modbus RTU\n            → D160+ / SHA-18i",
             15, INK, True)
    add_text(slide, 1.05, 4.22, 4.72, 0.5, "风险：串口独占、协议写入和安全语义尚未形成生产证据。", 11.5, RED)
    add_card(slide, 7.02, 1.65, 5.55, 3.55, GREEN_LIGHT, GREEN)
    add_text(slide, 7.32, 1.98, 3.8, 0.3, "当前方案 · 采用", 16, GREEN, True)
    add_text(slide, 7.32, 2.55, 4.95, 1.35,
             "ShineLab（TCP Client）\n            ↓ TCP + JSON + LF\n中控 TCP Server / MES → WPF",
             15, INK, True)
    add_text(slide, 7.32, 4.22, 4.72, 0.5, "边界：ShineLab 控制设备；中控负责任务、状态、审计和恢复。", 11.5, GREEN)
    add_text(slide, 0.78, 5.75, 11.5, 0.35, "D160+ 与 SHA-18i 的基础串口 / 抓包证据保留为厂商设备边界依据，不再作为中控直接写入路径。", 12, MUTED)
    add_footer(slide, "依据：docs/ION-CHROMATOGRAPHY-INTERFACE-MEETING-REPORT-2026-09-01.md")

    # 9 ion chromatography status and gaps
    slide = new_slide(prs)
    add_title(slide, "08 · 离子色谱当前状态", "通信骨架已具备，正式协议和真机联调仍未完成",
              "把“已能模拟接入”与“厂家 ShineLab 已现场闭环”明确分开。", 8)
    add_card(slide, 0.72, 1.55, 5.85, 4.95, GREEN_LIGHT, GREEN)
    add_text(slide, 1.02, 1.9, 3.3, 0.3, "中控侧已具备", 16, GREEN, True)
    add_bullets(slide, 1.02, 2.42, 5.05, 2.6, [
        "ShineLab TCP Server、换行分帧和 Certification 响应",
        "UpdateInfo / AlarmInfo / TaskFinish / Result 状态缓存",
        "Config / Command 高层业务下发和 strID 关联",
        "task_uuid 幂等、任务事件、断线 / 重启 Unknown 恢复",
        "WPF 设备状态查询和实验任务下发页面",
    ], 11.6)
    add_status_pill(slide, 1.02, 5.72, 2.0, "实验骨架：具备", BLUE)
    add_card(slide, 6.82, 1.55, 5.9, 4.95, RED_LIGHT, RED)
    add_text(slide, 7.12, 1.9, 3.3, 0.3, "协议 / 联调缺口", 16, RED, True)
    add_bullets(slide, 7.12, 2.42, 5.08, 2.8, [
        "厂家 Client、端口和 SHA-18i equipmentCode 尚未现场确认",
        "JSON 示例无效，响应 result/msg 层级存在冲突",
        "状态缺 task/sample/stage/progress；错误码和恢复语义不完整",
        "Config / Command / Result 缺少完整方法、结果文件和批量序列定义",
        "CSV/RPA 导入成功不等于 TCP 自动进样；D160+ 写入继续关闭",
    ], 11.6)
    add_status_pill(slide, 7.12, 5.72, 2.0, "生产协议：不通过", RED)
    add_footer(slide, "依据：docs/SHINELAB-下游表协议-需求符合性分析-2026-09-04.md")

    # 10 next steps
    slide = new_slide(prs)
    add_title(slide, "09 · 下一阶段重点", "先补齐现场证据，再推进受监督闭环",
              "每一步都以新鲜数据、新授权和可回放证据作为完成条件。", 9)
    priorities = [
        (0.75, "01", "现场网络与就绪", "AMR 无线只读复核\n补齐 AGV 完整快照\n确认地图、位置、任务和控制权", BLUE),
        (4.35, "02", "标准流程受监督运行", "新 RunId / 新许可\n9 节点一次性执行\nUnknown 不重试，结束释放控制权", GREEN),
        (7.95, "03", "ShineLab 第一轮联调", "Certification + UpdateInfo\n再做单样品 Config/Command\n最后验证 Result 与恢复", PURPLE),
    ]
    for x, no, title, detail, color in priorities:
        add_card(slide, x, 1.85, 3.05, 3.3, WHITE, color)
        add_text(slide, x + 0.22, 2.12, 0.6, 0.45, no, 24, color, True)
        add_text(slide, x + 0.9, 2.18, 1.8, 0.3, title, 15, NAVY, True)
        add_text(slide, x + 0.22, 2.9, 2.55, 1.3, detail, 13, INK)
        add_status_pill(slide, x + 0.22, 4.45, 1.35, "受监督", color)
    add_card(slide, 0.75, 5.65, 12.0, 0.85, NAVY, NAVY)
    add_text(slide, 1.0, 5.9, 11.3, 0.32,
             "阶段性判断：软件平台可继续交付；实体设备与离子色谱自动化仍以证据、授权和人工核销为前提。",
             14, WHITE, True, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE)
    add_footer(slide, "下一阶段：现场无线只读 / 标准流程复核 / ShineLab TCP 首轮联调")

    # 11 evidence appendix
    slide = new_slide(prs)
    add_title(slide, "附录 · 证据索引", "本版汇报所依据的最新资料",
              "以证据文件为准；历史截图和旧版进度只作为背景，不作为当前状态判断。", 10)
    add_card(slide, 0.72, 1.55, 12.0, 4.95, LIGHT, LINE)
    refs = [
        ("平台与现场状态", "docs/PROGRESS.md（更新：2026.09.08）"),
        ("标准流程现场证据", "artifacts/physical-acceptance/phase2-std-20260904-093754/phase2-field-acceptance-evidence-20260904.md"),
        ("无线只读证据", "artifacts/physical-acceptance/wireless-readonly-20260907-133352-8608f5f2/wireless-readonly-evidence.json"),
        ("WPF 流程管理界面", "artifacts/field-debug-20260901-104448/wpf-final-autofit.png"),
        ("WPF 运行监控界面", "artifacts/physical-acceptance/phase2-std-20260904-093754/wpf-live-screen-2.png"),
        ("离子色谱会议结论", "docs/ION-CHROMATOGRAPHY-INTERFACE-MEETING-REPORT-2026-09-01.md"),
        ("离子色谱协议评审", "docs/SHINELAB-下游表协议-需求符合性分析-2026-09-04.md"),
    ]
    for i, (label, ref) in enumerate(refs):
        y = 1.93 + i * 0.58
        add_text(slide, 1.03, y, 2.25, 0.25, label, 11.5, NAVY, True)
        add_text(slide, 3.45, y, 8.75, 0.28, ref, 10.5, MUTED)
    add_text(slide, 0.98, 6.18, 11.4, 0.28, "汇报口径：离线可回归 ≠ 现场可运行；无线可达 ≠ 已获得设备写入授权。", 12, RED, True, PP_ALIGN.CENTER)
    add_footer(slide, "自动化平台阶段性进度汇报 · 2026.09.08")

    prs.save(OUT)
    print(OUT)


if __name__ == "__main__":
    build()
