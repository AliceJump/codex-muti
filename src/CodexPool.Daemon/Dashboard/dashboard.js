'use strict';
const $ = id => document.getElementById(id);
let csrf = '', state = null, busy = false, lastActiveId = null, switchReady = false;
let statisticsPeriod = 'allTime', historyKey = '', historyRevision = 0, nextBefore = null, historyLoading = false;
let historyItems = [], historyStats = null, historyPage = null;
let historyRange = null;
let filterFrom = '', filterTo = '', presetDay = '';
let settingsEditVersion = 0, settingsSavedVersion = 0;
const number = value => Number(value).toLocaleString('zh-CN');
const tokenValue = (totals, field) => !totals || (totals.requests > 0 && totals.reportedRequests === 0) ? '—' : number(totals[field]);
const detailValue = (totals, field, coverage) => !totals || (totals.requests > 0 && totals[coverage] === 0) ? '—' : number(totals[field]);
const durationLabel = seconds => !Number.isInteger(seconds)||seconds<=0?'额度窗口':seconds===604800?'周':seconds%86400===0?`${seconds/86400}d`:seconds%3600===0?`${seconds/3600}h`:seconds%60===0?`${seconds/60}m`:`${seconds}s`;
const windowLabel = w => `${w.limitId&&w.limitId!=='codex'?(w.limitName||'附加额度')+' · ':''}${durationLabel(w.windowSeconds??(w.kind==='5h'?18000:w.kind==='weekly'?604800:null))}`;
const states = {active:'使用中',standby:'待命',paused:'已暂停',draining:'Drain',disabled:'已禁用',cooldown:'冷却中'};
const availabilityReasons={quota_threshold:'达到额度窗口切换阈值',quota_exhausted:'额度耗尽',auth_unavailable:'需要重新授权',access_unavailable:'套餐不包含模型权限',auth_temporarily_unavailable:'授权暂时不可用',configuration_unavailable:'账户配置不可用','5h_threshold':'达到 5h 切换阈值',weekly_threshold:'达到周切换阈值',cooldown:'冷却中',disabled:'已禁用',paused:'已暂停',draining:'排空中'};
function availabilityLabel(access){
  if(access.reason==='quota_exhausted'&&['usage_limit_reached','quota_exhausted','subscription_sharing_usage_limit_exceeded'].includes(access.errorCode))return '上次模型请求被额度限制';
  if(access.reason==='quota_exhausted'&&['insufficient_quota','credit_balance_exhausted','organization_spend_limit_exceeded','project_spend_limit_exceeded','organization_usage_limit_exceeded'].includes(access.errorCode))return '模型余额或支出额度不足';
  return availabilityReasons[access.reason]||'暂不可用';
}
const reasons = {quota_threshold:'达到额度窗口阈值','manual':'手动切换','5h_threshold':'达到 5h 阈值','weekly_threshold':'达到 Weekly 阈值',cooldown:'额度 / 认证冷却',disabled:'账户已禁用',paused:'账户已暂停',draining:'账户 Drain',auth_unavailable:'授权不可用',safe_precommit_failover:'请求发送前安全切换',quota_rejected:'明确额度拒绝',auth_rejected:'明确授权拒绝',no_active_account:'初始选择'};
const text = (tag, value, cls) => { const el = document.createElement(tag); el.textContent = value; if(cls) el.className=cls; return el; };
const keyed = (el, key) => { el.dataset.key=String(key); return el; };
function setText(id, value) { const el=$(id), next=String(value); if(el.textContent!==next)el.textContent=next; }
// Keep live elements (and their focus, selection and scroll state) in place.
// Detached descriptions provide fresh event handlers; only changed DOM is patched.
function patchElement(current, next) {
  for(const attr of Array.from(current.attributes))if(!next.hasAttribute(attr.name))current.removeAttribute(attr.name);
  for(const attr of next.attributes)if(current.getAttribute(attr.name)!==attr.value)current.setAttribute(attr.name,attr.value);
  current.onclick=next.onclick;
  if(next.children.length){for(const child of Array.from(current.childNodes))if(child.nodeType!==1)child.remove();patchChildren(current,Array.from(next.children));}
  else if(current.children.length||current.textContent!==next.textContent)current.textContent=next.textContent;
}
function patchChildren(parent, children) {
  const existing=new Map(Array.from(parent.children).filter(el=>el.dataset.key!==undefined).map(el=>[el.dataset.key,el]));
  let cursor=parent.firstElementChild;
  for(const next of children){
    const key=next.dataset.key;
    let current=key===undefined?(cursor?.dataset.key===undefined?cursor:null):existing.get(key);
    if(!current || current.tagName!==next.tagName)current=next;
    else patchElement(current,next);
    if(current!==cursor)parent.insertBefore(current,cursor);
    cursor=current.nextElementSibling;
  }
  while(cursor){const stale=cursor;cursor=cursor.nextElementSibling;stale.remove();}
}
function notice(message) { setText('notice',message); $('notice').hidden=!message; }
function reset(at) { const minutes=Math.max(0,Math.ceil((Date.parse(at)-Date.now())/60000)); return minutes>=1440?`${Math.floor(minutes/1440)}d ${Math.floor(minutes%1440/60)}h`:minutes>=60?`${Math.floor(minutes/60)}h ${minutes%60}m`:`${minutes}m`; }
function subscriptionDeadline(value) {
  const at=typeof value==='string'?new Date(value):null;
  return at&&Number.isFinite(at.getTime())?`订阅截止 ${at.toLocaleString('zh-CN',{year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hourCycle:'h23',timeZoneName:'longOffset'})}`:'订阅截止时间未知';
}
function remainingTone(remaining){ if(remaining==null||!Number.isFinite(remaining)) return ''; if(remaining<15) return 'danger'; if(remaining<30) return 'warn'; return 'ok'; }
async function api(path, data) { const response=await fetch(path,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-Codex-Pool-CSRF':csrf},body:data===undefined?undefined:JSON.stringify(data)}); if(!response.ok){let code='操作未完成';try{code=(await response.json()).error?.code||code;}catch{}throw new Error(code);} return response.json(); }
async function action(path, data={}, success='已应用，下一个模型请求使用更新后的账户设置。') { if(busy)return false; busy=true; try{await api(path,data);notice(success);render(await api('/status'));return true;}catch(e){notice(e.message); if(state)render(state);return false;}finally{busy=false;} }
function notifySwitch(s){
  const id=s.activeAccountId||null;
  const name=(s.accounts.find(a=>a.id===id)||{}).name;
  if(!switchReady){ lastActiveId=id; switchReady=true; return; }
  if(!id || id===lastActiveId) return;
  lastActiveId=id;
  notice(`已切换到 ${name||'账户'}`);
}
function renderStatistics(stats, names) {
  for (const button of $('statistics-periods').querySelectorAll('button')) { const pressed=String(button.dataset.period===statisticsPeriod);if(button.getAttribute('aria-pressed')!==pressed)button.setAttribute('aria-pressed',pressed); }
  if(!stats){$('statistics-note').textContent='当前代理版本尚未提供 Token 统计。';return;}
  const period=stats.selected, totals=period.totals;
  const persistence=stats.persistence==='unavailable'?' · 历史文件无法读取，当前统计仅在内存中':stats.persistence==='pending'?' · 等待保存':'';
  setText('statistics-note',`从 ${new Date(stats.startedAt).toLocaleString()} 开始记录 · ${stats.timeZone} · 历史长期保存 · 日期按代理时区筛选${stats.earliestDailyDay?' · 最早日汇总 '+stats.earliestDailyDay:''}${persistence}`);
  const cards=[['总 Token',tokenValue(totals,'totalTokens'),`${number(totals.reportedRequests)} / ${number(totals.requests)} 个请求已报告`],
    ['输入',tokenValue(totals,'inputTokens'),'包含缓存 Token'],['输出',tokenValue(totals,'outputTokens'),'包含推理 Token'],
    ['缓存输入',detailValue(totals,'cachedInputTokens','cachedReportedRequests'),`${number(totals.cachedReportedRequests)} 个请求有明细`],
    ['推理输出',detailValue(totals,'reasoningOutputTokens','reasoningReportedRequests'),`${number(totals.reasoningReportedRequests)} 个请求有明细`],
    ['未报告',number(totals.unreportedRequests),'个请求缺少有效 usage']];
  patchChildren($('token-summary'),cards.map(([label,value,note])=>{const card=keyed(text('div','','token-metric'),label);card.append(text('small',label),text('strong',value),text('small',note));return card;}));
  const rows=(items,label,key)=>items.map(item=>{const tr=keyed(document.createElement('tr'),key(item)),t=item.totals;for(const value of [label(item),tokenValue(t,'totalTokens'),tokenValue(t,'inputTokens'),tokenValue(t,'outputTokens'),`${number(t.reportedRequests)} / ${number(t.requests)}`])tr.append(text('td',value));return tr;});
  const empty=()=>{const tr=document.createElement('tr'),cell=text('td','暂无请求');cell.colSpan=5;tr.append(cell);return tr;};
  patchChildren($('token-accounts'),period.accounts.length?rows(period.accounts,x=>names.get(x.accountId)||'已移除账户',x=>x.accountId):[empty()]);
  patchChildren($('token-models'),period.models.length?rows(period.models,x=>x.model,x=>x.model):[empty()]);
  const maximum=stats.daily.reduce((max,x)=>Math.max(max,Number(x.totals.totalTokens)),1);
  patchChildren($('token-daily'),stats.daily.map(day=>{const row=keyed(text('div','','token-day'),day.day),value=tokenValue(day.totals,'totalTokens');
    const bar=document.createElement('progress');bar.max=maximum;bar.value=day.totals.totalTokens;bar.setAttribute('aria-label',`${day.day} 已报告 ${value} Token`);
    row.title=`${day.day} · ${value} Token · ${day.totals.reportedRequests} / ${day.totals.requests} 个请求已报告`;
    row.append(text('small',day.day),bar,text('span',value));return row;}));
}
function render(s) {
  state=s; notifySwitch(s); const names=new Map(s.accounts.map(a=>[a.id,a.name])); setText('active',names.get(s.activeAccountId)||'未选择');setText('automatic',s.automaticSwitching?'已开启':'已关闭');setText('total',s.accounts.length);setText('inflight',s.inFlightRequests.reduce((n,a)=>n+a.value,0));
  if(s.tokenStatistics?.currentDay!==presetDay){presetDay=s.tokenStatistics?.currentDay;if(!['custom','allTime'].includes(statisticsPeriod))setPresetRange();}
  setText('gate',s.boundaryProbe?'当前为迁移验收模式。':'自动切换的真实账户验收尚未完成。');
  const form=$('settings'); if(settingsEditVersion===settingsSavedVersion&&!form.contains(document.activeElement)){form.elements.autoSwitch.checked=s.settings.autoSwitch;form.elements.windowThreshold.value=s.settings.windowThreshold??Math.min(s.settings.fiveHourThreshold,s.settings.weeklyThreshold);}
  form.elements.autoSwitch.disabled=!s.boundaryProbe;
  if(historyStats)renderStatistics(historyStats,names);
  if(historyPage)renderRequests(historyPage);
  refreshHistory();
  patchChildren($('accounts'),s.accounts.map(a=>{
    const card=keyed(text('article','','card'+(a.active?' selected':'')),a.id),head=keyed(text('div','','account-head'),'head');
    const canActivate=a.enabled&&!a.paused&&!a.drain&&a.state!=='cooldown'&&a.auth!=='reauth_required'&&a.auth!=='refresh_pending';
    if(a.active)card.append(keyed(text('span','✓','check-mark'),'selected'));
    head.append(text('strong',a.name),text('span',states[a.state]||a.state,'badge'));card.append(head);const meta=keyed(text('div','','meta'),'meta');meta.append(text('span',a.plan||'Plan 待确认'),text('span',`授权：${a.auth}`));card.append(meta);
    card.append(keyed(text('div',subscriptionDeadline(a.subscriptionActiveUntil),'account-tokens'),'subscription'));
    if(canActivate&&!a.active){card.classList.add('selectable');card.onclick=e=>{if(e.target.closest('.controls')||e.target.closest('button'))return;action(`/accounts/${a.id}/activate`);};}
    const access=s.accountAvailability?.find(x=>x.accountId===a.id);if(access?.reason)card.append(keyed(text('small',`模型访问：${availabilityLabel(access)}`),'access'));
    if(a.cooldownUntil&&Date.parse(a.cooldownUntil)>Date.now())card.append(keyed(text('small',`冷却剩余 ${reset(a.cooldownUntil)}`),'cooldown'));
    const usage=s.usage.find(u=>u.accountId===a.id),fresh=usage?.updatedAt&&Date.now()-Date.parse(usage.updatedAt)<=120000&&Date.parse(usage.updatedAt)<=Date.now()+30000;
    if(s.quotaRefreshErrors?.some(x=>x.accountId===a.id))card.append(keyed(text('small','额度更新失败，保留上次数据'),'quota-error'));
    for(const w of usage?.windows||[]){const label=windowLabel(w),row=keyed(text('div','','quota'),JSON.stringify(['quota',w.limitId,w.kind,w.windowSeconds,w.model])),qhead=text('div','','quota-head');const valid=fresh&&w.resetAt&&Date.parse(w.resetAt)>Date.now(),remaining=valid?Number(w.remainingPercent):null;const tone=remainingTone(remaining);qhead.append(text('span',label),text('b',valid?`剩 ${remaining.toFixed(0)}%`:'待确认'));row.append(qhead);if(valid){const bar=document.createElement('progress');bar.max=100;bar.value=Math.max(0,Math.min(100,remaining));bar.className=tone?`quota-${tone}`:'';bar.setAttribute('aria-label',`${label}剩余 ${remaining.toFixed(0)}%`);row.append(bar,text('small',`${reset(w.resetAt)} 后重置`));}else row.append(text('small',!fresh?'额度数据已过期':!w.resetAt?'重置时间未知':'等待重置后的新额度'));card.append(row);}
    if(!usage?.windows?.length)card.append(keyed(text('div',fresh&&usage.windowState==='not_reported'?'上游未提供固定额度窗口':'额度窗口未知','account-tokens'),'no-quota'));
    if(usage?.credits){const c=usage.credits;card.append(keyed(text('div',`${!fresh?'历史 ':''}Credits：${c.unlimited?'无额度上限':c.balance!=null?number(c.balance):c.hasCredits?'可用':'不可用'}`,'account-tokens'),'credits'));}
    if(fresh&&usage?.spendControlReached)card.append(keyed(text('small','已达到账户支出限制'),'spend-control'));
    card.append(keyed(text('small',usage?.updatedAt?`更新于 ${new Date(usage.updatedAt).toLocaleTimeString()}${Date.now()-Date.parse(usage.updatedAt)>120000?' · 数据已过期':''}`:'额度来源待验证'),'updated'));
    const accountTokens=s.tokenStatistics?.today?.accounts.find(x=>x.accountId===a.id)?.totals;
    if(s.tokenStatistics)card.append(keyed(text('div',`今日 ${accountTokens?tokenValue(accountTokens,'totalTokens'):'0'} Token · ${accountTokens?.requests||0} 个请求`,'account-tokens'),'tokens'));
    const controls=keyed(text('div','','controls'),'controls');for(const [op,label] of [[a.paused?'resume':'pause',a.paused?'恢复':'暂停'],[a.drain?'undrain':'drain',a.drain?'取消 Drain':'Drain'],[a.enabled?'disable':'enable',a.enabled?'禁用':'启用']]){const button=text('button',label);button.onclick=()=>action(`/accounts/${a.id}/${op}`);controls.append(button);}
    const rename=text('button','改名');rename.onclick=()=>{if(busy)return;const name=prompt('输入新的账户名称（1–80 字符）',a.name);if(name===null)return;if(!name.trim()||name.trim().length>80||/[\u0000-\u001f\u007f-\u009f]/u.test(name)){notice('名称需为 1–80 字符，不能包含控制字符。');return;}action(`/accounts/${a.id}/rename`,{name:name.trim()},'账户名称已更新。');};controls.append(rename);
    const del=text('button','删除','danger'); del.onclick=()=>{ if(!confirm(`确定永久删除账户「${a.name}」？\n凭据与控制状态将从本机 Vault 移除，且不可恢复。`)) return; action(`/accounts/${a.id}/delete`); }; controls.append(del);
    card.append(controls);return card;}));
  patchChildren($('switches'),s.recentSwitches.length?s.recentSwitches.slice().reverse().map(x=>keyed(text('div',`${new Date(x.at).toLocaleTimeString()} · ${names.get(x.from)||'—'} → ${names.get(x.to)||'—'} · ${reasons[x.reason]||x.reason}`,'switch'),JSON.stringify([x.at,x.from,x.to,x.reason]))):[text('p','暂无切换记录')]);
  patchChildren($('logins'),s.nativeLoginAttempts.map(x=>keyed(text('p',x.state==='completed'?'账户已添加':x.state==='waiting_for_browser'?'等待浏览器授权…':x.state),x.attemptId??x.id)));
}
$('settings').oninput=()=>settingsEditVersion++;
$('settings').onsubmit=async e=>{e.preventDefault();const f=e.target,version=settingsEditVersion;if(await action('/settings',{autoSwitch:f.elements.autoSwitch.checked,windowThreshold:Number(f.elements.windowThreshold.value)}))settingsSavedVersion=version;};
$('login').onsubmit=e=>{e.preventDefault();action('/accounts/native-login',{name:e.target.elements.name.value});};
$('refresh').onclick=async()=>{try{await api('/usage');const s=await api('/status');render(s);const failed=s.quotaRefreshErrors?.length||0;notice(failed?`${failed} 个账户额度更新失败，保留上次数据`:'额度已更新');}catch(e){notice(e.message);}};
const dateText = date => [date.getUTCFullYear(),String(date.getUTCMonth()+1).padStart(2,'0'),String(date.getUTCDate()).padStart(2,'0')].join('-');
function historyQuery() {
  const query=new URLSearchParams();
  if(filterFrom)query.set('from',filterFrom);
  if(filterTo)query.set('to',filterTo);
  return query;
}
function historyNames(){return new Map((state?.accounts||[]).map(a=>[a.id,a.name]));}
function renderRequests(page) {
  const names=historyNames();
  const rows=historyItems.map(x=>{const tr=keyed(document.createElement('tr'),requestKey(x)),u=x.tokenUsage;for(const value of [new Date(x.timestamp).toLocaleString(),names.get(x.account)||'已移除账户',x.model,`${x.durationMs} ms`,`${x.status} · ${x.outcome}`,x.attempts,u?`${number(u.inputTokens)} / ${number(u.outputTokens)}`:'未报告',u?`${u.cachedInputTokens==null?'—':number(u.cachedInputTokens)} / ${u.reasoningOutputTokens==null?'—':number(u.reasoningOutputTokens)}`:'—'])tr.append(text('td',value));return tr;});
  if(!rows.length){const row=document.createElement('tr'),cell=text('td','该时间范围暂无请求记录');cell.colSpan=8;row.append(cell);rows.push(row);}
  const viewport=$('requests').closest('.table-scroll'),top=viewport.scrollTop,anchor=Array.from($('requests').children).find(row=>row.getBoundingClientRect().bottom>viewport.getBoundingClientRect().top),offset=anchor?.getBoundingClientRect().top;
  patchChildren($('requests'),rows);
  // Keep the visible request anchored when newer rows arrive above it.
  if(top>0&&anchor?.isConnected)viewport.scrollTop+=anchor.getBoundingClientRect().top-offset;
  setText('requests-note',`已显示 ${number(historyItems.length)} 条 · 请求明细从 ${new Date(page.startedAt).toLocaleString()} 开始记录${page.persistence==='pending'?' · 部分记录等待保存':page.persistence==='unavailable'?' · 历史存储不可用':''}`);
  $('requests-more').hidden=nextBefore===null;
}
async function refreshHistory(force=false, more=false) {
  if(!state?.tokenStatistics)return;
  const query=historyQuery(), key=JSON.stringify([query.toString(),state.tokenStatistics.updatedAt,state.tokenStatistics.persistence,state.tokenStatistics.currentDay,state.tokenStatistics.allTime.totals.requests]);
  if(!force&&(key===historyKey||historyLoading))return;
  const range=query.toString(),rangeChanged=range!==historyRange;
  const revision=++historyRevision; historyKey=key; historyLoading=true;
  $('requests-more').disabled=true;
  if(rangeChanged){historyRange=range;historyItems=[];nextBefore=null;historyStats=null;historyPage=null;$('requests').replaceChildren();setText('requests-note','正在读取历史…');$('requests-more').hidden=true;setText('statistics-note','正在读取历史…');for(const id of ['token-summary','token-accounts','token-models','token-daily'])$(id).replaceChildren();}
  if(more&&nextBefore!==null)query.set('before',nextBefore);
  try {
    const [stats,page]=await Promise.all([more?Promise.resolve(historyStats):api('/statistics?'+historyQuery()),api('/requests?'+query)]);
    if(revision!==historyRevision)return;
    const hadOlder=!more&&historyItems.some(x=>x.sequence>0&&page.items.length&&x.sequence<page.items[page.items.length-1].sequence);
    historyStats=stats;historyPage=page;
    const persisted=new Set(page.items.filter(x=>x.sequence>0).map(requestFingerprint));
    const retained=historyItems.filter(x=>x.sequence>0||!persisted.has(requestFingerprint(x)));
    historyItems=Array.from(new Map([...retained,...page.items].map(x=>[requestKey(x),x])).values()).sort((a,b)=>a.sequence>0&&b.sequence>0?b.sequence-a.sequence:Date.parse(b.timestamp)-Date.parse(a.timestamp));
    if(!hadOlder)nextBefore=page.nextBefore;
    renderStatistics(stats,historyNames());renderRequests(page);
  } catch(e){if(revision===historyRevision){historyKey='';$('requests-note').textContent='历史读取失败：'+e.message;$('statistics-note').textContent='历史读取失败，请刷新重试。';}}
  finally{if(revision===historyRevision){historyLoading=false;$('requests-more').disabled=false;}}
}
function requestFingerprint(x){return JSON.stringify([x.timestamp,x.account,x.model,x.durationMs,x.status,x.attempts]);}
function requestKey(x){return x.sequence>0?String(x.sequence):requestFingerprint(x);}
function setPresetRange(){
  // Use the calendar day reported by the proxy, even if the browser is in another time zone.
  const today=new Date((state?.tokenStatistics.currentDay||dateText(new Date()))+'T00:00:00Z');
  $('history-from').value=statisticsPeriod==='allTime'?'':dateText(new Date(today.getTime()-(statisticsPeriod==='last7Days'?6:statisticsPeriod==='last30Days'?29:0)*86400000));
  $('history-to').value=statisticsPeriod==='allTime'?'':dateText(today);
  filterFrom=$('history-from').value;filterTo=$('history-to').value;
}
for(const button of $('statistics-periods').querySelectorAll('button'))button.onclick=()=>{
  statisticsPeriod=button.dataset.period;
  setPresetRange();
  refreshHistory(true);
};
$('history-range').onsubmit=e=>{e.preventDefault();if($('history-from').value&&$('history-to').value&&$('history-from').value>$('history-to').value){notice('开始日期不能晚于结束日期。');return;}filterFrom=$('history-from').value;filterTo=$('history-to').value;statisticsPeriod='custom';refreshHistory(true);};
$('history-refresh').onclick=()=>refreshHistory(true);
$('requests-more').onclick=()=>{if(!historyLoading&&nextBefore!==null)refreshHistory(true,true);};
async function start(){try{csrf=(await api('/browser-session')).csrf;$('workspace').hidden=false;render(await api('/status'));const events=new EventSource('/events');events.addEventListener('status',e=>{render(JSON.parse(e.data));$('connection').textContent='服务在线';});events.onerror=()=>{$('connection').textContent='正在重新连接';};}catch{$('locked').hidden=false;$('connection').textContent='等待安全会话';}}
start();setInterval(()=>{if(state)render(state);},30000);
