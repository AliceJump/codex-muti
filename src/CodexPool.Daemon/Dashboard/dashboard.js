'use strict';
const $ = id => document.getElementById(id);
let state = null, busy = false, lastActiveId = null, switchReady = false;
let statisticsPeriod = 'allTime', historyKey = '', historyRevision = 0, nextBefore = null, historyLoading = false;
let historyItems = [], historyStats = null, historyPage = null;
let historyRange = null;
let filterFrom = '', filterTo = '', presetDay = '';
let settingsEditVersion = 0, settingsSavedVersion = 0;
let switchPage = 0, switchSnapshot = null, eventStream = null, starting = false, loginSubmitting = false;
const switchPageSize = 8;
const views = {
  accounts: ['账户与额度','查看可用额度，选择接下来使用的账户。'],
  usage: ['用量统计','按账户、模型与日期，查看经过代理的实际用量。'],
  activity: ['活动记录','查看账户切换的原因，以及模型请求的处理记录。'],
  access: ['应用接入','为你的应用连接模型，并管理独立的访问密钥。'],
  settings: ['偏好设置','设置额度阈值与自动切换方式。']
};
function navigate() {
  const requested=location.hash.replace(/^#\/?/,'');
  const view = Object.hasOwn(views,requested) ? requested : 'accounts';
  for(const page of document.querySelectorAll('[data-page]'))page.hidden=page.dataset.page!==view;
  for(const link of document.querySelectorAll('[data-view]')){
    if(link.dataset.view===view)link.setAttribute('aria-current','page');else link.removeAttribute('aria-current');
  }
  setText('page-title',views[view][0]);setText('page-breadcrumb',views[view][0]);setText('page-description',views[view][1]);
  document.title=`Codex Pool · ${views[view][0]}`;
  document.documentElement.scrollTop=0;document.body.scrollTop=0;
}
window.addEventListener('hashchange',navigate);
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
  for(const attr of Array.from(current.attributes))if(!next.hasAttribute(attr.name)&&!(current.tagName==='DETAILS'&&attr.name==='open'))current.removeAttribute(attr.name);
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
function notice(message) { setText('notice-message',message); $('notice').hidden=!message; }
function reset(at) { const minutes=Math.max(0,Math.ceil((Date.parse(at)-Date.now())/60000)); return minutes>=1440?`${Math.floor(minutes/1440)}d ${Math.floor(minutes%1440/60)}h`:minutes>=60?`${Math.floor(minutes/60)}h ${minutes%60}m`:`${minutes}m`; }
function subscriptionDeadline(value, status, reason) {
  if(status==='stale')return `订阅截止时间待同步（${({deadline_in_past:'上游仍返回旧日期',id_token_expired:'授权资料已过期',id_token_not_refreshed:'授权资料未更新'})[reason]||'旧授权资料'}）`;
  if(status==='not_reported')return '订阅截止时间未知（上游未提供）';
  const at=typeof value==='string'?new Date(value):null;
  return at&&Number.isFinite(at.getTime())?`订阅截止 ${at.toLocaleString('zh-CN',{year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hourCycle:'h23',timeZoneName:'longOffset'})}`:'订阅截止时间未知';
}
const quotaFailureLabel=code=>({quota_unauthorized:'额度接口认证失败（401）',quota_forbidden:'额度接口拒绝访问（403）',quota_rate_limited:'额度接口限流（429）',quota_upstream_unavailable:'额度上游服务异常',quota_network_unavailable:'额度网络连接失败',quota_refresh_timeout:'额度查询超时',invalid_quota_response:'额度响应格式异常',quota_auth_unavailable:'额度凭据恢复失败',quota_refresh_required:'授权已更新，等待重新查询额度',quota_storage_or_io_failed:'额度保存或读取失败'}[code]||'额度查询失败');
function remainingTone(remaining){ if(remaining==null||!Number.isFinite(remaining)) return ''; if(remaining<15) return 'danger'; if(remaining<30) return 'warn'; return 'ok'; }
const loginMessages={native_login_already_running:'已有登录正在等待浏览器授权，请完成授权或取消后重试。',native_login_cancelled:'登录已取消，可重新发起。',native_login_timed_out:'登录已超时，请重新发起。',native_login_failed:'登录未完成，请重试。',native_login_not_completed:'浏览器授权未完成，请重试。',native_login_helper_unavailable:'无法启动官方 Codex，请确认已安装 Codex CLI。',credential_cleanup_required:'登录临时文件未能清理，请检查本机文件访问权限。',native_credential_file_missing:'官方 Codex 未生成登录凭据，请重试。',native_account_metadata_missing:'登录凭据缺少账户信息，请重试。',native_account_identity_mismatch:'登录凭据的账户身份校验失败，请重试。',native_credential_invalid:'登录凭据无效，请重试。',native_login_stopping:'代理正在停止，无法发起登录。'};
async function api(path, data) { const response=await fetch(path,{method:data===undefined?'GET':'POST',headers:{'X-Codex-Pool-Local':'1',...(data===undefined?{}:{'Content-Type':'application/json'})},body:data===undefined?undefined:JSON.stringify(data)}); if(!response.ok){let code='操作未完成';try{code=(await response.json()).error?.code||code;}catch{}throw new Error(loginMessages[code]||code);} return response.json(); }
async function action(path, data={}, success='已应用，下一个模型请求使用更新后的账户设置。') { if(busy)return false; busy=true; try{await api(path,data);notice(success);render(await api('/status'));return true;}catch(e){notice(e.message);if(path==='/accounts/native-login'){try{render(await api('/status'));}catch{if(state)render(state);}}else if(state)render(state);return false;}finally{busy=false;} }
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
  setText('nav-total',s.accounts.length);setText('available',s.accountAvailability?.filter(x=>x.available).length??s.accounts.filter(a=>a.enabled&&a.auth==='ready'&&!a.paused&&!a.drain).length);
  $('accounts-empty').hidden=s.accounts.length>0;
  if(s.tokenStatistics?.currentDay!==presetDay){presetDay=s.tokenStatistics?.currentDay;if(!['custom','allTime'].includes(statisticsPeriod))setPresetRange();}
  setText('gate',s.boundaryProbe?'':'当前服务未启用自动切换，可手动选择账户。');
  const form=$('settings'); if(settingsEditVersion===settingsSavedVersion&&!form.contains(document.activeElement)){form.elements.autoSwitch.checked=s.settings.autoSwitch;form.elements.windowThreshold.value=s.settings.windowThreshold??Math.min(s.settings.fiveHourThreshold,s.settings.weeklyThreshold);}
  form.elements.autoSwitch.disabled=!s.boundaryProbe;
  if(historyStats)renderStatistics(historyStats,names);
  if(historyPage)renderRequests(historyPage);
  refreshHistory();
  patchChildren($('accounts'),s.accounts.map(a=>{
    const card=keyed(text('article','','card'+(a.active?' selected':'')),a.id),head=keyed(text('div','','account-head'),'head');
    const canActivate=a.enabled&&!a.paused&&!a.drain&&a.state!=='cooldown'&&a.auth!=='reauth_required'&&a.auth!=='refresh_pending';
    head.append(text('span',Array.from(a.name||'?')[0].toUpperCase(),'account-avatar'),text('strong',a.name),text('span',states[a.state]||a.state,'badge'));card.append(head);
    const meta=keyed(text('div','','meta'),'meta');meta.append(text('span',a.plan||'套餐待确认'),text('span',({ready:'授权正常',refresh_needed:'待刷新授权',refresh_pending:'正在刷新授权',reauth_required:'需要重新登录'})[a.auth]||'授权待确认'));card.append(meta);
    if(canActivate&&!a.active){card.classList.add('selectable');card.onclick=e=>{if(e.target.closest('details')||e.target.closest('button'))return;action(`/accounts/${a.id}/activate`);};}
    const access=s.accountAvailability?.find(x=>x.accountId===a.id);if(access?.reason)card.append(keyed(text('div',availabilityLabel(access),'access-warning'),'access'));
    if(a.cooldownUntil&&Date.parse(a.cooldownUntil)>Date.now())card.append(keyed(text('small',`冷却剩余 ${reset(a.cooldownUntil)}`),'cooldown'));
    const usage=s.usage.find(u=>u.accountId===a.id),failure=s.quotaRefreshErrors?.find(x=>x.accountId===a.id)||usage?.refreshError;
    const fresh=!failure&&usage?.cacheState!=='historical'&&usage?.updatedAt&&Date.now()-Date.parse(usage.updatedAt)<=120000&&Date.parse(usage.updatedAt)<=Date.now()+30000;
    if(failure)card.append(keyed(text('small',`历史缓存 · ${quotaFailureLabel(failure.code)}${failure.recoveryCode?' · '+failure.recoveryCode:''}${failure.at?' · '+new Date(failure.at).toLocaleTimeString():''}`),'quota-error'));
    for(const w of usage?.windows||[]){const label=windowLabel(w),row=keyed(text('div','','quota'),JSON.stringify(['quota',w.limitId,w.kind,w.windowSeconds,w.model])),qhead=text('div','','quota-head');const valid=fresh&&w.resetAt&&Date.parse(w.resetAt)>Date.now(),remaining=valid?Number(w.remainingPercent):null;const tone=remainingTone(remaining);qhead.append(text('span',label),text('b',valid?`剩 ${remaining.toFixed(0)}%`:'待确认'));row.append(qhead);if(valid){const bar=document.createElement('progress');bar.max=100;bar.value=Math.max(0,Math.min(100,remaining));bar.className=tone?`quota-${tone}`:'';bar.setAttribute('aria-label',`${label}剩余 ${remaining.toFixed(0)}%`);row.append(bar,text('small',`${reset(w.resetAt)} 后重置`));}else row.append(text('small',!fresh?'额度数据已过期':!w.resetAt?'重置时间未知':'等待重置后的新额度'));card.append(row);}
    if(!usage?.windows?.length)card.append(keyed(text('div',fresh&&usage.windowState==='not_reported'?'上游未提供固定额度窗口':'额度窗口未知','account-tokens'),'no-quota'));
    if(usage?.credits){const c=usage.credits;card.append(keyed(text('div',`${!fresh?'历史 ':''}Credits：${c.unlimited?'无额度上限':c.balance!=null?number(c.balance):c.hasCredits?'可用':'不可用'}`,'account-tokens'),'credits'));}
    if(fresh&&usage?.spendControlReached)card.append(keyed(text('small','已达到账户支出限制'),'spend-control'));
    const accountTokens=s.tokenStatistics?.today?.accounts.find(x=>x.accountId===a.id)?.totals;
    if(s.tokenStatistics)card.append(keyed(text('div',`今日 ${accountTokens?tokenValue(accountTokens,'totalTokens'):'0'} Token · ${accountTokens?.requests||0} 个请求`,'account-tokens'),'tokens'));
    const details=keyed(text('details','','account-details'),'details');details.append(keyed(text('summary','账户详情与管理'),'summary'));
    details.append(keyed(text('div',subscriptionDeadline(a.subscriptionActiveUntil,a.subscriptionState,a.subscriptionSyncReason),'account-tokens'),'subscription'));
    if(a.planSource)details.append(keyed(text('small',`套餐来源：${a.planSource==='native_codex_usage'?'额度接口':a.planSource==='validated_id_token'?'已验证授权资料':a.planSource==='login_id_token'?'登录资料':'旧资料'}${a.planState==='historical'?' · 待同步':''}`),'plan-source'));
    details.append(keyed(text('small',usage?.updatedAt?`更新于 ${new Date(usage.updatedAt).toLocaleString()}${!fresh?' · 历史缓存':''}`:'额度来源待验证'),'updated'));
    const controls=keyed(text('div','','controls'),'controls');for(const [op,label] of [[a.paused?'resume':'pause',a.paused?'恢复':'暂停'],[a.drain?'undrain':'drain',a.drain?'取消 Drain':'Drain'],[a.enabled?'disable':'enable',a.enabled?'禁用':'启用']]){const button=text('button',label);button.onclick=()=>action(`/accounts/${a.id}/${op}`);controls.append(button);}
    const rename=text('button','改名');rename.onclick=()=>{if(busy)return;const name=prompt('输入新的账户名称（1–80 字符）',a.name);if(name===null)return;if(!name.trim()||name.trim().length>80||/[\u0000-\u001f\u007f-\u009f]/u.test(name)){notice('名称需为 1–80 字符，不能包含控制字符。');return;}action(`/accounts/${a.id}/rename`,{name:name.trim()},'账户名称已更新。');};controls.append(rename);
    const del=text('button','删除','danger'); del.onclick=()=>{ if(!confirm(`确定永久删除账户「${a.name}」？\n凭据与控制状态将从本机 Vault 移除，且不可恢复。`)) return; action(`/accounts/${a.id}/delete`); }; controls.append(del);
    const reauthorize=text('button','重新登录');reauthorize.onclick=()=>openLogin(a.name);controls.append(reauthorize);
    details.append(controls);card.append(details);
    const actions=keyed(text('div','','account-actions'),'actions');actions.append(text('span',a.active?'当前请求使用此账户':'切换只影响后续请求'));
    if(a.active)actions.append(text('b','✓ 使用中','account-current'));
    else {const choose=text('button',a.auth==='reauth_required'?'重新登录':'使用此账户',canActivate?'primary':'');choose.disabled=!canActivate&&a.auth!=='reauth_required';choose.onclick=()=>a.auth==='reauth_required'?openLogin(a.name):action(`/accounts/${a.id}/activate`);actions.append(choose);}
    card.append(actions);return card;}));
  renderSwitches();
  renderLogins(s.nativeLoginAttempts);
}
function renderLogins(attempts=[]){
  const waiting=attempts.some(x=>x.state==='waiting_for_browser'),submit=$('login').querySelector('button[type="submit"]');
  submit.disabled=waiting||loginSubmitting;submit.textContent=waiting?'等待浏览器授权…':'使用官方 Codex 登录 ↗';
  patchChildren($('logins'),attempts.map(x=>{
    const id=x.attemptId??x.id,row=keyed(text('div','','login-attempt'),id);
    row.append(keyed(text('p',x.state==='completed'?'账户已添加':x.state==='waiting_for_browser'?'等待浏览器授权…':loginMessages[x.state]||x.state),'state'));
    if(x.state==='waiting_for_browser'){const cancel=keyed(text('button','取消登录'),'cancel');cancel.type='button';cancel.onclick=()=>action(`/accounts/native-login/${encodeURIComponent(id)}/cancel`,{},'登录已结束，可重新发起。');row.append(cancel);}
    return row;
  }));
}
const switchKey=x=>JSON.stringify([x.at,x.from,x.to,x.reason,x.epoch]);
function switchRow(x,names){
  const row=keyed(text('div','','switch'),switchKey(x)),at=new Date(x.at),time=text('time',at.toLocaleString('zh-CN',{year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hourCycle:'h23'}));
  time.dateTime=x.at;time.title=at.toLocaleString();
  const route=text('div','','switch-route');
  const from=text('span',names.get(x.from)||(x.from?'已移除账户':'未选择')),to=text('span',names.get(x.to)||(x.to?'已移除账户':'未选择'));
  from.title=from.textContent;to.title=to.textContent;route.append(from,text('i','→'),to);
  const reason=text('span',reasons[x.reason]||x.reason,'switch-reason');reason.title=reason.textContent;
  row.append(time,route,reason);return row;
}
function renderSwitches(){
  if(!state)return;
  const names=new Map(state.accounts.map(a=>[a.id,a.name])),all=(state.recentSwitches||[]).slice().reverse();
  patchChildren($('switch-preview'),all.length?all.slice(0,3).map(x=>switchRow(x,names)):[text('p','暂无切换记录','muted')]);
  const filter=$('switch-filter').value,current=all.filter(x=>filter==='all'||(filter==='manual')===(x.reason==='manual'));
  const rows=switchPage>0&&switchSnapshot?switchSnapshot:current,pages=Math.max(1,Math.ceil(rows.length/switchPageSize));
  switchPage=Math.min(switchPage,pages-1);
  patchChildren($('switches'),rows.length?rows.slice(switchPage*switchPageSize,(switchPage+1)*switchPageSize).map(x=>switchRow(x,names)):[text('p',filter==='all'?'暂无切换记录':'暂无此类切换记录','muted')]);
  const known=new Set(rows.map(switchKey)),added=current.filter(x=>!known.has(switchKey(x))).length;
  setText('switch-count',`${rows.length} 条${added?' · 有 '+added+' 条新记录，返回第一页查看':''}`);setText('switch-page',`${switchPage+1} / ${pages}`);
  $('switch-prev').disabled=switchPage===0;$('switch-next').disabled=switchPage>=pages-1;
}
$('switch-filter').onchange=()=>{switchPage=0;switchSnapshot=null;renderSwitches();};
$('switch-prev').onclick=()=>{switchPage=Math.max(0,switchPage-1);if(switchPage===0)switchSnapshot=null;renderSwitches();};
$('switch-next').onclick=()=>{if(switchPage===0){const filter=$('switch-filter').value;switchSnapshot=(state.recentSwitches||[]).slice().reverse().filter(x=>filter==='all'||(filter==='manual')===(x.reason==='manual'));}switchPage++;renderSwitches();};
for(const button of document.querySelectorAll('[data-activity]'))button.onclick=()=>{
  for(const other of document.querySelectorAll('[data-activity]'))other.setAttribute('aria-pressed',String(other===button));
  $('switch-history').hidden=button.dataset.activity!=='switches';$('request-history').hidden=button.dataset.activity!=='requests';
};
function openLogin(name=''){$('login').elements.name.value=name;$('login-title').textContent=name?'重新登录账户':'添加账户';$('login-dialog').showModal();$('login').elements.name.focus();}
$('add-account').onclick=()=>openLogin();
for(const button of document.querySelectorAll('[data-open-login]'))button.onclick=()=>openLogin();
$('login-close').onclick=()=>$('login-dialog').close();
$('notice-close').onclick=()=>notice('');
$('settings').oninput=()=>settingsEditVersion++;
$('settings').onsubmit=async e=>{e.preventDefault();const f=e.target,version=settingsEditVersion;if(await action('/settings',{autoSwitch:f.elements.autoSwitch.checked,windowThreshold:Number(f.elements.windowThreshold.value)}))settingsSavedVersion=version;};
$('login').onsubmit=async e=>{e.preventDefault();if(loginSubmitting||state?.nativeLoginAttempts.some(x=>x.state==='waiting_for_browser'))return;loginSubmitting=true;renderLogins(state?.nativeLoginAttempts);try{await action('/accounts/native-login',{name:e.target.elements.name.value},'已启动官方 Codex 登录，请在浏览器中完成授权。');}finally{loginSubmitting=false;renderLogins(state?.nativeLoginAttempts);}};
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
let issuedModelKeyId = null;
function clearIssuedModelKey(){issuedModelKeyId=null;$('model-key-secret').value='';$('model-key-issued').hidden=true;}
async function refreshModelKeys(){
  const keys=await api('/model-keys');
  $('model-keys').replaceChildren(...keys.map(key=>{
    const row=text('div','');row.className='section-title';row.append(text('span',key.name));
    const revoke=text('button','撤销');revoke.type='button';revoke.onclick=async()=>{
      revoke.disabled=true;
      try{await api('/model-keys/'+encodeURIComponent(key.id)+'/revoke',{});if(issuedModelKeyId===key.id)clearIssuedModelKey();await refreshModelKeys();notice('密钥已撤销，后续请求将被拒绝。');}
      catch(e){notice(e.message);revoke.disabled=false;}
    };row.append(revoke);return row;
  }));
}
$('model-key-create').onsubmit=async e=>{
  e.preventDefault();const button=e.submitter;if(button)button.disabled=true;
  try{const issued=await api('/model-keys/create',{name:$('model-key-name').value});issuedModelKeyId=issued.id;$('model-key-secret').value=issued.key;$('model-key-issued').hidden=false;await refreshModelKeys();notice('模型密钥已创建，请复制到应用配置中。');}
  catch(e){notice(e.message);}finally{if(button)button.disabled=false;}
};
$('model-key-copy').onclick=async()=>{try{await navigator.clipboard.writeText($('model-key-secret').value);notice('密钥已复制。');}catch{notice('复制未完成，请允许浏览器访问剪贴板后重试。');}};
$('model-key-dismiss').onclick=clearIssuedModelKey;
$('copy-endpoint').onclick=async()=>{try{await navigator.clipboard.writeText('http://127.0.0.1:3210/api/v1');notice('API 地址已复制。');}catch{notice('复制未完成，请手动复制地址。');}};
$('load-models').onclick=async()=>{
  const button=$('load-models');button.disabled=true;
  try{const catalog=await api('/models');patchChildren($('model-list'),catalog.models.length?catalog.models.map(m=>keyed(text('span',m.slug||m.id),m.slug||m.id)):[text('p','当前账户未返回可用模型。')]);}
  catch{notice('模型目录读取失败，请检查当前账户授权。');}finally{button.disabled=false;}
};
window.addEventListener('pagehide',clearIssuedModelKey);
function connection(online){document.body.dataset.offline=String(!online);setText('connection',online?'服务在线':'正在重新连接');}
async function start(){
  if(starting)return;starting=true;eventStream?.close();
  try{render(await api('/status'));$('workspace').hidden=false;$('locked').hidden=true;connection(true);refreshModelKeys().catch(()=>notice('模型密钥列表读取失败。'));eventStream=new EventSource('/events');eventStream.addEventListener('status',e=>{render(JSON.parse(e.data));connection(true);});eventStream.onerror=()=>connection(false);}
  catch{$('locked').hidden=false;$('workspace').hidden=true;connection(false);}finally{starting=false;}
}
$('retry').onclick=start;
navigate();start();setInterval(()=>{if(state)render(state);},30000);
