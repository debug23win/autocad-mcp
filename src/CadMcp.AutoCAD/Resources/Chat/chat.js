const conversation = document.getElementById("conversation");
let selected = -1;
function update(data) {
  const follow = window.innerHeight + window.scrollY >= document.body.scrollHeight - 90;
  document.body.classList.toggle("dark", data.dark === true);
  conversation.innerHTML = data.html || "<p id='empty'>Напишите запрос о чертеже или прикрепите файлы.</p>";
  selected = Number.isInteger(data.selected) ? data.selected : selected;
  conversation.querySelectorAll(".message.assistant").forEach(x => x.classList.toggle("selected", Number(x.dataset.index) === selected));
  if (window.renderMathInElement) renderMathInElement(conversation, {
    delimiters: [{left:"$$",right:"$$",display:true},{left:"\\[",right:"\\]",display:true},
      {left:"\\(",right:"\\)",display:false},{left:"$",right:"$",display:false}],
    throwOnError:false, trust:false
  });
  if (follow) window.scrollTo(0,document.body.scrollHeight);
}
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
