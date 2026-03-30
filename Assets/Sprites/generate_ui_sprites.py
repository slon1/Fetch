#!/usr/bin/env python3
"""
Generate dark futuristic UI sprites for WebRTC app.
Style: minimal, clean shapes, high contrast, rounded corners, blue/purple edge accents.
No glow, no bloom, no soft halo.
"""
from PIL import Image, ImageDraw
import math
import os

OUTPUT_DIR = os.path.dirname(os.path.abspath(__file__))

# Colors
BLACK = (0, 0, 0)
DARK_BG = (18, 18, 24)
DARK_SURFACE = (24, 24, 32)
DARK_CENTER = (32, 32, 42)
BLUE = (64, 128, 255)
PURPLE = (128, 64, 255)
BLUE_PURPLE = (96, 96, 255)
GREEN = (64, 200, 96)
BORDER = 2
# Chat bubble: flat accents, no glow (darker, less saturated)
CHAT_EDGE_LEFT = (72, 72, 140)   # muted purple
CHAT_EDGE_RIGHT = (72, 96, 140)  # muted blue


def rounded_rect(draw, xy, r, fill=None, outline=None, width=1):
    """Draw rounded rectangle. xy = (x1,y1,x2,y2)"""
    x1, y1, x2, y2 = xy
    draw.rounded_rectangle(xy, radius=r, fill=fill, outline=outline, width=width)


def draw_gradient_rect(draw, xy, r, dark, light, outline_color, outline_width=2):
    """Draw rounded rect with subtle inner gradient and edge accent."""
    # Base fill - dark surface
    rounded_rect(draw, xy, r, fill=dark, outline=None)
    # Edge accent - bright blue/purple border
    rounded_rect(draw, xy, r, fill=None, outline=outline_color, width=outline_width)


def create_button_bg():
    """1. Button background"""
    w, h, r = 256, 64, 12
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pad = 8
    xy = (pad, pad, w - pad, h - pad)
    draw_gradient_rect(draw, xy, r, DARK_SURFACE, DARK_CENTER, BLUE_PURPLE)
    img.save(os.path.join(OUTPUT_DIR, "ui_button_bg.png"))


def create_icon(draw, x, y, size, icon_type, color):
    """Draw single icon at center (x,y) with given size."""
    s = size // 2
    w = 2
    if icon_type == "mic":
        # Microphone: stem + base
        draw.rectangle([x - 4, y - s + 8, x + 4, y + s - 4], outline=color, width=w)
        draw.ellipse([x - 12, y + s - 12, x + 12, y + s + 4], outline=color, width=w)
    elif icon_type == "camera":
        # Camera body
        draw.rounded_rectangle([x - s + 4, y - 6, x + s - 4, y + 8], radius=4, outline=color, width=w)
        draw.ellipse([x - 8, y - 10, x + 8, y + 6], outline=color, width=w)
    elif icon_type == "switch":
        # Two overlapping rectangles (switch camera)
        draw.rectangle([x - s, y - 6, x - 2, y + 6], outline=color, width=w)
        draw.rectangle([x + 2, y - 6, x + s, y + 6], outline=color, width=w)
    elif icon_type == "chat":
        # Chat bubble
        draw.rounded_rectangle([x - s + 4, y - 8, x + s - 4, y + 8], radius=6, outline=color, width=w)
        points = [(x - 4, y + 8), (x, y + 14), (x + 4, y + 8)]
        draw.polygon(points, outline=color, fill=None)
    elif icon_type == "hangup":
        # Phone hangup - rotated handset
        draw.arc([x - 10, y - 10, x + 10, y + 10], 200, 340, fill=color, width=w)
        draw.line([x - 8, y + 6, x + 8, y + 6], fill=color, width=w)


def create_icons_set():
    """2. Icon set: mic, camera, switch, chat, hangup"""
    w, h = 512, 128
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    icons = ["mic", "camera", "switch", "chat", "hangup"]
    colors = [BLUE, BLUE, BLUE, PURPLE, (200, 64, 64)]  # hangup red accent
    step = w // (len(icons) + 1)
    for i, (icon, color) in enumerate(zip(icons, colors)):
        x = step * (i + 1)
        create_icon(draw, x, h // 2, 48, icon, color)
    img.save(os.path.join(OUTPUT_DIR, "ui_icons_set.png"))


def create_panel_card():
    """3. Panel / Card"""
    w, h, r = 400, 160, 16
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pad = 12
    xy = (pad, pad, w - pad, h - pad)
    draw_gradient_rect(draw, xy, r, DARK_SURFACE, DARK_CENTER, BLUE_PURPLE)
    img.save(os.path.join(OUTPUT_DIR, "ui_panel_card.png"))


def create_chat_bubble(side="left"):
    """4. Chat bubble - side: 'left' (tail left) or 'right' (tail right). No glow."""
    w, h = 200, 72
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pad = 8
    r = 14
    edge_color = CHAT_EDGE_LEFT if side == "left" else CHAT_EDGE_RIGHT
    if side == "left":
        xy = (pad + 24, pad, w - pad, h - pad)  # tail on left
        draw_gradient_rect(draw, xy, r, DARK_SURFACE, DARK_CENTER, edge_color, outline_width=1)
        points = [(pad + 24, h - pad - 20), (pad + 4, h - pad), (pad + 14, h - pad - 10)]
    else:
        xy = (pad, pad, w - pad - 24, h - pad)  # tail on right
        draw_gradient_rect(draw, xy, r, DARK_SURFACE, DARK_CENTER, edge_color, outline_width=1)
        points = [(w - pad - 24, h - pad - 20), (w - pad - 4, h - pad), (w - pad - 14, h - pad - 10)]
    draw.polygon(points, fill=DARK_SURFACE, outline=edge_color)
    fname = "ui_chat_bubble_left.png" if side == "left" else "ui_chat_bubble_right.png"
    img.save(os.path.join(OUTPUT_DIR, fname))


def create_room_list_item():
    """5. Room list item"""
    w, h, r = 400, 56, 10
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pad = 8
    xy = (pad, pad, w - pad, h - pad)
    draw_gradient_rect(draw, xy, r, DARK_SURFACE, DARK_CENTER, BLUE_PURPLE)
    img.save(os.path.join(OUTPUT_DIR, "ui_room_list_item.png"))


def create_input_field():
    """6. Input field"""
    w, h, r = 400, 52, 10
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pad = 8
    xy = (pad, pad, w - pad, h - pad)
    rounded_rect(draw, xy, r, fill=DARK_SURFACE, outline=BLUE, width=2)
    img.save(os.path.join(OUTPUT_DIR, "ui_input_field.png"))


def create_video_frame():
    """7. Video frame"""
    w, h, r = 320, 200, 12
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pad = 8
    xy = (pad, pad, w - pad, h - pad)
    # Dark overlay - solid dark fill
    rounded_rect(draw, xy, r, fill=(20, 20, 28), outline=None)
    rounded_rect(draw, xy, r, fill=None, outline=BLUE_PURPLE, width=2)
    img.save(os.path.join(OUTPUT_DIR, "ui_video_frame.png"))


def create_progress_bar():
    """8. Progress bar"""
    w, h = 400, 16
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pad = 2
    r = 6
    # Dark base
    rounded_rect(draw, (pad, pad, w - pad, h - pad), r, fill=DARK_SURFACE, outline=None)
    # Fill 60%
    fill_w = int((w - pad * 2) * 0.6)
    if fill_w > r * 2:
        rounded_rect(draw, (pad, pad, pad + fill_w, h - pad), r, fill=GREEN, outline=None)
    # Edge
    rounded_rect(draw, (pad, pad, w - pad, h - pad), r, fill=None, outline=BLUE_PURPLE, width=1)
    img.save(os.path.join(OUTPUT_DIR, "ui_progress_bar.png"))


def create_top_bar():
    """9. Top bar"""
    w, h = 400, 44
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pad = 4
    xy = (0, 0, w, h)
    rounded_rect(draw, xy, 0, fill=DARK_SURFACE, outline=None)
    # Bottom accent line
    draw.line([(0, h - 2), (w, h - 2)], fill=BLUE_PURPLE, width=2)
    img.save(os.path.join(OUTPUT_DIR, "ui_top_bar.png"))


def main():
    create_button_bg()
    create_icons_set()
    create_panel_card()
    create_chat_bubble("left")
    create_chat_bubble("right")
    create_room_list_item()
    create_input_field()
    create_video_frame()
    create_progress_bar()
    create_top_bar()
    print("All UI sprites generated in", OUTPUT_DIR)


if __name__ == "__main__":
    main()
