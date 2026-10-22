# Test resources

`sample-scan.tif` is the live test's fixture: one 1700 × 2200 grayscale frame at 200 DPI,
LZW-compressed, white with four lines of black text and no text layer. The live test asserts
that the word `MARIGOLD` comes back. It was drawn with Pillow 11.1 and the Liberation Sans font,
because no PDF rasteriser was available to render `sample-scanned.pdf`. To regenerate it, run
this from this directory:

```bash
python3 -I -c '
from PIL import Image, ImageDraw, ImageFont
img = Image.new("L", (1700, 2200), 255)
draw = ImageDraw.Draw(img)
font = ImageFont.truetype("/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf", 56)
lines = ["Rag.NET scanned fixture", "This page was drawn as an image.", "The keyword is MARIGOLD.", "It has no text layer at all."]
for i, line in enumerate(lines):
    draw.text((150, 200 + i * 110), line, fill=0, font=font)
img.save("sample-scan.tif", compression="tiff_lzw", dpi=(200, 200))
'
```
