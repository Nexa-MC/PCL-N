#!/usr/bin/env python3
"""Build a self-contained preview gallery and a strictly allowlisted ZIP.

Python standard library only. --directory is required; no workspace-specific
paths, .NET builds, app launches or network requests are used by this builder.
"""

import argparse
import base64
import hashlib
import html
import json
from pathlib import Path
import struct
import time
import zipfile


THEMES = ('light', 'dark')
CATEGORIES = {'splash': 'Splash', 'main': '主窗口', 'resources': '资源列表与详情', 'settings': '设置 · 外观'}
RESOURCE_PHASES = (
    ('list-initial', '初始资源列表', '受控目录数据，展示实际资源列表界面。'),
    ('append-loading', '滚动后追加中', '保留已加载的记录，在列表尾部追加下一批。'),
    ('appended', '追加完成', '新记录进入同一连续列表。'),
    ('next-loading', '继续加载下一批', '真实列表尾部的加载状态。'),
    ('failed', '追加失败', '受控失败场景；已加载记录仍保留。'),
    ('retry-loading', '重试中', '通过实际控件重试失败批次。'),
    ('retry-success', '重试成功', '受控目录响应恢复后继续显示列表。'),
    ('detail', '资源详情', '从实际列表进入受控资源详情。'),
    ('back', '返回连续列表', '关闭详情后回到之前的列表。'),
)
SETTINGS_PHASES = (
    ('top', '主题与外观', '实际设置页顶部：主题、强调色和图像设置。'),
    ('motion', '动态效果与帧率', '实际设置页中段：减少动态效果和动画帧率。'),
    ('bottom', '外观页底部', '实际外观设置页底部及背景音乐设置。'),
    ('theme-switch', '保存后主题切换', ''),
)


def gif_info(data):
    if data[:6] not in (b'GIF87a', b'GIF89a') or len(data) < 13:
        raise ValueError('Not a complete GIF header')
    width, height = struct.unpack_from('<HH', data, 6)
    packed = data[10]
    offset = 13 + (3 * (2 ** ((packed & 7) + 1)) if packed & 128 else 0)
    frames, duration, delay, loop = 0, 0, 0, None

    def blocks(position):
        output = []
        while position < len(data):
            size = data[position]
            position += 1
            if not size:
                return position, output
            if position + size > len(data):
                raise ValueError('Truncated GIF extension/image block')
            output.append(data[position:position + size])
            position += size
        raise ValueError('Unterminated GIF blocks')

    while offset < len(data):
        marker = data[offset]
        offset += 1
        if marker == 0x3b:
            if not frames:
                raise ValueError('GIF has no image frames')
            return {'width': width, 'height': height, 'frames': frames,
                    'duration_ms': duration, 'loop': loop}
        if marker == 0x21:
            label = data[offset]
            offset += 1
            offset, chunks = blocks(offset)
            if label == 0xf9 and chunks and len(chunks[0]) == 4:
                delay = struct.unpack_from('<H', chunks[0], 1)[0] * 10
            if label == 0xff and chunks and chunks[0] in (b'NETSCAPE2.0', b'ANIMEXTS1.0'):
                if len(chunks) >= 2 and len(chunks[1]) >= 3 and chunks[1][0] == 1:
                    loop = struct.unpack_from('<H', chunks[1], 1)[0]
            continue
        if marker == 0x2c:
            if offset + 9 > len(data):
                raise ValueError('Truncated GIF image descriptor')
            local = data[offset + 8]
            offset += 9
            if local & 128:
                offset += 3 * (2 ** ((local & 7) + 1))
            offset += 1  # LZW minimum code size.
            offset, _ = blocks(offset)
            frames += 1
            duration += delay
            delay = 0
            continue
        if marker != 0:
            raise ValueError(f'Unknown GIF block 0x{marker:02x}')
    raise ValueError('GIF trailer missing')


def specs():
    output = []

    def add(path, category, theme, title, caption, poster=None):
        output.append({'path': path, 'category': category, 'theme': theme,
                       'title': title, 'caption': caption, 'poster': poster})

    for theme in THEMES:
        native = f'native/{theme}/'
        add(native + 'splash.png', 'splash', theme, '加载卡片', '真实原生 Splash，使用相应主题的用户提供 N 图标。')
        add(native + 'splash-entrance.gif', 'splash', theme, 'Splash 入场 · 180ms',
            '实际 X11 帧采样；一次淡入后停留，保留采样到的短暂前后静止区间。', native + 'splash.png')
        add(native + 'shell.png', 'main', theme, '主窗口', '实际启动页；使用隔离数据目录，未导入账户和游戏版本。')
        add(native + 'entrance.gif', 'main', theme, '主窗口入场 · 420ms',
            '实际原生窗口中的青绿 N 轨迹和场景入场，按真实采样时间播放。', native + 'shell.png')
        add(native + 'close.gif', 'main', theme, '主窗口关闭',
            '实际关闭过程与当前主题图标；无合成器环境使用不透明呈现。', native + 'shell.png')
        resource = f'resources-{theme}/'
        for phase, title, caption in RESOURCE_PHASES:
            add(resource + f'{phase}-{theme}.png', 'resources', theme, title, caption)
        add(resource + f'resource-continuous-{theme}.gif', 'resources', theme, '连续列表 · 完整操作',
            '真实 X11 录制：滚动、追加、失败、重试、详情与返回。资源记录及失败均为受控演示数据。',
            resource + f'list-initial-{theme}.png')
        for phase, title, caption in SETTINGS_PHASES:
            if phase == 'theme-switch':
                caption = '从浅色切到深色并保存后的实际界面。' if theme == 'light' else '从深色切到浅色并保存后的实际界面。'
            add(f'settings/settings-{theme}-{phase}-native.png', 'settings', theme, title, caption)
        add(f'settings/settings-{theme}-native.gif', 'settings', theme, '外观设置 · 实际操作',
            '真实 X11 录制：外观页滚动、动态设置和保存后的主题切换；临时本地设置，空版本列表。',
            f'settings/settings-{theme}-top-native.png')
    return output


def metadata_paths():
    output = ['gif-validation.json', 'gallery-validation.json', 'validation-summary.md',
              'native/manifest.json', 'native/VERIFICATION.md',
              'settings/settings-preview-capture.json', 'settings/README.md',
              'settings/capture-validation.json', 'settings/VERIFICATION.md']
    for theme in THEMES:
        output.append(f'native/{theme}/observations.json')
        output.extend(f'resources-{theme}/{name}' for name in
                      ('resource-preview.json', 'capture-validation.json', 'README.md'))
        output.extend(f'settings/settings-{theme}-{phase}-native.json' for phase, _, _ in SETTINGS_PHASES)
    return output


def local_file(directory, relative):
    path = directory / relative
    if not path.resolve().is_relative_to(directory):
        raise ValueError(f'Artifact escapes its directory: {relative}')
    return path


HTML = r'''<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>NexaCL · 本轮界面预览</title>
<style>
:root{color-scheme:light;--bg:#eef5f9;--panel:#fff;--ink:#20364a;--muted:#587185;--line:#cfdfeb;--accent:#1269c6;--soft:#e8f3ff;--shadow:0 10px 32px #163b5410}
body[data-theme=dark]{color-scheme:dark;--bg:#0c1722;--panel:#152330;--ink:#e7f3fc;--muted:#afc5d8;--line:#2d4358;--accent:#81dbe8;--soft:#1d3b50;--shadow:0 10px 32px #0003}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.65 system-ui,"Noto Sans CJK SC","Microsoft YaHei",sans-serif}button,a{font:inherit}button{cursor:pointer}button:focus-visible,a:focus-visible{outline:3px solid var(--accent);outline-offset:3px}header,main,footer{max-width:1320px;margin:auto;padding:28px}header{padding-bottom:12px}.eyebrow{color:var(--accent);font-size:12px;letter-spacing:.12em}h1{font-size:clamp(26px,4vw,38px);line-height:1.3;margin:8px 0 10px}p{margin:8px 0}.intro{max-width:920px;color:var(--muted)}.toolbar{position:sticky;top:0;z-index:2;background:var(--bg);border-bottom:1px solid var(--line);padding:14px 28px;display:flex;gap:16px;justify-content:space-between;flex-wrap:wrap}.toolbar-inner{max-width:1264px;margin:auto;width:100%;display:flex;gap:14px;align-items:center;justify-content:space-between;flex-wrap:wrap}.switches{display:flex;gap:7px;flex-wrap:wrap;align-items:center}.switches span{color:var(--muted);font-size:13px;margin-right:3px}button,.download{border:1px solid var(--line);border-radius:9px;padding:7px 12px;color:var(--ink);background:var(--panel);text-decoration:none;display:inline-block}button[aria-pressed=true]{border-color:var(--accent);background:var(--soft);color:var(--accent)}.downloads{display:flex;gap:8px;flex-wrap:wrap}.notice{color:var(--muted);font-size:13px;max-width:1000px}.category{margin-bottom:34px}.category h2{margin:0 0 6px;font-size:23px}.section-note{color:var(--muted);margin:0 0 17px;font-size:14px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,430px),1fr));gap:20px}.card{background:var(--panel);border:1px solid var(--line);border-radius:16px;overflow:hidden;box-shadow:var(--shadow)}.preview{display:block;border:0;padding:0;border-radius:0;width:100%;background:#101922;cursor:zoom-in}.preview img{width:100%;height:auto;display:block;object-fit:contain;min-height:120px}.caption{padding:17px 20px 20px}.label{font-size:12px;color:var(--muted);display:flex;gap:10px;flex-wrap:wrap}.caption h3{font-size:18px;line-height:1.35;margin:8px 0}.caption p{font-size:14px;color:var(--muted)}.actions{display:flex;gap:9px;flex-wrap:wrap;margin-top:14px}.counter{color:var(--muted);font-size:13px;margin-bottom:22px}.empty{padding:30px;border:1px dashed var(--line);border-radius:14px}.technical{border-top:1px solid var(--line);padding-top:17px;color:var(--muted);font-size:13px}.technical summary{cursor:pointer;color:var(--ink)}.technical code{font-size:12px}.technical a{color:var(--accent)}footer{font-size:13px;color:var(--muted);padding-top:0}dialog{border:1px solid var(--line);background:var(--panel);color:var(--ink);border-radius:14px;padding:14px;max-width:96vw;max-height:96vh}dialog::backdrop{background:#071421c9}dialog img{display:block;max-width:calc(96vw - 28px);max-height:calc(96vh - 90px);object-fit:contain}dialog .dialog-tools{display:flex;justify-content:space-between;gap:20px;align-items:center;margin-bottom:10px}dialog h2{font-size:16px;margin:0}@media(max-width:650px){header,main,footer{padding:20px}.toolbar{padding:12px 20px}.caption{padding:14px}.grid{gap:15px}}
</style></head>
<body data-theme="light">
<header><div class="eyebrow">NEXACL · FIREFLY · 本轮改动</div><h1>实际界面，离线预览</h1>
<p class="intro">浅色与深色的 Splash、主窗口、资源列表与详情，以及外观设置。截图来自实际原生 UI；动画来自真实画面采样，点击后播放一次。</p>
<p class="notice">资源记录、追加失败与重试响应为受控演示数据；它们展示真实 UI 行为，不代表实时资源站返回或下载成功。</p></header>
<nav class="toolbar" aria-label="预览筛选"><div class="toolbar-inner"><div class="switches" id="themes"><span>预览主题</span><button data-theme="light" aria-pressed="true">浅色</button><button data-theme="dark" aria-pressed="false">深色</button><button data-theme="both" aria-pressed="false">对照</button></div><div class="switches" id="categories"><button data-category="all" aria-pressed="true">全部</button><button data-category="splash" aria-pressed="false">Splash</button><button data-category="main" aria-pressed="false">主窗口</button><button data-category="resources" aria-pressed="false">资源列表与详情</button><button data-category="settings" aria-pressed="false">设置</button></div></div></nav>
<main><div class="counter" id="counter" aria-live="polite"></div><div id="gallery"></div>
<details class="technical"><summary>来源与采集前提</summary><p>Linux X11 / 软件渲染。原生窗口画面按实际坐标采集；资源和设置使用实际产品 PXML、控制器与 Backend，并配合受控数据或临时本地设置。</p><p>专用 Xvfb 没有桌面窗口管理器。主窗使用真实 core focus 请求；无托盘宿主时关闭 isolated fixture 的托盘设置。真实像素可复核，物理合成器层叠与 WM 激活行为仍需平台验证。</p><p>GIF 有限播放，缩放或调色仅用于编码；没有使用静态截图合成动效。本文件包含全部图片数据，不加载 CDN 或网络资源。</p><p id="capture-summary"></p><div class="downloads"><button id="download-html">下载本页 HTML</button><a class="download" href="__ZIP_NAME__" download title="素材 ZIP 随目录单独提供">下载素材 ZIP</a><a class="download" href="__CATALOG_URL__" download="preview-catalog.json">下载素材清单</a></div></details></main>
<footer>主题按钮只切换预览内容。每张图均可放大或单独下载；动画支持点击重播。ZIP 仅包含本轮实际素材与采集说明。</footer>
<dialog id="zoom"><div class="dialog-tools"><h2 id="zoom-title"></h2><button id="close-zoom">关闭</button></div><img id="zoom-image" alt=""></dialog>
<script type="application/json" id="preview-data">__DATA__</script>
<script>
'use strict';
const data=JSON.parse(document.getElementById('preview-data').textContent), byPath=new Map(data.assets.map(a=>[a.path,a]));
const originalHtml='<!doctype html>\n'+document.documentElement.outerHTML;
const state={theme:'light',category:'all'}, activeUrls=new Set();
const labels={light:'浅色',dark:'深色'}, categories={splash:'Splash',main:'主窗口',resources:'资源列表与详情',settings:'设置 · 外观'};
const notes={splash:'两种主题的加载卡片与一次性入场。',main:'正式窗口、420ms N 轨迹入场，以及使用当前图标的关闭过程。',resources:'九个实际界面状态与完整操作录制。记录和失败响应均为受控演示数据。',settings:'外观页顶部、中段、底部与保存后的主题切换。切换截图的最终主题在说明中标明。'};
function text(tag,value,cls){const e=document.createElement(tag);e.textContent=value;if(cls)e.className=cls;return e;}
function blobFor(asset){const encoded=asset.url.slice(asset.url.indexOf(',')+1),raw=atob(encoded),bytes=new Uint8Array(raw.length);for(let i=0;i<raw.length;i++)bytes[i]=raw.charCodeAt(i);return new Blob([bytes],{type:asset.mime});}
function showZoom(asset){const image=document.getElementById('zoom-image');image.src=asset.url;image.alt=asset.title;document.getElementById('zoom-title').textContent=asset.title+' · '+labels[asset.theme];document.getElementById('zoom').showModal();}
function card(asset){const e=document.createElement('article');e.className='card';e.dataset.path=asset.path;const preview=document.createElement('button');preview.className='preview';preview.setAttribute('aria-label','放大 '+asset.title);const image=document.createElement('img');image.alt=asset.title+' · '+labels[asset.theme];image.loading='lazy';image.src=asset.poster&&byPath.has(asset.poster)?byPath.get(asset.poster).url:asset.url;preview.append(image);preview.addEventListener('click',()=>showZoom(asset));e.append(preview);const caption=document.createElement('div');caption.className='caption';const dimensions=asset.width+' × '+asset.height;caption.append(text('div',labels[asset.theme]+' · '+(asset.mime==='image/gif'?'GIF · '+asset.frames+' 帧 · '+(asset.duration_ms/1000).toFixed(2)+'s · 单次播放':'PNG')+' · '+dimensions,'label'));caption.append(text('h3',asset.title));caption.append(text('p',asset.caption));const actions=document.createElement('div');actions.className='actions';if(asset.mime==='image/gif'){const play=text('button','播放一次');let url=null;play.addEventListener('click',()=>{if(url){activeUrls.delete(url);URL.revokeObjectURL(url);}url=URL.createObjectURL(blobFor(asset));activeUrls.add(url);image.src=url;play.textContent='重播';});actions.append(play);}const download=text('a',asset.mime==='image/gif'?'下载 GIF':'下载 PNG','download');download.href=asset.url;download.download=asset.filename;actions.append(download);caption.append(actions);e.append(caption);return e;}
function render(){for(const url of activeUrls)URL.revokeObjectURL(url);activeUrls.clear();document.body.dataset.theme=state.theme==='dark'?'dark':'light';for(const b of document.querySelectorAll('#themes button'))b.setAttribute('aria-pressed',String(b.dataset.theme===state.theme));for(const b of document.querySelectorAll('#categories button'))b.setAttribute('aria-pressed',String(b.dataset.category===state.category));const gallery=document.getElementById('gallery');gallery.replaceChildren();const shown=data.assets.filter(a=>(state.theme==='both'||a.theme===state.theme)&&(state.category==='all'||a.category===state.category));document.getElementById('counter').textContent='显示 '+shown.length+' 份实际素材 · 共 '+data.assets.length+' 份 · 可离线查看';for(const [category,label]of Object.entries(categories)){const assets=shown.filter(a=>a.category===category);if(!assets.length)continue;const section=document.createElement('section');section.className='category';section.append(text('h2',label));section.append(text('p',notes[category],'section-note'));const grid=document.createElement('div');grid.className='grid';for(const asset of assets)grid.append(card(asset));section.append(grid);gallery.append(section);}if(!shown.length)gallery.append(text('p','此分类暂未生成素材。','empty'));}
document.getElementById('themes').addEventListener('click',e=>{const button=e.target.closest('button');if(button){state.theme=button.dataset.theme;render();}});
document.getElementById('categories').addEventListener('click',e=>{const button=e.target.closest('button');if(button){state.category=button.dataset.category;render();}});
document.getElementById('close-zoom').addEventListener('click',()=>document.getElementById('zoom').close());
document.getElementById('zoom').addEventListener('close',()=>document.getElementById('zoom-image').removeAttribute('src'));
document.getElementById('download-html').addEventListener('click',()=>{const url=URL.createObjectURL(new Blob([originalHtml],{type:'text/html;charset=utf-8'}));const a=document.createElement('a');a.href=url;a.download='nexacl-previews.html';a.click();setTimeout(()=>URL.revokeObjectURL(url),3000);});
document.getElementById('capture-summary').textContent='生成时间：'+data.generatedUtc+' · 所有素材 SHA-256 和有限播放检查保存在清单中。';
render();
</script></body></html>
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', required=True, type=Path, help='Root containing native/, resources-light/dark/ and settings/')
    parser.add_argument('--html', type=Path, help='HTML output (default: DIRECTORY/index.html)')
    parser.add_argument('--zip', type=Path, help='ZIP output (default: DIRECTORY/previews.zip)')
    parser.add_argument('--allow-missing', action='store_true', help='Generate an explicitly partial gallery while captures are still in progress')
    args = parser.parse_args()
    directory = args.directory.resolve()
    if not directory.is_dir():
        parser.error(f'Artifact directory does not exist: {directory}')
    html_path = (args.html or directory / 'index.html').resolve()
    zip_path = (args.zip or directory / 'previews.zip').resolve()
    assets, original_files, missing = [], [], []
    for item in specs():
        path = local_file(directory, item['path'])
        if not path.is_file():
            missing.append(item['path'])
            continue
        content = path.read_bytes()
        gif = path.suffix == '.gif'
        if gif:
            info = gif_info(content)
            if info['loop'] is not None or info['frames'] <= 1:
                raise ValueError(f'GIF must contain multiple frames and play once without a loop extension: {item["path"]}')
        else:
            if content[:8] != b'\x89PNG\r\n\x1a\n' or content[12:16] != b'IHDR':
                raise ValueError(f'Expected actual PNG asset: {item["path"]}')
            width, height = struct.unpack_from('>II', content, 16)
            info = {'width': width, 'height': height}
        mime = 'image/gif' if gif else 'image/png'
        assets.append(dict(item, **info, mime=mime, filename=path.name,
                           bytes=len(content), sha256=hashlib.sha256(content).hexdigest(),
                           url=f'data:{mime};base64,' + base64.b64encode(content).decode('ascii')))
        original_files.append((item['path'], content))
    if missing and not args.allow_missing:
        parser.error('Capture incomplete; missing required assets: ' + ', '.join(missing))
    utc = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
    catalog = {'version': 1, 'generatedUtc': utc, 'scope': 'Current neon/startup/resource-continuous-list/appearance-settings changes',
               'capture': 'Actual native X11 pixels; controlled fixture resource data and temporary settings',
               'complete': not missing, 'missing': missing,
               'assets': [{k: v for k, v in item.items() if k != 'url'} for item in assets]}
    data = json.dumps(dict(catalog, assets=assets), ensure_ascii=False, separators=(',', ':')).replace('<', r'\u003c')
    catalog_bytes = (json.dumps(catalog, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
    catalog_url = 'data:application/json;base64,' + base64.b64encode(catalog_bytes).decode('ascii')
    page = HTML.replace('__DATA__', data).replace('__ZIP_NAME__', html.escape(zip_path.name, quote=True)).replace('__CATALOG_URL__', catalog_url)
    html_path.parent.mkdir(parents=True, exist_ok=True)
    html_path.write_text(page, encoding='utf-8')
    (directory / 'preview-catalog.json').write_bytes(catalog_bytes)
    package_readme = ('# NexaCL 本轮实际界面预览\n\n打开 index.html 可离线浏览、切换主题、重播有限 GIF、下载单项图片。\n'
        '范围：Splash、主窗口、资源连续列表与详情、外观设置。资源记录和失败响应为受控演示数据；设置使用临时本地文件。\n'
        '全部图片来自实际 X11 原生 UI；GIF 来自真实采样，只有一次播放，不以静态图伪造。\n'
        'Xvfb/software/private DBus/no-WM 的前提见各目录 metadata 和验证说明。\n'
        'previews.zip 只包含 allowlist 的本轮实际图片、离线 HTML、素材清单和采集说明；不包含 fixtures、原始帧、录像、日志或探索失败目录。\n').encode('utf-8')
    zip_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zip_path, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=7) as archive:
        archive.writestr('index.html', page.encode('utf-8'))
        archive.writestr('preview-catalog.json', catalog_bytes)
        archive.writestr('README.md', package_readme)
        for name, content in original_files:
            archive.writestr(name, content)
        for relative in metadata_paths():
            path = local_file(directory, relative)
            if path.is_file():
                archive.writestr(relative, path.read_bytes())
    print(f'Built {html_path} ({html_path.stat().st_size:,} bytes); {len(assets)} actual assets, complete={not missing}')
    print(f'Built {zip_path} ({zip_path.stat().st_size:,} bytes); allowlisted images and capture metadata only')


if __name__ == '__main__':
    main()
