'use strict';
const $ = id => document.getElementById(id);
let csrf = '', state = null, busy = false, lastActiveId = null, switchReady = false;
let statisticsPeriod = 'today';
const number = value => Number(value).toLocaleString('zh-CN');
const tokenValue = (totals, field) => !totals || (totals.requests > 0 && totals.reportedRequests === 0) ? '—' : number(totals[field]);
const detailValue = (totals, field, coverage) => !totals || (totals.requests > 0 && totals[coverage] === 0) ? '—' : number(totals[field]);
const durationLabel = seconds => !Number.isInteger(seconds)||seconds<=0?'额度窗口':seconds===604800?'周':seconds%86400===0?`${seconds/86400}d`:seconds%3600===0?`${seconds/3600}h`:seconds%60===0?`${seconds/60}m`:`${seconds}s`;
const windowLabel = w => `${w.limitId&&w.limitId!=='codex'?(w.limitName||'附加额度')+' · ':''}${durationLabel(w.windowSeconds??(w.kind==='5h'?18000:w.kind==='weekly'?604800:null))}`;
const states = {active:'使用中',standby:'待命',paused:'已暂停',draining:'Drain',disabled:'已禁用',cooldown:'冷却中'};
const availabilityReasons={quota_threshold:'达到额度窗口切换阈值',quota_exhausted:'额度耗尽',auth_unavailable:'需要重新授权',access_unavailable:'套餐不包含模型权限',auth_temporarily_unavailable:'授权暂时不可用',configuration_unavailable:'账户配置不可用','5h_threshold':'达到 5h 切换阈值',weekly_threshold:'达到周切换阈值',cooldown:'冷却中',disabled:'已禁用',paused:'已暂停',draining:'排空中'};
const reasons = {quota_threshold:'达到额度窗口阈值','manual':'手动切换','5h_threshold':'达到 5h 阈值','weekly_threshold':'达到 Weekly 阈值',cooldown:'额度 / 认证冷却',disabled:'账户已禁用',paused:'账户已暂停',draining:'账户 Drain',auth_unavailable:'授权不可用',safe_precommit_failover:'请求发送前安全切换',quota_rejected:'明确额度拒绝',auth_rejected:'明确授权拒绝',no_active_account:'初始选择'};
const text = (tag, value, cls) => { const el = document.createElement(tag); el.textContent = value; if(cls) el.className=cls; return el; };
function notice(message) { $('notice').textContent=message; $('notice').hidden=!message; }
function reset(at) { const minutes=Math.max(0,Math.ceil((Date.parse(at)-Date.now())/60000)); return minutes>=1440?`${Math.floor(minutes/1440)}d ${Math.floor(minutes%1440/60)}h`:minutes>=60?`${Math.floor(minutes/60)}h ${minutes%60}m`:`${minutes}m`; }
function remainingTone(remaining){ if(remaining==null||!Number.isFinite(remaining)) return ''; if(remaining<15) return 'danger'; if(remaining<30) return 'warn'; return 'ok'; }
async function api(path, data) { const response=await fetch(path,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-Codex-Pool-CSRF':csrf},body:data===undefined?undefined:JSON.stringify(data)}); if(!response.ok){let code='操作未完成';try{code=(await response.json()).error?.code||code;}catch{}throw new Error(code);} return response.json(); }
async function action(path, data={}, success='已应用，下一个模型请求使用更新后的账户设置。') { if(busy)return; busy=true; try{await api(path,data);notice(success);render(await api('/status'));}catch(e){notice(e.message); if(state)render(state);}finally{busy=false;} }
function notifySwitch(s){
  const id=s.activeAccountId||null;
  const name=(s.accounts.find(a=>a.id===id)||{}).name;
  if(!switchReady){ lastActiveId=id; switchReady=true; return; }
  if(!id || id===lastActiveId) return;
  lastActiveId=id;
  notice(`已切换到 ${name||'账户'}`);
}
function renderStatistics(stats, names) {
  for (const button of $('statistics-periods').querySelectorAll('button')) button.setAttribute('aria-pressed', String(button.dataset.period===statisticsPeriod));
  if(!stats){$('statistics-note').textContent='当前代理版本尚未提供 Token 统计。';return;}
  const period=stats[statisticsPeriod], totals=period.totals;
  const persistence=stats.persistence==='unavailable'?' · 历史文件无法读取，当前统计仅在内存中':stats.persistence==='pending'?' · 等待保存':'';
  $('statistics-note').textContent=`从 ${new Date(stats.startedAt).toLocaleString()} 开始记录 · ${stats.timeZone} · 保留 ${stats.retainedDays} 天日汇总${persistence}`;
  const cards=[['总 Token',tokenValue(totals,'totalTokens'),`${number(totals.reportedRequests)} / ${number(totals.requests)} 个请求已报告`],
    ['输入',tokenValue(totals,'inputTokens'),'包含缓存 Token'],['输出',tokenValue(totals,'outputTokens'),'包含推理 Token'],
    ['缓存输入',detailValue(totals,'cachedInputTokens','cachedReportedRequests'),`${number(totals.cachedReportedRequests)} 个请求有明细`],
    ['推理输出',detailValue(totals,'reasoningOutputTokens','reasoningReportedRequests'),`${number(totals.reasoningReportedRequests)} 个请求有明细`],
    ['未报告',number(totals.unreportedRequests),'个请求缺少有效 usage']];
  $('token-summary').replaceChildren(...cards.map(([label,value,note])=>{const card=text('div','','token-metric');card.append(text('small',label),text('strong',value),text('small',note));return card;}));
  const rows=(items,label)=>items.map(item=>{const tr=document.createElement('tr'),t=item.totals;for(const value of [label(item),tokenValue(t,'totalTokens'),tokenValue(t,'inputTokens'),tokenValue(t,'outputTokens'),`${number(t.reportedRequests)} / ${number(t.requests)}`])tr.append(text('td',value));return tr;});
  const empty=()=>{const tr=document.createElement('tr'),cell=text('td','暂无请求');cell.colSpan=5;tr.append(cell);return tr;};
  $('token-accounts').replaceChildren(...(period.accounts.length?rows(period.accounts,x=>names.get(x.accountId)||'已移除账户'): [empty()]));
  $('token-models').replaceChildren(...(period.models.length?rows(period.models,x=>x.model): [empty()]));
  const maximum=Math.max(1,...stats.daily.map(x=>Number(x.totals.totalTokens)));
  $('token-daily').replaceChildren(...stats.daily.map(day=>{const row=text('div','','token-day'),value=tokenValue(day.totals,'totalTokens');
    const bar=document.createElement('progress');bar.max=maximum;bar.value=day.totals.totalTokens;bar.setAttribute('aria-label',`${day.day} 已报告 ${value} Token`);
    row.title=`${day.day} · ${value} Token · ${day.totals.reportedRequests} / ${day.totals.requests} 个请求已报告`;
    row.append(text('small',day.day.slice(5)),bar,text('span',value));return row;}));
}
function render(s) {
  state=s; notifySwitch(s); const names=new Map(s.accounts.map(a=>[a.id,a.name])); $('active').textContent=names.get(s.activeAccountId)||'未选择';$('automatic').textContent=s.automaticSwitching?'已开启':'已关闭';$('total').textContent=s.accounts.length;$('inflight').textContent=s.inFlightRequests.reduce((n,a)=>n+a.value,0);
  $('gate').textContent=s.boundaryProbe?'当前为迁移验收模式。':'自动切换的真实账户验收尚未完成。';
  const form=$('settings'); if(!form.contains(document.activeElement)){form.elements.autoSwitch.checked=s.settings.autoSwitch;form.elements.windowThreshold.value=s.settings.windowThreshold??Math.min(s.settings.fiveHourThreshold,s.settings.weeklyThreshold);}
  form.elements.autoSwitch.disabled=!s.boundaryProbe;
  renderStatistics(s.tokenStatistics,names);
  $('accounts').replaceChildren(...s.accounts.map(a=>{
    const card=text('article','','card'+(a.active?' selected':'')),head=text('div','','account-head');
    const canActivate=a.enabled&&!a.paused&&!a.drain&&a.state!=='cooldown'&&a.auth!=='reauth_required'&&a.auth!=='refresh_pending';
    if(a.active)card.append(text('span','✓','check-mark'));
    head.append(text('strong',a.name),text('span',states[a.state]||a.state,'badge'));card.append(head);const meta=text('div','','meta');meta.append(text('span',a.plan||'Plan 待确认'),text('span',`授权：${a.auth}`));card.append(meta);
    if(canActivate&&!a.active){card.classList.add('selectable');card.addEventListener('click',e=>{if(e.target.closest('.controls')||e.target.closest('button'))return;action(`/accounts/${a.id}/activate`);});}
    const access=s.accountAvailability?.find(x=>x.accountId===a.id);if(access?.reason)card.append(text('small',`模型访问：${availabilityReasons[access.reason]||'暂不可用'}`));
    const usage=s.usage.find(u=>u.accountId===a.id),fresh=usage?.updatedAt&&Date.now()-Date.parse(usage.updatedAt)<=120000&&Date.parse(usage.updatedAt)<=Date.now()+30000;
    for(const w of usage?.windows||[]){const label=windowLabel(w),row=text('div','','quota'),qhead=text('div','','quota-head');const valid=fresh&&w.resetAt&&Date.parse(w.resetAt)>Date.now(),remaining=valid?Number(w.remainingPercent):null;const tone=remainingTone(remaining);qhead.append(text('span',label),text('b',valid?`剩 ${remaining.toFixed(0)}%`:'待确认'));row.append(qhead);if(valid){const bar=document.createElement('progress');bar.max=100;bar.value=Math.max(0,Math.min(100,remaining));bar.className=tone?`quota-${tone}`:'';bar.setAttribute('aria-label',`${label}剩余 ${remaining.toFixed(0)}%`);row.append(bar,text('small',`${reset(w.resetAt)} 后重置`));}else row.append(text('small',!fresh?'额度数据已过期':!w.resetAt?'重置时间未知':'等待重置后的新额度'));card.append(row);}
    if(!usage?.windows?.length)card.append(text('div',fresh&&usage.windowState==='not_reported'?'上游未提供固定额度窗口':'额度窗口未知','account-tokens'));
    if(usage?.credits){const c=usage.credits;card.append(text('div',`${!fresh?'历史 ':''}Credits：${c.unlimited?'无额度上限':c.balance!=null?number(c.balance):c.hasCredits?'可用':'不可用'}`,'account-tokens'));}
    if(fresh&&usage?.spendControlReached)card.append(text('small','已达到账户支出限制'));
    card.append(text('small',usage?.updatedAt?`更新于 ${new Date(usage.updatedAt).toLocaleTimeString()}${Date.now()-Date.parse(usage.updatedAt)>120000?' · 数据已过期':''}`:'额度来源待验证'));
    const accountTokens=s.tokenStatistics?.today?.accounts.find(x=>x.accountId===a.id)?.totals;
    if(s.tokenStatistics)card.append(text('div',`今日 ${accountTokens?tokenValue(accountTokens,'totalTokens'):'0'} Token · ${accountTokens?.requests||0} 个请求`,'account-tokens'));
    const controls=text('div','','controls');for(const [op,label] of [[a.paused?'resume':'pause',a.paused?'恢复':'暂停'],[a.drain?'undrain':'drain',a.drain?'取消 Drain':'Drain'],[a.enabled?'disable':'enable',a.enabled?'禁用':'启用']]){const button=text('button',label);button.onclick=()=>action(`/accounts/${a.id}/${op}`);controls.append(button);}
    const rename=text('button','改名');rename.onclick=()=>{if(busy)return;const name=prompt('输入新的账户名称（1–80 字符）',a.name);if(name===null)return;if(!name.trim()||name.trim().length>80||/[\u0000-\u001f\u007f-\u009f]/u.test(name)){notice('名称需为 1–80 字符，不能包含控制字符。');return;}action(`/accounts/${a.id}/rename`,{name:name.trim()},'账户名称已更新。');};controls.append(rename);
    const del=text('button','删除','danger'); del.onclick=()=>{ if(!confirm(`确定永久删除账户「${a.name}」？\n凭据与控制状态将从本机 Vault 移除，且不可恢复。`)) return; action(`/accounts/${a.id}/delete`); }; controls.append(del);
    card.append(controls);return card;}));
  $('switches').replaceChildren(...s.recentSwitches.slice().reverse().map(x=>text('div',`${new Date(x.at).toLocaleTimeString()} · ${names.get(x.from)||'—'} → ${names.get(x.to)||'—'} · ${reasons[x.reason]||x.reason}`,'switch')));if(!s.recentSwitches.length)$('switches').append(text('p','暂无切换记录'));
  $('requests').replaceChildren(...s.recentRequests.slice().reverse().map(x=>{const tr=document.createElement('tr'),u=x.tokenUsage;for(const value of [new Date(x.timestamp).toLocaleTimeString(),names.get(x.account)||'—',x.model,`${x.durationMs} ms`,`${x.status} · ${x.outcome}`,x.attempts,u?`${number(u.inputTokens)} / ${number(u.outputTokens)}`:'未报告',u?`${u.cachedInputTokens==null?'—':number(u.cachedInputTokens)} / ${u.reasoningOutputTokens==null?'—':number(u.reasoningOutputTokens)}`:'—'])tr.append(text('td',value));return tr;}));
  $('logins').replaceChildren(...s.nativeLoginAttempts.map(x=>text('p',x.state==='completed'?'账户已添加':x.state==='waiting_for_browser'?'等待浏览器授权…':x.state)));
}
$('settings').onsubmit=e=>{e.preventDefault();const f=e.target;action('/settings',{autoSwitch:f.elements.autoSwitch.checked,windowThreshold:Number(f.elements.windowThreshold.value)});};
$('login').onsubmit=e=>{e.preventDefault();action('/accounts/native-login',{name:e.target.elements.name.value});};
$('refresh').onclick=async()=>{try{await api('/usage');render(await api('/status'));notice('额度已更新');}catch(e){notice(e.message);}};
for(const button of $('statistics-periods').querySelectorAll('button'))button.onclick=()=>{statisticsPeriod=button.dataset.period;if(state)renderStatistics(state.tokenStatistics,new Map(state.accounts.map(a=>[a.id,a.name])));};
async function start(){try{csrf=(await api('/browser-session')).csrf;$('workspace').hidden=false;render(await api('/status'));const events=new EventSource('/events');events.addEventListener('status',e=>{render(JSON.parse(e.data));$('connection').textContent='服务在线';});events.onerror=()=>{$('connection').textContent='正在重新连接';};}catch{$('locked').hidden=false;$('connection').textContent='等待安全会话';}}
start();setInterval(()=>{if(state)render(state);},30000);
