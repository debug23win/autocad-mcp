const conversation = document.getElementById("conversation");
let selected = -1, lastSequence = -1, following = true, anchor = null, manualVersion = 0;
let applying = false, scheduled = false;
const scroller = () => document.scrollingElement || document.documentElement;
const nearBottom = () => scroller().scrollHeight - scroller().scrollTop - window.innerHeight < 90;
function captureAnchor() {
  const card = [...conversation.children].find(x => x.getBoundingClientRect().bottom > 0);
  return {index:card?.dataset.index, top:card?.getBoundingClientRect().top || 0, scroll:scroller().scrollTop, version:manualVersion};
}
function restorePosition() {
  scheduled = false;
  if (!anchor || anchor.version !== manualVersion) return;
  applying = true;
  if (following) scroller().scrollTop = scroller().scrollHeight;
  else {
    const card = [...conversation.children].find(x => x.dataset.index === anchor.index);
    scroller().scrollTop = card ? scroller().scrollTop + card.getBoundingClientRect().top - anchor.top : anchor.scroll;
  }
  requestAnimationFrame(() => { applying = false; });
}
function schedulePosition() { if (!scheduled) { scheduled = true; requestAnimationFrame(restorePosition); } }
function userInput() { manualVersion++; anchor = null; following = nearBottom(); }
window.addEventListener("wheel", userInput, {passive:true});
window.addEventListener("touchstart", userInput, {passive:true});
window.addEventListener("pointerdown", userInput, {passive:true});
window.addEventListener("keydown", e => { if (["PageUp","PageDown","Home","End","ArrowUp","ArrowDown"," "].includes(e.key)) userInput(); });
window.addEventListener("scroll", () => { if (!applying) { following = nearBottom(); anchor = captureAnchor(); } }, {passive:true});
function patchArticle(existing, incoming) {
  const old = [...existing.children];
  const children = [...incoming.children];
  children.forEach((child, i) => {
    const previous = old[i];
    if (previous?.outerHTML === child.outerHTML) return;
    if (previous) previous.replaceWith(child); else existing.appendChild(child);
  });
  old.slice(children.length).forEach(child => child.remove());
}
function renderMath(element) {
  if (window.renderMathInElement) renderMathInElement(element, {
    delimiters: [{left:"$$",right:"$$",display:true},{left:"\\[",right:"\\]",display:true},
      {left:"\\(",right:"\\)",display:false},{left:"$",right:"$",display:false}],
    throwOnError:false, trust:false
  });
}
function update(data) {
  if (Number.isInteger(data.sequence) && data.sequence <= lastSequence) return;
  lastSequence = data.sequence ?? lastSequence;
  following = nearBottom(); anchor = captureAnchor();
  document.body.classList.toggle("dark", data.dark === true);
  const template = document.createElement("template");
  template.innerHTML = data.html || "<p id='empty'>Напишите запрос о чертеже или прикрепите файлы.</p>";
  const incoming = [...template.content.children];
  if (incoming.length < conversation.children.length) { following = true; anchor.index = undefined; }
  incoming.forEach((card, i) => {
    const previous = conversation.children[i];
    const source = card.outerHTML;
    if (previous?.dataset.index === card.dataset.index && previous.tagName === card.tagName) {
      if (previous._sourceHtml !== source) { patchArticle(previous, card); previous._sourceHtml = source; renderMath(previous); }
    } else { card._sourceHtml = source; if (previous) previous.replaceWith(card); else conversation.appendChild(card); renderMath(card); }
  });
  while (conversation.children.length > incoming.length) conversation.lastElementChild.remove();
  selected = Number.isInteger(data.selected) ? data.selected : selected;
  conversation.querySelectorAll(".message.assistant").forEach(x => x.classList.toggle("selected", Number(x.dataset.index) === selected));
  restorePosition(); schedulePosition();
}
new ResizeObserver(schedulePosition).observe(conversation);
window.chrome.webview.addEventListener("message", e => update(e.data));
conversation.addEventListener("click", e => {
  const link = e.target.closest("a");
  if (link) {
    e.preventDefault();
    window.chrome.webview.postMessage({type:"link",url:link.href});
    return;
  }
  const card = e.target.closest(".message.assistant");
  if (!card) return;
  selected = Number(card.dataset.index);
  conversation.querySelectorAll(".message.assistant").forEach(x => x.classList.toggle("selected", Number(x.dataset.index) === selected));
  window.chrome.webview.postMessage({type:"select",index:selected});
});
