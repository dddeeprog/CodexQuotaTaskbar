import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// Keep the vendored MIT engine as the oracle, not as a production browser dependency.
const root = new URL('../src/CodexQuotaTaskbar.Host/Assets/Companion/', import.meta.url);
const source = {window:{},console};
vm.runInNewContext(fs.readFileSync(new URL('grok-ball.js', root),'utf8'), source);
const native = JSON.parse(fs.readFileSync(new URL('poses.json',root),'utf8'));
const definitions = source.window.EmotionBall.config.list();
const plain = value => JSON.parse(JSON.stringify(value));
const body = ['x','y','scale','rotate','breathe','ribbons','confetti','sketch','zzz','orbit','yaw'];
const eyes = ['x','y','scaleX','scaleY','rotate','open','lookX','lookY'];
const pose = p => [...body.map(k=>p.body[k]||0),...eyes.map(k=>p.left[k]||0),...eyes.map(k=>p.right[k]||0)];

test('all 32 native definitions match every upstream pose, pool, keyframe and animation',()=>{
  assert.equal(native.emotions.length,32);
  for (const upstream of definitions) {
    const expected={...upstream,base:pose(upstream.base),sequence:upstream.sequence?{
      ...upstream.sequence,frames:upstream.sequence.frames.map(f=>({at:f.at,pose:pose(f.pose)}))
    }:null,raw:undefined,en:undefined};
    assert.deepEqual(native.emotions.find(d=>d.id===upstream.id),plain(expected));
  }
});
test('all 25 eye contours and the original body contour are retained exactly',()=>{
  assert.deepEqual(native.expressions,plain(source.window.EB_RINGS.EXPRESSIONS));
  assert.deepEqual(native.head,plain(source.window.EB_RINGS.SHAPES.blob.ring));
});
test('native catalog fits in 60 KiB and does not contain bitmap frames',()=>{
  assert.ok(fs.statSync(new URL('poses.json',root)).size<60*1024);
  assert.ok(!JSON.stringify(native).includes('data:image'));
});
test('only native data and license are embedded; companion never constructs WebView2',()=>{
  const project=fs.readFileSync(new URL('../src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj',import.meta.url),'utf8');
  assert.match(project,/Companion\\poses.json/);
  assert.ok(!project.includes('Companion\\*'));
  for(const name of ['CompanionBallControl.cs','CompanionBallVisual.cs','CompanionPoseEngine.cs']){
    const content=fs.readFileSync(new URL('../src/CodexQuotaTaskbar.Host/UI/'+name,import.meta.url),'utf8');
    assert.ok(!/new\s+WebView2|CoreWebView2Environment|PostWebMessage|CompanionWebView2/.test(content));
  }
});
