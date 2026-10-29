// On a wide screen, a documentation page shows its "On this page" list in a rail beside the text, and marks the
// section in view. Without this script, or on a narrow screen, the list stays where the page puts it.
(function () {
  var docs = document.querySelector(".docs");
  var toc = docs && docs.querySelector(".page > .toc");
  if (!toc) return;

  var rail = document.createElement("aside");
  rail.className = "rail";
  rail.setAttribute("aria-label", "On this page");
  var title = document.createElement("p");
  title.textContent = "On this page";
  rail.appendChild(title);
  rail.appendChild(toc.querySelector("ol").cloneNode(true));
  docs.appendChild(rail);
  docs.classList.add("has-rail");

  var links = {};
  rail.querySelectorAll("a[href^='#']").forEach(function (a) { links[a.getAttribute("href").slice(1)] = a; });
  var headings = Object.keys(links).map(function (id) { return document.getElementById(id); }).filter(Boolean);
  if (!headings.length) return;

  var current = null;
  function mark(id) {
    if (current) current.classList.remove("current");
    current = links[id] || null;
    if (current) current.classList.add("current");
  }

  // The current section is the last heading above the top third of the window.
  function update() {
    var line = window.innerHeight / 3, id = headings[0].id;
    for (var i = 0; i < headings.length; i++) {
      if (headings[i].getBoundingClientRect().top <= line) id = headings[i].id; else break;
    }
    mark(id);
  }

  var pending = false;
  window.addEventListener("scroll", function () {
    if (pending) return;
    pending = true;
    window.requestAnimationFrame(function () { pending = false; update(); });
  }, { passive: true });
  update();
})();
