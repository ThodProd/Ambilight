'use strict';
const CalibrationWizard=(()=>{
 const steps=[
  {kind:'first',title:'Где находится первый LED?',help:'Сейчас выбран LED №1 у входа DIN. Нажмите на контуре монитора там, где он светится.'},
  {kind:'tl',title:'Верхний левый угол',point:{x:0,y:0}},
  {kind:'tr',title:'Верхний правый угол',point:{x:1,y:0}},
  {kind:'br',title:'Нижний правый угол',point:{x:1,y:1}},
  {kind:'bl',title:'Нижний левый угол',point:{x:0,y:1}},
  {kind:'last',title:'Где заканчивается лента?',help:'Сейчас выбран последний LED. Нажмите на контуре там, где он светится. Это учитывает зазор между началом и концом ленты.'}
 ];
 let active=false,step=0,count=0,index=0,anchors=[],map=null,ack=-1,selection=0,run=0,queue=Promise.resolve(),queued=0,timer=null,busy=false,keyboardT=0;
 const el=id=>document.getElementById(id);
 function issue(e){el('cal-error').hidden=false;el('cal-error').textContent=e.message||String(e);}
 function clearIssue(){el('cal-error').hidden=true;el('cal-error').textContent='';}
 function action(fn){return async(...args)=>{try{await fn(...args);}catch(e){issue(e);}};}
 function enqueue(method,params=null){queued++;const task=queue.catch(()=>{}).then(()=>call(method,params));queue=task;return task.finally(()=>queued--);}
 function at(t){t=(t%4+4)%4;return t<1?{x:t,y:0}:t<2?{x:1,y:t-1}:t<3?{x:3-t,y:1}:{x:0,y:4-t};}
 function svgElement(tag,attrs,parent){const node=document.createElementNS('http://www.w3.org/2000/svg',tag);for(const[k,v]of Object.entries(attrs))node.setAttribute(k,v);parent.append(node);return node;}
 function draw(){
  const svg=el('cal-visual').querySelector('svg');if(!svg)return;
  const dots=svg.querySelector('.leds'),marks=svg.querySelector('.selection');dots.replaceChildren();marks.replaceChildren();
  const points=step===6?map?.points:anchors.filter(Boolean);
  for(const p of points||[])svgElement('circle',{cx:83+p.x*474,cy:65+p.y*270,r:step===6?3:5,fill:'var(--muted)'},dots);
  const target=step===6?map?.points[index]:steps[step]?.point;
  if(target)svgElement('circle',{cx:83+target.x*474,cy:65+target.y*270,r:14,fill:'var(--selected)',stroke:'var(--accent)','stroke-width':2},marks);
  if(step===6&&target)svgElement('circle',{cx:83+target.x*474,cy:65+target.y*270,r:4,fill:'var(--accent)'},marks);
  svg.querySelector('.monitor-text').textContent=`LED ${index+1}`;
  svg.querySelector('.monitor-label').textContent=step===6?'Проверьте совпадение со схемой':'Найдите его на настоящей ленте';
 }
 function render(){
  el('cal-intro').hidden=active||!el('cal-count-step').hidden;
  el('cal-work').hidden=!active;
  el('cal-saved-count').textContent=`Сейчас сохранено: ${settings.points.length} LED`;
  if(!active)return;
  const fixed=step===0||step===5;
  el('cal-progress').textContent=step===6?'ПРОВЕРКА СХЕМЫ':`ТОЧКА ${step+1} ИЗ 6`;
  el('cal-step-title').textContent=step===6?'Проверьте движение по ленте':steps[step].title;
  el('cal-instruction').textContent=step===6?'Переключайте LED стрелками. Светящийся LED на настоящей ленте должен совпадать с выделенным местом на схеме. Затем сохраните результат.':steps[step].help||'Стрелками или ползунком найдите LED, ближайший к этому углу. Затем нажмите выделенный угол на схеме или кнопку подтверждения ниже.';
  el('cal-led-number').textContent=`LED ${index+1} из ${count}`;
  el('cal-led-slider').max=count;el('cal-led-slider').value=index+1;el('cal-led-slider').disabled=fixed||busy;
  el('cal-led-prev').disabled=fixed||index===0||busy;el('cal-led-next').disabled=fixed||index===count-1||busy;
  el('cal-mark-corner').hidden=step<1||step>4;el('cal-mark-corner').disabled=ack!==index||busy;
  el('cal-save').hidden=step!==6;el('cal-save').disabled=busy||ack!==index;
  el('cal-back').disabled=busy;el('cal-number-help').hidden=fixed;
  el('cal-review-note').hidden=step!==6;
  if(map)el('cal-review-note').textContent=`В схеме ${count} LED. Направление определено по углам: ${map.clockwise?'по часовой стрелке':'против часовой стрелки'}, если смотреть спереди. Между отмеченными точками LED распределены автоматически.`;
  draw();
 }
 async function sendSelected(){
  if(!active)return;const wanted=index,version=selection,id=run;
  await enqueue('calibration.select',{index:wanted});
  if(active&&id===run&&version===selection){ack=wanted;render();}
 }
 function choose(value){
  if(!active||busy||step===0||step===5)return;
  index=Math.max(0,Math.min(count-1,value));ack=-1;selection++;clearTimeout(timer);render();
  timer=setTimeout(()=>{timer=null;action(sendSelected)();},60);
 }
 async function stage(next){
  clearIssue();step=next;map=next===6?map:null;
  if(next===0)index=0;else if(next===5)index=count-1;else if(next<6&&anchors[next])index=anchors[next].index;
  ack=-1;selection++;keyboardT=0;render();await sendSelected();
 }
 async function accept(point){
  if(!active||busy||step===6)return;
  if(ack!==index){issue(new Error('Дождитесь переключения выбранного LED.'));return;}
  if(step>=1&&step<=4){
   const other=anchors.find((a,i)=>i>=1&&i<=4&&i!==step&&a?.index===index);
   if(other){issue(new Error(`LED ${index+1} уже отмечен у другого угла. Выберите LED для текущего угла.`));return;}
  }
  anchors[step]={kind:steps[step].kind,index,x:point.x,y:point.y};
  if(step<5){await stage(step+1);return;}
  busy=true;render();
  try{const id=run;const result=await enqueue('calibration.preview',{anchors});if(!active||id!==run)return;map=result;step=6;index=0;ack=-1;selection++;await sendSelected();}
  finally{busy=false;render();}
 }
 async function clicked(event){
  if(!active||step===6)return;
  const svg=el('cal-visual').querySelector('svg'),pt=svg.createSVGPoint();pt.x=event.clientX;pt.y=event.clientY;
  const pos=pt.matrixTransform(svg.getScreenCTM().inverse());let x=Math.max(0,Math.min(1,(pos.x-83)/474)),y=Math.max(0,Math.min(1,(pos.y-65)/270));
  if(steps[step].point){const p=steps[step].point;if(Math.hypot((x-p.x)*474,(y-p.y)*270)>65){issue(new Error('Нажмите выделенный угол или кнопку «Этот LED у выделенного угла».'));return;}await accept(p);return;}
  for(const p of [{x:0,y:0},{x:1,y:0},{x:1,y:1},{x:0,y:1}])if(Math.hypot((x-p.x)*474,(y-p.y)*270)<14){await accept(p);return;}
  const d=[y*270,(1-x)*474,(1-y)*270,x*474],edge=d.indexOf(Math.min(...d));if(edge===0)y=0;if(edge===1)x=1;if(edge===2)y=1;if(edge===3)x=0;
  await accept({x,y});
 }
 async function cancel(){
  run++;clearTimeout(timer);timer=null;const wasActive=active;active=false;busy=false;selection++;anchors=[];map=null;el('cal-count-step').hidden=true;clearIssue();render();
  if(wasActive)await enqueue('calibration.cancel');
 }
 function enter(){render();}
 function init(){
  el('cal-visual').innerHTML=monitorSvg(true);const svg=el('cal-visual').querySelector('svg');svg.setAttribute('tabindex','0');svg.setAttribute('role','application');svg.setAttribute('aria-label','Контур монитора. Нажмите положение LED. С клавиатуры: стрелки двигают метку, Enter отмечает точку.');
  svg.onclick=action(clicked);
  svg.onkeydown=action(async event=>{
   if(!active||step===6)return;
   if(event.key==='Enter'){event.preventDefault();await accept(steps[step].point||at(keyboardT));}
   else if(['ArrowRight','ArrowDown','ArrowLeft','ArrowUp'].includes(event.key)){event.preventDefault();keyboardT+=(['ArrowRight','ArrowDown'].includes(event.key)?1:-1)*.02;const p=steps[step].point||at(keyboardT);draw();svgElement('circle',{cx:83+p.x*474,cy:65+p.y*270,r:9,fill:'none',stroke:'var(--accent)','stroke-width':2},svg.querySelector('.selection'));}
  });
  el('cal-start').onclick=()=>{clearIssue();el('cal-result').textContent='';el('cal-count').value='';el('cal-count-step').hidden=false;el('cal-begin').textContent=settings.power?'Начать калибровку':'Включить и начать калибровку';render();setTimeout(()=>el('cal-count').focus(),0);};
  el('cal-count-cancel').onclick=action(cancel);el('cal-cancel').onclick=action(cancel);
  el('cal-begin').onclick=action(async()=>{
   if(!el('cal-count').reportValidity())return;
   count=Number(el('cal-count').value);if(!Number.isInteger(count)||count<4||count>360)throw new Error('Укажите целое количество от 4 до 360.');
   if(!state?.connected)throw new Error('ESP32 не подключена. Сначала откройте «Подключение» и дождитесь состояния «ESP32 подключена по Wi-Fi».');
   const id=++run;el('cal-begin').disabled=true;
   try{
    await saveNow();if(id!==run)return;
    if(!settings.power){settings.power=true;dirty=true;editGeneration++;await saveNow();renderSettings();}
    if(id!==run)return;active=true;anchors=[];map=null;step=0;index=0;ack=-1;selection++;
    await enqueue('calibration.begin',{count});if(!active||id!==run)return;
    el('cal-count-step').hidden=true;clearIssue();render();await sendSelected();
   }catch(e){active=false;render();throw e;}finally{el('cal-begin').disabled=false;}
  });
  el('cal-led-prev').onclick=()=>choose(index-1);el('cal-led-next').onclick=()=>choose(index+1);el('cal-led-slider').oninput=()=>choose(Number(el('cal-led-slider').value)-1);
  el('cal-mark-corner').onclick=action(()=>accept(steps[step].point));
  el('cal-back').onclick=action(async()=>{if(step===0){const old=count;await cancel();el('cal-count').value=old;el('cal-count-step').hidden=false;render();}else await stage(step-1);});
  el('cal-save').onclick=action(async()=>{
   if(!active||step!==6||busy)return;busy=true;clearTimeout(timer);timer=null;render();
   try{const result=await enqueue('calibration.finish',{anchors});active=false;run++;settings=result.settings;revision=result.revision;dirty=false;buildLeds();renderSettings();el('cal-result').textContent=`Сохранено: ${settings.points.length} LED. Обычная подсветка восстановлена.`;}
   finally{busy=false;render();}
  });
  render();
 }
 async function heartbeat(){if(!active||busy||queued||timer)return;try{await sendSelected();}catch(e){await cancel();issue(e);}}
 function update(s){
  if(!active){el('cal-saved-count').textContent=`Сейчас сохранено: ${settings.points.length} LED`;return;}
  el('cal-device-note').textContent=!settings.power?'Подсветка выключена. Включите питание справа вверху, чтобы увидеть выбранный LED.':s.connected?'ESP32 на связи. Найдите выбранный LED на реальной ленте. Без подключённой ленты физическую позицию проверить нельзя.':'ESP32 не подключена: команды на ленту сейчас не передаются. Отметки на схеме пока нельзя подтвердить физически.';
 }
 return{init,enter,leave:cancel,heartbeat,update};
})();
