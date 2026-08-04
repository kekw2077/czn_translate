"""The control-panel page (HTML + CSS + JS) as one string, imported by panel.py.

Kept separate so panel.py stays about behaviour and this stays about presentation. Dark,
orange-accented Nexus-style theme; vanilla JS, no external assets (the panel serves nothing but
this and a handful of JSON endpoints).
"""

PAGE = r"""<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>CZN Translator — Панель</title>
<style>
  :root{
    --bg:#0f1620; --panel:#18212e; --panel2:#1f2a39; --tile:#212d3d;
    --border:#2b3a4d; --border2:#35485e;
    --accent:#f0a04b; --accent-h:#f6b268; --accent-dim:#c9853c;
    --text:#e7ecf3; --muted:#8b98ab; --faint:#637185;
    --good:#5cc98a; --warn:#f0a04b; --bad:#ff6b6b;
  }
  *{box-sizing:border-box}
  html,body{margin:0;height:100%}
  body{font:14px/1.55 "Segoe UI",system-ui,sans-serif;background:var(--bg);color:var(--text);display:flex}
  a{color:var(--accent)}

  /* sidebar */
  aside{width:224px;flex:none;background:linear-gradient(180deg,#141c27,#111823);border-right:1px solid var(--border);
        display:flex;flex-direction:column;height:100vh;position:sticky;top:0}
  .brand{padding:18px 18px 14px;border-bottom:1px solid var(--border);display:flex;align-items:center;gap:10px}
  .brand .logo{width:30px;height:30px;border-radius:7px;background:linear-gradient(135deg,var(--accent),#e0742a);
        display:grid;place-items:center;color:#1a1206;font-weight:800;font-size:16px}
  .brand b{font-size:15px;letter-spacing:.2px}
  .brand small{display:block;color:var(--muted);font-size:11px;font-weight:400}
  nav{padding:10px 8px;display:flex;flex-direction:column;gap:2px}
  nav button{all:unset;display:flex;align-items:center;gap:10px;padding:9px 12px;border-radius:8px;cursor:pointer;
        color:var(--muted);font-weight:600;font-size:13px}
  nav button .ic{width:18px;text-align:center;opacity:.9}
  nav button:hover{background:var(--panel2);color:var(--text)}
  nav button.active{background:rgba(240,160,75,.14);color:var(--accent)}
  nav .badge{margin-left:auto;background:var(--accent);color:#20160a;border-radius:10px;padding:0 7px;font-size:11px;font-weight:700}
  .sidefoot{margin-top:auto;padding:12px 16px;border-top:1px solid var(--border);color:var(--faint);font-size:11px}
  .dot{display:inline-block;width:8px;height:8px;border-radius:50%;background:var(--bad);margin-right:6px;vertical-align:middle}
  .dot.on{background:var(--good)}

  /* main */
  main{flex:1;min-width:0;height:100vh;overflow:auto;padding:26px 30px 60px}
  .head{margin:0 0 18px}
  .head h1{margin:0;font-size:20px;font-weight:700}
  .head p{margin:4px 0 0;color:var(--muted);font-size:13px}
  section{display:none;max-width:940px}
  section.show{display:block;animation:fade .18s ease}
  @keyframes fade{from{opacity:0;transform:translateY(4px)}to{opacity:1;transform:none}}

  .card{background:var(--panel);border:1px solid var(--border);border-radius:12px;padding:18px;margin-bottom:16px}
  .card h2{margin:0 0 4px;font-size:15px}
  .card .sub{color:var(--muted);font-size:12.5px;margin:0 0 14px}

  .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}
  .tile{background:var(--tile);border:1px solid var(--border);border-radius:10px;padding:14px 16px}
  .tile .n{font-size:24px;font-weight:800;letter-spacing:.3px}
  .tile .l{color:var(--muted);font-size:12px;margin-top:2px}
  .tile.accent .n{color:var(--accent)}
  .tile.good .n{color:var(--good)}

  .bar{height:10px;background:var(--panel2);border-radius:6px;overflow:hidden;border:1px solid var(--border)}
  .bar > i{display:block;height:100%;width:0;background:linear-gradient(90deg,var(--accent-dim),var(--accent));transition:width .4s}
  .barwrap{margin-top:14px}
  .barwrap .lab{display:flex;justify-content:space-between;color:var(--muted);font-size:12px;margin-bottom:6px}

  label.f{display:block;margin:0 0 12px}
  label.f > span{display:block;color:var(--muted);font-size:12px;margin-bottom:5px}
  input[type=text],input[type=password],input[type=number],select,textarea{
    width:100%;background:#0e151f;color:var(--text);border:1px solid var(--border2);border-radius:8px;
    padding:9px 11px;font:inherit;outline:none}
  input:focus,select:focus,textarea:focus{border-color:var(--accent);box-shadow:0 0 0 3px rgba(240,160,75,.12)}
  textarea{min-height:52px;resize:vertical}
  .row2{display:flex;gap:12px;flex-wrap:wrap}
  .row2 > *{flex:1;min-width:160px}

  .seg{display:inline-flex;background:var(--panel2);border:1px solid var(--border);border-radius:9px;padding:3px;gap:3px}
  .seg button{all:unset;padding:7px 14px;border-radius:7px;cursor:pointer;color:var(--muted);font-weight:600;font-size:13px}
  .seg button.on{background:var(--accent);color:#20160a}

  button.btn{all:unset;display:inline-flex;align-items:center;gap:8px;background:var(--accent);color:#20160a;
    font-weight:700;padding:10px 18px;border-radius:9px;cursor:pointer;font-size:13.5px}
  button.btn:hover{background:var(--accent-h)}
  button.btn.ghost{background:transparent;color:var(--text);border:1px solid var(--border2)}
  button.btn.ghost:hover{border-color:var(--accent);color:var(--accent)}
  button.btn:disabled{opacity:.45;cursor:not-allowed}
  button.btn.sm{padding:7px 12px;font-size:12.5px}
  .actions{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-top:6px}

  .pill{font-size:11.5px;padding:2px 9px;border-radius:20px;font-weight:700}
  .pill.set{background:rgba(92,201,138,.16);color:var(--good)}
  .pill.unset{background:rgba(255,107,107,.15);color:var(--bad)}
  .hint{color:var(--muted);font-size:12px;margin-top:8px}
  .toast{position:fixed;right:20px;bottom:20px;background:var(--panel2);border:1px solid var(--border2);
    border-left:3px solid var(--accent);border-radius:9px;padding:12px 16px;max-width:340px;opacity:0;
    transform:translateY(8px);transition:.25s;pointer-events:none}
  .toast.show{opacity:1;transform:none}
  .toast.err{border-left-color:var(--bad)}

  .console{background:#0b1119;border:1px solid var(--border);border-radius:9px;padding:12px 14px;margin-top:14px;
    font:12px/1.6 "Cascadia Code",Consolas,monospace;color:#b7c4d4;max-height:260px;overflow:auto;white-space:pre-wrap}
  .console .cmd{color:var(--accent)}

  .rev{border:1px solid var(--border);border-radius:10px;padding:12px 14px;margin-bottom:10px;background:var(--panel2)}
  .rev .k{color:var(--faint);font-size:11.5px;font-family:"Cascadia Code",Consolas,monospace}
  .rev .en{color:#c3cedd;margin:5px 0 8px;white-space:pre-wrap}
  .rev .prob{color:var(--warn);font-size:12px;margin-top:7px}
  .pager{display:flex;gap:10px;align-items:center;justify-content:center;margin-top:6px;color:var(--muted)}
  .empty{color:var(--muted);text-align:center;padding:34px 0}
</style>
</head>
<body>
<aside>
  <div class="brand">
    <div class="logo">CZ</div>
    <div><b>CZN Translator</b><small>Панель управления</small></div>
  </div>
  <nav>
    <button data-s="dash" class="active"><span class="ic">▤</span>Обзор</button>
    <button data-s="key"><span class="ic">🔑</span>Ключ API</button>
    <button data-s="translate"><span class="ic">⚙</span>Перевод</button>
    <button data-s="review"><span class="ic">✓</span>Ревью<span class="badge" id="navBadge" style="display:none">0</span></button>
    <button data-s="update"><span class="ic">⟳</span>Обновление</button>
  </nav>
  <div class="sidefoot"><span class="dot" id="keyDot"></span><span id="keyFoot">ключ не задан</span></div>
</aside>

<main>
  <!-- DASHBOARD -->
  <section id="dash" class="show">
    <div class="head"><h1>Обзор базы</h1><p>Состояние czn.db и покрытие переводом.</p></div>
    <div class="card">
      <div class="tiles" id="tiles"></div>
      <div class="barwrap">
        <div class="lab"><span>Покрытие переводом</span><span id="covPct">—</span></div>
        <div class="bar"><i id="covBar"></i></div>
      </div>
    </div>
  </section>

  <!-- KEY -->
  <section id="key">
    <div class="head"><h1>Ключ API</h1><p>Ключ хранится локально в tools/.env и не попадает в git.</p></div>
    <div class="card">
      <h2>Провайдер и ключ</h2>
      <p class="sub">Anthropic (Claude) — лучшее качество для игрового текста. OpenAI-совместимый — как альтернатива.</p>
      <label class="f"><span>Провайдер</span>
        <div class="seg" id="keyProv">
          <button data-p="anthropic" class="on">Anthropic <span class="pill unset" id="pill-anthropic">нет</span></button>
          <button data-p="openai">OpenAI <span class="pill unset" id="pill-openai">нет</span></button>
        </div>
      </label>
      <label class="f"><span>API-ключ</span>
        <input type="password" id="keyInput" placeholder="sk-ant-…  (оставьте пустым, чтобы не менять)" autocomplete="off">
      </label>
      <div class="actions">
        <button class="btn" id="keySave">Сохранить ключ</button>
        <span class="hint" id="keyHint"></span>
      </div>
    </div>
  </section>

  <!-- TRANSLATE -->
  <section id="translate">
    <div class="head"><h1>Перевод</h1><p>Пакетный EN→RU. Дубли схлопываются, память переводов переиспользуется.</p></div>
    <div class="card">
      <h2>Запуск</h2>
      <p class="sub">Результат пишется как «mt» (машинный) → потом принимается во вкладке «Ревью».</p>
      <div class="row2">
        <label class="f"><span>Провайдер</span>
          <select id="tProv"><option value="anthropic">Anthropic (Claude)</option><option value="openai">OpenAI-совместимый</option></select>
        </label>
        <label class="f"><span>Модель (необязательно)</span>
          <input type="text" id="tModel" placeholder="по умолчанию для провайдера"></label>
        <label class="f"><span>Объём</span>
          <input type="number" id="tLimit" min="0" placeholder="все (или число строк)"></label>
      </div>
      <div class="actions">
        <button class="btn" id="tRun">▶ Перевести</button>
        <button class="btn ghost" id="tStop" disabled>■ Стоп</button>
        <span class="hint" id="tHint"></span>
      </div>
      <div class="barwrap" id="tBarWrap" style="display:none">
        <div class="lab"><span id="tStat">—</span><span id="tPct">0%</span></div>
        <div class="bar"><i id="tBar"></i></div>
      </div>
      <div class="console" id="tLog" style="display:none"></div>
    </div>
  </section>

  <!-- REVIEW -->
  <section id="review">
    <div class="head"><h1>Ревью</h1><p>Очередь машинного перевода. «Принять» делает строку памятью переводов.</p></div>
    <div class="card">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:12px">
        <h2 style="margin:0" id="revTitle">Очередь</h2>
        <button class="btn ghost sm" id="revReload">Обновить</button>
      </div>
      <div id="revList"><div class="empty">Загрузка…</div></div>
      <div class="pager" id="revPager"></div>
    </div>
  </section>

  <!-- UPDATE -->
  <section id="update">
    <div class="head"><h1>Обновление после патча</h1><p>Пере-декодировать data.pack, сравнить с базой, перевести только изменения.</p></div>
    <div class="card">
      <h2>Источник</h2>
      <p class="sub">Новые → в очередь; изменённые → «stale» (старый перевод остаётся запасным); удалённые → сохраняются; ваш ручной перевод не теряется. Потом добейте новые/устаревшие во вкладке «Перевод».</p>
      <label class="f"><span>Путь к data.pack</span><input type="text" id="updPack" placeholder="…/cznlive/data.pack"></label>
      <div class="actions">
        <button class="btn ghost" id="updCheck">Проверить обновление</button>
        <button class="btn" id="updApply">Применить изменения</button>
        <span class="hint" id="updHint">Извлечение занимает ~1 минуту.</span>
      </div>
      <div class="tiles" id="updTiles" style="margin-top:16px;display:none"></div>
      <div class="console" id="updLog" style="display:none"></div>
    </div>
  </section>
</main>

<div class="toast" id="toast"></div>

<script>
const $ = s => document.querySelector(s);
const api = async (m,u,b) => { const r = await fetch(u,{method:m,headers:{'Content-Type':'application/json'},body:b?JSON.stringify(b):undefined});
  const t = await r.text(); let j={}; try{j=t?JSON.parse(t):{}}catch(e){}; if(!r.ok) throw new Error(j.error||t||r.status); return j; };
const fmt = n => (n||0).toLocaleString('ru-RU');
let toastT;
function toast(msg,err){ const el=$('#toast'); el.textContent=msg; el.classList.toggle('err',!!err); el.classList.add('show');
  clearTimeout(toastT); toastT=setTimeout(()=>el.classList.remove('show'),3200); }

// nav
document.querySelectorAll('nav button').forEach(b=>b.onclick=()=>{
  document.querySelectorAll('nav button').forEach(x=>x.classList.remove('active'));
  b.classList.add('active');
  document.querySelectorAll('section').forEach(s=>s.classList.remove('show'));
  $('#'+b.dataset.s).classList.add('show');
  if(b.dataset.s==='review') loadReview(0);
});

// status / dashboard
async function refresh(){
  let s; try{ s = await api('GET','/api/status'); }catch(e){ return; }
  const d=s.db, tiles=[
    ['Всего строк', d.total, ''], ['Переведено', d.translated, 'good'],
    ['В очереди', d.pending, 'accent'], ['На ревью (mt)', d.reviewQueue, ''],
    ['Принято', (d.byStatus.reviewed||0)+(d.byStatus.locked||0), 'good'] ];
  $('#tiles').innerHTML = tiles.map(t=>`<div class="tile ${t[2]}"><div class="n">${fmt(t[1])}</div><div class="l">${t[0]}</div></div>`).join('');
  const pct=Math.round(d.coverage*100); $('#covPct').textContent=pct+'%'; $('#covBar').style.width=pct+'%';
  const badge=$('#navBadge'); if(d.reviewQueue>0){badge.style.display='';badge.textContent=fmt(d.reviewQueue);}else badge.style.display='none';
  for(const p of ['anthropic','openai']){ const on=s.providers[p]; const pill=$('#pill-'+p);
    pill.textContent=on?'есть':'нет'; pill.className='pill '+(on?'set':'unset'); }
  const anyKey=s.providers.anthropic||s.providers.openai;
  $('#keyDot').classList.toggle('on',anyKey); $('#keyFoot').textContent=anyKey?'ключ задан':'ключ не задан';
}

// key
let keyProv='anthropic';
$('#keyProv').querySelectorAll('button').forEach(b=>b.onclick=()=>{
  $('#keyProv').querySelectorAll('button').forEach(x=>x.classList.remove('on')); b.classList.add('on'); keyProv=b.dataset.p; });
$('#keySave').onclick=async()=>{
  const key=$('#keyInput').value.trim(); if(!key){toast('Введите ключ',true);return;}
  try{ await api('POST','/api/key',{provider:keyProv,key}); $('#keyInput').value=''; toast('Ключ сохранён'); refresh(); }
  catch(e){ toast(e.message,true); } };

// translate
let jobPoll;
$('#tRun').onclick=async()=>{
  const provider=$('#tProv').value, model=$('#tModel').value.trim(), limit=$('#tLimit').value.trim();
  try{ await api('POST','/api/translate',{provider,model,limit:limit?Number(limit):0});
    $('#tBarWrap').style.display=''; $('#tLog').style.display=''; $('#tRun').disabled=true; $('#tStop').disabled=false;
    toast('Перевод запущен'); pollJob(); }
  catch(e){ toast(e.message,true); } };
$('#tStop').onclick=async()=>{ try{ await api('POST','/api/job/stop',{});}catch(e){} };
async function pollJob(){
  clearInterval(jobPoll);
  const tick=async()=>{ let j; try{ j=await api('GET','/api/job'); }catch(e){ return; }
    const pct=Math.round((j.progress||0)*100); $('#tBar').style.width=pct+'%'; $('#tPct').textContent=pct+'%';
    $('#tStat').textContent = j.running ? `${fmt(j.done)} / ${fmt(j.pendingAtStart)} · ${Math.round(j.elapsed)}с`
      : (j.returncode===0?'готово':(j.returncode===null?'—':'завершено с кодом '+j.returncode));
    $('#tLog').innerHTML = j.log.map(l=>l.startsWith('$')?`<span class="cmd">${esc(l)}</span>`:esc(l)).join('\n');
    $('#tLog').scrollTop=$('#tLog').scrollHeight;
    if(!j.running){ clearInterval(jobPoll); $('#tRun').disabled=false; $('#tStop').disabled=true; refresh();
      if(j.returncode===0) toast('Перевод завершён'); } };
  await tick(); jobPoll=setInterval(tick,1500);
}
const esc=s=>s.replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));

// review
async function loadReview(offset){
  const box=$('#revList'); box.innerHTML='<div class="empty">Загрузка…</div>';
  let p; try{ p=await api('GET','/api/review?offset='+offset);}catch(e){box.innerHTML='<div class="empty">Ошибка</div>';return;}
  $('#revTitle').textContent=`Очередь — ${fmt(p.total)} строк`;
  if(!p.items.length){ box.innerHTML='<div class="empty">Очередь пуста 🎉</div>'; $('#revPager').innerHTML=''; return; }
  box.innerHTML=p.items.map(it=>`<div class="rev" id="rev-${it.id}">
    <div class="k">#${it.id} ${esc(it.key||'(без ключа)')}</div>
    <div class="en">${esc(it.en)}</div>
    <textarea id="ru-${it.id}">${esc(it.ru)}</textarea>
    ${it.problems.map(pr=>`<div class="prob">⚠ ${esc(pr)}</div>`).join('')}
    <div class="actions"><button class="btn sm" onclick="saveRev(${it.id},'reviewed')">Принять</button>
    <button class="btn ghost sm" onclick="saveRev(${it.id},'locked')">Принять и закрепить</button></div></div>`).join('');
  const prev=p.offset>0?`<button class="btn ghost sm" onclick="loadReview(${Math.max(0,p.offset-p.pageSize)})">← назад</button>`:'';
  const next=p.offset+p.pageSize<p.total?`<button class="btn ghost sm" onclick="loadReview(${p.offset+p.pageSize})">вперёд →</button>`:'';
  $('#revPager').innerHTML=prev+`<span>${p.offset+1}–${Math.min(p.offset+p.pageSize,p.total)} из ${fmt(p.total)}</span>`+next;
}
async function saveRev(id,status){
  const ru=$('#ru-'+id).value;
  try{ await api('POST','/api/review/save',{id,ru,status}); const el=$('#rev-'+id); el.style.display='none'; refresh(); }
  catch(e){ toast(e.message,true); } }
$('#revReload').onclick=()=>loadReview(0);

// update after patch
let updPoll;
function renderUpd(j){
  if(j.defaultPack && !$('#updPack').value) $('#updPack').value=j.defaultPack;
  $('#updLog').innerHTML = j.log.map(l=>l.startsWith('$')?`<span class="cmd">${esc(l)}</span>`:esc(l)).join('\n');
  $('#updLog').scrollTop=$('#updLog').scrollHeight;
  const s=j.summary||{};
  if(Object.keys(s).length){ $('#updTiles').style.display='';
    const map=[['new','Новые','accent'],['changed','Изменены','accent'],['removed','Удалены',''],['unchanged','Без изменений','']];
    $('#updTiles').innerHTML=map.map(m=>`<div class="tile ${m[2]}"><div class="n">${fmt(s[m[0]]||0)}</div><div class="l">${m[1]}</div></div>`).join(''); }
  $('#updHint').textContent = j.running ? ('Идёт: '+j.phase+'…')
    : (j.returncode===0?'Готово.':(j.returncode===null?'Извлечение занимает ~1 минуту.':'Ошибка (код '+j.returncode+')'));
  const busy=j.running||!j.extractorAvailable; $('#updCheck').disabled=busy; $('#updApply').disabled=busy;
  if(!j.extractorAvailable) $('#updHint').textContent='Экстрактор не найден: extracted/scripts/extract_pack.py';
}
function pollUpdate(){ clearInterval(updPoll);
  const tick=async()=>{ let j; try{ j=await api('GET','/api/update/job'); }catch(e){ return; }
    renderUpd(j); if(!j.running){ clearInterval(updPoll); refresh(); } };
  tick(); updPoll=setInterval(tick,1500);
}
$('#updCheck').onclick=async()=>{ try{ await api('POST','/api/update/check',{packPath:$('#updPack').value.trim()});
  $('#updLog').style.display=''; toast('Проверка запущена'); pollUpdate(); }catch(e){ toast(e.message,true); } };
$('#updApply').onclick=async()=>{ if(!confirm('Извлечь из data.pack и применить изменения к базе?')) return;
  try{ await api('POST','/api/update/apply',{packPath:$('#updPack').value.trim()});
    $('#updLog').style.display=''; toast('Применение запущено'); pollUpdate(); }catch(e){ toast(e.message,true); } };
(async()=>{ try{ renderUpd(await api('GET','/api/update/job')); }catch(e){} })();

refresh(); setInterval(refresh,5000);
</script>
</body>
</html>
"""
