// Runs in the browser over OUTPUTS ([{name, html}]): parses every sanitiser output four ways and reports what the allowlists do not permit.
(function () {
  var XHTML = 'http://www.w3.org/1999/xhtml';
  var ELEMENTS = new Set(('a abbr acronym address article aside b bdi bdo big blockquote br caption center cite code col colgroup dd del details dfn div dl dt em ' +
    'figcaption figure font footer h1 h2 h3 h4 h5 h6 header hr i img ins kbd li main mark nav ol p picture pre q rp rt ruby s samp section small source span strike ' +
    'strong sub summary sup table tbody td tfoot th thead time tr tt u ul var wbr style').split(' '));
  var ATTRS = new Set(('class id style title alt lang dir align valign width height border cellpadding cellspacing colspan rowspan hspace vspace size span start value ' +
    'bgcolor color bordercolor face nowrap noshade reversed open scope headers abbr summary rules frame clear char datetime type href rel target src srcset sizes ' +
    'referrerpolicy background').split(' '));
  var DATA_IMAGE = /^data:image\/(png|gif|jpeg|jpg|webp|bmp|avif|x-icon|vnd\.microsoft\.icon);base64,[A-Za-z0-9+\/=_%-]*$/i;
  var violations = [];
  function bad(name, what) { if (violations.length < 200) violations.push(name + ': ' + what); }

  function urlOk(attr, v) {
    var t = v.replace(/^[\u0000-\u0020]+|[\u0000-\u0020]+$/g, '').replace(/[\t\n\r]/g, '');
    if (attr === 'href') return /^(https?:\/\/[^\/\\]|mailto:|tel:|#)/i.test(t);
    return /^https?:\/\/[^\/\\]/i.test(t) || DATA_IMAGE.test(t);
  }

  function checkTree(name, root) {
    var els = root.querySelectorAll('*');
    for (var i = 0; i < els.length; i++) {
      var el = els[i], tag = el.localName;
      if (el.namespaceURI !== XHTML) { bad(name, 'foreign element ' + tag); continue; }
      if (!ELEMENTS.has(tag)) bad(name, 'element ' + tag);
      for (var j = 0; j < el.attributes.length; j++) {
        var a = el.attributes[j], an = a.name;
        if (a.namespaceURI) bad(name, 'namespaced attribute ' + an);
        if (!ATTRS.has(an)) { bad(name, 'attribute ' + an + ' on ' + tag); continue; }
        if (an === 'href' || an === 'src' || an === 'background') { if (!urlOk(an, a.value)) bad(name, an + '=' + a.value.slice(0, 80)); }
        if (an === 'srcset') {
          a.value.split(/\s+/).forEach(function (tok) { if (/^[a-z][a-z0-9+.-]*:/i.test(tok) && !urlOk('src', tok)) bad(name, 'srcset ' + tok.slice(0, 80)); });
        }
        if (an === 'style' && /expression|behavior|binding|@import|javascript:|position\s*:\s*(fixed|absolute)/i.test(noStrings(a.value))) bad(name, 'style ' + a.value.slice(0, 80));
      }
      if (tag === 'style') {
        var t = el.textContent;
        if (/<|@import|expression|javascript:|behavior|binding/i.test(noStrings(t))) bad(name, 'style text ' + t.slice(0, 80));
        if (/position\s*:\s*fixed/i.test(noStrings(t))) bad(name, 'style fixed');
      }
    }
  }

  // Browsers that do not yet write the newline the parser drops after <pre> serialise it away; the whitespace is the only change.
  function norm(s) { return s.replace(/(<pre[^>]*>)\n/g, '$1'); }
  // CSS strings may hold anything (they are data); only what is outside them can act.
  function noStrings(css) { return css.replace(/"(?:[^"\\\n]|\\[\s\S])*"|'(?:[^'\\\n]|\\[\s\S])*'/g, '""'); }
  function serialise(frag) { var d = document.createElement('div'); d.appendChild(frag.cloneNode(true)); return d.innerHTML; }

  // Positive controls: known-bad outputs must be flagged, otherwise a clean result proves nothing.
  var controls = ['<img src=x onerror=alert(1)>', '<a href="javascript:alert(1)">x</a>', '<svg><circle/></svg>', '<p style="background:expression(x)">x</p>',
    '<script>x</script>', '<img src="http://h.test/a" srcset="javascript:x 1x">', '<style>@import url(x);</style>'];
  var flagged = 0;
  controls.forEach(function (c) { var before = violations.length; var d = document.createElement('div'); d.innerHTML = c; checkTree('control', d); if (violations.length > before) flagged++; });
  violations.length = 0;
  var controlsOk = flagged === controls.length;

  OUTPUTS.forEach(function (o) {
    var n = o.name, h = o.html, s1, s2, s3, s4;
    try {
      var d = document.createElement('div'); d.innerHTML = h; s1 = d.innerHTML; checkTree(n, d);
      var d2 = document.createElement('div'); d2.innerHTML = s1; s2 = d2.innerHTML;
      if (norm(s1) !== norm(s2)) bad(n, 'innerHTML is not idempotent: ' + JSON.stringify(s1.slice(-120)) + ' -> ' + JSON.stringify(s2.slice(-120)));
      var tpl = document.createElement('template'); tpl.innerHTML = h; s3 = serialise(tpl.content);
      if (norm(s3) !== norm(s1)) bad(n, 'template parse differs from div parse: ' + JSON.stringify(s3.slice(-120)) + ' vs ' + JSON.stringify(s1.slice(-120)));
      var doc = new DOMParser().parseFromString('<!doctype html><html><body>' + h + '</body></html>', 'text/html');
      s4 = doc.head.innerHTML + doc.body.innerHTML;
      if (norm(s4) !== norm(s1)) bad(n, 'document parse differs from div parse: ' + JSON.stringify(s4.slice(-120)) + ' vs ' + JSON.stringify(s1.slice(-120)));
      var rng = document.createRange(); rng.selectNode(document.body);
      if (norm(serialise(rng.createContextualFragment(h))) !== norm(s1)) bad(n, 'contextual fragment differs');
      // the output as the body of a document, then checked again as a tree
      checkTree(n + '[doc]', doc.body);
      var all = doc.querySelectorAll('script,iframe,object,embed,form,svg,math,link,meta,base,input,textarea,select,template,noscript');
      if (all.length) bad(n, 'active element after document parse: ' + all[0].localName);
    } catch (e) { bad(n, 'exception ' + e); }
  });
  window.__structure = 'VIOLATIONS=' + (violations.length + (controlsOk ? 0 : 1)) + ' OUTPUTS=' + OUTPUTS.length + (controlsOk ? '' : ' CONTROLS_FAILED') + '\n' + violations.join('\n');
})();
