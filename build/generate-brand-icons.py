"""Point Island icon: derive the existing companion contour and eye rings.

Offline rasterization only. This does not load the app, browser or any UI.
Usage: python generate_icons.py [repo-root] [output-directory]
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import io, json, math, struct, sys

ROOT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[1]
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parent
OUT.mkdir(parents=True, exist_ok=True)
CATALOG = json.loads((ROOT / 'src/CodexQuotaTaskbar.Host/Assets/Companion/poses.json').read_text(encoding='utf-8'))
HEAD = CATALOG['head']
# Open eye contours from the native listening/look-up expression. Recenter for
# an immediately legible app mark, preserving their softly irregular contours.
EYES = CATALOG['expressions'][24]
SIZES = [16,20,24,32,40,48,64,128,256]
SS = 6

def eye_shape(ring, cx, cy, width, height):
    xs, ys = [p[0] for p in ring], [p[1] for p in ring]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    return [(cx + (p[0]-(x0+x1)/2)/(x1-x0)*width,
             cy + (p[1]-(y0+y1)/2)/(y1-y0)*height) for p in ring]

def shapes(size):
    # Fill ~83% of the square; reserve transparent breathing room for the edge.
    scale = size * .83 / 228.54
    ox = (size - 228.54*scale)/2
    oy = (size - 228.44*scale)/2 - size*.012
    head = [(ox+x*scale,oy+y*scale) for x,y in HEAD]
    small = size <= 24
    # A slight shared upward-right gaze, rather than two disconnected pupils.
    dimensions = [(95,105,32 if small else 31,51), (153,100,32 if small else 31,50)]
    eyes = [[(ox+x*scale,oy+y*scale) for x,y in eye_shape(ring,*d)] for ring,d in zip(EYES,dimensions)]
    return head, eyes

def render(size):
    n=size*SS
    head, eyes=shapes(size)
    points=lambda p:[(x*SS,y*SS) for x,y in p]
    mask=Image.new('L',(n,n)); md=ImageDraw.Draw(mask)
    md.polygon(points(head),fill=255)
    # Neutral cool edge remains readable on both light and dark desktop themes.
    stroke=max(.60,size*.0048)*SS
    body=Image.new('RGBA',(n,n))
    px=body.load()
    for y in range(n):
        for x in range(n):
            radius=math.hypot((x/n-.37)/.79,(y/n-.29)/.79)
            t=min(1,max(0,(radius-.18)/.93))**1.65
            px[x,y]=(round(255-35*t),round(255-32*t),round(255-26*t),255)
    body.putalpha(mask)
    edge=Image.new('RGBA',(n,n)); ed=ImageDraw.Draw(edge)
    ed.line(points(head+[head[0]]),fill=(131,140,154,205),width=round(stroke),joint='curve')
    body=Image.alpha_composite(body,edge)
    shadow=Image.new('RGBA',(n,n))
    shadow_alpha=mask.filter(ImageFilter.GaussianBlur(max(1,size*.016*SS)))
    shadow_alpha=shadow_alpha.point(lambda a: round(a*.16))
    shadow.paste((36,45,62),(0,0,n,n),shadow_alpha)
    shadow=Image.frombytes('RGBA',(n,n),shadow.tobytes())
    shifted=Image.new('RGBA',(n,n)); shifted.paste(shadow,(0,round(size*.016*SS)))
    image=Image.alpha_composite(shifted,body)
    d=ImageDraw.Draw(image)
    for ring in eyes: d.polygon(points(ring),fill=(34,38,46,255))
    if size>=32:
        # Tiny quiet highlights only on desktop sizes; tray keeps solid eyes.
        for ring in eyes:
            x=min(p[0] for p in ring)+(max(p[0] for p in ring)-min(p[0] for p in ring))*.32
            y=min(p[1] for p in ring)+(max(p[1] for p in ring)-min(p[1] for p in ring))*.22
            r=size*.0075
            d.ellipse(((x-r)*SS,(y-r)*SS,(x+r)*SS,(y+r)*SS),fill=(255,255,255,230))
    image=image.resize((size,size),Image.Resampling.LANCZOS)
    # Remove the sub-alpha ringing of Lanczos at the canvas edge; the actual
    # contour already has >1px breathing room even in the smallest image.
    border=ImageDraw.Draw(image)
    border.rectangle((0,0,size-1,size-1),outline=(0,0,0,0),width=1)
    return image

def svg():
    head,eyes=shapes(512)
    path=lambda ring:'M '+' L '.join(f'{x:.3f},{y:.3f}' for x,y in ring)+' Z'
    data=f'''<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 512 512">
<title>点岛</title>
<defs><radialGradient id="body" cx="37%" cy="29%" r="88%"><stop offset="0" stop-color="#fff"/><stop offset=".5" stop-color="#f8f9fb"/><stop offset="1" stop-color="#dce0e6"/></radialGradient><filter id="shadow" x="-15%" y="-15%" width="130%" height="140%"><feDropShadow dx="0" dy="8" stdDeviation="8" flood-color="#242d3e" flood-opacity=".16"/></filter></defs>
<path d="{path(head)}" fill="url(#body)" stroke="#8d96a6" stroke-opacity=".8" stroke-width="2.45" stroke-linejoin="round" filter="url(#shadow)"/>
'''
    for ring in eyes:
        data+=f'<path d="{path(ring)}" fill="#22262e"/>\n'
        x=min(p[0] for p in ring)+(max(p[0] for p in ring)-min(p[0] for p in ring))*.32
        y=min(p[1] for p in ring)+(max(p[1] for p in ring)-min(p[1] for p in ring))*.22
        data+=f'<circle cx="{x:.3f}" cy="{y:.3f}" r="3.84" fill="#fff" fill-opacity=".9"/>\n'
    return data+'</svg>\n'

images={size:render(size) for size in SIZES+[512]}
for size,img in images.items(): img.save(OUT/f'DianDao-{size}.png')
images[512].save(OUT/'DianDao.png')
(OUT/'DianDao.svg').write_text(svg(),encoding='utf8')
# PNG-compressed ICO entries preserve each independently optimized raster.
blobs=[]
for size in SIZES:
    b=io.BytesIO(); images[size].save(b,format='PNG'); blobs.append(b.getvalue())
offset=6+len(SIZES)*16
with (OUT/'DianDao.ico').open('wb') as f:
    f.write(struct.pack('<HHH',0,1,len(SIZES)))
    for size,blob in zip(SIZES,blobs):
        f.write(struct.pack('<BBBBHHII',size if size<256 else 0,size if size<256 else 0,0,0,1,32,len(blob),offset));offset+=len(blob)
    for blob in blobs:f.write(blob)

font_path=Path(r'C:\Windows\Fonts\msyh.ttc')
font=lambda n:ImageFont.truetype(str(font_path),n) if font_path.exists() else ImageFont.load_default()
preview=Image.new('RGB',(1280,940),(241,243,247)); pd=ImageDraw.Draw(preview)
pd.text((48,25),'点岛',font=font(32),fill=(32,38,46))
pd.text((142,37),'白色小球 · 灵动双眼 · 透明图标',font=font(15),fill=(99,108,123))
for x,bg in [(32,(255,255,255)),(656,(27,32,43))]:
    pd.rounded_rectangle((x,92,x+592,494),radius=24,fill=bg)
    preview.paste(images[256],(x+168,153),images[256])
    pd.text((x+24,460),'浅色背景' if x==32 else '深色背景',font=font(15),fill=(117,126,140) if x==32 else (166,177,194))
pd.text((48,524),'实际像素大小 / 明暗背景',font=font(18),fill=(65,75,91))
for top,bg,fg in [(570,(255,255,255),(101,110,124)),(750,(27,32,43),(166,177,194))]:
    pd.rounded_rectangle((32,top,1248,top+164),radius=18,fill=bg)
    x=58
    for size in SIZES:
        shown=min(size,128)
        image=images[size] if size<=128 else images[size].resize((128,128),Image.Resampling.LANCZOS)
        preview.paste(image,(x+(105-shown)//2,top+12+(128-shown)//2),image)
        label=str(size)+'px'+('→128' if size==256 else '')
        pd.text((x+(105-pd.textlength(label,font=font(12)))//2,top+141),label,font=font(12),fill=fg)
        x+=130
preview.save(OUT/'DianDao-preview.png')

report={'source':'Companion/poses.json head + expression 24 eye contours','sizes':SIZES,'rasterSupersampling':SS,'entries':[]}
for size,img in images.items():
    alpha=img.getchannel('A'); corners=[alpha.getpixel(p) for p in [(0,0),(size-1,0),(0,size-1),(size-1,size-1)]]
    assert corners==[0,0,0,0],(size,corners)
    bbox=alpha.getbbox(); partial=sum(1 for a in alpha.getdata() if 0<a<255)
    report['entries'].append({'size':size,'cornersAlpha':corners,'alphaBounds':bbox,'partialAlphaPixels':partial,'allBordersTransparent':all(alpha.getpixel((x,0))==0 and alpha.getpixel((x,size-1))==0 and alpha.getpixel((0,x))==0 and alpha.getpixel((size-1,x))==0 for x in range(size))})
ico=Image.open(OUT/'DianDao.ico')
assert ico.ico.sizes()==set((s,s) for s in SIZES)
report['icoDecodedSizes']=sorted([list(s) for s in ico.ico.sizes()])
(OUT/'icon-validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps({'output':str(OUT),'icoEntries':len(SIZES),'cornersTransparent':True,'preview':str(OUT/'DianDao-preview.png')},ensure_ascii=False))
