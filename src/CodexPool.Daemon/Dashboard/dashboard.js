'use strict';
const $ = id => document.getElementById(id);
let csrf = '', state = null, busy = false;
const states = {active:'使用中',standby:'待命',paused:'已暂停',draining:'Drain',disabled:'已禁用',cooldown:'冷却中'};
const reasons = {'manual':'手动切换','5h_threshold':'达到 5h 阈值','weekly_threshold':'达到 Weekly 阈值',cooldown:'额度 / 认证冷却',disabled:'账户已禁用',paused:'账户已暂停',draining:'账户 Drain',auth_unavailable:'授权不可用',safe_precommit_failover:'请求发送前安全切换',no_active_account:'初始选择'};
const text = (tag, value, cls) => { const el = document.createElement(tag); el.textContent = value; if(cls) el.className=cls; return el; };
function notice(message) { $('notice').textContent=message; $('notice').hidden=!message; }
function reset(at) { const minutes=Math.max(0,Math.ceil((Date.parse(at)-Date.now())/60000)); return minutes>=1440?`${Math.floor(minutes/1440)}d ${Math.floor(minutes%1440/60)}h`:minutes>=60?`${Math.floor(minutes/60)}h ${minutes%60}m`:`${minutes}m`; }
async function api(path, data) { const response=await fetch(path,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-Codex-Pool-CSRF':csrf},body:data===undefined?undefined:JSON.stringify(data)}); if(!response.ok){let code='操作未完成';try{code=(await response.json()).error?.code||code;}catch{}throw new Error(code);} return response.json(); }
async function action(path, data={}) { if(busy)return; busy=true; try{await api(path,data);notice('已应用，下一个模型请求使用更新后的账户设置。');render(await api('/status'));}catch(e){notice(e.message); if(state)render(state);}finally{busy=false;} }
function render(s) {
  state=s; const names=new Map(s.accounts.map(a=>[a.id,a.name])); $('active').textContent=names.get(s.activeAccountId)||'未选择';$('automatic').textContent=s.automaticSwitching?'已开启':'已关闭';$('total').textContent=s.accounts.length;$('inflight').textContent=s.inFlightRequests.reduce((n,a)=>n+a.value,0);
  $('gate').textContent=s.boundaryProbe?'当前为迁移验收模式。':'自动切换的真实账户验收尚未完成。';
  const form=$('settings'); if(!form.contains(document.activeElement)){form.elements.autoSwitch.checked=s.settings.autoSwitch;form.elements.fiveHourThreshold.value=s.settings.fiveHourThreshold;form.elements.weeklyThreshold.value=s.settings.weeklyThreshold;}
  form.elements.autoSwitch.disabled=!s.boundaryProbe;
  $('accounts').replaceChildren(...s.accounts.map(a=>{
    const card=text('article','','card'+(a.active?' selected':'')),head=text('div','','account-head'),radio=document.createElement('input'); radio.type='radio';radio.name='active-account';radio.checked=a.active;radio.setAttribute('aria-label',`选择 ${a.name}`);radio.disabled=!a.enabled||a.paused||a.drain||a.state==='cooldown'||a.auth==='reauth_required'||a.auth==='refresh_pending';radio.addEventListener('change',()=>action(`/accounts/${a.id}/activate`));
    head.append(radio,text('strong',a.name),text('span',states[a.state]||a.state,'badge'));card.append(head);const meta=text('div','','meta');meta.append(text('span',a.plan||'Plan 待确认'),text('span',`授权：${a.auth}`));card.append(meta);
    const usage=s.usage.find(u=>u.accountId===a.id);for(const [kind,label] of [['5h','5h'],['weekly','Weekly']]){const row=text('div','','quota'),w=usage?.windows?.find(w=>w.kind===kind),qhead=text('div','','quota-head');qhead.append(text('span',label),text('b',w?`${w.usedPercent.toFixed(0)}% 已用`:'待确认'));row.append(qhead);if(w){const bar=document.createElement('progress');bar.max=100;bar.value=w.usedPercent;bar.setAttribute('aria-label',`${label}已用 ${w.usedPercent}%`);row.append(bar,text('small',`剩余 ${w.remainingPercent.toFixed(0)}% · ${reset(w.resetAt)} 后重置`));}card.append(row);}
    card.append(text('small',usage?.updatedAt?`更新于 ${new Date(usage.updatedAt).toLocaleTimeString()}${Date.now()-Date.parse(usage.updatedAt)>120000?' · 数据已过期':''}`:'额度来源待验证'));
    const controls=text('div','','controls');for(const [op,label] of [[a.paused?'resume':'pause',a.paused?'恢复':'暂停'],[a.drain?'undrain':'drain',a.drain?'取消 Drain':'Drain'],[a.enabled?'disable':'enable',a.enabled?'禁用':'启用']]){const button=text('button',label);button.onclick=()=>action(`/accounts/${a.id}/${op}`);controls.append(button);}card.append(controls);return card;}));
  $('switches').replaceChildren(...s.recentSwitches.slice().reverse().map(x=>text('div',`${new Date(x.at).toLocaleTimeString()} · ${names.get(x.from)||'—'} → ${names.get(x.to)||'—'} · ${reasons[x.reason]||x.reason}`,'switch')));if(!s.recentSwitches.length)$('switches').append(text('p','暂无切换记录'));
  $('requests').replaceChildren(...s.recentRequests.slice().reverse().map(x=>{const tr=document.createElement('tr');for(const value of [new Date(x.timestamp).toLocaleTimeString(),names.get(x.account)||'—',x.model,`${x.durationMs} ms`,`${x.status} · ${x.outcome}`,x.attempts])tr.append(text('td',value));return tr;}));
  $('logins').replaceChildren(...s.nativeLoginAttempts.map(x=>text('p',x.state==='completed'?'账户已添加':x.state==='waiting_for_browser'?'等待浏览器授权…':x.state)));
}
$('settings').onsubmit=e=>{e.preventDefault();const f=e.target;action('/settings',{autoSwitch:f.elements.autoSwitch.checked,fiveHourThreshold:Number(f.elements.fiveHourThreshold.value),weeklyThreshold:Number(f.elements.weeklyThreshold.value)});};
$('login').onsubmit=e=>{e.preventDefault();action('/accounts/native-login',{name:e.target.elements.name.value});};
$('refresh').onclick=async()=>{try{await api('/usage');render(await api('/status'));notice('额度已更新。');}catch(e){notice(e.message);}};
async function start(){try{csrf=(await api('/browser-session')).csrf;$('workspace').hidden=false;render(await api('/status'));const events=new EventSource('/events');events.addEventListener('status',e=>{render(JSON.parse(e.data));$('connection').textContent='服务在线';});events.onerror=()=>{$('connection').textContent='正在重新连接';};}catch{$('locked').hidden=false;$('connection').textContent='等待安全会话';}}
start();setInterval(()=>{if(state)render(state);},30000);
