from PIL import Image, ImageDraw, ImageFilter
import math, sys
S = 1024
RED = (255, 31, 61)

def hexagon(cx, cy, r):
    return [(cx + r*math.cos(math.radians(a)), cy + r*math.sin(math.radians(a))) for a in range(-90, 270, 60)]

def render():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # fundo: quadrado arredondado escuro com gradiente
    bg = Image.new("RGBA", (S, S))
    d = ImageDraw.Draw(bg)
    for y in range(S):
        t = y / S
        d.line([(0, y), (S, y)], fill=(int(24 - 10*t), int(16 - 8*t), int(26 - 12*t), 255))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([40, 40, S-40, S-40], radius=210, fill=255)
    img.paste(bg, (0, 0), mask)

    # riscas diagonais subtis
    stripes = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sd = ImageDraw.Draw(stripes)
    for x in range(-S, S*2, 46):
        sd.line([(x, S), (x + S, 0)], fill=(255, 255, 255, 10), width=6)
    img.paste(stripes, (0, 0), Image.composite(stripes, Image.new("RGBA", (S, S)), mask))

    # brilho vermelho atras do emblema
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.polygon(hexagon(S/2, S/2+10, 330), outline=RED + (255,), width=70)
    glow = glow.filter(ImageFilter.GaussianBlur(45))
    img = Image.alpha_composite(img, glow)

    # hexagono + "A" angular
    fg = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    fd = ImageDraw.Draw(fg)
    fd.polygon(hexagon(S/2, S/2+10, 330), outline=RED + (255,), width=52)
    cx, cy = S/2, S/2 + 30
    w = 64
    fd.line([(cx-170, cy+190), (cx, cy-210)], fill="white", width=w)
    fd.line([(cx, cy-210), (cx+170, cy+190)], fill="white", width=w)
    fd.line([(cx-105, cy+70), (cx+105, cy+70)], fill="white", width=w-6)
    # ponta do A
    fd.polygon([(cx-36, cy-196), (cx, cy-262), (cx+36, cy-196)], fill="white")
    img = Image.alpha_composite(img, fg)
    return img

big = render()
big.save("allyhub-logo.png")
big.resize((256, 256), Image.LANCZOS).save("preview.png")
big.save("icon.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print("ok")
