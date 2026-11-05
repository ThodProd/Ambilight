'use strict';
const $=id=>document.getElementById(id), $$=s=>[...document.querySelectorAll(s)];
const theme=new URLSearchParams(location.search).get('theme');
document.documentElement.dataset.theme=theme|| (matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light');
const call=(method,params=null)=>thodHub.call(`worker.${method}`,params);
const names={screen:'За экраном',solid:'Один цвет',night:'Ночной свет',day:'Дневной свет',breath:'Дыхание',rainbow:'Перелив'};
let settings=null,state=null,page='light',revision=-1,busy=false,saveTimer=null,saveQueue=Promise.resolve(),dirty=false,editGeneration=0,monitorKey='',lastColors=[],pollErrors=0,pairingOpen=false,regionPinned=false,regionVisible=false,regionTimer=null,regionSendTimer=null,regionTouch=0;
function error(e){$('error').hidden=false;$('error').querySelector('span').textContent=e.message||String(e);}
function safe(fn){return async (...args)=>{try{await fn(...args);}catch(e){error(e);}};}
function text(id,value){$(id).textContent=value;}
async function setPage(next){const leavingSettings=page==='settings'&&next!=='settings';if(page==='calibration'&&next!==page)await CalibrationWizard.leave();page=next;$$('[data-view]').forEach(e=>e.hidden=e.dataset.view!==next);$$('[data-page]').forEach(e=>e.classList.toggle('active',e.dataset.page===next));if(leavingSettings)hideRegionNow();if(next==='connection'&&!state?.connected&&!state?.scanning)await scan();if(next==='calibration')CalibrationWizard.enter();}
$$('[data-page]').forEach(b=>b.onclick=safe(()=>setPage(b.dataset.page)));$$('[data-goto]').forEach(b=>b.onclick=safe(()=>setPage(b.dataset.goto)));$('dismiss-error').onclick=()=>{$('error').hidden=true;};
function monitorSvg(cal=false){return `<svg viewBox="0 0 640 420" role="img" aria-label="Монитор и расположение ленты"><defs><linearGradient id="screen-fill${cal?'-cal':''}" x2="1" y2="1"><stop stop-color="var(--screen)"/><stop offset="1" stop-color="var(--screen2)"/></linearGradient></defs><path class="monitor-stand" d="M302 330h36l5 31h31v7H266v-7h31z"/><rect class="monitor-frame" x="83" y="65" width="474" height="270" rx="13"/><rect x="92" y="74" width="456" height="252" rx="6" fill="url(#screen-fill${cal?'-cal':''})"/><path d="M92 245Q210 130 340 228T548 191V326H92Z" fill="var(--bg)" opacity=".18"/><path d="M92 280Q245 195 355 255T548 241V326H92Z" fill="var(--bg)" opacity=".12"/><text class="monitor-text" x="320" y="187" font-size="22">${cal?'Калибровка':'Ambilight'}</text><text class="monitor-label" x="320" y="212">${cal?'Вид спереди':'Цвета продолжаются за экраном'}</text><g class="leds"></g><g class="selection"></g>${cal?'<rect x="68" y="50" width="504" height="300" fill="transparent" class="click-surface"/>':''}</svg>`;}
function buildLeds(){if(!settings)return;const g=$('main-visual').querySelector('.leds');g.replaceChildren();settings.points.forEach((p,i)=>{const dot=document.createElementNS('http://www.w3.org/2000/svg','circle');dot.setAttribute('cx',83+p.x*474);dot.setAttribute('cy',65+p.y*270);dot.setAttribute('r',settings.points.length>180?2.4:3.4);dot.setAttribute('class','led');dot.setAttribute('fill',p.skip?'var(--line)':'var(--muted)');dot.dataset.index=i;g.append(dot);});text('led-total',`В схеме: ${settings.points.length} LED`);paintLeds(lastColors);}
function paintLeds(colors){lastColors=colors||[];if(!settings?.preview)return;$('main-visual').querySelectorAll('.led').forEach((d,i)=>{const p=settings.points[i],base=i*3;d.setAttribute('fill',p.skip?'var(--line)':`rgb(${lastColors[base]||0},${lastColors[base+1]||0},${lastColors[base+2]||0})`);});}
function renderMonitors(monitors){const key=JSON.stringify(monitors);if(key!==monitorKey){monitorKey=key;$('monitor').replaceChildren();for(const m of monitors){const o=document.createElement('option');o.value=m.id;o.textContent=m.name;$('monitor').append(o);}}document.querySelector('.monitor-picker').hidden=monitors.length<2&&(!settings.monitor||monitors.some(m=>m.id===settings.monitor));$('monitor').value=settings.monitor||monitors.find(m=>m.primary)?.id||monitors[0]?.id||'';}
function renderSettings(){if(!settings)return;const s=settings;$('power').setAttribute('aria-pressed',s.power);$('power').querySelector('span').textContent=s.power?'Подсветка включена':'Подсветка выключена';$('preview').checked=s.preview;$('main-visual').hidden=!s.preview;$('preview-off').hidden=s.preview;
 const brightness=state?.mode==='night'?s.nightBrightness:s.brightness;$('brightness').value=brightness;text('brightness-value',`${brightness}%`);
 $$('[data-mode]').forEach(b=>b.classList.toggle('selected',b.dataset.mode===s.mode));$('color-row').hidden=!['solid','breath'].includes(s.mode);$('color').value=s.color;
 const ids={'night-brightness':'nightBrightness','day-at':'dayAt','night-at':'nightAt',fps:'fps',gamma:'gamma',red:'red',green:'green',blue:'blue'};for(const [id,k]of Object.entries(ids))if(document.activeElement!==$(id))$(id).value=s[k];
 for(const [id,k]of Object.entries({schedule:'schedule','black-bars':'blackBars'}))$(id).checked=s[k];
 $('smoothing').value=Math.round(s.smoothing*100);$('depth').value=Math.round(s.depth*100);text('smoothing-value',`${Math.round(s.smoothing*100)}%`);text('depth-value',`${Math.round(s.depth*100)}%`);text('night-value',`${s.nightBrightness}%`);text('schedule-note',s.schedule?`Ночной свет ${s.nightAt}–${s.dayAt}`:'Расписание выключено');
 if(!pairingOpen&&document.activeElement!==$('device-ip'))$('device-ip').value=s.deviceIp;
}
function scheduleSave(){dirty=true;editGeneration++;text('save-state','Сохранение…');clearTimeout(saveTimer);saveTimer=setTimeout(()=>safe(saveNow)(),250);}
function saveNow(){clearTimeout(saveTimer);const snapshot=structuredClone(settings),generation=editGeneration;saveQueue=saveQueue.catch(()=>{}).then(async()=>{const result=await call('settings',snapshot);revision=result.revision;if(generation===editGeneration){dirty=false;settings=result.settings;text('save-state','Сохранено');}}).catch(e=>{text('save-state','Не сохранено');throw e;});return saveQueue;}
function change(key,value){settings[key]=value;renderSettings();scheduleSave();}
function regionLabel(){const b=$('show-region');b.setAttribute('aria-pressed',regionPinned);b.textContent=regionPinned?'Скрыть область захвата':'Показать область захвата';}
async function sendRegion(){if(!settings)return;try{await call('overlay',{enabled:regionVisible,depth:settings.depth,monitor:settings.monitor||''});}catch(e){regionVisible=false;regionPinned=false;regionLabel();throw e;}}
function showRegion(){if(!settings)return;clearTimeout(regionTimer);regionVisible=true;regionTouch=Date.now();clearTimeout(regionSendTimer);regionSendTimer=setTimeout(()=>safe(sendRegion)(),30);}
function hideRegionSoon(delay=1200){if(regionPinned)return;clearTimeout(regionTimer);regionTimer=setTimeout(()=>{regionVisible=false;safe(sendRegion)();},delay);}
function hideRegionNow(){clearTimeout(regionTimer);clearTimeout(regionSendTimer);regionPinned=false;regionVisible=false;regionLabel();if(settings)safe(sendRegion)();}
$('power').onclick=()=>change('power',!settings.power);$('preview').onchange=safe(async()=>{change('preview',$('preview').checked);await call('preview',{enabled:settings.preview&&page==='light'});});
$('brightness').oninput=()=>change(state?.mode==='night'?'nightBrightness':'brightness',+$('brightness').value);
$$('[data-mode]').forEach(b=>b.onclick=()=>change('mode',b.dataset.mode));$('color').oninput=()=>change('color',$('color').value);$('monitor').onchange=()=>{change('monitor',$('monitor').value);if(regionVisible)showRegion();};
for(const [id,key,scale] of [['night-brightness','nightBrightness',1],['smoothing','smoothing',.01]])$(id).oninput=()=>change(key,+$(id).value*scale);
$('depth').oninput=()=>{change('depth',+$('depth').value/100);showRegion();};
$('depth').onchange=()=>hideRegionSoon();
$('depth').onpointerup=()=>hideRegionSoon();
$('depth').onkeyup=()=>hideRegionSoon();
$('depth').onblur=()=>hideRegionSoon(300);
$('show-region').onclick=safe(async()=>{regionPinned=!regionPinned;regionLabel();if(regionPinned){showRegion();}else{clearTimeout(regionTimer);clearTimeout(regionSendTimer);regionVisible=false;await sendRegion();}});
document.addEventListener('visibilitychange',()=>{if(document.hidden&&regionVisible){regionPinned=false;regionVisible=false;regionLabel();safe(sendRegion)();}});
addEventListener('pagehide',()=>{call('overlay',{enabled:false}).catch(()=>{});});
for(const [id,key]of Object.entries({schedule:'schedule','black-bars':'blackBars'}))$(id).onchange=()=>change(key,$(id).checked);
for(const [id,key]of Object.entries({'day-at':'dayAt','night-at':'nightAt'}))$(id).onchange=()=>change(key,$(id).value);
for(const id of ['fps','gamma','red','green','blue'])$(id).onchange=()=>{if($(id).checkValidity())change(id,+$(id).value);else $(id).reportValidity();};
$('autostart').onchange=safe(async()=>{const wanted=$('autostart').checked;try{$('autostart').checked=await call('autostart',{enabled:wanted});}catch(e){$('autostart').checked=!wanted;throw e;}});
async function scan(){await call('scan');if(state){state={...state,scanning:true};renderConnection(state);}}
$('scan').onclick=safe(scan);
$('show-pairing').onclick=safe(async()=>{pairingOpen=true;$('pair-code').value='';text('pairing-result','');renderConnection(state);await scan();});
$('cancel-pairing').onclick=()=>{pairingOpen=false;$('pair-code').value='';text('pairing-result','');renderConnection(state);};
$('pair').onclick=safe(async()=>{
 const ip=$('device-ip').value.trim();
 if(state?.connected&&ip===settings.deviceIp){pairingOpen=false;$('pair-code').value='';text('pairing-result','');renderConnection(state);return;}
 $('pair').disabled=true;text('pairing-result','Проверяем код привязки…');
 try{await saveQueue;await call('pair',{ip,code:$('pair-code').value.trim()});$('pair-code').value='';pairingOpen=false;text('pairing-result','Привязка сохранена');$('error').hidden=true;const s=await call('status');renderState(s);}
 catch(e){text('pairing-result',e.message||String(e));}
 finally{$('pair').disabled=false;}
});
function renderConnection(s){
 if(!s)return;
 const hasBinding=s.paired&&!!s.settings.deviceId,formVisible=pairingOpen||!hasBinding;
 $('pairing-panel').hidden=!formVisible;$('show-pairing').hidden=!hasBinding||pairingOpen;$('cancel-pairing').hidden=!hasBinding||!pairingOpen;
 $('network-visual').classList.toggle('scanning',s.scanning);$('scan').disabled=s.scanning;$('scan').textContent=s.scanning?'Поиск…':s.connected?'Найти другие устройства':'Найти устройства';
 $('connected-device').hidden=!hasBinding;text('device-details',hasBinding?`ID платы: ${s.settings.deviceId}. Это идентификатор, а не код привязки.`:'');
 if(s.connected){text('scan-title','ESP32 подключена по Wi-Fi');text('scan-description',`Локальный адрес: ${s.settings.deviceIp}. Привязка сохранена, повторно вводить код не нужно.`);}
 else if(hasBinding){text('scan-title','Ожидаем сохранённую ESP32');text('scan-description',s.deviceError||'Привязка сохранена. Проверьте питание платы и Wi-Fi — соединение восстановится автоматически.');}
 else{text('scan-title',s.scanning?'Ищем ESP32':s.devices.length?'Выберите вашу ESP32':'Устройства пока не найдены');text('scan-description',s.discoveryError||'Компьютер и ESP32 должны быть подключены к одному роутеру. Сначала выберите устройство, затем введите одноразовый код настройки.');}
 const devices=formVisible?s.devices.filter(d=>!(s.connected&&d.id===s.settings.deviceId)):[];
 const key=JSON.stringify(devices);if($('devices').dataset.key!==key){$('devices').dataset.key=key;$('devices').replaceChildren();for(const d of devices){const b=document.createElement('button');b.className='device-button';const name=document.createElement('span');name.textContent=`ESP32 · ID ${d.id.slice(-6)}`;const address=document.createElement('small');address.textContent=`Адрес: ${d.ip}`;b.append(name,address);b.onclick=()=>{$('device-ip').value=d.ip;$('pair-code').focus();};$('devices').append(b);}}
}
function renderState(s){state=s;renderMonitors(s.monitors);if(!dirty&&s.revision!==revision){settings=s.settings;revision=s.revision;buildLeds();renderSettings();}text('mode-caption',(names[s.mode]||s.mode).toUpperCase());text('frame-info',`${s.fps} кадр/с`);text('capture-status',s.captureError||(!settings.power?'Свет выключен':s.mode==='screen'?'Цвета с выбранного монитора':'Сценарий работает'));document.querySelector('.status-dot').classList.toggle('online',!s.captureError&&settings.power);text('connection-note',s.connected?`ESP32 подключена · ${settings.deviceIp}`:'Виртуальная лента · ESP32 не подключена');$('nav-dot').classList.toggle('online',s.connected);$('footer-status').replaceChildren();const dot=document.createElement('i');dot.className=`dot ${s.connected?'online':''}`;$('footer-status').append(dot,document.createTextNode(s.connected?'ESP32 на связи':s.paired?'Переподключение к ESP32':'Виртуальная лента'));
 renderConnection(s);
 if(!dirty){const bright=s.mode==='night'?settings.nightBrightness:settings.brightness;$('brightness').value=bright;text('brightness-value',`${bright}%`);}if(s.colors?.length)paintLeds(s.colors);CalibrationWizard.update(s);
}
async function poll(){if(busy||!settings)return;busy=true;try{await call('preview',{enabled:!document.hidden&&(page==='calibration'||settings.preview&&page==='light')});if(page==='calibration'&&!document.hidden)await CalibrationWizard.heartbeat();const s=await call('status');renderState(s);if(regionPinned&&regionVisible&&Date.now()-regionTouch>4000){regionTouch=Date.now();safe(sendRegion)();}pollErrors=0;}catch(e){if(++pollErrors===1)error(new Error('Связь с рабочим процессом потеряна. '+e.message));}finally{busy=false;setTimeout(poll,document.hidden?1500:150);}}
async function start(){const initial=await call('init');settings=initial.settings;$('autostart').checked=initial.autostart;text('version',initial.version);regionLabel();$('main-visual').innerHTML=monitorSvg();CalibrationWizard.init();buildLeds();renderSettings();renderMonitors(initial.monitors);if(initial.configError)error(new Error(initial.configError));
 await call('scan');poll();}
safe(start)();
