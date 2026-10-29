// Colors the code blocks of a documentation page. A block's label names its kind: a shell command, a JSON file, or
// what Fuse prints. The text stays as it is; only spans with a class are added, so copying a block copies its text.
(function () {
  var shellLabels = { bash: 1, shell: 1, powershell: 1, usage: 1 };
  var commands = /^(fuse|dotnet|git|gh|npm|npx|node|claude|codex|cursor|gemini|muse|opencode|copilot|cd|export|set|echo|mkdir|rm|cp|cat)\b/;

  function esc(s) { return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;"); }
  function span(cls, s) { return '<span class="tk-' + cls + '">' + esc(s) + "</span>"; }

  // Splits text with a list of [class, regex] rules: the earliest match wins, ties go to the rule listed first.
  function tokenize(text, rules) {
    var out = "", rest = text;
    while (rest.length) {
      var best = null;
      for (var i = 0; i < rules.length; i++) {
        rules[i][1].lastIndex = 0;
        var m = rules[i][1].exec(rest);
        if (m && m[0].length && (!best || m.index < best.m.index)) best = { cls: rules[i][0], m: m };
      }
      if (!best) { out += esc(rest); break; }
      out += esc(rest.slice(0, best.m.index)) + span(best.cls, best.m[0]);
      rest = rest.slice(best.m.index + best.m[0].length);
    }
    return out;
  }

  var shellRules = [
    ["comment", /(^|\s)#.*$/],
    ["string", /"(?:[^"\\]|\\.)*"|'[^']*'/],
    ["placeholder", /<[^<>\s][^<>]*>/],
    ["flag", /(?:^|(?<=[\s\[|(]))--?[A-Za-z][\w-]*(?::[\w.]+)?/],
    ["path", /(?:^|(?<=\s))\.[A-Za-z][\w.\/-]*|(?:\.{0,2}\/|~\/|[A-Za-z]:\\)[\w.\\/<>-]+|\b[\w-]+\.(?:slnx?|csproj|json|yml|cs|md|txt|log)\b/],
  ];

  function shellLine(line) {
    var prompt = /^(\s*)(\$|PS>|>)\s/.exec(line), lead = "";
    if (prompt) { lead = esc(prompt[1]) + span("prompt", prompt[2]) + " "; line = line.slice(prompt[0].length); }
    var cmd = commands.exec(line);
    if (cmd && !/^\s*#/.test(line)) return lead + span("command", cmd[0]) + tokenize(line.slice(cmd[0].length), shellRules);
    return lead + tokenize(line, shellRules);
  }

  var jsonRules = [
    ["comment", /\/\/.*$/],
    ["key", /"(?:[^"\\]|\\.)*"(?=\s*:)/],
    ["string", /"(?:[^"\\]|\\.)*"/],
    ["number", /-?\b\d+(?:\.\d+)?\b/],
    ["literal", /\b(?:true|false|null)\b/],
  ];

  var outputRules = [
    ["path", /(?:^|(?<=\s))\.[A-Za-z][\w.\/-]*|[\w.\\/<>-]+\.(?:cs|razor|cshtml|csproj|slnx?|json|log|txt|md)(?:\(\d+,\d+\))?/],
    ["error", /\b(?:error|failed|FAILED|refused)\b/],
    ["warning", /\bwarning\b/],
    ["id", /\b(?:CS|MSB|NU|SA|CA|IDE)\d{3,5}\b/],
    ["string", /'[^'\n]*'/],
    ["number", /\b\d[\d,.]*(?:\s?(?:ms|s|MB|%))?\b/],
  ];

  function outputLine(line) {
    if (/^\s*\$\s/.test(line)) return shellLine(line);
    if (/^\s*(?:changed|removed|generated|locked|reran|ran):/.test(line)) return span("cause", line);
    if (/^\s*fuse:/.test(line)) return '<span class="tk-summary">' + tokenize(line, outputRules) + "</span>";
    var stamp = /^(\d{4}-\d\d-\d\d[ T][\d:.]+)(.*)$/.exec(line);
    if (stamp) return span("dim", stamp[1]) + tokenize(stamp[2], outputRules);
    return tokenize(line, outputRules);
  }

  function kindOf(pre, text) {
    var label = (pre.getAttribute("data-label") || "").toLowerCase();
    if (shellLabels[label]) return shellLine;
    if (/\.json$/.test(label) || /^\s*[\[{]/.test(text)) return function (l) { return tokenize(l, jsonRules); };
    if (pre.classList.contains("out") || label === "output" || label === "message" || /\.log$/.test(label)) return outputLine;
    return null;
  }

  document.querySelectorAll(".page pre > code").forEach(function (code) {
    if (code.children.length) return;
    var text = code.textContent, line = kindOf(code.parentElement, text);
    if (!line) return;
    code.innerHTML = text.split("\n").map(line).join("\n");
    code.parentElement.classList.add("highlighted");
  });
})();
